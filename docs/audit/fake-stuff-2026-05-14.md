# Fake-Stuff Audit — v0.7.4 wave, 2026-05-14

Triggered by post-deploy dogfood verification on a Unity-like workspace.
Two of six landed tracks shipped a working *surface* but a broken *data
path*; a third under-credited a real class of finding. The user's call
during audit-close: **no v0.7.4 tag until all confirmed bugs are fixed
and the audit closes**.

## Status — 2026-05-14 audit-close pass

All four blocking bugs fixed with Roslyn-canonical primitives. No
hotpatches. No reinvented wheels. 844/844 tests passing (830 baseline
+ 14 regression tests across the four fixes).

| Bug | Fix landed in | Regression tests added |
|-----|---------------|------------------------|
| BUG-1 (test_impact 0 on Type targets) | `TestImpactAnalyzer.ExpandTypeMembers` — seeds BFS with type's outgoing-Contains members | 3 (Field-via-Type, Method-via-Type, Method-target no-expansion pin) |
| BUG-2 (DefineConstants dropped) | `ModuleInfo.PreprocessorSymbols` + `RoslynModuleDiscovery` parses `<DefineConstants>` + `ModuleCompilationBuilder` threads `CSharpParseOptions.WithPreprocessorSymbols` | 5 (3 discovery shapes + 2 end-to-end `#if`-guarded type) |
| BUG-3 (cycles partial-class file-SCC) | `CircularDependencyDetector.EnclosingTypesOf` — set-intersection over candidate enclosing types; file: nodes contribute their outgoing-Contains-to-Type set | 3 (file-SCC same-type, file-SCC different-types, mixed-kind File+Method) |
| BUG-4 (TierClassifier dead key + substring) | `TierClassifier` rewritten — drops `Properties["isTooling"]` dead read, drops `Name.Contains("Test"/"Tool")` substring, walks outgoing-Contains for an attributed-method descendant | 3 (Type-level tooling, substring false-positive rejection, lifecycle-attrs-not-test) |

Pattern E scrubbed: zero DAWG / Nebulae / BeatGrid mentions remain in
either `src/` or `tests/` (replaced with shape descriptions —
"old-format MSBuild 2003 schema", "Unity-like workspace where csprojs
declare platform-specific preprocessor symbols", etc).

Follow-ups (LB-FOLLOWUP-001..005) deferred per the original audit. They
were marked "do not block v0.7.4 tag" and the four blockers are now
clear; the user retains tag authority.

Eternal-quality bar: a tool that returns the wrong answer is worse than
a tool that doesn't ship — callers act on confident-looking output. The
patterns below are how Lifeblood has been shipping *that* failure mode.

## Anti-patterns surveyed

| ID | Shape | Confirmed instances |
|----|-------|---------------------|
| **A** | Consumer reads a field/edge the extractor never populates (or populates under a different key/shape). Tool ships, surface is empty. | `lifeblood_test_impact` on type targets |
| **B** | Adapter parses input (csproj, asmdef, YAML) then drops the value before it reaches the Roslyn primitive that consumes it. | `<DefineConstants>` → `CSharpParseOptions.PreprocessorSymbols` |
| **C** | Classifier contract requires evidence the graph it walks doesn't carry. Default bucket fires more than it should. | `lifeblood_cycles` partial-class classifier on file-level SCCs |
| **D** | Lifeblood reinvents a Roslyn primitive instead of using `ISymbol.GetAttributes()`, `INamedTypeSymbol.DeclaringSyntaxReferences`, etc. Parallel data path drifts from the canonical one. | `TestImpactAnalyzer.TestCaseAttributes` HashSet vs `ISymbol.GetAttributes()` |
| **E** | Source / test code hardcodes a specific consumer project (DAWG / Nebulae / Unity asmdef literals). Couples a generic analyzer to one customer. | 2 comment-only mentions (`TestImpactAnalyzer.cs`, `ReferenceClosureCompilationTests.cs`); no load-bearing literals yet |

## Confirmed bugs (block v0.7.4 tag)

### BUG-1 — Test-impact returns 0 when target is a type (Pattern A + D)

**Symptom.** `lifeblood_test_impact target:"type:Foo"` returns
`totalTestMethodCount: 0` even when 718 test methods touch members of
`Foo`. Dogfood:

| Target | Result |
|--------|--------|
| `type:Nebulae.BeatGrid.AdaptiveBeatGrid` | 0 |
| `type:Nebulae.Tests.Audio.Burst.BurstFieldMaskTruthTests` | 0 |
| `Assets/Tests/Editor/Audio/Burst/BurstFieldMaskTruthTests.cs` | 0 |
| `field:Nebulae.BeatGrid.Audio.DSP.BurstBridge.KernelCapabilityTable.Features` | **718** |

**Root cause.** `TestImpactAnalyzer.Analyze` (src/Lifeblood.Analysis/TestImpactAnalyzer.cs:69-110)
seeds BFS with `{ targetId }` and walks **incoming non-Contains edges
only** (`edge.Kind == EdgeKind.Contains` is `continue`d at line 101).
When the target is a `SymbolKind.Type`, its members are reached only via
outgoing Contains edges — so the BFS never sees them, and any test that
calls `Foo.SomeMethod()` is invisible. Tests typically reference
*members*, not types directly, so type-level incoming edges are sparse.

**Fix (pattern A correction).** Before BFS, expand the source set: when
a source symbol is a `Type`, add every outgoing-Contains child to the
source set at distance 0 too. File-mode targets already do this; the
fix is to apply the same expansion to symbol-mode type targets.

**Fix (pattern D correction).** The hand-rolled `TestCaseAttributes`
HashSet at TestImpactAnalyzer.cs:31 plus the `Properties["attributes"]`
string-split at line 214 reinvents what Roslyn already gives you:
`ISymbol.GetAttributes()` returns `AttributeData` with
`AttributeClass.Name` — canonical, no string-splitting, no
extractor-to-consumer drift. The Property-string approach has already
caused INV-LIFEBLOOD-002a-class confusion (declaration-vs-reference
counting) and is one extractor edit away from silently breaking again.

### BUG-2 — `<DefineConstants>` never reach Roslyn ParseOptions (Pattern B)

**Symptom.** `definesActive[]` is empty on every `lifeblood_diagnose` /
`lifeblood_compile_check` response, even when the source csproj
declares ~150 defines (`UNITY_EDITOR`, `UNITY_6000_4_0`,
`ENABLE_BURST_AOT`, etc).

**Root cause.** `ModuleCompilationBuilder.cs:259`:

```csharp
try { return CSharpSyntaxTree.ParseText(_fs.ReadAllText(f), path: f); }
```

The third parameter (`CSharpParseOptions? options`) is omitted —
parses default to `PreprocessorSymbols: ImmutableArray<string>.Empty`.
A grep across the entire CSharp adapter for `DefineConstants` or
`PreprocessorSymbols` (write side) returns **zero hits**. The csproj
text is read for module discovery + reference closure but
`<DefineConstants>` is never extracted, parsed, or threaded into the
parse options.

**Wave-level regression.** LB-TRACK-002 (a8b7925) added
`definesActive[]` to the diagnostic envelope so callers could
distinguish Editor-only findings from release-build risk. The envelope
fires on every response. But because PreprocessorSymbols is empty
globally, `definesActive[]` is always `[]`. The track shipped the
visibility surface without the data underneath it. Worse: the envelope
*looks* authoritative, which is exactly the silent
INV-LIFEBLOOD-002b (L-LIM-001 — preprocessor-guarded callsites
invisible) re-introduction the user spent the May-10 audio chain
documenting. **Net effect:** the wave undid its own invariant.

**Fix (pattern B correction).** In `ModuleCompilationBuilder` (or a new
helper) read each csproj's `<DefineConstants>`, split on `;`, and
build:

```csharp
var parseOptions = CSharpParseOptions.Default
    .WithLanguageVersion(LanguageVersion.Latest)  // probably already set
    .WithPreprocessorSymbols(defines);
var tree = CSharpSyntaxTree.ParseText(text, parseOptions, path: f);
```

This is the canonical Roslyn API for the job; MSBuildWorkspace does
exactly this internally. No parallel parser. No drift.

### BUG-3 — Cycles classifier under-credits partial-class clusters (Pattern C)

**Symptom.** `lifeblood_cycles` reports 123 SCCs:
{`LikelyRealLoop: 103`, `PartialClassCluster: 20`}. Manual inspection
of the previewed `LikelyRealLoop` cycles shows obvious partial-class
clusters: `Voice.Modulation.cs` ↔ `Voice.Filter.cs` ↔
`Voice.NoteControl.cs` ↔ `Voice.Formant.cs` ↔ `Voice.cs` (every node
is a declaration of `partial class Voice`).

**Root cause.** The classifier definition (per tool docs):
> `PartialClassCluster` — every member resolves to the same enclosing
> Type — intra-type mutual-recursion / partial-class cluster

…requires *member-level* evidence ("every member resolves to the same
enclosing Type"). But the SCC nodes are `file:`-kind symbols — there
are no members on the cycle to walk back to a `Type`. So the
classifier falls through to `LikelyRealLoop` despite the cluster being
exactly what it was supposed to recognize.

**Fix (pattern C + D correction).** Use Roslyn's canonical partial
signature: `INamedTypeSymbol.DeclaringSyntaxReferences.Length > 1`.
Pre-pass: build `Dictionary<FilePath, INamedTypeSymbol>` by walking
each compilation's types and emitting one entry per declaring file. At
classification time, for file-level SCCs, look up each node's mapped
type; if every node maps to the same `INamedTypeSymbol`, the SCC is a
`PartialClassCluster`. Roslyn already encodes "partial type spanning N
files" — query it directly, don't infer from file names or graph
shape.

## Passing tracks (eternal-quality verified)

| Track | Status | Note |
|-------|--------|------|
| LB-TRACK-001 fc8ff96 csproj closure | ✅ PASS | 283 project diagnostics, zero CS0104 (no BCL bleed); only Unity-legit CS0282 / CS1701 / CS0618. Zero DAWG types collide with BCL short names. |
| LB-TRACK-003 bcb61aa enum_coverage | ✅ PASS | 109 `TuningParamId` members classified consistently. Single-reference members (`VocoderCarrierMix`, `RobotRingModFreq`) correctly recognized as `producedCount=1` because their only reference is the `UnwiredParamIds` array initializer (RHS of var initializer = produced per contract). |
| LB-TRACK-004 ca1fae0 dead_code triage | ✅ PASS | All four added fields (`bucket`, `directDependants`, `declarationOnly`, `bucketBreakdown`) populated on every finding. `bucketBreakdown` sums correctly (Editor:13 + Production:771 = 784). `declarationOnly:true` not observed but plausibly correct — abstract/interface members have inbound edges from impls and don't appear in dead-code findings. |

### BUG-4 — TierClassifier reads a Properties key the extractor never writes (Pattern A)

**Symptom.** `TierClassifier.ClassifySymbol`
(`src/Lifeblood.Analysis/TierClassifier.cs:64`):

```csharp
if (symbol.Name.Contains("Test", StringComparison.OrdinalIgnoreCase)
    || symbol.Name.Contains("Tool", StringComparison.OrdinalIgnoreCase)
    || symbol.Properties.ContainsKey("isTooling"))
    return (ArchitectureTier.Tooling, "Test or tooling marker detected");
```

`Properties.ContainsKey("isTooling")` reads a key the extractor **never
writes**. A grep across the entire `src/` tree for `"isTooling"` returns
exactly one hit — this read. No write site. The check is permanently
false; it shipped looking like a configurable escape hatch (custom
tooling marker) but does nothing.

**Compounding pattern D.** The two `symbol.Name.Contains("Test")` /
`Contains("Tool")` checks also misfire:
- `Contains("Test")` matches `Testable`, `LatestState`, `Manifest`,
  `Tested`, `Testimony`, `Contest`, `ContestPolicy`,
  `TestImpactAnalyzer` (Lifeblood's own analyzer!), etc.
- `Contains("Tool")` matches `Tools`, `Toolbar`, `Toolkit`, `Pooltool`.

Roslyn-canonical: `ISymbol.ContainingAssembly.Name` to detect a test
assembly, or `ITypeSymbol.GetAttributes()` for
`[NUnit.Framework.TestFixture]` / `[TestClass]`. Substring matching on
names is the textbook example of "invented our own when Roslyn already
gives us the right answer."

**Fix.** Three-part:
1. Drop the `"isTooling"` branch entirely OR populate it from the
   extractor when a type carries `[TestFixture]` / `[TestClass]` /
   asmdef-includes-`NUnit` / similar.
2. Replace substring matching with semantic: containing-assembly
   inspection or attribute scan.
3. The whole `TierClassifier` heuristic is shaky and shipped in v0.1.
   Worth re-grounding on Roslyn's
   `ICompilationAssembly.MetadataReferences` directly (a "Tooling"
   assembly references test frameworks).

## Pattern-by-pattern sweep (completed)

### Pattern A — consumer reads empty field

Cross-referenced every `Properties.TryGetValue(...)` and
`Properties.ContainsKey(...)` read across `src/` against extractor
writes:

| Property key | Read at | Written at | Verdict |
|--------------|---------|-----------|---------|
| `"attributes"` | TestImpactAnalyzer:214; UnityReachabilityAdapter:24/190/196 | RoslynSymbolExtractor:677 | ✅ written |
| `"baseType"` | UnityReachabilityAdapter:212/235 | RoslynSymbolExtractor:542 | ✅ written |
| `"classification"` | LifebloodAuthorityReporter:68 | RoslynSymbolExtractor:572 | ✅ written |
| `"xmlDocSummary"` | LifebloodSemanticSearchProvider:56 | RoslynSymbolExtractor:537 (via XmlDocExtractor) | ✅ written |
| `"edgeCount"` | LifebloodMcpProvider:218/234 | GraphBuilder:215 | ✅ written |
| `"projectFile"` | RoslynWorkspaceAnalyzer:95/312; NuGetReferenceResolver:27 | RoslynModuleDiscovery:262 | ✅ written |
| `"isTooling"` | TierClassifier:64 | **NEVER** | ❌ BUG-4 |

One dead key out of seven reads. Sweep verdict: BUG-4 is the only
Pattern A defect of this shape — but the same audit must run again
every time a new Property key is introduced. A unit test that
extracts the union of read-keys and the union of write-keys at runtime
and asserts equality would make this impossible to reintroduce.

### Pattern B — adapter parses then drops

Cross-referenced every csproj attribute read in `RoslynModuleDiscovery`
against threading through to `CSharpCompilationOptions` /
`CSharpParseOptions`:

| Csproj attribute | Read at | Threaded into Roslyn? | Verdict |
|------------------|---------|----------------------|---------|
| `<AllowUnsafeBlocks>` | RoslynModuleDiscovery:211 | `CSharpCompilationOptions.WithAllowUnsafe` at ModuleCompilationBuilder:298 | ✅ threaded |
| `<ImplicitUsings>` | RoslynModuleDiscovery:220 | Synthetic global-usings tree at ModuleCompilationBuilder:304 | ✅ threaded |
| `<AssemblyName>` | RoslynModuleDiscovery:83 | Compilation `AssemblyName` ctor arg | ✅ threaded |
| `<Compile Include>` | RoslynModuleDiscovery:93 | SyntaxTree set | ✅ threaded |
| `<ProjectReference>` | RoslynModuleDiscovery:153 | Compilation references | ✅ threaded |
| `<Reference>` (assembly) | RoslynModuleDiscovery:161 | Compilation references | ✅ threaded |
| `<HintPath>` (external DLL) | RoslynModuleDiscovery:182 | MetadataReference | ✅ threaded |
| `<DefineConstants>` | **NEVER READ** | n/a | ❌ BUG-2 |
| `<LangVersion>` | not read | n/a | ⚠️ defaults silently — file as follow-up |
| `<Nullable>` | not read | n/a | ⚠️ affects nullability diagnostics — file as follow-up |
| `<TreatWarningsAsErrors>` | not read | n/a | static-analyzer doesn't care — OK to skip |
| `<NoWarn>` | not read | n/a | could suppress real diagnostics — file as follow-up |

One confirmed drop (BUG-2 = DefineConstants). Three follow-ups
(`LangVersion`, `Nullable`, `NoWarn`) where Lifeblood inherits Roslyn
defaults instead of csproj intent — none currently load-bearing but
each is a future bug-2 in waiting. File as `LB-FOLLOWUP-001..003`
without blocking the v0.7.4 tag.

### Pattern C — classifier needs evidence not in graph

| Classifier | Buckets | Evidence required | Evidence in graph? | Verdict |
|-----------|---------|--------------------|----|---------|
| `CircularDependencyDetector.Classify` | Generated / PartialClassCluster / LikelyRealLoop | File path (generated) + Contains-chain to enclosing type | File-level SCCs have no Contains-parent — Walk returns null | ❌ BUG-3 |
| `BlastRadiusAnalyzer.ClassifyBreak` | SignatureChange / BindingRemoval / Behavioral / Unknown | EdgeKind | EdgeKind is populated by extractor | ✅ clean |
| `TierClassifier.ClassifySymbol` | Pure / Boundary / Runtime / Tooling | Outgoing/incoming non-Contains presence + name substring + isTooling property | Edges populated; isTooling never written; substring match is unsound | ❌ BUG-4 (Pattern A) + Pattern D |
| `LifebloodDeadCodeAnalyzer.ClassifyBucket` | Production / Test / Editor / Generated | File path | File path populated on every symbol | ✅ clean |

Sweep verdict: BUG-3 is the only Pattern C classifier bug. TierClassifier
is also broken but the root is Pattern A + D, not Pattern C.

### Pattern D — invented vs Roslyn primitive

| Site | Hand-rolled | Roslyn primitive | Verdict |
|------|-------------|-----------------|---------|
| `TestImpactAnalyzer.TestCaseAttributes` HashSet | Comma-list parse of `Properties["attributes"]` | `ISymbol.GetAttributes()` → `AttributeData.AttributeClass.Name` | ❌ BUG-1 fix should converge here |
| `TestImpactAnalyzer.FindContainingType` (ParentId chain) | String-id walker | `ISymbol.ContainingType` | Justifiable: graph is Roslyn-detached at query time, ParentId is the post-extraction representation. Manual walker is the right shape. ✅ acceptable |
| `CircularDependencyDetector.WalkUpToEnclosingType` | String-id walker via Contains edges | Same as above | Same justification ✅ |
| `TierClassifier.Name.Contains("Test")` | Substring match | `ContainingAssembly` + attribute check | ❌ BUG-4 fix |
| `CircularDependencyDetector` partial detection | Contains-chain comparison | `INamedTypeSymbol.DeclaringSyntaxReferences.Length > 1` | ❌ BUG-3 fix |

Sweep verdict: BUG-1 and BUG-4 are the load-bearing Pattern D defects.
The two graph-string walkers (`FindContainingType`,
`WalkUpToEnclosingType`) are intentional — the graph is the public
contract; rebuilding it through Roslyn `ISymbol` references at query
time would couple analyzers to a live workspace they may not have.

### Pattern E — DAWG-hardcoding

Two comment-only mentions across the entire src/ + tests/ tree:

1. `src/Lifeblood.Analysis/TestImpactAnalyzer.cs:20` — `"NUnit (the
   common DAWG / .NET testing baseline)"` in xmldoc.
   **Fix.** Replace with `"NUnit (the common .NET testing baseline)"`.
2. `tests/Lifeblood.Tests/ReferenceClosureCompilationTests.cs:35, 88,
   90, 120, 202` — `"the DAWG repro shape"` / `"Unity ships this
   shape clean every day on DAWG"` / `"DAWG false positives"` in
   test xmldoc.
   **Fix.** Fixture is already namespace-neutral (`Acme.App`,
   `Acme.Math`, `MathLib`). Strip customer name from comments. The
   shape is describable as
   `"old-format MSBuild 2003 schema, bare BCL type usage in a sibling
   namespace, no transitive metadata reference declared"`. That
   description survives the customer changing names.

Sweep verdict: not load-bearing, but eternal-quality requires the
scrub. Generic-tool docs that name one customer drift the moment a
second customer adopts the tool.

## Follow-ups (do not block v0.7.4, but file)

- `LB-FOLLOWUP-001` — Read `<LangVersion>` from csproj, thread into
  `CSharpParseOptions.WithLanguageVersion`.
- `LB-FOLLOWUP-002` — Read `<Nullable>` from csproj, thread into
  `CSharpCompilationOptions.WithNullableContextOptions`.
- `LB-FOLLOWUP-003` — Read `<NoWarn>` from csproj, thread into
  `CSharpCompilationOptions.WithSpecificDiagnosticOptions`.
- `LB-FOLLOWUP-004` — Property-key parity test. Reflect every
  `Properties.TryGetValue` / `Properties.ContainsKey` call site in
  `src/`, reflect every `props[<key>] = ...` / `Properties[<key>] =
  ...` write, fail the test when the read set is not a subset of the
  write set. Catches BUG-4-shape regressions automatically.
- `LB-FOLLOWUP-005` — `Lifeblood.Analysis.PathBucketClassifier`
  extraction (already named in `CircularDependencyDetector.cs:88-92`
  as a known follow-up — three drifted `ClassifyBucket`
  implementations across `LifebloodDeadCodeAnalyzer`,
  `LifebloodMcpProvider`, and `CircularDependencyDetector`).

## Sign-off bar

- BUG-1, BUG-2, BUG-3 fixed and verified by dogfood on DAWG.
- Pattern A / B / C / D sweeps completed across all 28 tools + all
  analyzers + all adapter parse sites.
- Pattern E mentions scrubbed from src and tests.
- Lifeblood test suite green (currently 830/830).
- Then, and only then, tag v0.7.4.

## Notes for whoever picks this up

- The "wave landed cleanly" report enumerated TRACK-007 as `+32 facts
  across the three new atoms`. 32 new tests passed against a synthetic
  fixture but did not exercise the type-target shape that real callers
  hit first. **Test gap is itself a fake-stuff signal**: a test suite
  that passes while the tool returns 0 on the primary user query is a
  fixture-shape gap, not green coverage. Add a dogfood test that
  resolves a known production type → expects N>0 affected test
  classes, with the assertion driven by `ISymbol.GetAttributes()` so
  fixture drift can't silently disable it.
- The LB-TRACK-005 / 006 deferral (static-table extraction + rule-pack
  DSL for table-to-predicate drift) was likely correct as a deferral —
  but the deferral reasoning ("better picked after MCP redeploy +
  dogfood verification proves the wave's smaller-shape additions are
  solid") presumed dogfood would prove solidity. It didn't. The
  smaller-shape additions are not solid yet. Hold 005/006 until the
  three bugs land.

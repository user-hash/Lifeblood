# Dogfood Findings

First successful self-analysis: 2026-04-07. Lifeblood analyzed its own codebase (9 modules at the time, now 11). These are the real issues discovered by running our own tool on ourselves. All findings were fixed in the same session. The numbers below reflect the codebase state at the time of discovery.

**Current state (2026-04-13, session 8, post-v0.6.4 dead-code accuracy pass):** 1,887 symbols, 8,223 edges, 11 modules, 238 types, 0 violations (17 rules). Verified on a real 400k+ LOC Unity workspace (see Session 8 below): 151,827 edges (was 90,486, +68%). **22 MCP tools (12 read + 10 write).** All Roslyn capabilities at Proven. **557 tests.** **22 port interfaces.** Implicit global usings injection closes the 42% `GetSymbolInfo` null-resolution class that silently degraded every call-graph tool since v0.3.0.

### Session 8. Dead-code accuracy pass and call-graph completeness (2026-04-13)

v0.6.4 shipped five extraction fixes and a root-cause compilation fix. Verified on both Lifeblood itself (self-analysis) and a real 75-module 400k+ LOC Unity workspace, anonymized as `WorkspaceX`.

**Self-analysis impact:**
- Dead code findings: 150 to 10 (93% reduction)
- Edges: 5,777 to 8,223 (+42%)
- Remaining 10 findings: runtime entry points (6), static field initializer method-groups (2), static field in accessor (1), internal constructor (1). All correct.

**WorkspaceX verification (independent user-run, 2026-04-13):**

Call-graph tools verified against known ground truth:

| Tool | Test symbol | Result |
|------|------------|--------|
| `find_references` | `UtilityA.ComputeDelta` | 3 refs (was 4 before a recent code change that removed one caller - correct) |
| `dependants` | `UtilityB.EnforceConstraint` | 11 dependants (was 7 - +4 from new callers added by user - correct) |
| `blast_radius` | `UtilityA.ComputeDelta` | 7 affected (was 9 - correctly reflects narrower chain after refactor) |
| `file_impact` | `Operations.cs` | 9 deps, 3 dependants - rich, accurate |

Edge count: 90,486 to 151,827 (+68%). The +61K edges are real semantic relationships that were previously missed due to the implicit global usings gap.

Dead-code accuracy on WorkspaceX:
- 10/10 known-used methods verified as NOT flagged (zero false negatives on spot check)
- 2 recently deleted methods correctly absent from findings
- 867 total candidates, 461 in project code (non-lifecycle)
- Known remaining false-positive classes (expected and documented):
  - 8 Unity engine callbacks (`OnAudioFilterRead`, `OnApplicationFocus`, etc.) - called by the Unity runtime via `SendMessage`, invisible to static analysis
  - 16 event handlers (`Raise*`, `Handle*`) - wired via delegates at sites the extractor does not yet trace

The Unity callback and event handler classes are structural limitations of static analysis against a runtime that uses reflection-based dispatch. They are documented in `INV-DEADCODE-001` and surfaced in every response via the `warning` field.

### Session 7. Post-BCL three-seam framing (2026-04-10)

Two-phase fix shipped against the silent zero-result class on Unity, .NET Framework, and Mono workspaces. Five reviewer-reported findings collapsed into three architectural seams. All evidence below comes from a real 75-module 400k+ LOC Unity workspace, anonymized as `WorkspaceX` with module `WorkspaceX.AudioModule` containing the canonical regression target `Voice.SetPatch(VoicePatch)`.

**DF-S7-1. BCL double-load corrupted every semantic model in Unity workspaces.** `ModuleCompilationBuilder.CreateCompilation` always prepended the host .NET 8 BCL bundle, even for modules that already shipped their own BCL via `<Reference Include="netstandard|mscorlib|System.Runtime">`. Result: every System type existed in two assemblies. Roslyn emitted CS0433 (ambiguous type) and CS0518 (predefined type missing) on every System usage, the semantic model became unusable, `GetSymbolInfo` returned null at every call site, and every walker tool silently produced empty results. `find_references` for `method:Voice.SetPatch(VoicePatch)` returned 0 results (the correct count was 18). Fix: new `BclOwnershipMode` enum on `ModuleInfo`, decided ONCE during `RoslynModuleDiscovery.ParseProject`. `ModuleCompilationBuilder` reads the field and gates host BCL injection. Empirical impact on `WorkspaceX` (75 modules): `WorkspaceX.AudioModule` went from 29,523 errors before to 3 unused-field warnings after. Total graph edges: 78,126 to 86,334, restoring +8,208 silently-dropped edges.

**DF-S7-2. Display-string match across the source/metadata boundary silently dropped legitimate call sites.** `RoslynCompilationHost.FindReferences` compared `ISymbol.ToDisplayString()` against the resolved target. Different parameter formatters across source and metadata symbols (driven by Roslyn's default `CSharpErrorMessageFormat` and version drift) silently produced different strings for the same parameter type. The walker dropped legitimate matches without diagnostic. Fix: replace display-string comparison with canonical Lifeblood symbol-ID comparison via `BuildSymbolId(resolved) == targetCanonicalId`. The graph and the walker now share one builder.

**DF-S7-3. Resolver silent fallback returned `methods[0]` for missing overloads.** `RoslynWorkspaceManager.FindInCompilation` enumerated all methods on a type when no overload matched, returning `methods[0]`. So asking for a method that didn't exist returned an unrelated method's call sites. The wrong target then matched no nodes and the query came back empty. Fix: kind-filtered, name-filtered, signature-strict member lookup with documented contract. The resolver never silently substitutes an unrelated member. Lenient escape valves (single overload, no signature given) are explicit and tested.

**DF-S7-4. Lifeblood's symbol ID grammar was implicitly inherited from Roslyn's default.** Multiple method-ID builders in the C# adapter (`RoslynSymbolExtractor`, `RoslynEdgeExtractor`, `RoslynCompilationHost.BuildSymbolId`, `RoslynWorkspaceManager.FindInCompilation`) each formatted parameter types via `ToDisplayString()` with different `SymbolDisplayFormat` choices. Drift was inevitable. Fix: `Internal.CanonicalSymbolFormat.ParamType` is now the single pinned `SymbolDisplayFormat` for parameter type display strings. Every method-ID builder routes through it.

**DF-S7-5. GraphBuilder dropped all but the last partial declaration for partial types.** The Contains edge tracker used last-write-wins on `parentId`, so `lookup OuterPartialType` returned ONE partial file out of 160 on a real workspace. Fix: track every observed `ParentId` per symbol id, and have `Build()` synthesize one Contains edge per unique parent. Domain-layer change, no schema bump. Workspace impact: +874 edges from partial-type unification.

**DF-S7-6. Truncated method ids (`method:Voice.SetPatch` without parens) returned empty results.** Reviewer reports kept saying "the same bug is still broken" because they were typing the natural human form of the symbol id instead of the parens-bearing canonical form. Fix: introduce the `ISymbolResolver` port with explicit resolution order. The order is exact canonical, then truncated method form, then bare short name. All read-side handlers route through it. `LB-BUG-002` was a misdiagnosed format mismatch, not a code-path bug.

**DF-S7-7. Lookup of partial types was non-deterministic.** The single `filePath` field on `lookup` came from last-write-wins on partial declaration order. Fix: `LifebloodSymbolResolver.ChoosePrimaryFilePath` picks deterministically. The order is filename matches type name, then filename starts with `<TypeName>.`, then lexicographic first. Lookup response now also includes the full sorted `filePaths[]`.

**DF-S7-8. Csproj edits did not invalidate cached `ModuleInfo` under incremental analyze.** A user could fix a csproj reference, run `lifeblood_analyze` with `incremental: true`, and the cached stale BclOwnership would silently re-introduce the bug. Fix: `AnalysisSnapshot.CsprojTimestamps` tracks csproj timestamps. `IncrementalAnalyze` checks csproj timestamps before the .cs file loop and forces re-discovery and recompile when a csproj edits. INV-BCL-005.

**DF-S7-9. CS0227 false positives on modules that use `unsafe` blocks.** A real-world Unity package contained `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` in its csproj, but compilation was always created without `WithAllowUnsafe(true)`. Fix: `ModuleInfo.AllowUnsafeCode` typed bool field, parsed in discovery, consumed in compilation. Closes the broader bug class. Any csproj with unsafe blocks no longer poisons its semantic model. This is the canonical example of the **csproj-driven compilation facts as a documented convention** seam.

**DF-S7-10. `lifeblood_execute` had no ergonomic access to the loaded semantic model.** Scripts had to thread through `_compilations` private fields by reflection. This was undocumented and brittle. Fix: `RoslynSemanticView` is now a typed read-only POCO carrying `Compilations`, `Graph`, and `ModuleDependencies`. It is constructed once per `GraphSession.Load` and shared by reference across consumers. It is passed as the script-host globals object via `CSharpScript.RunAsync<RoslynSemanticView>(...)`, so scripts read `Graph`, `Compilations`, and `ModuleDependencies` as bare top-level identifiers.

**DF-S7-11. `xUnit` parallelism violated the `Console.Out` global redirection in `RoslynCodeExecutor.Execute`.** Test classes in different projects calling `Execute()` simultaneously would clobber each other's stdout capture. Fix: `[CollectionDefinition("ScriptExecutorSerial", DisableParallelization = true)]` collection applied to every test class that calls `Execute()`. Don't try to fix the global state. Pin the test execution to be serial.

**Lessons from session 7:**

- Fixing the underlying semantic model fixes every consumer of that model simultaneously. The BCL fix transitively closed `find_references`, `dependants`, null-conditional invocation handling, and partial interface dispatch, without changing those code paths.
- Three-seam framing beats five-fix piecemeal framing. The seam framing produces lasting architectural contracts (`INV-RESOLVER-001..004`, `INV-COMPFACT-001..003`, `INV-VIEW-001..003`) that prevent the same bug class from recurring.
- Schema changes are expensive. Read models on the resolution result are cheap. Partial-type unification stayed off the Domain layer. The resolver computes the merge on read.
- Reviewer diagnoses can be wrong about which layer is broken. Always reproduce with the canonical input format before assuming the framing is correct.

### Session 3 Dogfood Findings (2026-04-08, passes 16-25)

**DF-S3-1: ScriptSecurityScanner `dynamic` keyword check was ineffective** — The AST check `id.Parent is TypeSyntax` failed for the most common usage `dynamic x = ...;` because the parent of the `IdentifierNameSyntax("dynamic")` is `VariableDeclarationSyntax`, not a `TypeSyntax`. The `dynamic` keyword — which bypasses compile-time checks and enables late-bound calls that evade the string blocklist — passed the scanner unchallenged. Fixed by removing the parent type check. Added 2 test cases that would have caught this.

**DF-S3-2: RoslynWorkspaceManager.FindInCompilation bypassed overload disambiguation** — When resolving a method symbol like `method:Ns.Type.Foo(int,string)`, the traversal loop used `GetMembers(part).FirstOrDefault()` and returned the first matching method immediately, ignoring the `ParamSignature`. The overload disambiguation code (lines 86-101) was unreachable. FindReferences and Rename on overloaded methods would operate on the wrong overload. Fixed to check for multiple overloads and match by param signature before returning.

**DF-S3-3: ProcessIsolatedCodeExecutor path with spaces** — The ScriptHost path was interpolated into dotnet CLI arguments without quotes: `run --project {path} --no-build`. Paths containing spaces would break argument parsing. Fixed to quote the path.

**DF-S3-4: Rules pack LB-010 description/enforcement mismatch** — Description said "Application must not reference CLI or Server" but `mustNotReference` only enforced "Lifeblood.CLI". Added LB-010a for "Lifeblood.Server". Rule count: 16 → 17.

**DF-S3-5: Python adapter import edges used wrong file ID for `__init__.py`** — `_extract_edges` generated sourceId from `py_module.replace('.', '/') + '.py'`, producing `file:pkg.py` for `__init__.py` files instead of `file:pkg/__init__.py`. The edge source didn't match any symbol and was silently dropped by the dangling-edge filter. Fixed by passing `rel_path` to `_extract_edges` and using it directly.

All 5 fixed in-session. 197 tests pass (was 195). Build: 0 warnings, 0 errors.

### Session 3 Late Findings (passes 26-35)

**DF-S3-6: String blocklist bypassed by whitespace between tokens** — `code.Contains("Process.Start")` does not match `"Process . Start"`. C# allows arbitrary whitespace between member-access tokens, so `System . IO . File . Delete("x")` compiles fine and bypasses every pattern in the blocklist. Fixed by normalizing whitespace around dots before pattern matching. New `NormalizeMemberAccess` collapses `"foo . bar"` → `"foo.bar"` for checking without modifying the executed code.

**DF-S3-7: AST IsBlockedStaticCall bypassed by whitespace/comments** — `memberAccess.ToString()` preserves trivia (whitespace, comments), so `"Process . Start"` produces `Contains("Process.Start")` = false. Fixed by reconstructing the member chain from AST nodes (`ReconstructMemberChain`), which strips all trivia and produces the canonical dotted name.

**DF-S3-8: AdhocWorkspace resource leak on MCP server reload** — `RoslynWorkspaceManager` created `AdhocWorkspace` (IDisposable) lazily but never disposed it. Each MCP `Load()` created new `RoslynCompilationHost` and `RoslynWorkspaceRefactoring`, each with their own workspace. Old instances leaked until GC. Fixed: `RoslynWorkspaceManager : IDisposable`, `RoslynCompilationHost : IDisposable`, `RoslynWorkspaceRefactoring : IDisposable`. `WorkspaceSession.Clear()` now disposes old services via `(x as IDisposable)?.Dispose()`.

**DF-S3-9: Weak assertion in CycleRepo blast radius test** — Used `blastA.AffectedCount > 0 || blastB.AffectedCount > 0` (OR) instead of asserting BOTH. In a true A↔B cycle, both must have affected symbols. Fixed to assert each independently.

**DF-S3-10: GraphSession allocated new JsonSerializerOptions per Load()** — `new JsonSerializerOptions { ... }` inside `Load()` instead of a static field. Fixed by hoisting to `static readonly RulesJsonOpts`.

All 5 fixed in-session. 201 tests pass (was 197). Build: 0 warnings, 0 errors.

### Session 4 Dogfood Findings (2026-04-08, passes 36-45)

**DF-S4-1: Comment injection bypassed string blocklist for patterns not in AST scanner** — `File./**/Delete("x")` normalizes whitespace around dots but not comments. The AST scanner only checked `Process.Start`, `Assembly.Load`, `Thread.Abort` — not `File.Delete`, `Environment.Exit`, `Marshal.*`, `WebRequest.Create`, etc. Fixed: added all file/directory/environment/marshal/network patterns to `IsBlockedStaticCall`, which uses `ReconstructMemberChain` (trivia-immune).

**DF-S4-2: Object creation not checked by AST scanner** — `new FileInfo(...)`, `new HttpClient()`, `new Socket()` were only in the string blocklist. The AST scanner had no `ObjectCreationExpressionSyntax` case. Fixed: added creation type check with `IsBlockedCreationType` for all dangerous types including `Process` and `ProcessStartInfo`.

**DF-S4-3: `new Process()` + `p.Start()` bypass** — `var p = new Process(); p.Start()` bypassed both layers: the string blocklist had `Process.Start` but not `new Process`, and `p.Start()` doesn't match the pattern because the call is on a variable. Fixed: added `"new Process"` and `"new ProcessStartInfo"` to string blocklist, added `Process` to `IsBlockedCreationType`.

**DF-S4-4: C# 9 target-typed `new()` bypassed object creation check** — `Process p = new();` uses `ImplicitObjectCreationExpressionSyntax`, not `ObjectCreationExpressionSyntax`. Fixed: added case for implicit creation that walks up to the variable declaration to find the declared type.

**DF-S4-5: GraphBuilder created self-referencing Contains edge on `ParentId == Id`** — If a symbol's ParentId equals its own Id, the builder would synthesize a self-referencing Contains edge (caught by validator downstream but architecturally wrong). Fixed: added `symbol.ParentId == symbol.Id` early-exit guard.

All 5 fixed in-session. 209 tests pass (was 201). Build: 0 warnings, 0 errors.

### Session 5 Dogfood Findings (2026-04-08, passes 46-55)

**DF-S5-1: Local function calls produced dangling edges** — `FindContainingMethodOrLocal` returned the local function's `IMethodSymbol`, but local functions aren't extracted as graph symbols. Calls inside local functions had source IDs like `method:Type.LocalFunc()` that didn't exist in the graph, so GraphBuilder silently dropped them. Fixed: changed `LocalFunctionStatementSyntax` case from `return` to `continue` (same pattern as `AccessorDeclarationSyntax`), attributing calls to the enclosing method instead.

**DF-S5-2: CI python-adapter path doubled up** — `working-directory: adapters/python` + argument `adapters/python/test-fixtures/mini-app` resolved to `adapters/python/adapters/python/test-fixtures/mini-app`. Fixed argument to `test-fixtures/mini-app`.

Both fixed in-session. 210 tests pass (was 209). Build: 0 warnings, 0 errors.

### Session 6 Dogfood Findings (2026-04-08, passes 56-65)

**DF-S6-1: ReconstructMemberChain stopped at InvocationExpressionSyntax — chained calls bypassed security scanner** — `Process.GetCurrentProcess().Kill()` passed both security layers unblocked. The AST scanner's `ReconstructMemberChain` walked `MemberAccessExpressionSyntax` nodes but stopped at `InvocationExpressionSyntax`, producing only `"Kill"` instead of `"Process.GetCurrentProcess.Kill"`. The string blocklist also missed it: `"Process.GetCurrentProcess().Kill()"` doesn't contain `"Process.Kill"` as a contiguous substring. Fixed architecturally: (1) `ReconstructMemberChainParts` now walks through `InvocationExpressionSyntax` to reconstruct the full chain, (2) `IsBlockedStaticCall` replaced substring `Contains()` with structured `BlockedReceiverMethods` dictionary — receiver+method pairs checked against chain parts. The terminal method is matched against the blocked set, and the receiver type is checked anywhere earlier in the chain.

**DF-S6-2: RoslynEdgeExtractor missed ImplicitObjectCreationExpressionSyntax (C# 9 target-typed new)** — Same bug class as DF-S4-4 in the security scanner. `ObjectCreationExpressionSyntax` was handled but C# 9's `Foo x = new()` uses `ImplicitObjectCreationExpressionSyntax`, a separate AST node type. Constructor call edges for all target-typed `new()` usage were silently dropped. Fixed: changed the `case` from `ObjectCreationExpressionSyntax` to `BaseObjectCreationExpressionSyntax` (common base in Roslyn 4.x), and updated `ExtractConstructorCallEdge` parameter type to match. +9 edges recovered in self-analysis (2371 → 2382, now 2386 with test additions).

Both fixed in-session. 214 tests pass (was 210 + 4 new). Build: 0 warnings, 0 errors.

**DF-S6-3: Self-referencing Calls edge for recursive methods** — `RoslynSymbolExtractor.ExtractType` calls itself recursively for nested types. The edge extractor created a self-loop Calls edge (`method:X → method:X`). Discovered by exporting the self-analysis graph and checking for `sourceId == targetId`. Self-referencing edges carry no dependency information (a symbol always depends on itself), so they waste space and could mislead analysis. Fixed: added `if (sourceId == targetId) return;` guard in `AddEdge`. The GraphValidator already allows self-referencing Calls (recursion is valid structurally), but the adapter correctly filters them as analytically useless.

**DF-S6-4: Indexer override edge ID mismatch** — ExtractIndexer creates IDs with paramSig (`property:Type.this[int]`) but the override edge used `prop.Name` (`property:Type.this[]`). ID mismatch caused dangling edges silently dropped by GraphBuilder. Found during 10-pass manual code audit. Fixed: property override case checks `IsIndexer` and uses matching paramSig format.

**DF-S6-5: Memory architecture — 32GB OOM on 100+ assembly project** — Analyzing a 75-module Unity project consumed 32GB RAM and crashed the .NET host. Root causes: (1) ALL compilations loaded simultaneously in a Dictionary, (2) NuGet MetadataReferences duplicated per-module with no cross-module cache, (3) no streaming — everything at once. Fixed architecturally: streaming compilation with downgrading. Each module is compiled, extracted, then `Emit()` → `MetadataReference.CreateFromImage()` produces a ~10-100KB PE reference instead of keeping the ~200MB full compilation. `SharedMetadataReferenceCache` deduplicates NuGet references across modules. `RetainCompilations` flag on `AnalysisConfig` controls whether full compilations are kept (MCP server) or released (CLI). Memory: 32GB → 4GB for the same project.

**DF-S6-6: Unity csproj filesystem scan hang** — Unity-generated .csproj files use old MSBuild format with explicit `<Compile Include="..."/>` items. Module discovery ignored those and did `FindFiles("*.cs", recursive: true)` instead — 75 projects all rooted at the same directory caused 75 full recursive scans of the entire Unity project. Hung indefinitely. Fixed: detect `<Compile Include>` items in the csproj XML. If present (Unity/legacy format), use those. If absent (SDK-style), fall back to filesystem scan. Also extract `<Reference Include>` for Unity assembly-level dependencies.

**DF-S6-7: Duplicate edges from partial classes** — `typeSymbol.GetMembers()` returns ALL members including from other partial declarations. Each file's `Extract()` call has its own dedup set. Partial classes caused the same Overrides/Inherits/Implements edges to be emitted once per partial file. On a 75-module Unity workspace: 11,423 duplicate edge validation errors. Fixed: `GraphBuilder.Build()` now deduplicates ALL edges by `(sourceId, targetId, kind)` using a Dictionary (first-write-wins, consistent with symbol dedup). The per-file `seen` set remains as a fast first-pass filter.

241 tests pass. 1057 symbols, 2594 edges (self). 43,800 symbols, 70,600 edges (75-module Unity workspace). 0 violations. Build: 0 warnings, 0 errors.

### Session 2 Dogfood Findings (2026-04-08)

**DF-S2-1: BlastRadiusAnalyzer depth boundary off-by-one** — maxDepth=1 was including nodes at depth 2. BFS used `> maxDepth` but enqueue happened before depth check. Fixed to `>= maxDepth`. Found by strengthening a weak test that only checked inclusion, not exclusion.

**DF-S2-2: SymbolKind.Property missing from domain enum** — Properties were mapped to `Field` kind but used `property:` ID prefix. Mismatch between graph model and symbol identity. Added `Property` to SymbolKind enum and JSON schema.

**DF-S2-3: Evidence.Default confidence was Proven** — Domain default for all evidence was `ConfidenceLevel.Proven`. Edges without explicit evidence got `Proven` confidence for free. Fixed: evidence must always set confidence explicitly (no default).

**DF-S2-4: JsonEvidence DTO defaulted to Proven** — External adapters that omitted confidence got `Proven`. Fixed default to `BestEffort`.

**DF-S2-5: ProcessIsolatedCodeExecutor pipe deadlock** — WaitForExit called before ReadToEnd. If child process writes more than pipe buffer, WaitForExit hangs forever. Fixed: read stdout/stderr asynchronously before waiting.

**DF-S2-6: AnalyzeWorkspaceUseCase had no validation** — Graph could pass through use case without validation. GraphSession and CLI validated independently, but the use case itself didn't. Fixed: added GraphValidator.Validate() call in use case.

**DF-S2-7: TypeScript adapter used 'high' confidence (removed from enum)** — Schema change removed `high` from ConfidenceLevel. TS compiler caught the mismatch at build time. Fixed capabilities to `proven`/`bestEffort`.

All fixed in-session. Total: 7 findings, 7 fixes, 0 remaining.

### Session 2 Late Findings (passes 6-15)

**DF-S2-8: TS/Python adapters didn't filter dangling edges** — Same bug class as GraphBuilder. Edges to non-existent symbols inflated metrics. Fixed in both adapter output pipelines.

**DF-S2-9: IsFromSource false positive on "System" prefix** — `ns.StartsWith("System")` also filtered user namespaces like `SystemManager`. Fixed to segment-based check: `ns == "System" || ns.StartsWith("System.")`.

**DF-S2-10: TS/Python crossModuleReferences claimed "bestEffort"** — Both are single-module adapters, cannot do cross-module resolution. Fixed to `"none"`.

**DF-S2-11: TS symbol-extractor used `kind: 'field'` for properties** — After adding `SymbolKind.Property` to C# domain, TS adapter wasn't updated. Fixed to `kind: 'property'` with `id: 'property:...'`.

**DF-S2-12: FileInfo/DirectoryInfo/DllImport bypassed blocklist** — Instance methods and P/Invoke not covered by string blocklist. Added `new FileInfo`, `new DirectoryInfo`, `DllImport`, `Marshal.*` patterns.

**DF-S2-13: Lifeblood rule pack incomplete** — Only 11 rules, missing Analysis→Application, Connectors→Analysis boundaries. Expanded to 16 rules covering full hexagonal boundary.

**DF-S2-14: Stale `high` confidence in 4 adapter READMEs + ADAPTERS.md** — Removed `high` from ConfidenceLevel enum but didn't update documentation. Fixed in Go, Rust, TypeScript READMEs and ADAPTERS.md.

**DF-S2-15: CONTRIBUTING.md said "9 frozen ADRs"** — Same drift as ARCHITECTURE.md, fixed to 11.

All fixed in-session. Total: 15 additional findings, 15 fixes, 0 remaining.

## F1: JSON Exporter Silently Drops Fields

**Severity:** Critical — silent data loss

The JSON exporter uses `DefaultIgnoreCondition = WhenWritingDefault`. For enums, the default value is index 0. This means the **first member of every enum is omitted** from exported JSON when it's the active value:

- `SymbolKind.Module` = index 0 → `kind` field **missing** from all 9 module symbols
- `EdgeKind.Contains` = index 0 → `kind` field **missing** from 485 containment edges (74% of all edges)
- `Visibility.Public` = index 0 → `visibility` field **missing** from 350 public symbols

The round-trip test passed by accident: the importer defaults missing enums to index 0, which happens to be the correct value. The test only exercised `EdgeKind.Implements` and `SymbolKind.Type` — both non-zero — so it never caught the bug.

**Root cause:** `WhenWritingDefault` is designed for optional/nullable fields. Using it with enums that have meaningful zero values is a semantic mismatch.

## F2: Architecture Rules Were Never Enforced

**Severity:** Critical — false sense of safety

Both shipped rule packs (`packs/hexagonal/rules.json`, `packs/clean-architecture/rules.json`) use `must_not_reference` (snake_case). The rules loader uses `JsonNamingPolicy.CamelCase`, which expects `mustNotReference`. Result: every rule loaded with `MustNotReference = null` → zero violations, always.

`lifeblood analyze --project . --rules packs/hexagonal/rules.json` reported 0 violations. Not because the architecture was clean — because no rules were actually checked.

**Root cause:** The rules schema (`rules.schema.json`) specifies camelCase. The rule pack files were written in snake_case. No validation step catches the mismatch. No test exercises rule loading from actual pack files.

## F3: Cross-Module Dependency Matrix Is Empty — RESOLVED

**Severity:** Moderate — feature gap → **Fixed (2026-04-09)**

The context pack's `dependencyMatrix` (which should show how many type-level edges cross module boundaries) returned 0 entries. Module-level `DependsOn` edges exist (from `.csproj` parsing), but no type-level cross-module edges survived.

**Root cause:** The `IsFromSource` filter (now `IsTracked`) rejected metadata symbols from other analyzed modules because `DeclaringSyntaxReferences.Length == 0` for PE-downgraded references. Types from other modules appeared as metadata, not source, so all cross-module edges were dropped.

**Fix:** `RoslynEdgeExtractor.IsTracked` now accepts metadata symbols whose `ContainingAssembly.Name` matches a known workspace module (via `KnownModuleAssemblies`). The analyzer sets this before compilation starts. Cross-module edges are now extracted at Proven confidence. Capability upgraded from `BestEffort` to `Proven`. Additionally: `FindReferences` rewritten to direct compilation scan (cross-assembly), `FindDefinition`/`GetDocumentation` prefer source-defined symbols via `ResolveFromSource`, and `ModuleInfo.ExternalDllPaths` loads HintPath DLLs (Unity engine assemblies) to resolve compilation diagnostics.

## F4: 57 "Invariants" — Noise, Not Signal

**Severity:** Moderate — undermines trust in output

The context pack's `invariants` array contained 57 entries. Almost all were "`type:X is pure (zero dependencies)`" for enums, value types, and leaf classes. Technically correct, but useless: telling an AI agent that `EdgeKind` is "pure" adds no architectural insight.

**Root cause:** `ExtractInvariants` iterates all Pure-tier symbols. TierClassifier marks any symbol with zero outgoing non-Contains edges as Pure. For a codebase with 80 types, most leaf types qualify. The invariant generator doesn't distinguish "architecturally significant purity" (modules) from "trivially obvious purity" (enums).

## F5: Reading Order Puts High-Instability File First

**Severity:** Minor — counterintuitive output

`ReadingOrderGenerator` sorts by lowest instability first (stable core files), then highest fan-in. But `JsonGraphImporter.cs` (instability 1.0, fan-in 12) appeared first in the reading order. The file-level instability is computed as the minimum instability among child types — if any child type has low instability, the file ranks as stable even though its primary type is highly unstable.

**Root cause:** Min-aggregation of child instability at file level can be misleading when a file contains a mix of stable and unstable types.

## F6: Hotspots Include Composition Roots

**Severity:** Minor — noisy output

The hotspot detector flagged `mod:Lifeblood.CLI` and `mod:Lifeblood.Tests` as hotspots. These are composition roots and test projects — they're *supposed* to have high fan-out. Flagging them as hotspots is technically correct but architecturally meaningless.

**Root cause:** Hotspot detection uses raw coupling metrics without considering the symbol's architectural role (composition root, test project, etc.).

## What Dogfooding Proved

The good news: the core pipeline works. Module discovery, symbol extraction, edge extraction, graph construction, validation, and all five analyzers produced correct results for a real 9-module codebase. The hexagonal boundaries were accurately detected. Domain was correctly identified as the pure leaf.

The bad news: the output layer (JSON export, rule loading, context pack generation) had multiple bugs that were invisible to unit tests. Every one of these bugs could have been caught earlier by running the tool on a real project — including itself.

**Lesson:** Unit tests prove components work in isolation. Dogfooding proves the product works as a product.

## Resolution

All six findings were fixed in the same session they were discovered:

- **F1:** `WhenWritingDefault` replaced with `WhenWritingNull`. All enum, bool, and int fields now always serialize.
- **F2:** Rule packs rewritten to camelCase. New `packs/lifeblood/rules.json` validates Lifeblood's own architecture.
- **F3:** `BuildDependencyMatrix` now uses module-level DependsOn edges with cross-module member edge counting.
- **F4:** Invariants filtered to module-level only (2 invariants instead of 57).
- **F5:** Reading order sort uses stable-first ordering (lowest instability, then highest fan-in).
- **F6:** Hotspot detection excludes modules (composition roots have high coupling by design).

Post-fix dogfood output: 495 symbols, 661 edges, 9 modules, 0 violations, 0 dangling edges, 0 duplicates.

## Second Dogfood: TypeScript Adapter

The TypeScript adapter was the second major dogfood milestone. It analyzed its own source code (a TypeScript project) and produced valid `graph.json` that the C# CLI consumed without modification.

```
# TS adapter analyzes itself
node adapters/typescript/dist/index.js adapters/typescript > graph.json

# Lifeblood C# CLI reads the JSON graph
dotnet run --project src/Lifeblood.CLI -- analyze --graph graph.json
Symbols: 49
Edges:   51
Modules: 1
Types:   9
```

Zero dangling edges. Zero duplicate symbols. Full adapter metadata round-trips (version, language, capabilities).

This proved three things:
1. The JSON protocol works for non-C# languages without modification
2. External process adapters integrate cleanly through `graph.json`
3. The universal model handles TypeScript semantics (classes, interfaces, type aliases, enums, heritage clauses)

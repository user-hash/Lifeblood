# C# Adapter Invariants

Canonical symbol IDs, the typed semantic-view handle, BCL ownership, and
csproj-driven compilation facts. Every rule on this page lives in or is
consumed by `Lifeblood.Adapters.CSharp`.

## Canonical Symbol ID Determinism

Lifeblood symbol IDs must be byte-identical for the same underlying
method regardless of which compilation extracts it. Drift between two
instances of the same method (one source, one metadata; or one declared
in module A and one implementing the same interface in module B)
silently corrupts every downstream feature: `find_references`,
`dependants`, blast radius, the fuzzy short-name resolver, and every
cross-module tool that relies on string equality of canonical IDs.

- **INV-CANONICAL-001. Transitive-mode compilations receive the full transitive dependency closure, not just direct `ModuleInfo.Dependencies`.** Scoped to `ReferenceClosureMode.Transitive` (SDK-style MSBuild) — see INV-MODULE-REFS-001 for the dual-mode contract. Unlike MSBuild's ProjectReference flow, `CSharpCompilation.Create` does NOT walk references transitively. If C references B and B references A, compiling C must pass BOTH B and A as explicit `MetadataReference`s. Missing transitive A → every type from A appearing through B's public surface becomes a Roslyn error type with empty `ContainingNamespace` → `CanonicalSymbolFormat.BuildParamSignature` emits the short type name without qualifier → canonical ID drifts (`method:C.Impl.M(BarType)` instead of `method:C.Impl.M(A.BarType)`). Drift is silent: compilation succeeds, extraction runs, graph is populated; only cross-module lookups fail. The enforcement site is `Internal.ModuleCompilationBuilder.ProcessInOrder`, which routes each Transitive-mode module's direct deps through `ComputeTransitiveDependencies` before collecting PE references. `ModuleInfo.Dependencies` keeps direct-only semantics (feeds module-to-module graph edges + topological sort, both correct on direct deps). Pinned by `CanonicalSymbolFormatTests.ComputeTransitiveDependencies_*` (flat chain, diamond, cycle) plus the end-to-end `AnalyzeWorkspace_ThreeModuleTransitiveChain_ProducesCanonicalMethodIds`.

- **INV-MODULE-REFS-001. Compilation reference closure mirrors the build tool that owns the csproj.** SDK-style MSBuild closes ProjectReference transitively (the default for modern .NET projects). Old-format MSBuild 2003-schema csprojs (Unity asmdef generators, legacy .NET Framework projects) compile each module against direct dependencies only; transitively-reachable assemblies are NEVER on the compile classpath. Lifeblood mirrors the source-of-truth closure semantics per-module so the loaded compilation matches what the consumer's build tool would produce. The detection signal is csproj-shape only: a `<Project>` root whose XML namespace is `http://schemas.microsoft.com/developer/msbuild/2003` AND has no `Sdk` attribute is old-format → `ReferenceClosureMode.DirectOnly`; otherwise → `ReferenceClosureMode.Transitive`. Decided once at discovery time on `ModuleInfo.ReferenceClosure`, consumed once at compilation time in `ModuleCompilationBuilder.ProcessInOrder`, never re-derived. Failure mode if violated: in a Unity-shape workspace, sibling-namespace assemblies (e.g. `Acme.Math.dll`) become visible to lookup in modules that never asmdef-referenced them; bare unqualified calls (e.g. `Math.Min` inside `namespace Acme.Foo` with `using System;`) bind to the workspace-local namespace per C# §11.7.2 namespace-or-type lookup precedence, shadowing the BCL type. Unity ships clean; Lifeblood diagnose emits a spurious CS0234. Pinned by `ReferenceClosureModeDiscoveryTests` (csproj-shape detection theory) and `ReferenceClosureCompilationTests` (end-to-end binding behavior under both modes against a 3-module fixture with a sibling-namespace shadow).

## Adapter Semantic View

Each language adapter publishes a typed read-only accessor for its loaded
semantic state. Tools that need read access (script host today; debuggers,
visualizers, custom linters tomorrow) consume the view by reference, never
threading raw fields through individual constructors.

- **INV-VIEW-001. Each language adapter publishes a typed semantic view.**
  The C# adapter publishes `Lifeblood.Adapters.CSharp.RoslynSemanticView`
  (`Compilations`, `Graph`, `ModuleDependencies`). Future language adapters
  publish their own view type using their language's semantic model.

- **INV-VIEW-002. Tools consume the view, not raw fields.** The script host
  (`RoslynCodeExecutor`) takes a `RoslynSemanticView` in its constructor.
  Future consumers (debuggers, visualizers, linters, REPLs) take the same
  view. NEVER thread raw `Compilations` / `Graph` / `ModuleDependencies`
  through individual tool constructors.

- **INV-VIEW-003. The view is constructed once per workspace load and
  shared by reference.** `GraphSession.Load` (full and incremental paths)
  builds the view once with three field assignments and passes it by
  reference to consumers. The view never holds locks, never caches anything
  beyond its three references, never mutates state. It is purely a typed
  handle. Sharing avoids accidental divergence between consumers' views of
  the same workspace.

## BCL Ownership

Some csprojs ship their own base class library via `<Reference Include="netstandard|mscorlib|System.Runtime">` (Unity ships .NET Standard 2.1; .NET Framework / Mono ship mscorlib). Plain SDK-style csprojs (`<Project Sdk="Microsoft.NET.Sdk">`) don't. They rely on the host runtime BCL.

Lifeblood treats this as a discovered module fact: `Lifeblood.Application.Ports.Left.ModuleInfo.BclOwnership` is `BclOwnershipMode.HostProvided` or `BclOwnershipMode.ModuleProvided`. The decision is computed ONCE during `RoslynModuleDiscovery.ParseProject` and consumed by `ModuleCompilationBuilder.CreateCompilation`. Detection logic lives in exactly one place: `RoslynModuleDiscovery.ReferenceDeclaresBcl` plus its `ParseAssemblyIdentitySimpleName` and `IsBclSimpleName` helpers. The compilation builder reads the field and never re-derives.

- **INV-BCL-001. Single BCL per compilation.** Two BCLs causes CS0433 + CS0518 on every System type; semantic model becomes unusable (`GetSymbolInfo` returns null at every call site → `find_references`, `dependants`, call-graph extraction silently produce zero results).
- **INV-BCL-002. Module owns its BCL when its csproj declares one.** A `<Reference>` whose parsed assembly identity or HintPath basename matches `netstandard`, `mscorlib`, or `System.Runtime` is the authoritative declaration. Such modules MUST NOT also receive the host BCL bundle.
- **INV-BCL-003. Host BCL is the fallback for plain SDK-style projects.** Modules with no BCL-naming `<Reference>` receive `BclReferenceLoader.References.Value`.
- **INV-BCL-004. BCL ownership is decided at discovery time, single source of truth.** Detection logic in `RoslynModuleDiscovery`. `ModuleCompilationBuilder` reads the typed field — no filename sniffing, no re-derivation at the compilation layer.
- **INV-BCL-005. Incremental re-analyze respects csproj edits.** `AnalysisSnapshot.CsprojTimestamps` tracks csproj timestamps. A csproj-timestamp change forces re-discovery and recompile even if no .cs file changed. Without this, a csproj edit that flips BCL ownership silently re-introduces the double-BCL bug under incremental mode.

Full plan + empirical evidence + rollout history at `.claude/plans/bcl-ownership-fix.md`.

## Csproj-Driven Compilation Facts

Csproj is the source of truth for module-level compilation options. Each fact
flows through one path: discover at parse time → store on `ModuleInfo` →
consume at compilation time. The pattern is the contract. Every future
csproj-driven option follows the same shape.

- **INV-COMPFACT-001. Csproj is authoritative for module-level compilation
  options.** Shipped facts: `BclOwnership` (v2), `AllowUnsafeCode` (v4),
  `ImplicitUsings` (v0.6.4 — closes the implicit-global-usings extractor
  gap), `LangVersion` / `Nullable` warning level / `NoWarn` (FOLLOWUP-001..003,
  post-v0.7.3 — threaded into `CSharpParseOptions` /
  `CSharpCompilationOptions` so a csproj declaring `<LangVersion>13</LangVersion>`
  actually compiles under C# 13 instead of the host default), `DefineConstants`
  (BUG-2, post-v0.7.3 — preprocessor symbols flow into `ParseOptions` so
  `#if MY_FLAG` blocks survive parse instead of dead-code-eliminating).
  Future additions (`Platform`, `WarningLevel`, etc.) follow the same
  shape with zero new incremental work per INV-COMPFACT-003.

- **INV-COMPFACT-002. Each compilation fact lives as a typed field on
  `ModuleInfo`.** Default value preserves pre-fix behavior. Set during
  `RoslynModuleDiscovery.ParseProject`. Consumed exactly once during
  `Internal.ModuleCompilationBuilder.CreateCompilation`. NEVER re-derive from
  the csproj at the compilation layer; NEVER sniff filenames as a substitute
  for declared options.

- **INV-COMPFACT-003. Csproj edits invalidate cached module facts.** The
  `AnalysisSnapshot.CsprojTimestamps` tracking added in v2 (INV-BCL-005)
  covers every compilation fact for free. `IncrementalAnalyze` rebuilds the
  entire `ModuleInfo` on csproj edit, not just one field. The next compilation
  fact added under this convention ships with zero new incremental work.

## Diagnostic Parity Wall

Lifeblood's `diagnose` output on a workspace `dotnet build` calls clean
must itself be clean within a canonical parity diagnostic class.
Individual INVs below (`INV-DIAGNOSTIC-MSBUILD-IMPLICIT-NOWARN-001`,
`INV-DIAGNOSTIC-NUGET-BINDING-PARITY-001`, `INV-DIAGNOSTIC-IVT-PARITY-001`,
`INV-MODULE-REFS-001`) each close one historical regression class; the
wall ratchet pins all of them at one chokepoint so a future regression
that re-opens any of the classes fails the same test.

- **INV-DIAGNOSTIC-PARITY-001. Lifeblood diagnose on Lifeblood itself never fires parity-class diagnostics.** The canonical parity ID set is `{ CS0122, CS0117, CS0234, CS1503, CS1701, CS1702, CS1705, CS1729 }` — every member corresponds to one historical Lifeblood-side regression class an existing INV already prevents at the compilation seam (IVT propagation, reference closure, binding-redirect baseline, friend-assembly downstream). Lifeblood's own source tree is the fixture: every commit on `main` must pass `dotnet build` before reaching the test suite, so any parity-class diagnostic firing under Lifeblood's discovery + `ModuleCompilationBuilder` + `RoslynCompilationHost` pipeline against Lifeblood's own modules is by definition a Lifeblood-side false positive. Adding a new parity ID costs one string in the set; **removing one requires written justification + user approval (per `INV-WORK-008`) — relaxing the wall defeats the regression catch it exists to provide**. Pinned by `BuildDiagnosticParityTests.LifebloodSelfDiagnose_NeverFiresParityClassDiagnostics`.

## Reference Set Normalization

A workspace's reference set frequently carries multiple `MetadataReference`
entries that share an assembly simple-name but disagree on version (BCL ref
pack 8.0.x.x vs NuGet contract assembly 4.x.x.x is the canonical collision;
xunit / Microsoft.NET.Test.Sdk closures hit it deterministically). MSBuild
silently unifies these through its `<AutoUnify>true</AutoUnify>` default
before the compiler ever sees them. Roslyn does not.

- **INV-DIAGNOSTIC-IVT-PARITY-001. Csproj `<InternalsVisibleTo Include="X" />` items synthesize `[assembly: InternalsVisibleTo("X")]` attribute trees onto the producer compilation.** MSBuild's `GenerateAssemblyInfo` target writes the matching `[assembly: InternalsVisibleTo("X")]` attribute into `obj/<Tfm>/<AssemblyName>.AssemblyInfo.cs` during build; Lifeblood's SDK-style source scan skips `obj/` so that file never enters the compilation. Without surfacing the items the producer PE carries no friend-assembly metadata and every internal access from a friend module fires CS0122 — empirically 223 spurious findings on Lifeblood.Tests against the Adapters.CSharp surface while `dotnet build` was clean on the same compilation. The discovery seam (`RoslynModuleDiscovery.ParseProject`) parses `<InternalsVisibleTo>` items into `ModuleInfo.InternalsVisibleTo` (trim, distinct by ordinal, empty Includes drop). The compilation seam (`Internal.ModuleCompilationBuilder.CreateCompilation`) prepends a synthetic syntax tree at path `<InternalsVisibleTo>.cs` carrying one `[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("X")]` per declared target, parsed with the module's own `CSharpParseOptions` so downstream `AddSyntaxTrees` / `ReplaceSyntaxTree` calls do not throw "Inconsistent language versions" against modules declaring a non-default `<LangVersion>`. Modules that surface friend access through hand-authored `[assembly: InternalsVisibleTo]` attributes in source compile correctly regardless — their IVT lives on a `.cs` file that flows through normal source discovery. NEVER widen the SDK-style scan to include `obj/` as a substitute (the directory holds MSBuild-generated artifacts whose lifecycle is owned by `dotnet build`, not by the source tree). Pinned by `InternalsVisibleToParityTests` (discovery shape: single item, multi-item union + dedupe + empty-drop, no-items default; compilation shape: synthesized IVT attribute round-trips on the producer compilation's `Assembly.GetAttributes()`; end-to-end shape: a friend-named consumer reaching producer internals emits zero CS0122).

- **INV-DIAGNOSTIC-NUGET-BINDING-PARITY-001. Top-level compilation reference sets handed to `CSharpCompilation.Create` are unified by assembly simple-name, highest version wins.** Mirrors MSBuild's `<AutoUnify>true</AutoUnify>` default so the top-level reference graph Lifeblood compiles against matches the one MSBuild would resolve. This closes the front-loaded case: two refs in the same per-module reference list disagreeing on version for the same simple-name (canonical example: BCL ref pack provides `System.Memory 8.0.0.0` while a NuGet contract assembly hauls in `System.Memory 4.5.4`). Single enforcement seam: `Internal.MetadataReferenceDeduplicator.Deduplicate`, called by `Internal.ModuleCompilationBuilder.CreateCompilation` after the per-module reference list is assembled (BCL + NuGet + external DLLs + downgraded dependency PE refs) and immediately before `CSharpCompilation.Create`. Identity read off `PortableExecutableReference.GetMetadata()` via `AssemblyMetadata.GetModules()[0].GetMetadataReader().GetAssemblyDefinition()` — Roslyn's public primitive. References whose metadata cannot be parsed as an assembly (modules, native DLLs that escaped the loader filter, in-memory refs without an emitted identity) pass through unchanged so the loader can still surface load failures as regular diagnostics. **Scope note**: dedup does NOT silence cross-module TypeRef binding-redirect warnings (the empirical CS1701 / CS1702 firings against `Lifeblood.Tests` originated from upstream PE images like `xunit.core` carrying baked TypeRefs to older `System.Runtime` versions that Roslyn flags against the loaded BCL version — those firings are unreachable from top-level dedup). MSBuild handles that class via the implicit `<NoWarn>` baseline pinned by `INV-DIAGNOSTIC-MSBUILD-IMPLICIT-NOWARN-001` below; the two invariants are complementary, neither substitutes the other. Pinned by `MetadataReferenceDeduplicationTests` (6 facts: highest-version wins, order-independent, distinct simple-names all survive, empty input, unreadable identity passes through, end-to-end zero CS1701 on a synthesized duplicate-identity compilation).

- **INV-DIAGNOSTIC-MSBUILD-IMPLICIT-NOWARN-001. The MSBuild csc-default NoWarn baseline (`1701`, `1702`) is unioned into every discovered module's suppression set.** `Microsoft.CSharp.CurrentVersion.targets` sets `<NoWarn>$(NoWarn);1701;1702</NoWarn>` for every csc invocation MSBuild produces, regardless of csproj schema. Both IDs are cross-module TypeRef binding-redirect warnings (`CS1701`: "Assuming assembly reference matches identity"; `CS1702`: same family with stricter version comparison). They fire per consuming type-ref whenever an upstream PE's recorded version of a transitively-shared assembly disagrees with the version currently loaded — exactly the shape that produced 7,642 spurious `CS1701` findings against `Lifeblood.Tests` (xunit.core baked against `System.Runtime 4.0.0.0` vs BCL ref pack `8.0.0.0`) while `dotnet build` was clean on the same compilation. The baseline is documented MSBuild behavior, not a Lifeblood opinion: not mirroring it ships diagnose responses with thousands of warnings the workspace's own CI does not see. The seam is `RoslynModuleDiscovery.MsbuildImplicitNoWarnBaseline` (private static, one source of truth) unioned into `noWarnIds` during `ParseProject` before the field lands on `ModuleInfo`. User-declared `<NoWarn>` entries union with the baseline (dedupe by `OrdinalIgnoreCase`); a module that genuinely needs `1701`/`1702` back uses MSBuild's own escape hatch — `<WarningsNotAsErrors>` or per-finding `#pragma warning restore` — exactly the same shape MSBuild offers. Pinned by `BuildDiagnosticParityTests.LifebloodSelfDiagnose_NeverFiresParityClassDiagnostics` (the wall ratchet) and via the existing `INV-COMPFACT-001..003` thread-through (`noWarnIds` → `WithSpecificDiagnosticOptions(ReportDiagnostic.Suppress)` in `ModuleCompilationBuilder.CreateCompilation`).

## Symbol ID Grammar (C# Adapter)

Lifeblood symbol IDs are stable identifiers produced by `Lifeblood.Adapters.CSharp.Internal.SymbolIds`
and consumed by every read/write tool. The C# adapter format:

```
mod:AssemblyName
file:Relative/Forward/Slashed/Path.cs
ns:Fully.Qualified.Namespace
type:Fully.Qualified.TypeName
method:Fully.Qualified.TypeName.MethodName(Param1Type,Param2Type)
field:Fully.Qualified.TypeName.FieldName
property:Fully.Qualified.TypeName.PropertyName
property:Fully.Qualified.TypeName.this[Param1Type,Param2Type]   // indexer
```

**Method parameter signature is FULLY-QUALIFIED.** A method that takes one `MyApp.Item` parameter
appears as `method:MyApp.Service.Process(MyApp.Item)`. Never `method:MyApp.Service.Process(Item)`.

The exact format is pinned by `Internal.CanonicalSymbolFormat.ParamType`. Every method-ID builder
in the adapter routes through `CanonicalSymbolFormat.BuildParamSignature` so source and metadata
symbols ALWAYS produce the same ID. Do not call `ITypeSymbol.ToDisplayString()` from a method-ID
builder. Always use the canonical formatter.

**Constructors:** name part is `.ctor`. Example: `method:MyApp.Service..ctor(string,int)`.

**Operators / conversion operators:** name part is the Roslyn operator name (e.g. `op_Addition`,
`op_Implicit`).

**Special types:** C# aliases (`int`, `string`, `bool`, `void`, …) are used instead of their
`System.*` qualified names. The canonical format has `UseSpecialTypes` enabled.

**Nullability:** never reflected in IDs. `string?` and `string` share the same parameter signature
because they refer to the same underlying method symbol.

## Symbol Extraction Coverage

- **INV-EXTRACT-METHOD-ORIGINAL-DEFINITION-001. Graph edges target the source-declared `OriginalDefinition` of any `IMethodSymbol`, never an instantiated constructed-generic form.** Pre-fix, `RoslynEdgeExtractor.GetMethodId` built the canonical id directly off whichever `IMethodSymbol` the caller passed in; for a call to a generic method via type-inferred arguments (e.g. `Helper.Pick(items, 5)` binding to `Pick<string>(string[], int)`), the resulting target id used the constructed parameter signature (`(string[],int)`) instead of the source-declared open-generic form (`(T[],int)`). The graph-side mismatch silently dropped `dependants()` / `dead_code` / `blast_radius` results for every generic method consumed via type inference. Empirical class (LB-INBOX-010 part 2): `ToolHandler.ApplyCap` was called 5× yet showed `directDependants=0`. Fix: `GetMethodId` routes the parameter through `(IMethodSymbol)method.OriginalDefinition` at entry so accessor/property/event lookup AND parameter-signature construction all see the canonical open-generic form; `ExtractCallEdge` is refactored to use `GetMethodId(target)` directly instead of an inline `BuildParamSignature(target)` that bypassed the discipline. Mirrors the same `OriginalDefinition` routing `RoslynCompilationHost.FindReferences` already applies on the consumer side (line ~480) so producer-side and consumer-side ids are byte-identical for the same logical method. Pinned by `ExtractEdges_GenericMethodCall_AttributesToOriginalDefinitionId` (asserts target id is the open-generic form `method:App.Helper.Pick(T[],int)` AND the constructed form never reaches the graph).

- **INV-EXTRACT-METHOD-GROUP-CANDIDATE-001. Method-group references that Roslyn surfaces through `SymbolInfo.CandidateSymbols` emit graph edges to the first candidate's source-declared definition.** Pre-fix, `RoslynEdgeExtractor.ExtractReferenceEdge` early-returned whenever `SymbolInfo.Symbol == null` — Roslyn binds method-group identifiers via `CandidateSymbols` until the outer type-inference context narrows the choice, so target-typed `new(MethodGroup)`, delegate-ctor arguments, and `Task.Run(MyMethod)`-style call-sites silently produced zero `Calls` edges. The empirical class (LB-INBOX-010): `BclReferenceLoader.References = new(Load)` and `RoslynCodeExecutor._cache = new(LoadHostBclReferences)` in Lifeblood self showed `find_references` hits but `dependants=0` on the target methods. The single enforcement seam is `RoslynEdgeExtractor.ResolveCandidateMethodGroup` — called when `SymbolInfo.Symbol == null`, accepts `CandidateReason.MemberGroup` and `CandidateReason.OverloadResolutionFailure` (the two shapes Roslyn surfaces while target-type inference is incomplete), returns the first candidate; downstream extraction routes the symbol through the same paths as fully-bound symbols. For an overloaded method-group where target-type inference truly cannot pick a winner, attribution to the first candidate is approximate — but the only honest alternative is dropping the edge entirely, which is exactly the pre-fix behavior the empirical false-positive class proves is worse for downstream tooling (`dead_code` / `port_health` / `blast_radius` all consume these edges). Pinned by `ExtractEdges_StaticFieldInitializerMethodGroup_TargetTypedNew_AttributedToCctor` (the converted LB-INBOX-010 regression pin) and `ExtractEdges_TargetTypedNewMethodGroup_OverloadedMethod_PicksFirstCandidate` (overload disambiguation contract).

- **INV-EXTRACT-ENUMMEMBER-001. Every enum member is a first-class graph symbol.** `RoslynSymbolExtractor.ExtractEnum` walks `EnumDeclarationSyntax.Members` and emits one `Symbol` per member with `Kind = Field`, `Id = SymbolIds.Field(enumFqn, memberName)`, `ParentId = type:enumFqn`, `IsStatic = true`, `Properties[fieldKind] = "enumMember"`, `Properties[fieldType] = enumFqn`, `Properties[constantValue] = "<int-or-flags-bitfield>"`. Pre-fix, only the enum type entered the graph and three failure modes followed: (1) exact-ID lookups like `field:NS.Color.Red` missed, triggering resolver Rule 4 cross-type substitution (see `INV-RESOLVER-007`); (2) References edges to enum members were dropped by `GraphBuilder`'s dangling-edge filter, so `find_references`/`dependants` returned 0 hits for valid usages; (3) dead-code analysis could never observe enum-member usage. `RoslynEdgeExtractor`'s existing `IFieldSymbol` arm already emits the correct `field:` ID — the symbols just have to exist. Pinned by `RoslynExtractorTests.ExtractSymbols_EnumMembers_*` (six cases: emitted-as-Field, ID round-trip, implicit autoincrement constant value, explicit values, `[Flags]` bitfield, nested enum, xmldoc summary).

- **INV-EDGE-CALLSITE-001. Every expression-derived edge carries authoring provenance for its FIRST observed occurrence.** `RoslynEdgeExtractor.BuildCallSite` emits a `Lifeblood.Domain.Graph.CallSite` (`FilePath` / `Line` / `Column` / `EndLine` / `EndColumn` / `ContainingSymbolId`) for every edge sourced from a Roslyn `SyntaxNode` — invocation operations, member-access expressions, field references, property accesses, etc. `Edge.CallSite` is null for graph-derived edges with no single authoring location (module→module `DependsOn`, type→type `Inherits` without a surfaced clause node, type-level edges synthesised at graph-construction time). **First-occurrence semantics (load-bearing, interacts with `INV-STREAM-005`)**: the extractor dedups by `(sourceId, targetId, kind)` before emit, so multiple authoring expressions from the same source method to the same target of the same kind (e.g. two `obj.Method()` calls on different lines, or a typeof + new on the same target type within one body) collapse to ONE edge — the `CallSite` reflects the FIRST extracted occurrence, not every occurrence. Changing this would invert a long-standing graph invariant that downstream analysis (blast radius, cycle detection, dead-code) depends on; tools wanting per-occurrence data should query the source range via `lifeblood_compile_check` or `lifeblood_find_references` instead. The JSON graph importer / exporter round-trip the field so a cached graph preserves provenance across save / load. Downstream tools (`lifeblood_dependencies` / `lifeblood_dependants` wire surface, future tools) lift this directly without re-walking files — one call answers "where in source does X first depend on Y?". Pinned by `EdgeCallSiteTests.Extract_CallEdge_AttachesCallSiteWithFileLineColumn`, `Extract_FieldReference_AttachesCallSite`, `JsonGraph_RoundTripsCallSite`. Closes the field-report 2026-05-11 P1 CallSite ask.

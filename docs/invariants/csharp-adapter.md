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

## Reference Set Normalization

A workspace's reference set frequently carries multiple `MetadataReference`
entries that share an assembly simple-name but disagree on version (BCL ref
pack 8.0.x.x vs NuGet contract assembly 4.x.x.x is the canonical collision;
xunit / Microsoft.NET.Test.Sdk closures hit it deterministically). MSBuild
silently unifies these through its `<AutoUnify>true</AutoUnify>` default
before the compiler ever sees them. Roslyn does not.

- **INV-DIAGNOSTIC-NUGET-BINDING-PARITY-001. Reference sets handed to `CSharpCompilation.Create` are unified by assembly simple-name, highest version wins.** Mirrors MSBuild's `<AutoUnify>true</AutoUnify>` default so a Lifeblood compilation sees the same resolved reference graph an MSBuild invocation would. Without unification, Roslyn emits CS1701 / CS1702 / CS1705 once per consuming type-ref whenever the raw reference set carries multiple identities for the same simple-name — empirically 7,537 spurious findings on Lifeblood's own test assembly while `dotnet build` was clean on the same compilation. The single enforcement seam is `Internal.MetadataReferenceDeduplicator.Deduplicate`, called by `Internal.ModuleCompilationBuilder.CreateCompilation` after the per-module reference list is assembled (BCL + NuGet + external DLLs + downgraded dependency PE refs) and immediately before `CSharpCompilation.Create`. Identity is read off `PortableExecutableReference.GetMetadata()` as `AssemblyMetadata.GetAssembly().Identity`; references whose metadata cannot be parsed as an assembly (modules, native DLLs that escaped the loader filter, in-memory refs without an emitted identity) pass through unchanged so the loader can still surface the underlying load failure as a regular diagnostic. NEVER suppress CS1701 / CS1702 / CS1705 via `<NoWarn>` as a substitute for unification — the spec-correct fix removes the duplicate identity, not the symptom. Pinned by `MetadataReferenceDeduplicationTests` (highest-version wins, order-independent, distinct simple-names all survive, empty input, unreadable identity passes through, end-to-end zero CS1701 on a synthesized duplicate-identity compilation).

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

- **INV-EXTRACT-ENUMMEMBER-001. Every enum member is a first-class graph symbol.** `RoslynSymbolExtractor.ExtractEnum` walks `EnumDeclarationSyntax.Members` and emits one `Symbol` per member with `Kind = Field`, `Id = SymbolIds.Field(enumFqn, memberName)`, `ParentId = type:enumFqn`, `IsStatic = true`, `Properties[fieldKind] = "enumMember"`, `Properties[fieldType] = enumFqn`, `Properties[constantValue] = "<int-or-flags-bitfield>"`. Pre-fix, only the enum type entered the graph and three failure modes followed: (1) exact-ID lookups like `field:NS.Color.Red` missed, triggering resolver Rule 4 cross-type substitution (see `INV-RESOLVER-007`); (2) References edges to enum members were dropped by `GraphBuilder`'s dangling-edge filter, so `find_references`/`dependants` returned 0 hits for valid usages; (3) dead-code analysis could never observe enum-member usage. `RoslynEdgeExtractor`'s existing `IFieldSymbol` arm already emits the correct `field:` ID — the symbols just have to exist. Pinned by `RoslynExtractorTests.ExtractSymbols_EnumMembers_*` (six cases: emitted-as-Field, ID round-trip, implicit autoincrement constant value, explicit values, `[Flags]` bitfield, nested enum, xmldoc summary).

- **INV-EDGE-CALLSITE-001. Every expression-derived edge carries authoring provenance for its FIRST observed occurrence.** `RoslynEdgeExtractor.BuildCallSite` emits a `Lifeblood.Domain.Graph.CallSite` (`FilePath` / `Line` / `Column` / `EndLine` / `EndColumn` / `ContainingSymbolId`) for every edge sourced from a Roslyn `SyntaxNode` — invocation operations, member-access expressions, field references, property accesses, etc. `Edge.CallSite` is null for graph-derived edges with no single authoring location (module→module `DependsOn`, type→type `Inherits` without a surfaced clause node, type-level edges synthesised at graph-construction time). **First-occurrence semantics (load-bearing, interacts with `INV-STREAM-005`)**: the extractor dedups by `(sourceId, targetId, kind)` before emit, so multiple authoring expressions from the same source method to the same target of the same kind (e.g. two `obj.Method()` calls on different lines, or a typeof + new on the same target type within one body) collapse to ONE edge — the `CallSite` reflects the FIRST extracted occurrence, not every occurrence. Changing this would invert a long-standing graph invariant that downstream analysis (blast radius, cycle detection, dead-code) depends on; tools wanting per-occurrence data should query the source range via `lifeblood_compile_check` or `lifeblood_find_references` instead. The JSON graph importer / exporter round-trip the field so a cached graph preserves provenance across save / load. Downstream tools (`lifeblood_dependencies` / `lifeblood_dependants` wire surface, future tools) lift this directly without re-walking files — one call answers "where in source does X first depend on Y?". Pinned by `EdgeCallSiteTests.Extract_CallEdge_AttachesCallSiteWithFileLineColumn`, `Extract_FieldReference_AttachesCallSite`, `JsonGraph_RoundTripsCallSite`. Closes the field-report 2026-05-11 P1 CallSite ask.

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

- **INV-CANONICAL-001. Roslyn compilations receive the full transitive dependency closure, not just direct `ModuleInfo.Dependencies`.** Unlike MSBuild's ProjectReference flow, `CSharpCompilation.Create` does NOT walk references transitively. If C references B and B references A, compiling C must pass BOTH B and A as explicit `MetadataReference`s. Missing transitive A → every type from A appearing through B's public surface becomes a Roslyn error type with empty `ContainingNamespace` → `CanonicalSymbolFormat.BuildParamSignature` emits the short type name without qualifier → canonical ID drifts (`method:C.Impl.M(BarType)` instead of `method:C.Impl.M(A.BarType)`). Drift is silent: compilation succeeds, extraction runs, graph is populated; only cross-module lookups fail. The enforcement site is `Internal.ModuleCompilationBuilder.ProcessInOrder`, which routes each module's direct deps through `ComputeTransitiveDependencies` before collecting PE references. `ModuleInfo.Dependencies` keeps direct-only semantics (feeds module-to-module graph edges + topological sort, both correct on direct deps), but every compilation-reference consumer MUST use the closure helper. Pinned by `CanonicalSymbolFormatTests.ComputeTransitiveDependencies_*` (flat chain, diamond, cycle) plus the end-to-end `AnalyzeWorkspace_ThreeModuleTransitiveChain_ProducesCanonicalMethodIds`.

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
  options.** Examples already shipped: `BclOwnership` (v2), `AllowUnsafeCode`
  (v4). Future additions follow the same pattern: `LangVersion`, `Nullable`,
  `DefineConstants`, `Platform`, `WarningLevel`, etc.

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

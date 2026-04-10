# Lifeblood — AI Instruction File

## What This Project Is

Lifeblood is a hexagonal framework that pipes compiler-level semantics into AI agents. Language adapters on the left, AI connectors on the right, pure universal graph in the middle.

We do not build Roslyn-grade adapters for every language. We build the framework and the C# reference implementation. The community builds adapters for other languages.

## Architecture

```
Lifeblood.Domain                # Pure. ZERO deps. Graph model, rules, results, capabilities.
Lifeblood.Application           # Ports + use cases. Depends only on Domain.
  ├── Ports/Left/              # IWorkspaceAnalyzer, IModuleDiscovery
  ├── Ports/Right/             # IAgentContextGenerator, IMcpGraphProvider, IInstructionFileGenerator
  ├── Ports/GraphIO/           # IGraphImporter, IGraphExporter
  ├── Ports/Analysis/          # IRuleProvider
  ├── Ports/Output/            # IProgressSink
  ├── Ports/Infrastructure/    # IFileSystem
  └── UseCases/                # AnalyzeWorkspace, GenerateContext

Lifeblood.Adapters.CSharp      # LEFT SIDE. Roslyn. Reference implementation.
Lifeblood.Adapters.JsonGraph    # LEFT SIDE. Universal JSON protocol.
Lifeblood.Connectors.ContextPack # RIGHT SIDE. Context pack + CLAUDE.md generator.
Lifeblood.Connectors.Mcp       # RIGHT SIDE. MCP graph provider for AI agents.
Lifeblood.Analysis              # Optional analyzers (coupling, blast radius, cycles, tiers).
Lifeblood.Server.Mcp            # MCP server host. Stdio JSON-RPC. Interactive AI sessions.
Lifeblood.CLI                   # Composition root. Wires adapters to connectors.
```

## Invariants

### Domain Purity
- **INV-DOMAIN-001**: `Lifeblood.Domain` has ZERO dependencies. Not Roslyn, not JSON, not System.IO, not anything. If a PackageReference appears, the architecture is broken.
- **INV-DOMAIN-002**: Domain never references Application, Adapters, Connectors, or CLI.

### Application Layer
- **INV-APP-001**: Application depends only on Domain.
- **INV-APP-002**: Application never references concrete adapters or connectors. Only port interfaces.

### Graph Model
- **INV-GRAPH-001**: SymbolKind enum is language-agnostic. No C#-isms, no Python-isms.
- **INV-GRAPH-002**: Language-specific metadata goes in `Symbol.Properties` dictionary.
- **INV-GRAPH-003**: Every edge carries Evidence (kind, adapter, confidence, source span).
- **INV-GRAPH-004**: Analyzers do NOT modify the graph. Results are separate objects. The graph is read-only after construction.

### Left Side (Language Adapters)
- **INV-ADAPT-001**: Every adapter declares capabilities honestly via AdapterCapability.
- **INV-ADAPT-002**: C# adapter is the reference. Most complete, best tested.
- **INV-ADAPT-003**: External adapters communicate via JSON graph schema only.
- **INV-ADAPT-004**: No adapter code leaks into Domain or Application.

### Right Side (AI Connectors)
- **INV-CONN-001**: Connectors depend on Application ports, not on adapters.
- **INV-CONN-002**: MCP connector serves the graph read-only.
- **INV-CONN-003**: Context pack generator produces AI-consumable JSON, not human prose.

### Analysis
- **INV-ANALYSIS-001**: All analyzers are stateless. Input: graph + config. Output: typed result.
- **INV-ANALYSIS-002**: No analyzer modifies the graph. Read-only.
- **INV-ANALYSIS-003**: CouplingAnalyzer counts distinct dependants, not edge count.

### Testing
- **INV-TEST-001**: Every adapter passes the same golden repo contract tests.
- **INV-TEST-002**: Every analyzer is tested against golden repos.

### Pipeline
- **INV-PIPE-001**: The pipeline is deterministic. Same input = same output.

## Dependency Rules

```
Lifeblood.CLI
  → Lifeblood.Application
  → Lifeblood.Adapters.CSharp
  → Lifeblood.Adapters.JsonGraph
  → Lifeblood.Connectors.*
  → Lifeblood.Analysis

Lifeblood.Adapters.CSharp
  → Lifeblood.Application (ports only)
  → Lifeblood.Domain
  → Microsoft.CodeAnalysis.CSharp (Roslyn)

Lifeblood.Connectors.Mcp
  → Lifeblood.Application (ports only)
  → Lifeblood.Domain

Lifeblood.Analysis
  → Lifeblood.Domain

Lifeblood.Application
  → Lifeblood.Domain

Lifeblood.Server.Mcp
  → Lifeblood.Application
  → Lifeblood.Adapters.CSharp
  → Lifeblood.Adapters.JsonGraph
  → Lifeblood.Connectors.*
  → Lifeblood.Analysis

Lifeblood.ScriptHost
  → (nothing. Isolated process. Microsoft.CodeAnalysis.CSharp.Scripting only.)

Lifeblood.Domain
  → (nothing. Pure leaf. Forever.)
```

## Port Interfaces

### Left Side (Language Adapters)
```csharp
IWorkspaceAnalyzer.AnalyzeWorkspace(projectRoot, config) → SemanticGraph
IModuleDiscovery.DiscoverModules(projectRoot) → ModuleInfo[]
ICompilationHost.GetDiagnostics / CompileCheck / FindReferences  // Roslyn-backed
ICodeExecutor.Execute(code, imports, timeoutMs) → CodeExecutionResult
IWorkspaceRefactoring.Rename(symbolId, newName) → TextEdit[] / Format(code) → string
```

### Right Side (AI Connectors)
```csharp
IAgentContextGenerator.Generate(graph, analysis) → AgentContextPack
IMcpGraphProvider.LookupSymbol / GetDependencies / GetDependants / GetBlastRadius
IInstructionFileGenerator.Generate(graph, analysis) → string
```

### Graph I/O
```csharp
IGraphImporter.Import(stream) → SemanticGraph
IGraphExporter.Export(graph, stream)
```

### Analysis
```csharp
IRuleProvider.LoadRules(path) → ArchitectureRule[]
IBlastRadiusProvider.Analyze(graph, symbolId, maxDepth) → BlastRadiusResult
```

## Identifier Resolution (`ISymbolResolver`)

Every read-side MCP tool that takes a `symbolId` parameter must route through
`Lifeblood.Application.Ports.Right.ISymbolResolver` before doing graph or
workspace lookups. The resolver is the single source of truth for "what does
this user-supplied identifier mean" — it canonicalizes truncated method IDs,
resolves bare short names, and computes the merged read model for partial types.

- **INV-RESOLVER-001 — Identifier resolution is a port.** Every read-side tool
  that takes a `symbolId` parameter (`lifeblood_lookup`, `lifeblood_dependants`,
  `lifeblood_dependencies`, `lifeblood_blast_radius`, `lifeblood_file_impact`,
  `lifeblood_find_references`, `lifeblood_find_definition`,
  `lifeblood_find_implementations`, `lifeblood_documentation`, `lifeblood_rename`)
  routes through `ISymbolResolver` before any graph or workspace lookup. NEVER
  add a read-side tool that calls `graph.GetSymbol` or `graph.GetIncomingEdgeIndexes`
  directly with the user's raw input.

- **INV-RESOLVER-002 — The resolver accepts every input format.** Resolution
  order: exact canonical match → truncated method form (`method:NS.Type.Name`
  with no parens, lenient single-overload match) → bare short name (no kind
  prefix and no namespace, looks up the short-name index). Returns
  `SymbolResolutionResult` with `Outcome`, `CanonicalId`, `Symbol`,
  `PrimaryFilePath`, `DeclarationFilePaths`, `Candidates`, and `Diagnostic`.

- **INV-RESOLVER-003 — Partial-type unification is a read model.** The graph
  stores raw symbols (one per partial declaration; last-write-wins remains the
  storage policy in `GraphBuilder.AddSymbol`). The resolver walks the type's
  incoming `EdgeKind.Contains` edges from `SymbolKind.File` symbols to discover
  every partial declaration file at read time. Schema unchanged —
  `Lifeblood.Domain.Graph.Symbol.FilePath` stays a single value. The merged
  view lives entirely on `SymbolResolutionResult.PrimaryFilePath` +
  `SymbolResolutionResult.DeclarationFilePaths`.

- **INV-RESOLVER-004 — Primary file path for partial types is deterministic.**
  Picker rules in `LifebloodSymbolResolver.ChoosePrimaryFilePath`:
  (1) filename matches the type name exactly, (2) filename starts with
  `"<TypeName>."` (shortest match wins among prefix matches), (3) lexicographic
  first as final fallback. Same input + same graph → same primary, always.

## Csproj-Driven Compilation Facts

Csproj is the source of truth for module-level compilation options. Each fact
flows through one path: discover at parse time → store on `ModuleInfo` →
consume at compilation time. The pattern is the contract — every future
csproj-driven option follows the same shape.

- **INV-COMPFACT-001 — Csproj is authoritative for module-level compilation
  options.** Examples already shipped: `BclOwnership` (v2), `AllowUnsafeCode`
  (v4). Future additions follow the same pattern: `LangVersion`, `Nullable`,
  `DefineConstants`, `Platform`, `WarningLevel`, etc.

- **INV-COMPFACT-002 — Each compilation fact lives as a typed field on
  `ModuleInfo`.** Default value preserves pre-fix behavior. Set during
  `RoslynModuleDiscovery.ParseProject`. Consumed exactly once during
  `Internal.ModuleCompilationBuilder.CreateCompilation`. NEVER re-derive from
  the csproj at the compilation layer; NEVER sniff filenames as a substitute
  for declared options.

- **INV-COMPFACT-003 — Csproj edits invalidate cached module facts.** The
  `AnalysisSnapshot.CsprojTimestamps` tracking added in v2 (INV-BCL-005)
  covers every compilation fact for free — `IncrementalAnalyze` rebuilds the
  entire `ModuleInfo` on csproj edit, not just one field. The next compilation
  fact added under this convention ships with zero new incremental work.

## Adapter Semantic View

Each language adapter publishes a typed read-only accessor for its loaded
semantic state. Tools that need read access (script host today; debuggers,
visualizers, custom linters tomorrow) consume the view by reference, never
threading raw fields through individual constructors.

- **INV-VIEW-001 — Each language adapter publishes a typed semantic view.**
  The C# adapter publishes `Lifeblood.Adapters.CSharp.RoslynSemanticView`
  (`Compilations`, `Graph`, `ModuleDependencies`). Future language adapters
  publish their own view type using their language's semantic model.

- **INV-VIEW-002 — Tools consume the view, not raw fields.** The script host
  (`RoslynCodeExecutor`) takes a `RoslynSemanticView` in its constructor.
  Future consumers (debuggers, visualizers, linters, REPLs) take the same
  view. NEVER thread raw `Compilations` / `Graph` / `ModuleDependencies`
  through individual tool constructors.

- **INV-VIEW-003 — The view is constructed once per workspace load and
  shared by reference.** `GraphSession.Load` (full and incremental paths)
  builds the view once with three field assignments and passes it by
  reference to consumers. The view never holds locks, never caches anything
  beyond its three references, never mutates state — it is purely a typed
  handle. Sharing avoids accidental divergence between consumers' views of
  the same workspace.

## BCL Ownership (C# Adapter)

Some csprojs ship their own base class library via `<Reference Include="netstandard|mscorlib|System.Runtime">` (Unity ships .NET Standard 2.1; .NET Framework / Mono ship mscorlib). Plain SDK-style csprojs (`<Project Sdk="Microsoft.NET.Sdk">`) don't — they rely on the host runtime BCL.

Lifeblood treats this as a discovered module fact: `Lifeblood.Application.Ports.Left.ModuleInfo.BclOwnership` is `BclOwnershipMode.HostProvided` or `BclOwnershipMode.ModuleProvided`. The decision is computed ONCE during `RoslynModuleDiscovery.ParseProject` and consumed by `ModuleCompilationBuilder.CreateCompilation`. Detection logic lives in exactly one place — `RoslynModuleDiscovery.ReferenceDeclaresBcl` plus its `ParseAssemblyIdentitySimpleName` and `IsBclSimpleName` helpers. The compilation builder reads the field and never re-derives.

- **INV-BCL-001 — Single BCL per compilation.** Two BCLs causes CS0433 (ambiguous type) and CS0518 (predefined type missing) on every System type. The semantic model becomes unusable: `GetSymbolInfo` returns null at every call site, so `find_references`, `dependants`, and call-graph extraction silently produce zero results. Type-only references survive because their resolution is partially syntactic — methods do not.
- **INV-BCL-002 — Module owns its BCL when its csproj declares one.** A `<Reference>` element whose Include value (parsed as assembly identity, handles both `Include="netstandard"` and `Include="netstandard, Version=2.1.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51"`) or HintPath basename matches `netstandard`, `mscorlib`, or `System.Runtime` is the authoritative declaration. Such modules MUST NOT also receive the host BCL bundle.
- **INV-BCL-003 — Host BCL is the fallback for plain SDK-style projects.** Modules with no BCL-naming `<Reference>` element receive `BclReferenceLoader.References.Value` so System types resolve.
- **INV-BCL-004 — BCL ownership is decided at discovery time, single source of truth.** Detection logic lives in `RoslynModuleDiscovery`. `ModuleCompilationBuilder` reads the typed field — no filename sniffing, no re-derivation, no detection logic at the compilation layer.
- **INV-BCL-005 — Incremental re-analyze respects csproj edits.** `AnalysisSnapshot.CsprojTimestamps` tracks csproj timestamps. When a csproj timestamp changes, the affected module is re-discovered and recompiled even if no .cs file changed. Without this, a csproj edit that adds or removes a BCL reference produces a stale `BclOwnership` value forever and the double-BCL bug returns under incremental mode.

The full plan with empirical evidence, rollout history, and the regression test matrix lives at `.claude/plans/bcl-ownership-fix.md`.

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
appears as `method:MyApp.Service.Process(MyApp.Item)` — never `method:MyApp.Service.Process(Item)`.

The exact format is pinned by `Internal.CanonicalSymbolFormat.ParamType`. Every method-ID builder
in the adapter routes through `CanonicalSymbolFormat.BuildParamSignature` so source and metadata
symbols ALWAYS produce the same ID. Do not call `ITypeSymbol.ToDisplayString()` from a method-ID
builder — always use the canonical formatter.

**Constructors:** name part is `.ctor`. Example: `method:MyApp.Service..ctor(string,int)`.

**Operators / conversion operators:** name part is the Roslyn operator name (e.g. `op_Addition`,
`op_Implicit`).

**Special types:** C# aliases (`int`, `string`, `bool`, `void`, …) are used instead of their
`System.*` qualified names. The canonical format has `UseSpecialTypes` enabled.

**Nullability:** never reflected in IDs — `string?` and `string` share the same parameter signature
because they refer to the same underlying method symbol.

## Serialization Naming

JSON schemas use **camelCase**. C# models use **PascalCase**. The mapping is mechanical:

| C# Property | JSON Field | Notes |
|-------------|-----------|-------|
| `SourceId` | `sourceId` | Edge reference |
| `TargetId` | `targetId` | Edge reference |
| `QualifiedName` | `qualifiedName` | Symbol FQN |
| `ParentId` | `parentId` | Containment hierarchy |
| `FilePath` | `filePath` | Source location |
| `CanDiscoverSymbols` | `discoverSymbols` | Capability: "Can" prefix dropped |
| `MustNotReference` | `mustNotReference` | Rule constraint |
| `MayOnlyReference` | `mayOnlyReference` | Rule constraint |

Rule: JSON serializers should use `System.Text.Json` with `JsonNamingPolicy.CamelCase`. Capability fields have manual names (documented in `schemas/graph.schema.json`).

## Naming Conventions

| Pattern | Usage |
|---------|-------|
| `*Analyzer` | Stateless analysis pass |
| `*Classifier` | Categorizes nodes |
| `*Detector` | Finds patterns |
| `*Validator` | Checks rules |
| `*Builder` | Constructs complex objects |
| `*Generator` | Produces output artifacts |
| `*Provider` | Supplies data (read-oriented) |
| `*Importer/*Exporter` | Serialization ports |
| `I*` | Port interface |

## Rules for Adding Features

1. **New graph concept** → Domain. Check INV-GRAPH-001 first. Language-specific = Properties.
2. **New analysis** → Lifeblood.Analysis. Stateless. Graph in, result out.
3. **New language adapter** → Lifeblood.Adapters.{Language}/ or external JSON.
4. **New AI connector** → Lifeblood.Connectors.{Name}/
5. **New CLI command** → Lifeblood.CLI/
6. **New use case** → Lifeblood.Application/UseCases/

## Streaming Compilation Architecture (v0.3.0)

- **INV-STREAM-001**: `ModuleCompilationBuilder.ProcessInOrder` compiles one module at a time in topological order. After extraction, `Emit()` → `MetadataReference.CreateFromImage()` downgrades the full compilation (~200MB) to a lightweight PE reference (~10-100KB). Peak memory: O(1 compilation), not O(N).
- **INV-STREAM-002**: `SharedMetadataReferenceCache` deduplicates NuGet MetadataReferences across modules. One instance per `AnalyzeWorkspace` call.
- **INV-STREAM-003**: `AnalysisConfig.RetainCompilations` controls mode. `false` (default, CLI) = streaming/memory-safe. `true` (MCP server) = retained for write-side tools.
- **INV-STREAM-004**: Unity csproj support: if `<Compile Include>` items exist (old-format), use them. If absent (SDK-style), scan filesystem.
- **INV-STREAM-005**: `GraphBuilder.Build()` deduplicates ALL edges by `(sourceId, targetId, kind)`. Partial classes emit duplicate edges — the builder is the authoritative dedup boundary.
- **INV-FILE-EDGE-001**: `GraphBuilder.Build()` derives file-level `References` edges from symbol-level edges. For each non-Contains edge between symbols in different files, a `file:X → file:Y References` edge is emitted with an `edgeCount` property. Evidence: `Inferred`, adapter: `GraphBuilder`. File edges are derived truth — not primary.
- **INV-INCR-001**: Incremental re-analyze (`lifeblood_analyze` with `incremental: true`) only recompiles modules whose files changed since the last analysis. Per-file extraction results are cached in `AnalysisSnapshot`. Changed files are detected via filesystem timestamps. Module additions/removals fall back to full re-analyze. v1 limitation: does not cascade to dependent modules when API surface changes.

## 17 MCP Tools

Read-side (7): analyze, context, lookup, dependencies, dependants, blast_radius, file_impact
Write-side (10): execute, diagnose, compile_check, find_references, find_definition, find_implementations, symbol_at_position, documentation, rename, format

## What NOT to Do

- Do not put language-specific logic in Domain or Application. Ever.
- Do not make analyzers stateful or mutable.
- Do not let adapters reference other adapters.
- Do not let connectors reference adapters.
- Do not hardcode file extensions or syntax patterns in Domain.
- Do not require adapters to be written in C#. JSON is the universal protocol.
- Do not add "AI features" to the graph model. The graph is pure data. AI consumption happens in connectors.

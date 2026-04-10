# Architecture

Hexagonal framework. Two sides. Pure core.

```
LEFT SIDE                     CORE                     RIGHT SIDE
(Language Adapters)        (The Pipe)               (AI Connectors)

Roslyn (C#)       ──┐                            ┌──  AgentContextGenerator
JSON graph        ──┼→  Domain  →  Application  →┤──  InstructionFileGenerator
                  ──┘       ↑                     ├──  LifebloodMcpProvider
                      Analysis (optional)         └──  CLI / CI
```

## Three Layers of Truth

1. **Syntax truth.** What the parser proves from source text. Every adapter can provide this.
2. **Semantic truth.** What the compiler resolves. Type identity, overload resolution, call targets. Only compiler-grade adapters like Roslyn provide this at Proven confidence.
3. **Derived truth.** What Lifeblood computes from the graph. Coupling, blast radius, boundary violations, tier classification, cycle detection.

These layers stay separate. Every edge carries Evidence saying which layer produced it and how confident the adapter is.

## Dependency Flow

```
CLI (composition root)
  → Application → Domain (pure leaf, zero deps)
  → Adapters.CSharp → Application
  → Adapters.JsonGraph → Application
  → Connectors.ContextPack → Application
  → Connectors.Mcp → Application (blast radius via IBlastRadiusProvider port)
  → Analysis → Domain
```

Adapters and Connectors depend inward on Application ports. They never reference each other. Domain is always the leaf with zero dependencies.

## Assemblies

| Assembly | Role | Dependencies |
|----------|------|-------------|
| **Lifeblood.Domain** | Graph model, Evidence, ConfidenceLevel, GraphBuilder, GraphValidator, rules, results | None |
| **Lifeblood.Application** | 15 port interfaces including ISymbolResolver. AnalyzeWorkspaceUseCase, GenerateContextUseCase | Domain |
| **Lifeblood.Adapters.CSharp** | Roslyn reference adapter. Workspace analyzer, module discovery, symbol/edge extraction, RoslynSemanticView (typed read-only adapter view), CanonicalSymbolFormat (single source of truth for parameter-type display) | Application, Roslyn |
| **Lifeblood.Adapters.JsonGraph** | JSON import/export with round-trip fidelity | Application |
| **Lifeblood.Connectors.ContextPack** | AgentContextGenerator, InstructionFileGenerator, ReadingOrderGenerator | Application |
| **Lifeblood.Connectors.Mcp** | LifebloodMcpProvider (lookup, deps, dependants, blast radius, file impact) | Application |
| **Lifeblood.Analysis** | CouplingAnalyzer, BlastRadiusAnalyzer, CircularDependencyDetector, TierClassifier, RuleValidator | Domain |
| **Lifeblood.Server.Mcp** | MCP server host. Stdio JSON-RPC. 18 tools (8 read + 10 write). Bidirectional Roslyn. | Application, Adapters.CSharp, Connectors |
| **Lifeblood.ScriptHost** | Process-isolated code execution harness. Separate process, no shared memory. | Roslyn Scripting only |
| **Lifeblood.CLI** | Composition root: AnalysisPipeline, RulesLoader, thin dispatch | Everything |

## Domain Model

The domain is pure. No Roslyn, no JSON, no System.IO.

- **Symbol.** A node: module, file, namespace, type, method, field, property (including events via `isEvent` and indexers via `isIndexer`), parameter.
- **Edge.** A directed relationship: contains, dependsOn, implements, inherits, calls, references, overrides.
- **Evidence.** Provenance: syntax, semantic, or inferred. Plus the adapter name and a `ConfidenceLevel`.
- **GraphBuilder.** Constructs graphs, synthesizes Contains edges from ParentId, deduplicates symbols, derives file-level edges from symbol-level edges (INV-FILE-EDGE-001), sorts output deterministically.
- **GraphValidator.** Rejects malformed graphs: dangling edges, duplicates, orphaned parents, self-references.
- **SemanticGraph.** The immutable graph with thread-safe lazy indexes.
- **AnalysisSnapshot** (adapter-level). Caches per-file extraction results for incremental re-analyze.

Properties are `IReadOnlyDictionary` on the public surface. The graph is read-only after construction.

## Port Interfaces

### Left Side (Language Adapters)
- `IWorkspaceAnalyzer`. Primary entry point. Takes `projectRoot` plus config and returns a `SemanticGraph`.
- `IModuleDiscovery`. Module and project discovery, returns `ModuleInfo[]`. Each `ModuleInfo` carries `BclOwnership` and `AllowUnsafeCode` parsed from csproj.
- `ICompilationHost`. Diagnostics, compile-checking, reference finding (Roslyn-backed). `FindReferences` has an explicit `IncludeDeclarations` option for partial-type declaration sites.
- `ICodeExecutor`. Executes code snippets against the loaded workspace.
- `IWorkspaceRefactoring`. Rename (returns edits, does NOT apply), and format.
- `IGraphImporter`. Reads a stream into a `SemanticGraph` via the JSON protocol.
- `IGraphExporter`. Writes a `SemanticGraph` to a stream.

### Right Side (AI Connectors)
- `IAgentContextGenerator`. Builds an `AgentContextPack` from a graph and analysis result.
- `IInstructionFileGenerator`. Builds a markdown instruction file from a graph and analysis result.
- `IMcpGraphProvider`. Exposes `LookupSymbol`, `GetDependencies`, `GetDependants`, `GetBlastRadius`, `GetFileImpact`.
- `ISymbolResolver`. Resolves a bare short name, a truncated method id, or a canonical id into a `SymbolResolutionResult` carrying `Outcome`, `Candidates`, `Diagnostic`, and `DeclarationFilePaths`. Every read-side MCP handler routes through this resolver before any graph or workspace lookup. Partial-type unification is computed as a read model on the resolution result, not a graph schema change.

## Capability-Aware

Not all adapters are equal. Roslyn gives Proven type resolution. A tree-sitter adapter might give BestEffort. Every adapter declares what it can do via `AdapterCapability`. Analysis results carry the confidence level. No fake authority.

Current Roslyn adapter capabilities:
- CanDiscoverSymbols: true
- TypeResolution: Proven
- CallResolution: Proven
- ImplementationResolution: Proven
- CrossModuleReferences: Proven. The edge extractor tracks metadata symbols from known workspace modules via `KnownModuleAssemblies`. Cycles are still broken by skipping.
- OverrideResolution: Proven (virtual dispatch chain via IMethodSymbol.OverriddenMethod)

## File-Level Edge Derivation

GraphBuilder.Build() derives `file:X → file:Y References` edges from symbol-level edges. For each non-Contains edge between symbols in different files, a file-level edge is emitted with an `edgeCount` property. These edges are derived truth (Evidence: Inferred, adapter: GraphBuilder). This is what answers `lifeblood_file_impact`: "if I change this file, what other files break?"

## Incremental Re-Analyze

After a full analysis, subsequent calls with `incremental: true` only recompile modules whose source files changed. The `AnalysisSnapshot` caches per-file extraction results (symbols, edges, timestamps). Changed files are detected via filesystem timestamps and surgically replaced. Module additions/removals fall back to full re-analyze.

v1 limitation: does not cascade to dependent modules when an API surface changes.

## Unity Bridge

The Unity bridge lives at `unity/Editor/LifebloodBridge/`. It runs Lifeblood as a sidecar MCP server (separate .NET process), communicating via JSON-RPC 2.0 over stdin/stdout. Unity projects create a directory junction to this path. The bridge auto-discovers via `[McpForUnityTool]` attributes and exposes all 18 tools to Unity MCP.

```
Unity Editor ──→ Unity MCP (scenes, GameObjects, assets)
                     │
                     └── [McpForUnityTool] ──→ Lifeblood MCP (child process)
                         └── 18 semantic tools over JSON-RPC
```

## Deterministic Output

GraphBuilder sorts symbols by ID and edges by source+target+kind before producing the graph. File discovery is sorted. Same input always produces the same output (INV-PIPE-001).

## Three Architectural Seams (post-BCL framing)

After the BCL ownership fix (v2) closed the silent zero-result class on Unity, .NET Framework, and Mono workspaces, five remaining reviewer findings were collapsed into three architectural seams instead of five piecemeal patches. Each seam is a contract, not a hot-fix.

**Seam 1. `ISymbolResolver` (Application port).** Identifier resolution is a port. Every read-side handler routes through one resolver before any graph or workspace lookup. Resolution order is strict: exact canonical match, then truncated method form (single-overload lenient), then bare short name. Partial-type unification is a read model computed on the resolution result by walking existing `file:X Contains type:Y` edges. The graph stays raw, and `Lifeblood.Domain.Graph.Symbol` is unchanged. The deterministic primary file path picker (filename match, then prefix match, then lexicographic) lives in `LifebloodSymbolResolver.ChoosePrimaryFilePath`. Invariants: `INV-RESOLVER-001..004` in `CLAUDE.md`.

**Seam 2. Csproj-driven compilation facts as a documented convention.** Each module-level compilation option that the host needs to honor lives as a typed field on `ModuleInfo`, parsed once during `RoslynModuleDiscovery.ParseProject`, consumed in `ModuleCompilationBuilder.CreateCompilation`. Today the typed fields are `BclOwnership` (HostProvided or ModuleProvided) and `AllowUnsafeCode`. Csproj-edit invalidation flows for free through `AnalysisSnapshot.CsprojTimestamps`. Re-discovery rebuilds the entire `ModuleInfo`, not just one field. Invariants: `INV-COMPFACT-001..003`.

**Seam 3. `RoslynSemanticView` (typed adapter view).** Each language adapter publishes a typed read-only view of its loaded semantic state. The C# adapter's `RoslynSemanticView` exposes `Compilations`, `Graph`, and `ModuleDependencies`. It is constructed once per `GraphSession.Load`, and is shared by reference across consumers. `RoslynCodeExecutor` consumes the view as the script-host globals object. `lifeblood_execute` scripts read `Graph`, `Compilations`, and `ModuleDependencies` as bare top-level identifiers. Invariants: `INV-VIEW-001..003`.

The seam framing is the durable architectural contract. Future bug reports that look like "different surfaces broken in the same way" should first check whether one of these seams is the right place to fix it, before adding a per-surface patch.

## Invariant Enforcement

Architecture rules are not just documented. They are tested:
- `ArchitectureInvariantTests` verifies dependency direction on every build
- 11 frozen ADRs in `docs/ARCHITECTURE_DECISIONS.md`
- GraphValidator runs on every graph before analysis
- Rule packs (hexagonal, clean-architecture, lifeblood) validate boundaries
- 9 invariants for the three seams (`INV-RESOLVER-001..004`, `INV-COMPFACT-001..003`, `INV-VIEW-001..003`). See `CLAUDE.md`.
- 5 invariants for BCL ownership (`INV-BCL-001..005`). `BclOwnership` is decided ONCE during csproj parsing and is never re-detected at compilation time.

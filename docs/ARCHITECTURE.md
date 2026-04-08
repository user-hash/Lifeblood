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
| **Lifeblood.Application** | Port interfaces, AnalyzeWorkspaceUseCase, GenerateContextUseCase | Domain |
| **Lifeblood.Adapters.CSharp** | Roslyn reference adapter: workspace analyzer, module discovery, symbol/edge extraction | Application, Roslyn |
| **Lifeblood.Adapters.JsonGraph** | JSON import/export with round-trip fidelity | Application |
| **Lifeblood.Connectors.ContextPack** | AgentContextGenerator, InstructionFileGenerator, ReadingOrderGenerator | Application |
| **Lifeblood.Connectors.Mcp** | LifebloodMcpProvider (lookup, deps, dependants, blast radius, file impact) | Application |
| **Lifeblood.Analysis** | CouplingAnalyzer, BlastRadiusAnalyzer, CircularDependencyDetector, TierClassifier, RuleValidator | Domain |
| **Lifeblood.Server.Mcp** | MCP server host. Stdio JSON-RPC. 17 tools (7 read + 10 write). Bidirectional Roslyn. | Application, Adapters.CSharp, Connectors |
| **Lifeblood.ScriptHost** | Process-isolated code execution harness. Separate process, no shared memory. | Roslyn Scripting only |
| **Lifeblood.CLI** | Composition root: AnalysisPipeline, RulesLoader, thin dispatch | Everything |

## Domain Model

The domain is pure. No Roslyn, no JSON, no System.IO.

- **Symbol** — a node: module, file, namespace, type, method, field, property (including events via `isEvent` and indexers via `isIndexer`), parameter
- **Edge** — a directed relationship: contains, dependsOn, implements, inherits, calls, references, overrides
- **Evidence** — provenance: syntax/semantic/inferred + adapter name + ConfidenceLevel
- **GraphBuilder** — constructs graphs, synthesizes Contains edges from ParentId, deduplicates symbols, derives file-level edges from symbol-level edges (INV-FILE-EDGE-001), sorts output deterministically
- **GraphValidator** — rejects malformed graphs: dangling edges, duplicates, orphaned parents, self-references
- **SemanticGraph** — the immutable graph with thread-safe lazy indexes
- **AnalysisSnapshot** (adapter-level) — caches per-file extraction results for incremental re-analyze

Properties are `IReadOnlyDictionary` on the public surface. The graph is read-only after construction.

## Port Interfaces

### Left Side (Language Adapters)
- `IWorkspaceAnalyzer` — primary: projectRoot + config → SemanticGraph
- `IModuleDiscovery` — module/project discovery → ModuleInfo[]
- `ICompilationHost` — diagnostics, compile-checking, reference finding (Roslyn-backed)
- `ICodeExecutor` — execute code snippets against loaded workspace
- `IWorkspaceRefactoring` — rename (returns edits, does NOT apply), format
- `IGraphImporter` — stream → SemanticGraph (JSON protocol)
- `IGraphExporter` — SemanticGraph → stream

### Right Side (AI Connectors)
- `IAgentContextGenerator` — graph + analysis → AgentContextPack
- `IInstructionFileGenerator` — graph + analysis → markdown string
- `IMcpGraphProvider` — LookupSymbol, GetDependencies, GetDependants, GetBlastRadius, GetFileImpact

## Capability-Aware

Not all adapters are equal. Roslyn gives Proven type resolution. A tree-sitter adapter might give BestEffort. Every adapter declares what it can do via `AdapterCapability`. Analysis results carry the confidence level. No fake authority.

Current Roslyn adapter capabilities:
- CanDiscoverSymbols: true
- TypeResolution: Proven
- CallResolution: Proven
- ImplementationResolution: Proven
- CrossModuleReferences: BestEffort (compilations built in dependency order, but cycles are broken by skipping — degraded in cyclic graphs)
- OverrideResolution: Proven (virtual dispatch chain via IMethodSymbol.OverriddenMethod)

## File-Level Edge Derivation

GraphBuilder.Build() derives `file:X → file:Y References` edges from symbol-level edges. For each non-Contains edge between symbols in different files, a file-level edge is emitted with an `edgeCount` property. These edges are derived truth (Evidence: Inferred, adapter: GraphBuilder). This enables `lifeblood_file_impact` — "if I change this file, what other files break?"

## Incremental Re-Analyze

After a full analysis, subsequent calls with `incremental: true` only recompile modules whose source files changed. The `AnalysisSnapshot` caches per-file extraction results (symbols, edges, timestamps). Changed files are detected via filesystem timestamps and surgically replaced. Module additions/removals fall back to full re-analyze.

v1 limitation: does not cascade to dependent modules when an API surface changes.

## Unity Bridge

The Unity bridge lives at `unity/Editor/LifebloodBridge/`. It runs Lifeblood as a sidecar MCP server (separate .NET process), communicating via JSON-RPC 2.0 over stdin/stdout. Unity projects create a directory junction to this path. The bridge auto-discovers via `[McpForUnityTool]` attributes and exposes all 17 tools to Unity MCP.

```
Unity Editor ──→ Unity MCP (scenes, GameObjects, assets)
                     │
                     └── [McpForUnityTool] ──→ Lifeblood MCP (child process)
                         └── 17 semantic tools over JSON-RPC
```

## Deterministic Output

GraphBuilder sorts symbols by ID and edges by source+target+kind before producing the graph. File discovery is sorted. Same input always produces the same output (INV-PIPE-001).

## Invariant Enforcement

Architecture rules are not just documented. They are tested:
- `ArchitectureInvariantTests` verifies dependency direction on every build
- 11 frozen ADRs in `docs/ARCHITECTURE_DECISIONS.md`
- GraphValidator runs on every graph before analysis
- Rule packs (hexagonal, clean-architecture, lifeblood) validate boundaries

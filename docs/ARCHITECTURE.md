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
  → Connectors.Mcp → Application + Analysis
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
| **Lifeblood.Connectors.Mcp** | LifebloodMcpProvider (lookup, deps, dependants, blast radius) | Application, Analysis |
| **Lifeblood.Analysis** | CouplingAnalyzer, BlastRadiusAnalyzer, CircularDependencyDetector, TierClassifier, RuleValidator | Domain |
| **Lifeblood.Server.Mcp** | MCP server host. Stdio JSON-RPC. 12 tools (6 read + 6 write). Bidirectional Roslyn. | Application, Adapters.CSharp, Connectors |
| **Lifeblood.CLI** | Composition root: AnalysisPipeline, RulesLoader, thin dispatch | Everything |

## Domain Model

The domain is pure. No Roslyn, no JSON, no System.IO.

- **Symbol** — a node: module, file, namespace, type, method, field, parameter
- **Edge** — a directed relationship: contains, dependsOn, implements, inherits, calls, references, overrides
- **Evidence** — provenance: syntax/semantic/inferred + adapter name + ConfidenceLevel
- **GraphBuilder** — constructs graphs, synthesizes Contains edges from ParentId, deduplicates symbols, sorts output deterministically
- **GraphValidator** — rejects malformed graphs: dangling edges, duplicates, orphaned parents, self-references
- **SemanticGraph** — the immutable graph with thread-safe lazy indexes

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
- `IMcpGraphProvider` — LookupSymbol, GetDependencies, GetDependants, GetBlastRadius

## Capability-Aware

Not all adapters are equal. Roslyn gives Proven type resolution. A tree-sitter adapter might give BestEffort. Every adapter declares what it can do via `AdapterCapability`. Analysis results carry the confidence level. No fake authority.

Current Roslyn adapter capabilities:
- CanDiscoverSymbols: true
- TypeResolution: Proven
- CallResolution: Proven
- ImplementationResolution: Proven
- CrossModuleReferences: Proven (compilations built in dependency order with CompilationReferences)
- OverrideResolution: None (not yet extracted)

## Deterministic Output

GraphBuilder sorts symbols by ID and edges by source+target+kind before producing the graph. File discovery is sorted. Same input always produces the same output (INV-PIPE-001).

## Invariant Enforcement

Architecture rules are not just documented. They are tested:
- `ArchitectureInvariantTests` verifies dependency direction on every build
- 11 frozen ADRs in `docs/ARCHITECTURE_DECISIONS.md`
- GraphValidator runs on every graph before analysis
- Rule packs (hexagonal, clean-architecture, lifeblood) validate boundaries

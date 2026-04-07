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
  → Lifeblood.Analysis (blast radius delegation)
  → Lifeblood.Domain

Lifeblood.Analysis
  → Lifeblood.Domain

Lifeblood.Application
  → Lifeblood.Domain

Lifeblood.Domain
  → (nothing. Pure leaf. Forever.)
```

## Port Interfaces

### Left Side (Language Adapters)
```csharp
IWorkspaceAnalyzer.AnalyzeWorkspace(projectRoot, config) → SemanticGraph
IModuleDiscovery.DiscoverModules(projectRoot) → ModuleInfo[]
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
// Analyzers use static methods: CouplingAnalyzer.Analyze(), BlastRadiusAnalyzer.Analyze(), etc.
IRuleProvider.LoadRules(path) → ArchitectureRule[]
```

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
| `CanExpandMacros` | `macroExpansion` | Capability: verb → noun |
| `SupportsIncremental` | `incremental` | Capability: "Supports" prefix dropped |
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

## What NOT to Do

- Do not put language-specific logic in Domain or Application. Ever.
- Do not make analyzers stateful or mutable.
- Do not let adapters reference other adapters.
- Do not let connectors reference adapters.
- Do not hardcode file extensions or syntax patterns in Domain.
- Do not require adapters to be written in C#. JSON is the universal protocol.
- Do not add "AI features" to the graph model. The graph is pure data. AI consumption happens in connectors.

# Lifeblood — AI Instruction File

## What This Project Is

Lifeblood is a language-agnostic semantic code analysis framework. It lets you see the lifeblood flowing through any codebase: what depends on what, what is alive, what is dead, what violates your architecture rules.

The core is pure. Zero language-specific dependencies. Language support comes through adapters that implement a single port interface. The C# adapter (wrapping Roslyn) is the reference implementation.

## Architecture: Hexagonal (Ports & Adapters)

```
Lifeblood.Core          # Pure. Zero language deps. ALL analysis lives here.
  ├── Graph/            # Universal semantic graph model
  ├── Analysis/         # Coupling, blast radius, dead code, tiers, hubs
  ├── Rules/            # Architecture rule validation
  └── Ports/            # IWorkspaceAnalyzer, IGraphImporter, IAgentContextGenerator,
                        # IReportSink, IGraphExporter, IFileSystem, IRuleSource

Lifeblood.Adapters.*    # Language-specific. Implements IWorkspaceAnalyzer.
  └── CSharp/           # Reference adapter wrapping Roslyn

Lifeblood.Reporters     # Output formatters (JSON, HTML, CI)

Lifeblood.CLI           # Entry point. Wires adapters to core.
```

## Invariants

### Core Purity
- **INV-CORE-001**: `Lifeblood.Core` has zero references to any language-specific library (no Roslyn, no TypeScript compiler, no ast). If it compiles with language deps, the architecture is broken.
- **INV-CORE-002**: `Lifeblood.Core` has zero references to `Lifeblood.Adapters.*`. Core never knows which language it is analyzing.
- **INV-CORE-003**: All analysis algorithms operate on `SemanticGraph` only. They never touch source code, file contents, or language syntax.

### Graph Model
- **INV-GRAPH-001**: `SymbolKind` enum is language-agnostic. No `MonoBehaviour`, no `decorator`, no `trait`. Only universal concepts: Module, File, Namespace, Type, Method, Field, Parameter.
- **INV-GRAPH-002**: Language-specific metadata goes in `Symbol.Properties` dictionary, never in new fields on Symbol.
- **INV-GRAPH-003**: Symbols are identified by `Id` (string, globally unique within a graph). Format: `{filePath}:{kind}:{qualifiedName}`.
- **INV-GRAPH-004**: Edges are directional. `Source` depends on / references / calls `Target`.

### Adapters
- **INV-ADAPTER-001**: Every adapter implements `ICodeParser` and optionally `IProjectDiscovery`. No other coupling to core.
- **INV-ADAPTER-002**: Adapters can also be external processes that output graph JSON conforming to `schemas/graph.schema.json`. The core reads JSON graphs. This enables adapters in any language.
- **INV-ADAPTER-003**: The C# adapter is the reference. It must be the most complete and best tested. Other adapters follow its patterns.

### Analysis
- **INV-ANALYSIS-001**: All analyzers are stateless. Input: `SemanticGraph` + config. Output: `AnalysisResult`. No side effects.
- **INV-ANALYSIS-002**: No analyzer modifies the graph. Analysis is read-only.
- **INV-ANALYSIS-003**: Coupling metrics follow Robert C. Martin's definitions. Fan-in = afferent coupling (Ca). Fan-out = efferent coupling (Ce). Instability = Ce / (Ca + Ce).

### Rules
- **INV-RULES-001**: Architecture rules are defined in `lifeblood.rules.json` (or `xray.json` for backward compat). JSON format, no YAML.
- **INV-RULES-002**: Rule validation produces `Violation[]` with source, target, and the exact rule broken. Machine-readable.

## Assembly / Project Dependency Rules

```
Lifeblood.CLI
  → Lifeblood.Core
  → Lifeblood.Adapters.CSharp
  → Lifeblood.Reporters

Lifeblood.Adapters.CSharp
  → Lifeblood.Core
  → Microsoft.CodeAnalysis.CSharp (Roslyn)

Lifeblood.Reporters
  → Lifeblood.Core

Lifeblood.Core
  → (nothing. Leaf dependency. Pure.)
```

## Port Interfaces

### Primary adapter port (workspace-scoped):

```csharp
IWorkspaceAnalyzer.AnalyzeWorkspace(projectRoot, config) → SemanticGraph
```

INV-PORT-001: The primary contract is workspace → graph, not file → symbols.
File-level parsing is internal to adapters.

### Input ports:
```csharp
IGraphImporter.Import(stream) → SemanticGraph     // Read JSON graph from external adapter
IRuleSource.LoadRules(path) → ArchitectureRule[]   // Where rules come from
```

### Output ports:
```csharp
IReportSink.Report(AnalysisResult, stream)         // Analysis results output
IGraphExporter.Export(SemanticGraph, stream)        // Graph serialization
IAgentContextGenerator.Generate(graph, analysis) → AgentContextPack  // THE KILLER FEATURE
```

### Infrastructure ports:
```csharp
IFileSystem.ReadAllText(path) → string             // Abstracts disk access
IFileSystem.FindFiles(dir, pattern) → string[]     // Enables in-memory testing
```

## JSON as the Universal Adapter Protocol

Any language can be an adapter. Write a parser in Python/Go/Rust/whatever that outputs JSON conforming to `schemas/graph.schema.json`. The CLI reads it. All core analysis runs on the JSON graph.

This means: you do NOT need to write C# to add a language. You write a parser in whatever language is natural, output JSON, done.

## Naming Conventions

| Pattern | Usage |
|---------|-------|
| `*Analyzer` | Stateless analysis pass (CouplingAnalyzer, BlastRadiusAnalyzer) |
| `*Classifier` | Categorizes nodes (TierClassifier) |
| `*Detector` | Finds patterns (CircularDependencyDetector, HubBridgeDetector) |
| `*Validator` | Checks rules (RuleValidator) |
| `*Builder` | Constructs complex objects (GraphBuilder) |
| `I*` | Port interface (ICodeParser, IReporter) |

## Rules for Adding Features

1. **New analysis** → `Lifeblood.Core/Analysis/`. Must be stateless. Input: graph. Output: result.
2. **New graph concept** → check INV-GRAPH-001 first. If language-specific, use Properties.
3. **New language adapter** → `Lifeblood.Adapters.{Language}/` or external JSON process.
4. **New output format** → `Lifeblood.Reporters/`.
5. **New CLI command** → `Lifeblood.CLI/`.

## What NOT to Do

- Do not put language-specific logic in Core. Ever.
- Do not make analyzers stateful or mutable.
- Do not add Roslyn references anywhere except the C# adapter.
- Do not hardcode file extensions, import keywords, or syntax patterns in Core.
- Do not require adapters to be written in C#. JSON is the universal protocol.

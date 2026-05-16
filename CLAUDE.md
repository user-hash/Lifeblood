# Lifeblood. AI Instruction File

## What This Project Is

Lifeblood is a hexagonal framework that pipes compiler-level semantics into AI agents. Language adapters on the left, AI connectors on the right, pure universal graph in the middle.

We do not build Roslyn-grade adapters for every language. We build the framework and the C# reference implementation. The community builds adapters for other languages.

## Architecture

```
Lifeblood.Domain                # Pure. ZERO deps. Graph model, rules, results, capabilities.
Lifeblood.Application           # Ports + use cases. Depends only on Domain.
  ├── Ports/Left/               # IWorkspaceAnalyzer, IModuleDiscovery, ICompilationHost, ICodeExecutor, IWorkspaceRefactoring
  ├── Ports/Right/              # IAgentContextGenerator, IMcpGraphProvider, IInstructionFileGenerator, ISymbolResolver, ISemanticSearchProvider, IDeadCodeAnalyzer, IPartialViewBuilder, Invariants/IInvariantProvider
  ├── Ports/GraphIO/            # IGraphImporter, IGraphExporter
  ├── Ports/Analysis/           # IRuleProvider, IBlastRadiusProvider
  ├── Ports/Output/             # IProgressSink
  ├── Ports/Infrastructure/     # IFileSystem, IUsageProbe, IUsageCapture
  └── UseCases/                 # AnalyzeWorkspace, GenerateContext

Lifeblood.Adapters.CSharp       # LEFT SIDE. Roslyn. Reference implementation.
Lifeblood.Adapters.JsonGraph    # LEFT SIDE. Universal JSON protocol.
Lifeblood.Connectors.ContextPack # RIGHT SIDE. Context pack + CLAUDE.md generator.
Lifeblood.Connectors.Mcp        # RIGHT SIDE. MCP graph provider for AI agents.
Lifeblood.Analysis              # Optional analyzers (coupling, blast radius, cycles, tiers).
Lifeblood.Server.Mcp            # MCP server host. Stdio JSON-RPC. Interactive AI sessions.
Lifeblood.CLI                   # Composition root. Wires adapters to connectors.
Lifeblood.ScriptHost            # Process-isolated child for `lifeblood_execute`.
```

External JSON-emitting adapters live under `adapters/` and are not .NET projects:

```
adapters/typescript             # Node.js. ts.createProgram + TypeChecker. Self-analyzing.
adapters/python                 # Standalone ast module. Zero dependencies. Self-analyzing.
adapters/native-clang           # libclang-based C extractor (beta, v0.7.7). Reads compile_commands.json, emits graph.json. Hexagonal: LLVM stays outside core.
```

External adapters emit `graph.json` conforming to `schemas/graph.schema.json`. Lifeblood ingests through `JsonGraphImporter`. See `docs/NATIVE_CLANG.md` for the C capability page and `docs/ADAPTERS.md` for the adapter-building guide.

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

## Invariants

Architectural invariants live under [`docs/invariants/`](docs/invariants/INDEX.md), one file per domain. Read the specific file before touching that area. The tree-walker behind `lifeblood_invariant_check` aggregates `<root>/CLAUDE.md` + `<root>/AGENTS.md` + every `*.md` under `docs/invariants/` so every rule is callable by id from the MCP tool.

| Domain | File |
|--------|------|
| Hexagonal core (Domain purity, App layer, Graph, Adapters, Connectors, Analysis, Testing, Pipeline, ScriptHost, composition-root allowlist) | [architecture.md](docs/invariants/architecture.md) |
| Identifier resolution (`ISymbolResolver`, partial-type unification, kind correction) | [resolver.md](docs/invariants/resolver.md) |
| C# adapter (canonical IDs, semantic view, BCL ownership, csproj facts, symbol-ID grammar) | [csharp-adapter.md](docs/invariants/csharp-adapter.md) |
| Pipeline (streaming compilation, file-edge derivation, incremental analyze) | [pipeline.md](docs/invariants/pipeline.md) |
| Usage reporting (timings, memory, GC, phases) | [usage.md](docs/invariants/usage.md) |
| MCP protocol (wire format, tool registry, response envelope) | [mcp-protocol.md](docs/invariants/mcp-protocol.md) |
| Tool semantics (dead-code, invariant introspection, authority report, forwarder classifier, execute robustness, Unity reachability, find-implementations) | [tools.md](docs/invariants/tools.md) |
| Governance (STATUS counts, CHANGELOG link refs, test-pattern rules) | [governance.md](docs/invariants/governance.md) |

Authoring shapes recognised by the parser (any of these works for a new invariant):
```
Shape A:  - **INV-DOMAIN-001**: body...
Shape B:  - **INV-CANONICAL-001. Title sentence.** Body paragraph...
Shape C:  **INV-WORK-001: Title.** body...                    (no bullet)
Shape D:  - **INV-DSP-012** (v1.1.566): body...               (version tag)
Shape E:  - **INV-ANIM-1:** body...                            (colon inside bold)
```

## Port Interfaces

All ports live under `src/Lifeblood.Application/Ports/`. The directory layout is the contract: `Ports/Left/` (language adapters: `IWorkspaceAnalyzer`, `IModuleDiscovery`, `ICompilationHost`, `ICodeExecutor`, `IWorkspaceRefactoring`), `Ports/Right/` (AI connectors: `IAgentContextGenerator`, `IMcpGraphProvider`, `IInstructionFileGenerator`, `ISymbolResolver`, `ISemanticSearchProvider`, `IDeadCodeAnalyzer`, `IPartialViewBuilder`, `Invariants/IInvariantProvider`, `IAuthorityReporter`, `IPortHealthAnalyzer`, `IUnityReachabilityProvider`, `IRuntimeAssemblyResolver`, `IResponseDecorator`), `Ports/GraphIO/` (`IGraphImporter`, `IGraphExporter`), `Ports/Analysis/` (`IRuleProvider`, `IBlastRadiusProvider`), `Ports/Infrastructure/` (`IFileSystem`, `IUsageProbe`, `IUsageCapture`). Total count pinned by `docs/STATUS.md` `<!-- portCount -->` ratchet.

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
| `*Importer` / `*Exporter` | Serialization ports |
| `I*` | Port interface |

## Rules for Adding Features

1. **New graph concept** → Domain. Check INV-GRAPH-001 first. Language-specific = Properties.
2. **New analysis** → `Lifeblood.Analysis`. Stateless. Graph in, result out.
3. **New language adapter** → `Lifeblood.Adapters.{Language}/` or external JSON.
4. **New AI connector** → `Lifeblood.Connectors.{Name}/`.
5. **New CLI command** → `Lifeblood.CLI/`.
6. **New use case** → `Lifeblood.Application/UseCases/`.
7. **New invariant** → `docs/invariants/<domain>.md`. Use any of shapes A–E. Add a row to the index in this file if a new domain file is created.

## MCP Tools

Canonical count and per-tool detail live in `docs/STATUS.md` (ratchet-pinned by `DocsTests`) and `docs/TOOLS.md`. `ToolRegistry.cs` is the code-level source of truth.

## What NOT to Do

- Do not put language-specific logic in Domain or Application. Ever.
- Do not make analyzers stateful or mutable.
- Do not let adapters reference other adapters.
- Do not let connectors reference adapters.
- Do not hardcode file extensions or syntax patterns in Domain.
- Do not require adapters to be written in C#. JSON is the universal protocol.
- Do not add "AI features" to the graph model. The graph is pure data. AI consumption happens in connectors.
- Do not duplicate invariant text in this file. Every numbered `INV-XXX-NNN` rule lives in `docs/invariants/`. This file points at it.

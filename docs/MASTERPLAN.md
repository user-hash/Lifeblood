# Lifeblood Masterplan

The semantic pipe between compiler-level code understanding and AI agents.

---

## What Lifeblood Is

Lifeblood is a hexagonal framework with two sides:

```
LEFT SIDE                        CORE                      RIGHT SIDE
(Language Adapters)           (The Pipe)                (AI Connectors)

Roslyn (C#)         ──┐                             ┌──  MCP Server
TS Compiler API     ──┤   ┌───────────────────┐    ├──  CLAUDE.md generator
go/types            ──┼─→ │  Semantic Graph    │ ─→─┤──  Context Pack API
Python ast + mypy   ──┤   │  (universal model) │    ├──  LSP Bridge
rust-analyzer       ──┤   └───────────────────┘    ├──  CLI / CI
Java JDT            ──┘                             └──  JSON / REST API
```

**Left side:** compilers and language tools feed semantic data in.
**Core:** normalizes everything into one universal graph. Pure. Zero dependencies on either side.
**Right side:** AI tools and workflows consume the graph.

Both sides are ports. Both sides are pluggable. We build the framework and the reference implementations. The community builds the rest. Roslyn is open source. AI can help port concepts to other languages. We do not need to build Roslyn-grade quality for every language ourselves.

---

## What We Build vs What the Community Builds

| We build | Community builds |
|----------|-----------------|
| The core graph model | Language adapters for Python, Go, Rust, Java |
| The port interfaces (both sides) | IDE extensions |
| The C# Roslyn adapter (reference) | Visualization dashboards |
| The JSON adapter (universal protocol) | Custom reporters |
| MCP server connector (reference) | CI integrations |
| CLAUDE.md / context pack generator | Organization-specific rule packs |
| CLI tool | Cloud/SaaS wrappers |
| Golden repo test suite | Language-specific test fixtures |
| Hexagonal, Clean Architecture rule packs | Domain-specific rule packs |

We are the framework. We set the standard. We build one killer path end to end (C# + Roslyn + MCP/CLI). Everything else plugs in.

---

## The Model: How Unity MCP Works

Our Unity setup is the blueprint for Lifeblood's AI integration. Here is what we already have working:

```
Unity Editor
  ↓ (Roslyn + reflection + MCP tools)
MCP Server (30+ tools)
  ↓
Claude Code (reads CLAUDE.md, uses MCP tools, sees the codebase semantically)
```

The AI agent does not grep. It calls `execute_code` with Roslyn compilation. It calls `manage_script` to validate changes. It calls `read_console` to check for errors. It has IDE-level access through MCP.

**Lifeblood generalizes this pattern for any language and any AI tool.**

Instead of Unity MCP being a one-off integration, Lifeblood makes it a protocol. Any language adapter on the left, any AI connector on the right, same graph in the middle.

---

## Architecture

### Domain (pure, zero deps, the absolute core)

```
Lifeblood.Domain/
  ├── Graph/
  │   ├── Symbol.cs              # Node: file, type, method, field
  │   ├── Edge.cs                # Relationship: depends, calls, implements
  │   ├── Evidence.cs            # Provenance: syntax/semantic/inferred + confidence
  │   ├── SemanticGraph.cs       # The universal graph with indexed lookups
  │   └── GraphValidator.cs      # Rejects malformed graphs before analysis
  │
  ├── Rules/
  │   ├── ArchitectureRule.cs    # must_not_reference, may_only_reference
  │   └── Violation.cs           # Machine-readable violation with proof
  │
  ├── Results/
  │   ├── CouplingMetrics.cs     # Fan-in, fan-out, instability
  │   ├── TierAssignment.cs      # Pure / Boundary / Runtime / Tooling
  │   ├── BlastRadiusResult.cs   # What breaks if you change this
  │   └── GraphMetrics.cs        # Aggregate stats
  │
  └── Capabilities/
      ├── AdapterCapability.cs   # What the adapter can actually do
      └── ConfidenceLevel.cs     # Proven / High / BestEffort / None
```

**INV-DOMAIN-001:** This project has ZERO dependencies. Not on Roslyn, not on JSON, not on System.IO. If a PackageReference appears here, the architecture is broken.

### Application (orchestration, depends only on Domain + ports)

```
Lifeblood.Application/
  ├── Ports/
  │   │
  │   ├── Left Side (Language Adapters)
  │   │   ├── IWorkspaceAnalyzer.cs      # Primary: workspace → graph
  │   │   ├── IModuleDiscovery.cs        # Find modules/assemblies/packages
  │   │   └── ISourceProvider.cs         # Read source files
  │   │
  │   ├── Right Side (AI Connectors)
  │   │   ├── IAgentContextGenerator.cs  # Graph → context pack for AI
  │   │   ├── IMcpGraphProvider.cs       # Serve graph over MCP protocol
  │   │   └── IInstructionFileGenerator.cs # Graph → CLAUDE.md content
  │   │
  │   ├── Graph I/O
  │   │   ├── IGraphImporter.cs          # Read graph from JSON/external
  │   │   ├── IGraphExporter.cs          # Write graph to JSON/DOT/etc
  │   │   └── IGraphNormalizer.cs        # Deduplicate, validate, clean
  │   │
  │   ├── Analysis
  │   │   ├── IAnalyzer.cs               # Generic: graph → typed result
  │   │   ├── IRuleProvider.cs           # Where rules come from
  │   │   └── IEntryPointProvider.cs     # For dead code analysis
  │   │
  │   ├── Output
  │   │   ├── IReportSink.cs             # Where results go
  │   │   └── IProgressSink.cs           # Progress for long analysis
  │   │
  │   └── Infrastructure
  │       ├── IFileSystem.cs             # Abstracts disk
  │       ├── ICache.cs                  # Incremental analysis cache
  │       └── ILogger.cs                 # Logging
  │
  ├── UseCases/
  │   ├── AnalyzeWorkspaceUseCase.cs     # The main pipeline
  │   ├── ValidateRulesUseCase.cs        # Check rules against graph
  │   ├── GenerateContextUseCase.cs      # Produce AI context pack
  │   ├── ExportGraphUseCase.cs          # Serialize graph
  │   └── CompareSnapshotsUseCase.cs     # Diff two graphs
  │
  └── Pipeline/
      └── AnalysisPipeline.cs            # Deterministic execution order
```

**INV-APP-001:** Application depends only on Domain. Never on adapters, reporters, or CLI.

### Left Side Adapters (Language-Specific)

```
Lifeblood.Adapters.CSharp/          # Reference implementation
  ├── RoslynWorkspaceAnalyzer.cs     # IWorkspaceAnalyzer via Roslyn
  ├── RoslynModuleDiscovery.cs       # .csproj / .sln / .asmdef discovery
  ├── RoslynSymbolExtractor.cs       # Types, methods, fields from semantic model
  ├── RoslynEdgeExtractor.cs         # References, calls, implements from semantic model
  ├── RoslynCapability.cs            # Declares: proven for type/call/override resolution
  └── Internal/
      └── RoslynFileParser.cs        # Per-file syntax parsing (internal helper)

Lifeblood.Adapters.JsonGraph/       # Universal protocol adapter
  ├── JsonGraphImporter.cs           # Read graph.json from any external parser
  ├── JsonGraphExporter.cs           # Write graph.json
  ├── JsonSchemaValidator.cs         # Validate against schemas/graph.schema.json
  └── JsonCapabilityReader.cs        # Read capability metadata from JSON

# Community adapters (we provide the port, they implement):
# Lifeblood.Adapters.TypeScript/    # ts.createProgram + TypeChecker
# Lifeblood.Adapters.Go/            # go/analysis + go/types
# Lifeblood.Adapters.Python/        # ast + mypy
# Lifeblood.Adapters.Rust/          # syn + rust-analyzer
# Lifeblood.Adapters.Java/          # JDT / javac tree API
```

### Right Side Connectors (AI-Facing)

```
Lifeblood.Connectors.Mcp/           # MCP server that serves the graph
  ├── McpGraphProvider.cs            # AI agent queries graph via MCP tools
  ├── McpSymbolLookup.cs             # "What does this type depend on?"
  ├── McpBlastRadius.cs              # "What breaks if I change this?"
  └── McpContextPack.cs              # "Give me the context for this file"

Lifeblood.Connectors.ContextPack/   # CLAUDE.md / AI instruction generator
  ├── AgentContextGenerator.cs       # Graph → context pack JSON
  ├── InstructionFileGenerator.cs    # Graph → CLAUDE.md section
  └── ReadingOrderGenerator.cs       # Topological sort by importance

Lifeblood.Connectors.Lsp/           # (future) LSP bridge for IDE integration
```

### Analysis (optional addon, not the core product)

```
Lifeblood.Analysis/
  ├── CouplingAnalyzer.cs
  ├── BlastRadiusAnalyzer.cs
  ├── DeadCodeAnalyzer.cs
  ├── CircularDependencyDetector.cs
  ├── TierClassifier.cs
  ├── HubBridgeDetector.cs
  ├── BoundaryLeakAnalyzer.cs
  └── RuleValidator.cs
```

### Reporters + CLI

```
Lifeblood.Reporters.Json/
Lifeblood.Reporters.Html/
Lifeblood.Reporters.Sarif/          # CI/code-scanning standard

Lifeblood.CLI/                      # Composition root. Wires everything.
```

### Testing

```
Lifeblood.TestKit/                   # Adapter contract test helpers
Lifeblood.GoldenRepos/               # Fixture repos with expected outputs
```

---

## The Pipeline

When you run `lifeblood analyze`, this happens:

```
1. CLI resolves workspace root
2. Module discovery (IModuleDiscovery)
3. Source enumeration (ISourceProvider)
4. Language adapter produces SemanticGraph (IWorkspaceAnalyzer)
   OR: JSON graph imported (IGraphImporter)
5. Graph normalized and validated (IGraphNormalizer, GraphValidator)
6. Rules loaded (IRuleProvider)
7. Analysis passes run (IAnalyzer[])
8. Results aggregated with confidence levels
9. Output: report (IReportSink) + context pack (IAgentContextGenerator)
```

---

## How Other Languages Plug In

### Option A: In-process C# adapter
Write a C# class that implements `IWorkspaceAnalyzer`. Get full pipeline integration. This is what the Roslyn adapter does.

### Option B: External process + JSON
Write a parser in any language. Output `graph.json`. Lifeblood reads it via `JsonGraphImporter`. This is how Python, Go, Rust, Java plug in without writing C#.

```bash
# Python community builds a parser
python lifeblood-python ./my-project > graph.json

# Lifeblood consumes it
lifeblood analyze --graph graph.json --rules hexagonal.json
```

### Option C: AI-assisted porting
Roslyn is open source. The concepts (syntax trees, semantic models, symbol resolution) exist in every language's toolchain. AI agents can help port adapter implementations. We provide the port interface and golden repo tests. The community (with AI help) implements the adapters.

---

## How AI Tools Plug In (Right Side)

### MCP Server
Like our Unity MCP setup but for any codebase. AI agent calls MCP tools to query the graph:

```
lifeblood-mcp:symbol-lookup     "What is AuthService?"
lifeblood-mcp:dependencies      "What does Domain depend on?"
lifeblood-mcp:blast-radius      "What breaks if I change IUserRepository?"
lifeblood-mcp:context-pack      "Give me context for src/auth/"
lifeblood-mcp:violations        "What architecture rules are broken?"
```

### CLAUDE.md Generator
Analyze a codebase, produce a CLAUDE.md section with architecture boundaries, invariants, high-value files, reading order. Paste into your AI instruction file.

### Context Pack API
JSON output designed for AI consumption. Feed it to any AI tool.

### LSP Bridge (future)
IDE extension that shows tiers, coupling, violations inline. AI agents that support LSP get semantic info automatically.

---

## 10-Stage Plan

### Stage 1: Domain + Application Split
Split current Lifeblood.Core into Lifeblood.Domain (pure graph model) and Lifeblood.Application (ports + use cases). Fix the 8 audit holes. No features.

### Stage 2: Contract Hardening
Add rules.schema.json. Add evidence to graph.schema.json. Reconcile JSON/C# naming. GraphValidator rejects malformed graphs. Fix RuleValidator to not mutate graph. Fix GraphBuilder to wire containment. Fix CouplingAnalyzer to count distinct dependants.

### Stage 3: C# Roslyn Adapter
Reference implementation. Workspace-scoped. Module discovery from .csproj/.sln/.asmdef. Symbol extraction via Roslyn semantic model. Evidence on every edge. Capability declaration: proven.

### Stage 4: JSON Graph Adapter
Equal citizen. Schema validation. Import/export parity with Roslyn path. This is what makes "any language can plug in" real.

### Stage 5: CLI + JSON Reporter
End-to-end vertical slice. `lifeblood analyze --project ./my-app` works. `lifeblood analyze --graph graph.json` works. JSON output. Exit codes for CI.

### Stage 6: Golden Repo Test Suite
Contract tests every adapter must pass. Fixture repos with expected outputs. Adapter certification. The backbone for community quality.

### Stage 7: Context Pack Generator (The Killer Feature)
`lifeblood context --project ./my-app` produces an AI-consumable context pack. High-value files, boundaries, reading order, hotspots, blast radius map. Paste into CLAUDE.md or feed to any AI tool.

### Stage 8: MCP Server Connector
Lifeblood as an MCP server. AI agents query the graph through MCP tools. Same pattern as our Unity MCP setup but for any codebase, any language.

### Stage 9: Analysis Suite + Rule Packs
Full analyzer suite. Hexagonal, Clean Architecture, DDD, Monorepo rule packs. `lifeblood rules --pack hexagonal` applies a built-in pack.

### Stage 10: TypeScript Adapter + Ecosystem
Second language proves universality. Contributor guide. Plugin discovery. CI templates. The community takes over.

---

## Invariants

```
INV-DOMAIN-001:  Domain has zero dependencies. Not Roslyn, not JSON, not System.IO.
INV-DOMAIN-002:  Domain never references Application, Adapters, Connectors, or CLI.
INV-APP-001:     Application depends only on Domain.
INV-APP-002:     Application never references concrete adapters or connectors.
INV-GRAPH-001:   SymbolKind enum is language-agnostic.
INV-GRAPH-002:   Language-specific metadata goes in Properties, not new fields.
INV-GRAPH-003:   Every edge carries Evidence (kind, adapter, confidence).
INV-GRAPH-004:   Analyzers do not modify the graph. Results are separate.
INV-ADAPT-001:   Every adapter declares capabilities honestly.
INV-ADAPT-002:   C# adapter is the reference. Most complete, best tested.
INV-ADAPT-003:   External adapters communicate via JSON graph schema only.
INV-CONN-001:    Right-side connectors depend on Application ports, not on adapters.
INV-CONN-002:    MCP connector serves the graph, does not modify it.
INV-TEST-001:    Every adapter passes the same golden repo contract tests.
INV-PIPE-001:    The pipeline is deterministic. Same input = same output.
```

---

## The Vision

Lifeblood becomes the standard way to give AI agents semantic understanding of codebases. Not text search. Not embeddings. Real compiler-level understanding, normalized into one universal graph, consumable by any AI tool.

The left side grows as communities build language adapters.
The right side grows as AI tools adopt MCP and context pack protocols.
The core stays pure. Forever.

We are the lifeblood.

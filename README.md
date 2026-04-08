# Lifeblood

Compiler truth in, AI context out.

Lifeblood is a hexagonal framework that connects compiler-level code intelligence to AI tools. Language adapters on the left feed semantic data into a pure universal graph. AI connectors on the right consume that graph as structured context. The core normalizes everything in between.

```
Roslyn (C#)    ──┐                              ┌──  Context Pack (JSON)
TypeScript     ──┤  ┌────────────────────────┐  ├──  Instruction File (md)
JSON graph     ──┼→ │    Semantic Graph      │ →┤──  MCP Server (stdio)
               ──┤  │  (symbols · edges ·    │  ├──  CLI / CI
  community    ──┘  │   evidence · trust)    │  └──  JSON export
  adapters          └────────────────────────┘
```

We build the framework and two reference adapters (C# via Roslyn, TypeScript via the TS compiler API). The community builds the rest. We provide the contracts, schemas, golden repo fixtures, and a [13-item adapter checklist](docs/ADAPTERS.md).

Born from shipping a [400k LOC Unity project](https://github.com/user-hash/LivingDocFramework/blob/main/docs/CASE_STUDY.md) with AI assistance and realizing that AI writes code but does not verify what it wrote.

---

## Why This Matters

AI builds backends but does not wire frontends. It adds methods but does not update callers. It refactors types but misses downstream consumers. It moves fast and never looks back.

That was fine when AI built simple prototypes. But AI-assisted projects are getting complex. At that scale, unverified code is a liability. The prototyping era is ending. The framework era is starting.

Lifeblood is built for that era. Point it at a codebase and get a verified semantic graph:

- Every edge points to a real symbol (no dangling references)
- Every symbol is reachable in the containment tree (no orphans)
- No duplicate edges, no missing evidence
- Every relationship carries proof of how it was discovered and how confident the adapter is

We dogfood Lifeblood on itself. Every push, the CI analyzes the framework's own codebase:

```
$ dotnet run --project src/Lifeblood.CLI -- analyze --project . --rules packs/lifeblood/rules.json
Symbols: 634
Edges:   888
Modules: 10
Types:   100
```

Zero violations. Zero dangling edges. Zero duplicates.

The first dogfood run found 6 real issues, including 2 critical (JSON exporter silently dropping edges, rule packs not loading). All were fixed in the same session. That is exactly what dogfooding is for. [Full findings published](docs/DOGFOOD_FINDINGS.md).

**Cross-language proof:** The TypeScript adapter analyzes its own source code, exports a JSON graph, and the C# CLI imports and validates it. Two languages, one graph model, one pipeline. This runs on every push.

---

## Quick Start

### Install as a dotnet tool

```bash
dotnet tool install --global Lifeblood
dotnet tool install --global Lifeblood.Server.Mcp
```

Then run directly:

```bash
lifeblood analyze --project /path/to/your/project
lifeblood analyze --project /path/to/your/project --rules packs/hexagonal/rules.json
lifeblood context --project /path/to/your/project
lifeblood context --project /path/to/your/project --format md
lifeblood export  --project /path/to/your/project > graph.json
```

### Or build from source

```bash
git clone https://github.com/user-hash/Lifeblood.git
cd Lifeblood
dotnet build
dotnet test
dotnet run --project src/Lifeblood.CLI -- analyze --project /path/to/your/project
```

### Analyze TypeScript (or any language via JSON)

```bash
cd adapters/typescript && npm install && npm run build
node dist/index.js /path/to/ts-project > graph.json
lifeblood analyze --graph graph.json
```

### Connect an MCP client (Claude Code, etc.)

Using the installed tool:

```json
{
  "mcpServers": {
    "lifeblood": {
      "command": "lifeblood-mcp",
      "args": []
    }
  }
}
```

Or from source:

```json
{
  "mcpServers": {
    "lifeblood": {
      "command": "dotnet",
      "args": ["run", "--project", "src/Lifeblood.Server.Mcp"]
    }
  }
}
```

---

## Architecture

```
Lifeblood.Domain                Pure graph model. Zero dependencies. The absolute core.
Lifeblood.Application           Ports and use cases. Depends only on Domain.
Lifeblood.Adapters.CSharp      Roslyn reference adapter. Left side.
Lifeblood.Adapters.JsonGraph    Universal JSON protocol adapter. Left side.
Lifeblood.Connectors.ContextPack  Context pack and instruction file generator. Right side.
Lifeblood.Connectors.Mcp       MCP graph provider port. Right side.
Lifeblood.Analysis              Coupling, blast radius, cycles, tiers, rule validation.
Lifeblood.Server.Mcp            MCP server. Interactive AI agent sessions over stdio.
Lifeblood.CLI                   Composition root. Wires left side to right side.
adapters/typescript/            TypeScript adapter (standalone Node.js, JSON protocol).
```

Domain never references Application. Application never references Adapters or Connectors. Adapters never reference other Adapters. Connectors never reference Adapters. All enforced by [architecture invariant tests](tests/Lifeblood.Tests/ArchitectureInvariantTests.cs) and [11 frozen ADRs](docs/ARCHITECTURE_DECISIONS.md).

![Architecture Diagram](docs/architecture-screenshot.png)

[Full architecture](docs/ARCHITECTURE.md) and [interactive diagram](docs/architecture.html)

---

## Language Adapters

Two adapters ship today. Community adapters can be built via the JSON protocol ([contribution guides](docs/ADAPTERS.md) and [schema](schemas/graph.schema.json) are ready, no implementation code yet).

**C# / Roslyn (reference adapter):** Compiler-grade semantic analysis. Extracts types, methods, fields, inheritance, calls, references. Proven type and call resolution. Discovers modules from .sln/.csproj files.

**TypeScript (standalone Node.js):** Uses `ts.createProgram` + `TypeChecker`. High-confidence type and call resolution. Outputs `graph.json` that the C# CLI consumes directly.

**Any language via JSON:** Write a parser in your language. Output JSON conforming to `schemas/graph.schema.json`. See [docs/ADAPTERS.md](docs/ADAPTERS.md) for the contract, quality levels, and the 13-item adapter checklist.

---

## AI Connectors

**Context Pack** produces structured JSON with high-value files, module boundaries, reading order, hotspots, dependency matrix, and active violations. Feed it to any AI tool.

**Instruction File Generator** analyzes a codebase and produces CLAUDE.md or AGENTS.md sections with architecture boundaries, dependency rules, and high-value files.

**MCP Server** serves the semantic graph interactively over stdio (JSON-RPC 2.0). Six tools: analyze, context, lookup, dependencies, dependants, blast radius. AI agents can load a project and query its structure in real time.

**CLI** runs analysis, validates architecture rules, generates context, and exports graphs. Designed for CI integration with exit codes.

---

## Status

Dogfood-verified. 109 tests. CI green (3 jobs: build, TypeScript adapter, dogfood).

| Component | State |
|-----------|-------|
| Lifeblood.Domain | Implemented. Immutable graph model, GraphBuilder, GraphValidator, Evidence, ConfidenceLevel, GraphDocument. |
| Lifeblood.Application | Implemented. All port interfaces, AnalyzeWorkspaceUseCase, GenerateContextUseCase. |
| Lifeblood.Adapters.CSharp | Implemented. Roslyn workspace analyzer, module discovery, symbol and edge extractors. |
| Lifeblood.Adapters.JsonGraph | Implemented. Import and export with full metadata round-trip. |
| Lifeblood.Connectors.ContextPack | Implemented. Context pack with GraphSummary, instruction file, reading order. |
| Lifeblood.Connectors.Mcp | Implemented. Graph provider with blast radius delegation. |
| Lifeblood.Analysis | Implemented. Coupling, blast radius, cycles, tiers, rule validation. |
| Lifeblood.Server.Mcp | Implemented. MCP server with 6 tools over stdio. |
| Lifeblood.CLI | Implemented. analyze, context, export with centralized validation. |
| adapters/typescript | Implemented. Standalone TS compiler API adapter. Self-analyzing. |
| Lifeblood.Tests | 109 tests. Extractors, golden repos, round-trip, architecture invariants, MCP server, CLI pipeline. |

**Rule packs:** [hexagonal](packs/hexagonal/rules.json), [clean-architecture](packs/clean-architecture/rules.json), [lifeblood](packs/lifeblood/rules.json) (self-validating)

---

## Roadmap

These items are not yet implemented. They represent the direction, not the current state.

- **Cross-module Roslyn resolution** — currently best-effort (per-module compilation). Full cross-project type resolution requires a unified Roslyn workspace.
- **Community adapters** — contribution guides exist for [Go](adapters/go/), [Python](adapters/python/), and [Rust](adapters/rust/), but no implementation code yet.
- **NuGet / dotnet tool packaging** — install Lifeblood as a global tool instead of cloning the repo.
- **REST / LSP bridge** — expose the graph to IDE extensions and web services.

---

## Built With

Human direction + [Claude Code](https://claude.ai/code) + Claude Chat, working in concert. 44 commits in one session. Every commit builds. Every commit passes tests. Dogfood clean throughout. The [LivingDocFramework](https://github.com/user-hash/LivingDocFramework) methodology shaped the architecture.

---

## Related

- [LivingDocFramework](https://github.com/user-hash/LivingDocFramework) — The methodology framework
- [Roslyn](https://github.com/dotnet/roslyn) — The C# compiler platform
- [DAWG](https://dawgtools.org) | [itch.io](https://dawg-tools.itch.io/) — The 400k LOC project where we proved these ideas

## License

AGPL v3

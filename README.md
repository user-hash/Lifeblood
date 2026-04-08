# Lifeblood

Compiler-as-a-service for AI agents.

Lifeblood gives AI agents direct access to what compilers know. Type resolution, call graphs, diagnostics, reference finding, code execution. All over a standard MCP connection. No IDE required. Load a project, ask the compiler, get verified answers.

```
Roslyn (C#)    ──┐                              ┌──  Execute code against project types
TypeScript     ──┤  ┌────────────────────────┐  ├──  Diagnose · compile-check
JSON graph     ──┼→ │    Semantic Graph      │ →┤──  Find references · rename · format
               ──┤  │  (symbols · edges ·    │  ├──  Blast radius · dependency graphs
  community    ──┘  │   evidence · trust)    │  └──  Context packs · architecture rules
  adapters          └────────────────────────┘
```

Born from shipping a [400k LOC Unity project](https://github.com/user-hash/LivingDocFramework/blob/main/docs/CASE_STUDY.md) with AI assistance and realizing that AI writes code but does not verify what it wrote.

---

## What AI Agents Get

Connect an MCP client. Load a C# project. The AI agent now has **12 tools** in one session. 6 read, 6 write:

### Write-side (compiler-as-a-service)

| Tool | What it does |
|------|-------------|
| **Execute** | Run C# code against your actual project types, in-process. Access any class, call any method, inspect any state. |
| **Diagnose** | Real compiler diagnostics. Errors, warnings, with file, line, and module. Not guesses. |
| **Compile-check** | "Will this snippet compile in my project?" answered in milliseconds. |
| **Find references** | Every caller, every consumer, across the entire workspace. Verified by the compiler. |
| **Rename** | Safe rename across the workspace. Returns text edits as preview. The agent decides whether to apply. |
| **Format** | Roslyn's own formatter. Not regex hacks. |

### Read-side (semantic intelligence)

| Tool | What it does |
|------|-------------|
| **Analyze** | Load a project into a verified semantic graph. Symbols, edges, modules, violations. |
| **Context** | AI context pack with high-value files, boundaries, reading order, hotspots, dependency matrix. |
| **Lookup** | Symbol details: kind, file, line, visibility, properties. |
| **Dependencies** | What does this symbol depend on? |
| **Dependants** | What depends on this symbol? |
| **Blast radius** | Change this symbol, what breaks? Transitive BFS over the dependency graph. |

The difference: the AI agent doesn't guess what your code does. It **asks the compiler**.

---

## Quick Start

### Install as a dotnet tool

```bash
dotnet tool install --global Lifeblood
dotnet tool install --global Lifeblood.Server.Mcp
```

### Connect an MCP client (Claude Code, Cursor, etc.)

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

### CLI for analysis and CI

```bash
lifeblood analyze --project /path/to/your/project
lifeblood analyze --project /path/to/your/project --rules packs/hexagonal/rules.json
lifeblood context --project /path/to/your/project
lifeblood export  --project /path/to/your/project > graph.json
```

### Build from source

```bash
git clone https://github.com/user-hash/Lifeblood.git
cd Lifeblood
dotnet build
dotnet test
```

---

## Three Languages, One Graph

Language adapters feed compiler-level semantics into a universal graph model. The graph doesn't care what language produced it.

**C# / Roslyn (reference adapter):** Compiler-grade semantic analysis with cross-module resolution. Compilations built in dependency order. Proven type, call, and cross-module resolution. Full bidirectional Roslyn: analysis and code execution.

**TypeScript (standalone Node.js):** Uses `ts.createProgram` + `TypeChecker`. High-confidence type and call resolution. Self-analyzing.

**Python (standalone, zero dependencies):** Uses Python's built-in `ast` module. Classes, functions, imports, inheritance. Self-analyzing.

**Any language via JSON:** Write a parser in your language. Output JSON conforming to `schemas/graph.schema.json`. See [docs/ADAPTERS.md](docs/ADAPTERS.md) for the contract and the 13-item adapter checklist.

---

## Dogfooding

We test Lifeblood on itself. The MCP server loads its own source code, executes queries against its own types, and validates its own architecture:

```
$ lifeblood analyze --project . --rules packs/lifeblood/rules.json
Symbols: 959
Edges:   2348
Modules: 11
Types:   140
```

Zero violations. Zero dangling edges. Zero duplicates.

The first dogfood run found [6 real issues](docs/DOGFOOD_FINDINGS.md), including 2 critical. The code execution dogfood found [7 more bugs](docs/DOGFOOD_CODE_EXECUTION.md) that 121 unit tests missed. All fixed in the same session. Every bug was invisible to unit tests and would have hit real users on first connection.

---

## Architecture

Hexagonal. Pure domain core with zero dependencies. Language adapters on the left, AI connectors on the right. 14 port interfaces, all wired. Boundaries enforced by [architecture invariant tests](tests/Lifeblood.Tests/ArchitectureInvariantTests.cs) and [11 frozen ADRs](docs/ARCHITECTURE_DECISIONS.md).

```
Lifeblood.Domain                Pure graph model. Zero dependencies. The absolute core.
Lifeblood.Application           14 port interfaces + use cases. Depends only on Domain.
Lifeblood.Adapters.CSharp      Roslyn reference adapter. Bidirectional: analysis + code execution.
Lifeblood.Adapters.JsonGraph    Universal JSON protocol adapter. Left side.
Lifeblood.Connectors.ContextPack  Context pack and instruction file generator. Right side.
Lifeblood.Connectors.Mcp       MCP graph provider port. Right side.
Lifeblood.Analysis              Coupling, blast radius, cycles, tiers, rule validation.
Lifeblood.Server.Mcp            MCP server. 12 tools over stdio. Bidirectional Roslyn.
Lifeblood.CLI                   Composition root. Wires left side to right side.
adapters/typescript/            TypeScript adapter (standalone Node.js, JSON protocol).
adapters/python/                Python adapter (standalone, zero dependencies).
```

![Architecture Diagram](docs/architecture-screenshot.png)

[Full architecture](docs/ARCHITECTURE.md) and [interactive diagram](docs/architecture.html)

---

## Status

Dogfood-verified. 209 tests. 12 MCP tools (6 read + 6 write). CI green (4 jobs: build, TypeScript adapter, Python adapter, dogfood).

| Component | State |
|-----------|-------|
| Lifeblood.Domain | Implemented. Immutable graph model, GraphBuilder, GraphValidator, Evidence, ConfidenceLevel. |
| Lifeblood.Application | Implemented. 14 port interfaces, AnalyzeWorkspaceUseCase, GenerateContextUseCase. |
| Lifeblood.Adapters.CSharp | Implemented. Roslyn workspace analyzer + bidirectional compiler-as-a-service (execute, diagnose, compile-check, find references, rename, format). |
| Lifeblood.Adapters.JsonGraph | Implemented. Import and export with full metadata round-trip. |
| Lifeblood.Connectors.ContextPack | Implemented. Context pack with GraphSummary, instruction file, reading order. |
| Lifeblood.Connectors.Mcp | Implemented. Graph provider with blast radius delegation. |
| Lifeblood.Analysis | Implemented. Coupling, blast radius, cycles, tiers, rule validation. |
| Lifeblood.Server.Mcp | Implemented. MCP server with 12 tools over stdio. Bidirectional Roslyn. |
| Lifeblood.CLI | Implemented. analyze, context, export with centralized validation. |
| adapters/typescript | Implemented. Standalone TS compiler API adapter. Self-analyzing. |
| adapters/python | Implemented. Standalone ast-based adapter. Zero dependencies. Self-analyzing. |
| Lifeblood.Tests | 209 tests. Extractors, golden repos, round-trip, architecture invariants, MCP server, CLI pipeline, WorkspaceSession. |

**Rule packs:** [hexagonal](packs/hexagonal/rules.json), [clean-architecture](packs/clean-architecture/rules.json), [lifeblood](packs/lifeblood/rules.json) (self-validating)

---

## Roadmap

- **Community adapters**: contribution guides exist for [Go](adapters/go/) and [Rust](adapters/rust/) — contract + checklist only, no implementation code yet.
- **REST / LSP bridge**: expose the graph to IDE extensions and web services.
- **NuGet publishing**: packages are built in CI, but not yet published to nuget.org.

---

## Built With
The [LivingDocFramework](https://github.com/user-hash/LivingDocFramework) methodology shaped the architecture.

---

## Related

- [LivingDocFramework](https://github.com/user-hash/LivingDocFramework): The methodology framework
- [Roslyn](https://github.com/dotnet/roslyn): The C# compiler platform
- [DAWG](https://dawgtools.org) | [itch.io](https://dawg-tools.itch.io/): The 400k LOC project where we proved these ideas

## License

AGPL v3

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

Connect an MCP client. Load a C# project. The AI agent now has **17 tools** in one session. 7 read, 10 write:

### Write-side (compiler-as-a-service)

| Tool | What it does |
|------|-------------|
| **Execute** | Run C# code against your actual project types, in-process. Access any class, call any method, inspect any state. |
| **Diagnose** | Real compiler diagnostics. Errors, warnings, with file, line, and module. Not guesses. |
| **Compile-check** | "Will this snippet compile in my project?" answered in milliseconds. No domain reload. |
| **Find references** | Every caller, every consumer, across the entire workspace. Verified by the compiler. |
| **Find definition** | Go-to-definition. Resolves through interfaces, base classes, partials. Returns file, line, docs. |
| **Find implementations** | What types implement this interface? What methods override this? Semantic, not grep. |
| **Symbol at position** | Give a file:line:col, get the resolved symbol, type, and documentation. |
| **Documentation** | XML doc extraction — pulls `<summary>`, `<param>`, `<returns>` from resolved symbols. |
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
| **File impact** | Change this file, what other files break? Derived from symbol-level edges. |

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
lifeblood analyze --project /path/to/your/project --rules hexagonal
lifeblood analyze --project /path/to/your/project --rules /path/to/custom-rules.json
lifeblood context --project /path/to/your/project
lifeblood export  --project /path/to/your/project > graph.json
```

Built-in rule packs: `hexagonal`, `clean-architecture`, `lifeblood`. Or pass a path to your own `rules.json`.

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
$ lifeblood analyze --project . --rules lifeblood
Symbols: 1057
Edges:   2594
Modules: 11
Types:   145
```

Zero violations. Zero dangling edges. Zero duplicates.

**Tested on a real 75-module Unity project (400k+ LOC, 2,404 types):**
```
$ lifeblood analyze --project /path/to/unity-project
Symbols: 43,800
Edges:   70,600
Modules: 75
Types:   2,404
Cycles:  34
```
Peak memory: ~4GB (down from 32GB before streaming architecture). Streaming compilation with downgrading processes modules one at a time, emitting lightweight PE metadata references for downstream modules.

Six dogfood sessions found [23 real bugs](docs/DOGFOOD_FINDINGS.md) — security bypasses, silent data loss, off-by-one boundaries, resource leaks, missing AST node types, memory architecture. All fixed in-session. Every bug was invisible to unit tests and would have hit real users.

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
Lifeblood.Server.Mcp            MCP server. 17 tools over stdio. Bidirectional Roslyn.
Lifeblood.CLI                   Composition root. Wires left side to right side.
adapters/typescript/            TypeScript adapter (standalone Node.js, JSON protocol).
adapters/python/                Python adapter (standalone, zero dependencies).
```

![Architecture Diagram](docs/architecture-screenshot.png)

[Full architecture](docs/ARCHITECTURE.md) and [interactive diagram](docs/architecture.html)

---

## Status

Dogfood-verified. 241 tests. 17 MCP tools (7 read + 10 write). CI green (4 jobs: build, TypeScript adapter, Python adapter, dogfood).

| Component | State |
|-----------|-------|
| Lifeblood.Domain | Implemented. Immutable graph model, GraphBuilder, GraphValidator, Evidence, ConfidenceLevel. |
| Lifeblood.Application | Implemented. 14 port interfaces, AnalyzeWorkspaceUseCase, GenerateContextUseCase. |
| Lifeblood.Adapters.CSharp | Implemented. Roslyn workspace analyzer with streaming compilation + downgrading. Bidirectional compiler-as-a-service (execute, diagnose, compile-check, find references/definition/implementations, symbol-at-position, documentation, rename, format). |
| Lifeblood.Adapters.JsonGraph | Implemented. Import and export with full metadata round-trip. |
| Lifeblood.Connectors.ContextPack | Implemented. Context pack with GraphSummary, instruction file, reading order. |
| Lifeblood.Connectors.Mcp | Implemented. Graph provider with blast radius delegation. |
| Lifeblood.Analysis | Implemented. Coupling, blast radius, cycles, tiers, rule validation. |
| Lifeblood.Server.Mcp | Implemented. MCP server with 17 tools over stdio. Bidirectional Roslyn. |
| Lifeblood.CLI | Implemented. analyze, context, export with centralized validation. |
| adapters/typescript | Implemented. Standalone TS compiler API adapter. Self-analyzing. |
| adapters/python | Implemented. Standalone ast-based adapter. Zero dependencies. Self-analyzing. |
| Lifeblood.Tests | 241 tests. Extractors, golden repos, round-trip, architecture invariants, MCP server, CLI pipeline, WorkspaceSession, security scanner, write-side integration. |

**Rule packs:** [hexagonal](packs/hexagonal/rules.json), [clean-architecture](packs/clean-architecture/rules.json), [lifeblood](packs/lifeblood/rules.json) (self-validating)

---

## Unity Integration

Lifeblood runs as a **sidecar semantic engine** alongside Unity MCP. The Unity Editor stays in control of scenes, GameObjects, and assets. Lifeblood provides compiler-grade code intelligence.

### Architecture

```
Claude Code ──→ Unity MCP (action/control plane)
                    │
                    ├── built-in tools (scenes, GameObjects, scripts...)
                    │
                    └── [McpForUnityTool] custom tools ──→ Lifeblood MCP (child process)
                        └── 16 semantic tools (analyze, references, blast radius...)
```

Lifeblood does NOT run inside Unity. It spawns as a separate .NET process with its own Roslyn workspace — no assembly conflicts, no domain reload interference, no memory pressure on the Editor.

### Setup

The bridge package lives in `Assets/Editor/LifebloodBridge/` (3 files: asmdef, client, tools). It auto-discovers via `[McpForUnityTool]`. Requirements:

1. Lifeblood repo as a sibling directory (e.g., `D:\Projekti\Lifeblood` next to `D:\Projekti\YourUnityProject`)
2. Build Lifeblood once: `cd Lifeblood && dotnet build`
3. The bridge finds the server DLL automatically via convention

Override the server path if needed:
- **EditorPrefs:** Set `Lifeblood_ServerPath` to the full path of `Lifeblood.Server.Mcp.dll`
- **Environment:** Set `LIFEBLOOD_SERVER_DLL`

### Usage from CLI (no Unity needed)

```bash
# Analyze any C# project
lifeblood analyze --project /path/to/your/project

# With architecture rules
lifeblood analyze --project /path/to/your/project --rules packs/hexagonal/rules.json

# Export semantic graph as JSON
lifeblood export --project /path/to/your/project > graph.json
```

### Usage from MCP (interactive)

Start the server, then send JSON-RPC:

```bash
dotnet run --project src/Lifeblood.Server.Mcp
```

```json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"lifeblood_analyze","arguments":{"projectPath":"/path/to/project"}}}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"lifeblood_blast_radius","arguments":{"symbolId":"type:MyApp.MyService"}}}
```

### Memory

Streaming compilation with downgrading keeps memory bounded:

| Project size | Peak memory | Graph |
|---|---|---|
| ~10 modules (Lifeblood itself) | ~200 MB | 1,057 symbols, 2,594 edges |
| ~75 modules (40k LOC Unity project) | ~4 GB | 43,800 symbols, 70,600 edges |

Each module is compiled, extracted, then downgraded to a lightweight PE metadata reference (~10-100KB vs ~200MB full compilation). Only one full compilation is in memory at a time.

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

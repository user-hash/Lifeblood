# Lifeblood

Compiler-as-a-service for AI agents.

Lifeblood gives AI agents direct access to what compilers know. Type resolution, call graphs, diagnostics, reference finding, code execution. All over a standard MCP connection. No IDE required. Load a project, ask the compiler, get verified answers. 
Compilers already know everything about your code, just pipe that truth to AI agents instead of letting them grep and guess.

```
Roslyn (C#)    ──┐                              ┌──  Execute code against project types
TypeScript     ──┤  ┌────────────────────────┐  ├──  Diagnose / compile-check
JSON graph     ──┼→ │    Semantic Graph      │ →┤──  Find references / rename / format
               ──┤  │  (symbols / edges /    │  ├──  Blast radius / file impact
  community    ──┘  │   evidence / trust)    │  └──  Context packs / architecture rules
  adapters          └────────────────────────┘
```

Born from shipping a [400k LOC Unity project](https://github.com/user-hash/LivingDocFramework/blob/main/docs/CASE_STUDY.md) with AI assistance and realizing that AI writes code but does not verify what it wrote.

---

## Quick Start

### Install (30 seconds)

```bash
dotnet tool install --global Lifeblood
dotnet tool install --global Lifeblood.Server.Mcp
```

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

### Connect to Claude Code, Cursor, or any MCP client

Add to your project's `.mcp.json`:

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

See [MCP Setup Guide](docs/MCP_SETUP.md) for Claude Desktop, VS Code, Cursor, and raw stdio configs.

### Use

```
lifeblood_analyze projectPath="/path/to/your/project"   → load semantic graph
lifeblood_blast_radius symbolId="type:MyApp.AuthService" → what breaks if I change this?
lifeblood_file_impact filePath="src/AuthService.cs"      → what files are affected?
lifeblood_find_references symbolId="type:MyApp.IRepo"    → every caller, every consumer
lifeblood_execute code="typeof(MyApp.Foo).GetMethods()"  → run C# against your types
```

After the first analysis, use `incremental: true` for fast re-analysis (seconds instead of minutes).

### CLI (for CI and scripting)

```bash
lifeblood analyze --project /path/to/your/project
lifeblood analyze --project /path/to/your/project --rules hexagonal
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

## 18 Tools

Connect an MCP client. Load a project. The AI agent gets **18 tools**: 8 read, 10 write.

| | Tools |
|---|---|
| **Read** | Analyze, Context, Lookup, Dependencies, Dependants, Blast Radius, File Impact, Resolve Short Name |
| **Write** | Execute, Diagnose, Compile-check, Find References, Find Definition, Find Implementations, Symbol at Position, Documentation, Rename, Format |

Every read-side tool that takes a `symbolId` routes through one resolver. Exact canonical id, truncated method form, and bare short name all resolve to the same answer.

[Full tool reference](docs/TOOLS.md)

---

## Architecture

Hexagonal. Pure domain core with zero dependencies. Language adapters on the left, AI connectors on the right.

```
LEFT SIDE                     CORE                     RIGHT SIDE
(Language Adapters)        (The Pipe)               (AI Connectors)

Roslyn (C#)       ──┐                            ┌──  MCP Server (18 tools)
TypeScript        ──┼→  Domain  →  Application  →┤──  Context Pack Generator
JSON graph        ──┘       ↑                     ├──  Instruction File Generator
                      Analysis (optional)         └──  CLI / CI
```

15 port interfaces, all wired (left side adapters + right side connectors + `ISymbolResolver` for identifier resolution). Boundaries enforced by [architecture invariant tests](tests/Lifeblood.Tests/ArchitectureInvariantTests.cs) and [11 frozen ADRs](docs/ARCHITECTURE_DECISIONS.md).

![Architecture Diagram](docs/architecture-screenshot.png)

[Full architecture](docs/ARCHITECTURE.md) | [Interactive diagram](docs/architecture.html)

---

## Three Languages, One Graph

| Adapter | How it works | Confidence |
|---------|-------------|------------|
| **C# / Roslyn** | Compiler-grade semantic analysis. Cross-module resolution. Bidirectional: analysis + code execution. | Proven |
| **TypeScript** | Standalone Node.js. `ts.createProgram` + `TypeChecker`. | High |
| **Python** | Standalone `ast` module. Zero dependencies. | Structural |
| **Any language** | Output JSON conforming to `schemas/graph.schema.json`. [Adapter guide](docs/ADAPTERS.md). | Varies |

---

## Unity Integration

Lifeblood runs as a sidecar alongside [Unity MCP](https://github.com/CoplayDev/MCPForUnity). All 18 tools available in the Unity Editor via `[McpForUnityTool]` discovery. Runs as a separate process, so no assembly conflicts, no domain reload interference.

[Unity setup guide](docs/UNITY.md)

---

## Dogfooding

Self-analysis: 1,291 symbols, 3,620 edges, 11 modules, 165 types, 0 violations.

Production-verified on a 75-module Unity project: 44,569 symbols, 87,238 edges, 2,439 types. The +9,000-plus edges over the previous baseline come from the v0.6.0 BCL ownership fix (call-graph extraction stops returning null at every System usage in workspaces that ship their own BCL) and the multi-parent GraphBuilder fix (partial types now produce one Contains edge per declaration file).

Seven sessions found [50+ real bugs](docs/DOGFOOD_FINDINGS.md) invisible to unit tests.

---

## Roadmap

- **Community adapters**: contribution guides for [Go](adapters/go/) and [Rust](adapters/rust/). Contract and checklist ready, no implementation code yet.
- **REST / LSP bridge**: expose the graph to IDE extensions and web services.

---

## Documentation

| Page | Description |
|------|-------------|
| [Tools](docs/TOOLS.md) | All 18 tools with descriptions, symbol ID format, incremental usage |
| [MCP Setup](docs/MCP_SETUP.md) | Copy-paste configs for Claude Code, Cursor, VS Code, Claude Desktop, Unity |
| [Unity Integration](docs/UNITY.md) | Sidecar architecture, setup guide, incremental, memory |
| [Architecture](docs/ARCHITECTURE.md) | Hexagonal structure, dependency flow, port interfaces, invariants |
| [Architecture Decisions](docs/ARCHITECTURE_DECISIONS.md) | 11 frozen ADRs |
| [Status](docs/STATUS.md) | Component table, test counts, self-analysis, production stats |
| [Adapters](docs/ADAPTERS.md) | How to build a language adapter (13-item checklist) |
| [Dogfood Findings](docs/DOGFOOD_FINDINGS.md) | 50+ bugs found by self-analysis and reviewer dogfood sessions |

---

## Related

- [LivingDocFramework](https://github.com/user-hash/LivingDocFramework): The methodology that shaped the architecture
- [Roslyn](https://github.com/dotnet/roslyn): The C# compiler platform
- [Case study](https://github.com/user-hash/LivingDocFramework/blob/main/docs/CASE_STUDY.md): The 400k LOC Unity project where we proved these ideas

## License

AGPL v3

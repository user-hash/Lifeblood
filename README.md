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
lifeblood_search query="quantize timing to grid"         → ranked keyword + xmldoc search
lifeblood_invariant_check id="INV-CANONICAL-001"         → query architectural invariants
lifeblood_compile_check filePath="src/MyFile.cs"         → does this file still compile?
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

## 25 Tools

Connect an MCP client. Load a project. The AI agent gets **25 tools**: 15 read, 10 write.

| | Tools |
|---|---|
| **Read** | Analyze, Context, Lookup, Dependencies, Dependants, Blast Radius, File Impact, Resolve Short Name, Search, Dead Code, Partial View, Invariant Check, Authority Report, Port Health, Cycles |
| **Write** | Execute, Diagnose, Compile-check, Find References, Find Definition, Find Implementations, Symbol at Position, Documentation, Rename, Format |

Every read-side tool that takes a `symbolId` routes through one resolver (canonical id, truncated method form, bare short name, kind correction, wrong-namespace fallback). Every read-side response carries a typed truth envelope: truth tier, confidence band, evidence source, staleness, per-tool limitations.

[Full tool reference](docs/TOOLS.md) · [What's new](CHANGELOG.md)

---

## Architecture

Hexagonal. Pure domain core with zero dependencies. Language adapters on the left, AI connectors on the right.

```
LEFT SIDE                     CORE                     RIGHT SIDE
(Language Adapters)        (The Pipe)               (AI Connectors)

Roslyn (C#)       ──┐                            ┌──  MCP Server (25 tools)
TypeScript        ──┼→  Domain  →  Application  →┤──  Context Pack Generator
JSON graph        ──┘       ↑                     ├──  Instruction File Generator
                      Analysis (optional)         └──  CLI / CI
```

26 port interfaces, all wired. Boundaries enforced by [architecture invariant tests](tests/Lifeblood.Tests/ArchitectureInvariantTests.cs), [76 typed invariants under `docs/invariants/`](docs/invariants/INDEX.md) (queryable via `lifeblood_invariant_check`), and [11 frozen ADRs](docs/ARCHITECTURE_DECISIONS.md).

![Architecture Diagram](docs/architecture-screenshot.png)

[Full architecture](docs/ARCHITECTURE.md) · [Interactive diagram](docs/architecture.html)

---

## Three Languages, One Graph

| Adapter | How it works | Confidence |
|---------|-------------|------------|
| **C# / Roslyn** | Compiler-grade semantic analysis. Cross-module resolution. Bidirectional: analysis + code execution. | Proven |
| **TypeScript** | Standalone Node.js. `ts.createProgram` + `TypeChecker`. | High |
| **Python** | Standalone `ast` module. Zero dependencies. | Structural |
| **Any language** | Output JSON conforming to `schemas/graph.schema.json`. | Varies |

[Adapter guide](docs/ADAPTERS.md)

---

## Unity

Lifeblood runs as a sidecar alongside [Unity MCP](https://github.com/CoplayDev/MCPForUnity). All 25 tools available in the Unity Editor via `[McpForUnityTool]` discovery — separate process, no assembly conflicts, no domain-reload interference. `dead_code` recognizes Unity reflection dispatch (MonoBehaviour magic methods, full Editor attribute roster, type-via-child propagation). `compile_check filePath=...` resolves the file's owning compilation and swaps the existing tree, so module-owned files compile-check against their real reference set. `execute` auto-injects DLLs from `Library/ScriptAssemblies/`.

[Unity setup guide](docs/UNITY.md)

---

## Dogfooding

Self-analysis (post G1+G2+G4+R2-3 wave + reviewer-catch UTF-16/incremental fixes): 2,383 symbols, 11,720 edges, 11 modules, 272 types, 0 violations, 0 cycles. **751 tests** across `Lifeblood.Tests`, zero regressions. Lifeblood audits its own architectural invariants via `lifeblood_invariant_check` against `docs/invariants/`: **76 typed invariants across 39 categories**, zero duplicates, zero parse warnings.

Production-verified on a 90-module 400k LOC Unity workspace: 60,775 symbols, 214,097 edges, 122 SCCs. Authority report classifies methods across the full surface and identifies forwarder candidates for any host-with-many-subordinates triage (partial-class hosts, dispatchers, facades, ports). Edge count grew +18% over the prior baseline because enum-member references the dangling-edge filter was silently dropping (R2-3) now resolve. Memory profiles, throughput numbers, and the full dogfood story live in [Status](docs/STATUS.md). 50+ real bugs surfaced through dogfooding — methodology, examples, and per-finding history live in [Dogfood Findings](docs/DOGFOOD_FINDINGS.md).

---

## Roadmap

- **Community adapters**: contribution guides for [Go](adapters/go/) and [Rust](adapters/rust/). Contract and checklist ready, no implementation code yet.
- **REST / LSP bridge**: expose the graph to IDE extensions and web services.

---

## Documentation

| Page | Description |
|------|-------------|
| [Tools](docs/TOOLS.md) | All 25 tools — symbol ID format, incremental usage, dead_code caveats, file-mode compile_check, smart-dynamic context shaping |
| [MCP Setup](docs/MCP_SETUP.md) | Copy-paste configs for Claude Code, Cursor, VS Code, Claude Desktop, Unity |
| [Unity Integration](docs/UNITY.md) | Sidecar architecture, setup, Unity reachability + Editor reflection roster, file-mode compile_check |
| [Architecture](docs/ARCHITECTURE.md) | Hexagonal structure, dependency flow, 26 port interfaces, invariant tree |
| [Architecture Decisions](docs/ARCHITECTURE_DECISIONS.md) | 11 frozen ADRs |
| [Invariants tree](docs/invariants/INDEX.md) | 76 typed architectural invariants, queryable via `lifeblood_invariant_check` |
| [Status](docs/STATUS.md) | Component table, test counts, self-analysis, production stats, memory profiles |
| [Adapters](docs/ADAPTERS.md) | How to build a language adapter (13-item checklist) |
| [Dogfood Findings](docs/DOGFOOD_FINDINGS.md) | 50+ bugs found by self-analysis and reviewer dogfood sessions |
| [CHANGELOG](CHANGELOG.md) | Every release — additions, fixes, known limitations |

---

## Related

- [LivingDocFramework](https://github.com/user-hash/LivingDocFramework) — the methodology that shaped the architecture
- [Roslyn](https://github.com/dotnet/roslyn) — the C# compiler platform
- [Case study](https://github.com/user-hash/LivingDocFramework/blob/main/docs/CASE_STUDY.md) — the 400k LOC Unity project where these ideas were proven

---

## License

AGPL v3

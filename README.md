# Lifeblood

Compiler-grade code intelligence for AI agents over MCP.

Lifeblood loads a C# / Unity workspace through Roslyn or a C codebase through the beta libclang adapter, builds a persistent semantic graph with stable symbol IDs, and exposes it to AI agents over MCP, so an agent can ask *"what calls this?"*, *"what breaks if I rename it?"*, *"does this edited file still compile?"*, *"which architecture invariant declares this rule?"* and get verified answers instead of grep guesses. Every read-side response carries a truth envelope (evidence tier, confidence band, staleness) so the agent knows when an answer is Proven, Advisory, or Speculative.

Roslyn is the C# engine. libclang is the C engine (beta). TypeScript and Python ship as standalone JSON-emitting adapters. Lifeblood is the layer around them: persistent project graph, 29 MCP tools, Unity-aware reachability, incremental re-analysis, CI-wireable export and verify commands.

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
lifeblood export  --project /path/to/your/project --out graph.json
lifeblood verify  --incremental --project /path/to/your/project
```

`export --out` writes the graph JSON to a file directly (preferred over shell redirection on Windows PowerShell, where `>` defaults to UTF-16-LE-with-BOM and breaks JSON re-import without the `INV-JSON-IMPORT-BOM-001` BOM-aware reader). `verify --incremental` runs full + incremental analyze in one process and asserts `summary.edges` are identical (`INV-INCREMENTAL-XREF-001`); non-zero exit on drift makes it CI-wireable.

### Build from source

```bash
git clone https://github.com/user-hash/Lifeblood.git
cd Lifeblood
dotnet build
dotnet test
```

---

## 29 Tools

```
Roslyn (C#)    ──┐                              ┌──  Execute code against project types
libclang (C)   ──┤  ┌────────────────────────┐  ├──  Diagnose / compile-check
TypeScript     ──┼→ │    Semantic Graph      │ →┤──  Find references / rename / format
Python         ──┤  │  (symbols / edges /    │  ├──  Blast radius / file impact
JSON graph     ──┤  │   evidence / trust)    │  └──  Context packs / architecture rules
  community    ──┘  └────────────────────────┘
  adapters
```

Connect an MCP client. Load a project. The AI agent gets **29 tools**: 17 read, 12 write.

| | Tools |
|---|---|
| **Read** | Analyze, Context, Lookup, Dependencies, Dependants, Blast Radius, File Impact, Resolve Short Name, Resolve Member, Search, Dead Code, Partial View, Invariant Check, Authority Report, Port Health, Cycles, Test Impact |
| **Write** | Execute, Diagnose, Compile-check, Enum Coverage, Static Tables, Find References, Find Definition, Find Implementations, Symbol at Position, Documentation, Rename, Format |

Every read-side tool that takes a `symbolId` routes through one resolver (canonical id, truncated method form, bare short name, kind correction, wrong-namespace fallback). Every read-side response carries a typed truth envelope: truth tier, confidence band, evidence source, staleness, per-tool limitations.

[Full tool reference](docs/TOOLS.md) · [What's new](CHANGELOG.md)

---

## Architecture

Hexagonal. Pure domain core with zero dependencies. Language adapters on the left, AI connectors on the right.

```
LEFT SIDE                     CORE                     RIGHT SIDE
(Language Adapters)        (The Pipe)               (AI Connectors)

Roslyn (C#)       ──┐                            ┌──  MCP Server (29 tools)
libclang (C)      ──┤                            ├──  Context Pack Generator
TypeScript        ──┼→  Domain  →  Application  →┤──  Instruction File Generator
Python            ──┤       ↑                     ├──  CLI / CI
JSON graph        ──┘    Analysis (optional)      └
```

26 port interfaces, all wired. Boundaries enforced by [architecture invariant tests](tests/Lifeblood.Tests/ArchitectureInvariantTests.cs), [101 typed invariants across 63 categories under `docs/invariants/`](docs/invariants/INDEX.md) (queryable via `lifeblood_invariant_check`), and [11 frozen ADRs](docs/ARCHITECTURE_DECISIONS.md).

![Architecture Diagram](docs/architecture-screenshot.png)

[Full architecture](docs/ARCHITECTURE.md) · [Interactive diagram](docs/architecture.html)

---

## Four Languages, One Graph

| Adapter | How it works | Confidence |
|---------|-------------|------------|
| **C# / Roslyn** | Compiler-grade semantic analysis. Cross-module resolution. Bidirectional: analysis plus code execution. | Proven |
| **C / libclang** | Beta (v0.7.7). Reads `compile_commands.json`, parses each translation unit through libclang, emits Lifeblood-shape `graph.json`. Surfaces translation units, functions, globals, fields, type shells, enum members, macros, includes, callback-table rows and cells. Partial-parse tolerant. | Beta / High on covered C fixtures |
| **TypeScript** | Standalone Node.js. `ts.createProgram` plus `TypeChecker`. | High |
| **Python** | Standalone `ast` module. Zero dependencies. | Structural |
| **Any language** | Output JSON conforming to `schemas/graph.schema.json`. | Varies |

[Adapter guide](docs/ADAPTERS.md) · [Native C capability](docs/NATIVE_CLANG.md)

---

## Unity

Lifeblood runs as a sidecar alongside [Unity MCP](https://github.com/CoplayDev/MCPForUnity). All 29 tools available in the Unity Editor via `[McpForUnityTool]` discovery — separate process, no assembly conflicts, no domain-reload interference. `dead_code` recognizes Unity reflection dispatch (MonoBehaviour magic methods, full Editor attribute roster, type-via-child propagation). `compile_check filePath=...` resolves the file's owning compilation and swaps the existing tree, so module-owned files compile-check against their real reference set. `execute` auto-injects DLLs from `Library/ScriptAssemblies/`.

[Unity setup guide](docs/UNITY.md)

---

## Dogfooding

Self-analysis (post-v0.7.7): 3,278 symbols, 16,973 edges, 11 modules, 350 types, 0 violations, 0 cycles. **1011 tests, zero skipped** across `Lifeblood.Tests`, zero regressions. Lifeblood audits its own architectural invariants via `lifeblood_invariant_check` against `docs/invariants/`: **101 typed invariants across 63 categories**, zero duplicates, zero parse warnings.

Production-verified on a 90-module 400k LOC Unity workspace: 62,134 symbols, 219,548 edges, 123 SCCs. Authority report classifies methods across the full surface and identifies forwarder candidates for any host-with-many-subordinates triage (partial-class hosts, dispatchers, facades, ports). Edge count grew +18% over the prior baseline because enum-member references the dangling-edge filter was silently dropping (R2-3) now resolve. Memory profiles, throughput numbers, and the full dogfood story live in [Status](docs/STATUS.md). 50+ real bugs surfaced through dogfooding — methodology, examples, and per-finding history live in [Dogfood Findings](docs/DOGFOOD_FINDINGS.md).

---

## Roadmap

- **Native Clang maturity**: move from focused-slice scout to whole-build coverage on FFmpeg-class C codebases (WSL + bear, MSYS2 + bear, or a project-specific compile-database generator). Tracked in [`docs/plans/native-clang-adapter-masterplan-2026-05-16.md`](docs/plans/native-clang-adapter-masterplan-2026-05-16.md).
- **C++ over libclang**: extend the native adapter past C to C++. Same boundary, additional Clang AST coverage (templates, classes, member functions).
- **Community adapters**: contribution guides for [Go](adapters/go/) and [Rust](adapters/rust/). Contract and checklist ready, no implementation code yet.
- **REST / LSP bridge**: expose the graph to IDE extensions and web services.

---

## Documentation

| Page | Description |
|------|-------------|
| [Tools](docs/TOOLS.md) | All 29 tools — symbol ID format, incremental usage, dead_code caveats, file-mode compile_check, smart-dynamic context shaping |
| [MCP Setup](docs/MCP_SETUP.md) | Copy-paste configs for Claude Code, Cursor, VS Code, Claude Desktop, Unity |
| [Unity Integration](docs/UNITY.md) | Sidecar architecture, setup, Unity reachability + Editor reflection roster, file-mode compile_check |
| [Architecture](docs/ARCHITECTURE.md) | Hexagonal structure, dependency flow, 26 port interfaces, invariant tree |
| [Architecture Decisions](docs/ARCHITECTURE_DECISIONS.md) | 11 frozen ADRs |
| [Invariants tree](docs/invariants/INDEX.md) | 101 typed architectural invariants, queryable via `lifeblood_invariant_check` |
| [Status](docs/STATUS.md) | Component table, test counts, self-analysis, production stats, memory profiles |
| [Adapters](docs/ADAPTERS.md) | How to build a language adapter (13-item checklist) |
| [Native C support](docs/NATIVE_CLANG.md) | libclang-based C extractor: scope, build, fixtures, FFmpeg scout, what works, what is deferred |
| [Release checklist](docs/RELEASE.md) | Eternal pre-tag gate: tests green, CHANGELOG link refs, doc anchors current, NuGet single-use-key publish flow |
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

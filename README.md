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
lifeblood_invariant_check id="INV-CANONICAL-001"         → query architectural invariants from CLAUDE.md
lifeblood_compile_check code="var x = 1 + 1;"            → snippet compiles, auto-wraps for library modules
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
| **Read** | Analyze, Context, Lookup, Dependencies, Dependants, Blast Radius, File Impact, Resolve Short Name, Search, Dead Code¹, Partial View, Invariant Check, Authority Report, Port Health, Cycles |
| **Write** | Execute, Diagnose, Compile-check, Find References, Find Definition, Find Implementations, Symbol at Position, Documentation, Rename, Format |

¹ `lifeblood_dead_code` is experimental / advisory. The reachability port (`IUnityReachabilityProvider`, v0.6.7) cuts MonoBehaviour magic-method false positives by 97% on real Unity workspaces by walking the type's inheritance chain via `Properties["baseType"]` (set by the extractor) and matching against the Unity message-receiver bases. Dogfood vs DAWG (87 modules, 53,882 symbols): 1095 dead-code findings reduced to 729 (-33%), 378 magic-method FPs reduced to 13. Remaining false positives are structural (UI Toolkit `VisualElement` subclasses, audio callbacks on non-MonoBehaviour bases, reflection-based dispatch). See `INV-UNITY-001` and `INV-DEADCODE-001` in [CLAUDE.md](CLAUDE.md).

Every read-side tool that takes a `symbolId` routes through one resolver. Exact canonical id, truncated method form, bare short name, kind correction (`method:NS.Type.X` resolves to a property/field/event named `X` when no method exists; v0.6.6 / `INV-RESOLVER-006`), and wrong-namespace-correct-short-name (v0.6.3 / `INV-RESOLVER-005`) all resolve to the same answer.

Every read-side response carries a typed truth envelope (v0.6.7 / `INV-ENVELOPE-001`): truth tier (Semantic / Derived / Heuristic / Inferred), confidence band (Proven / Advisory / Speculative), evidence-source string, wall-clock staleness in seconds, files-changed-since-analyze count, per-tool documented limitations.

**New since v0.6.5 (P1..P6 of the DAWG-dogfood plan):**
- **Truth envelope** on every read-side response (P2).
- **Unity-aware reachability** + asmdef-edit incremental detection (P3).
- **Execute robustness**: Unity DLL probe (`Library/ScriptAssemblies`, `Library/Bee/artifacts`, `Library/PackageCache`), `targetProfile` for runtime ref-pack selection (`host` / `net-standard-2.1` / `net-6.0`), sandbox introspection helpers `Help` / `SymbolsOfKind(string)` / `EdgesOfKind(string)` (P4).
- **Authority + port health + cycles tools** plus a forwarder classifier on every method (`PureForwarder` / `ThinWrapper` / `RealLogic`) (P5).
- **Resolver kind correction**, file-scoped diagnose, `compile_check` from disk, `blast_radius` summarize mode + direct-vs-transitive split (P1).

[Full tool reference](docs/TOOLS.md)

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

26 port interfaces, all wired (left-side adapters + right-side connectors + `ISymbolResolver` for identifier resolution + `IResponseDecorator` for truth envelope + `IInvariantProvider` for CLAUDE.md invariant introspection + `IUnityReachabilityProvider` for runtime-dispatch reachability + `IRuntimeAssemblyResolver` for execute-time DLL probing + `IAuthorityReporter` for type-level authority reports). Boundaries enforced by [architecture invariant tests](tests/Lifeblood.Tests/ArchitectureInvariantTests.cs), 70 typed invariants in [CLAUDE.md](CLAUDE.md) (queryable via `lifeblood_invariant_check`), and [11 frozen ADRs](docs/ARCHITECTURE_DECISIONS.md).

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

Lifeblood runs as a sidecar alongside [Unity MCP](https://github.com/CoplayDev/MCPForUnity). All 25 tools available in the Unity Editor via `[McpForUnityTool]` discovery. Runs as a separate process, so no assembly conflicts, no domain reload interference.

`lifeblood_dead_code` recognizes Unity message-dispatch patterns automatically: `MonoBehaviour` magic methods (`Awake`, `Update`, `OnTriggerEnter`, full Unity catalog), entrypoint attributes (`RuntimeInitializeOnLoadMethod`, `MenuItem`, `ContextMenu`, `CustomEditor`, ...), transitive bases (`ScriptableObject`, `Editor`, `EditorWindow`, `StateMachineBehaviour`). `lifeblood_execute` auto-injects DLLs from `Library/ScriptAssemblies/`, `Library/Bee/artifacts/`, and `Library/PackageCache/` so scripts can touch `UnityEngine` types without Unity being open.

[Unity setup guide](docs/UNITY.md)

---

## Dogfooding

Self-analysis (MCP, post P1..P6): 2,132 symbols, 9,908 edges, 11 modules, 262 types, 0 violations, 0 cycles. The DAWG-dogfood plan landed in six phases (P1..P6); each phase shipped with a repeatable end-to-end MCP smoke harness (`smoke-mcp-p1-dogfood.ps1` ... `smoke-mcp-p5-dogfood.ps1`). 632 tests across `Lifeblood.Tests`, zero regressions across the plan.

Lifeblood audits its own architectural invariants via `lifeblood_invariant_check` against [CLAUDE.md](CLAUDE.md): 70 typed invariants, zero duplicate ids, zero parse warnings.

Production-verified on a 87-module 400k LOC Unity workspace (DAWG): 53,882 symbols, 180,814 edges. Authority report classifies 18,985 methods, identifies 3,367 `PureForwarder` candidates for ABG-extraction triage. Cycles tool surfaces 117 strongly-connected components in the existing dependency graph. Same workspace, two different paths, two different memory profiles. Both are correct, both are by design, both come from the native `usage` field on every `lifeblood_analyze` response.

| Path | Wall | CPU total | CPU % (1 core) | Peak working set | Use when |
|---|---|---|---|---|---|
| **CLI** (streaming, compilations released) | ~14 s | ~23 s | ~150% | ~570 MB | One-shot analyze, rules check, graph export. |
| **MCP** (compilations retained) | ~32 s | ~58 s | ~180% | ~2,800 MB | Interactive session with write-side tools (`execute`, `find_references`, `rename`, ...). |

The MCP retained profile sits around 4x the CLI streaming profile because the write-side tools need the loaded workspace in memory to answer follow-up queries. Pass `readOnly: true` to `lifeblood_analyze` to drop MCP back to the streaming profile in exchange for no write-side tools. Both measured on AMD Ryzen 9 5950X (16 cores / 32 threads). Exact numbers are surfaced live on every `lifeblood_analyze` response via the `usage` field so they can be cited deterministically.

Multiple dogfood sessions found [50+ real bugs](docs/DOGFOOD_FINDINGS.md) invisible to unit tests, including the v0.6.3 resolver wrong-namespace fallback (`INV-RESOLVER-005`), the v0.6.6 resolver kind-correction (`INV-RESOLVER-006`), the v0.6.7 truth envelope (`INV-ENVELOPE-001`), the v0.6.7 Unity reachability port (`INV-UNITY-001`, 97% MonoBehaviour-FP reduction on DAWG), the asmdef-edit incremental detection (`INV-UNITY-002`), the `compile_check` library-module auto-wrap, and the `lifeblood_dead_code` false-positive classes tracked under `INV-DEADCODE-001`.

---

## Roadmap

- **Community adapters**: contribution guides for [Go](adapters/go/) and [Rust](adapters/rust/). Contract and checklist ready, no implementation code yet.
- **REST / LSP bridge**: expose the graph to IDE extensions and web services.

---

## Documentation

| Page | Description |
|------|-------------|
| [Tools](docs/TOOLS.md) | All 25 tools with descriptions, symbol ID format, incremental usage, dead_code caveats |
| [MCP Setup](docs/MCP_SETUP.md) | Copy-paste configs for Claude Code, Cursor, VS Code, Claude Desktop, Unity |
| [Unity Integration](docs/UNITY.md) | Sidecar architecture, setup guide, incremental, memory, Unity reachability port |
| [Architecture](docs/ARCHITECTURE.md) | Hexagonal structure, dependency flow, 26 port interfaces, invariants |
| [Architecture Decisions](docs/ARCHITECTURE_DECISIONS.md) | 11 frozen ADRs |
| [Invariants](CLAUDE.md) | 70 typed architectural invariants, queryable via `lifeblood_invariant_check` |
| [Status](docs/STATUS.md) | Component table, test counts (632), self-analysis, production stats |
| [Adapters](docs/ADAPTERS.md) | How to build a language adapter (13-item checklist) |
| [Dogfood Findings](docs/DOGFOOD_FINDINGS.md) | 50+ bugs found by self-analysis and reviewer dogfood sessions |
| [CHANGELOG](CHANGELOG.md) | Every release: additions, fixes, known limitations |

---

## Related

- [LivingDocFramework](https://github.com/user-hash/LivingDocFramework): The methodology that shaped the architecture
- [Roslyn](https://github.com/dotnet/roslyn): The C# compiler platform
- [Case study](https://github.com/user-hash/LivingDocFramework/blob/main/docs/CASE_STUDY.md): The 400k LOC Unity project where we proved these ideas

## License

AGPL v3

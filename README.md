# Lifeblood

Compiler truth in, AI context out.

```
LEFT VEINS                    HEART                   RIGHT VEINS
(Language Adapters)        (The Core)              (AI Connectors)

Roslyn (C#)       ──┐                           ┌──  MCP Server
TS Compiler       ──┤  ┌─────────────────────┐  ├──  Context Pack (JSON)
go/types          ──┼→ │   Semantic Graph     │ →┤──  CLAUDE.md generator
Python ast+mypy   ──┤  │   (universal model)  │  ├──  CLI / CI
rust-analyzer     ──┤  └─────────────────────┘  └──  JSON / REST
Java JDT          ──┘
```

Lifeblood is a hexagonal framework that pipes compiler-level semantics into AI tools. Language adapters feed semantic truth in. AI connectors consume structured context out. The core normalizes everything in between — pure, zero dependencies on either side.

We build the framework and one reference implementation (C# + Roslyn). The community builds the rest.

Born from shipping a [400k LOC Unity project](https://github.com/user-hash/LivingDocFramework/blob/main/docs/CASE_STUDY.md) with AI assistance and realizing that AI agents write code but rarely verify the wiring.

---

## Why This Matters for AI-Assisted Development

AI agents build backends but don't wire frontends. They add methods but don't update callers. They refactor types but miss downstream consumers. **They write code — they don't verify the graph.**

Lifeblood catches exactly that. Run it on any codebase and get:

- **Zero dangling references** — every edge points to a real symbol
- **Zero orphaned symbols** — every node is reachable in the containment tree
- **Zero duplicate edges** — no redundant dependency claims
- **Complete evidence** — every relationship carries proof of how it was discovered
- **Honest capabilities** — adapters declare what they can actually prove, not what they wish

We dogfood Lifeblood on itself. Every push, the framework analyzes its own 9 modules, 495 symbols, and 661 edges — and the graph comes back clean.

```
$ lifeblood analyze --project . --rules packs/lifeblood/rules.json
Symbols: 495
Edges:   661
Modules: 9
Types:   80
```

Zero violations. Zero dangling edges. Zero duplicates. [Full dogfood findings →](docs/DOGFOOD_FINDINGS.md)

---

## What Semantic Access Gives AI Agents

| Capability | Without Lifeblood | With Lifeblood |
|------------|-------------------|----------------|
| Find references | Grep for text | Real callers, real dependants, with proof |
| Wiring verification | Hope it's connected | Proven: zero dangling edges, zero orphans |
| Architecture | Hope nobody broke a boundary | Compiler-level boundary enforcement with rules |
| Blast radius | No idea | Exact list of what breaks if you change this |
| Context loading | Read random files | Ranked reading order — stable core first |
| Coupling | Guess from file structure | Robert Martin metrics: fan-in, fan-out, instability |
| Cycles | Manual inspection | Tarjan SCC detection on the real dependency graph |

---

## Quick Start

```bash
git clone https://github.com/user-hash/Lifeblood.git
cd Lifeblood
dotnet build
```

### Analyze a project
```bash
lifeblood analyze --project ./my-app
lifeblood analyze --project ./my-app --rules packs/hexagonal/rules.json
```

### Generate AI context pack (JSON)
```bash
lifeblood context --project ./my-app
lifeblood context --project ./my-app --format md    # markdown instruction file
```

### Export the semantic graph
```bash
lifeblood export --project ./my-app > graph.json
```

### Analyze from any language via JSON
```bash
python my-parser.py ./project > graph.json
lifeblood analyze --graph graph.json --rules rules.json
```

---

## Architecture

```
Lifeblood.Domain                Pure graph model. Zero deps. The absolute core.
Lifeblood.Application           Ports + use cases. Depends only on Domain.
Lifeblood.Adapters.CSharp      Roslyn reference adapter. Left side.
Lifeblood.Adapters.JsonGraph    Universal JSON protocol adapter. Left side.
Lifeblood.Connectors.ContextPack  Context pack + instruction file generator. Right side.
Lifeblood.Connectors.Mcp       MCP graph provider for AI agents. Right side.
Lifeblood.Analysis              Optional: coupling, blast radius, cycles, tiers, rules.
Lifeblood.CLI                   Composition root. Wires left side to right side.
```

Domain never references Application. Application never references Adapters or Connectors. Adapters never reference other Adapters. Connectors never reference Adapters. All enforced by [architecture invariant tests](tests/Lifeblood.Tests/ArchitectureInvariantTests.cs) and [9 frozen ADRs](docs/ARCHITECTURE_DECISIONS.md).

[Full architecture →](docs/ARCHITECTURE.md) · [How to build an adapter →](docs/ADAPTERS.md)

---

## How Languages Plug In (Left Side)

**Option A: In-process C# adapter.** Implement `IWorkspaceAnalyzer`. Full pipeline integration.

**Option B: External process + JSON.** Write a parser in any language. Output `graph.json` conforming to `schemas/graph.schema.json`. Lifeblood reads it. No C# needed.

**Option C: AI-assisted porting.** Roslyn is open source. The concepts exist in every language toolchain. AI agents can help port adapter implementations. We provide the contracts and golden repo test fixtures.

---

## How AI Tools Plug In (Right Side)

**Context Pack** (the killer feature) — Structured JSON with high-value files, module boundaries, reading order, hotspots, dependency matrix, and active violations. Feed it to any AI tool.

**Instruction File Generator** — Analyze a codebase, produce a CLAUDE.md / AGENTS.md section with architecture boundaries, dependency rules, and high-value files.

**MCP Graph Provider** — Symbol lookup, dependencies, dependants, blast radius queries. Port interface implemented; MCP server hosting planned.

---

## Status

Dogfood-verified. Framework hardening complete. 80 tests, CI green.

| Assembly | State |
|----------|-------|
| **Lifeblood.Domain** | Implemented — graph model, GraphBuilder, GraphValidator, evidence, capabilities |
| **Lifeblood.Application** | Implemented — all port interfaces, use cases |
| **Lifeblood.Adapters.CSharp** | Implemented — Roslyn workspace analyzer, module discovery, symbol/edge extractors |
| **Lifeblood.Adapters.JsonGraph** | Implemented — JSON import/export with round-trip fidelity |
| **Lifeblood.Connectors.ContextPack** | Implemented — context pack, instruction file, reading order |
| **Lifeblood.Connectors.Mcp** | Implemented — graph provider with blast radius delegation |
| **Lifeblood.Analysis** | Implemented — coupling, rules, blast radius, cycles, tiers |
| **Lifeblood.CLI** | Implemented — analyze, context, export with rules support |
| **Lifeblood.Tests** | 80 tests — extractors, golden repos, round-trip, invariants |

**Rule packs:** [hexagonal](packs/hexagonal/rules.json), [clean-architecture](packs/clean-architecture/rules.json), [lifeblood](packs/lifeblood/rules.json) (self-validating)

**Next:** Cross-module Roslyn resolution, MCP server hosting, TypeScript adapter

---

## Related

- [LivingDocFramework](https://github.com/user-hash/LivingDocFramework) — The methodology that shaped this framework
- [Roslyn](https://github.com/dotnet/roslyn) — The C#/.NET compiler platform
- [DAWG](https://dawgtools.org) | [itch.io](https://dawg-tools.itch.io/) — The 400k LOC project where we proved these ideas

## License

AGPL v3

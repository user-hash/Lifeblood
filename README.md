# Lifeblood

Pipe compiler-level semantics into any AI agent. For any language.

```
LEFT SIDE                     CORE                    RIGHT SIDE
(Language Adapters)        (The Pipe)              (AI Connectors)

Roslyn (C#)       ──┐                           ┌──  MCP Server
TS Compiler       ──┤  ┌─────────────────────┐  ├──  CLAUDE.md generator
go/types          ──┼→ │   Semantic Graph     │ →┤──  Context Pack API
Python ast+mypy   ──┤  │   (universal model)  │  ├──  LSP Bridge
rust-analyzer     ──┤  └─────────────────────┘  ├──  CLI / CI
Java JDT          ──┘                           └──  JSON / REST
```

Lifeblood is a hexagonal framework. Language adapters plug into the left side and feed semantic data in. AI connectors plug into the right side and consume the graph. The core normalizes everything in between. Pure. Zero dependencies on either side.

We do not build Roslyn-grade adapters for every language. We build the framework and one reference implementation (C# + Roslyn). The community builds the rest. Roslyn is open source. AI can help port concepts to other languages.

Born from shipping a [400k LOC Unity project](https://github.com/user-hash/LivingDocFramework/blob/main/docs/CASE_STUDY.md) with AI assistance and realizing that [Roslyn](https://github.com/dotnet/roslyn) is the most underused tool in AI-assisted development.

---

## What Semantic Access Gives AI Agents

| Capability | Without Lifeblood | With Lifeblood |
|------------|-------------------|----------------|
| Find references | Grep for text | Real callers, real dependants, with proof |
| Flow checking | Read code and guess | Trace values through the full call chain |
| Math verification | Trust the AI | Verify calculations, argument ordering, formulas |
| Architecture | Hope nobody broke a boundary | Compiler-level boundary enforcement |
| Dead code | "I think this is unused" | Proven reachability from entry points |
| Blast radius | No idea | Exact list of what breaks if you change this |
| Context loading | Read random files | Ranked reading order by importance |

We found a case where the precise math function was both faster AND 17% more accurate than the approximation an AI agent chose. Roslyn sees the semantic contract, not just the syntax.

---

## How Languages Plug In (Left Side)

**Option A: In-process C# adapter.** Implement `IWorkspaceAnalyzer`. Full pipeline integration.

**Option B: External process + JSON.** Write a parser in any language. Output `graph.json` conforming to `schemas/graph.schema.json`. Lifeblood reads it. No C# needed.

```bash
python lifeblood-python ./my-project > graph.json
lifeblood analyze --graph graph.json
```

**Option C: AI-assisted porting.** Roslyn is open source. The concepts exist in every language toolchain. AI agents can help port adapter implementations.

---

## How AI Tools Plug In (Right Side)

**MCP Server** — Like our Unity MCP setup but for any codebase. AI agent calls tools:
```
lifeblood-mcp:symbol-lookup     "What is AuthService?"
lifeblood-mcp:blast-radius      "What breaks if I change IUserRepository?"
lifeblood-mcp:context-pack      "Give me context for src/auth/"
```

**Context Pack Generator** — Produces an AI-consumable JSON with high-value files, boundaries, reading order, hotspots, blast radius map.

**CLAUDE.md Generator** — Analyze a codebase, produce architecture sections for your AI instruction file.

---

## Architecture

```
Lifeblood.Domain              Pure graph model. Zero deps. The absolute core.
Lifeblood.Application          Ports + use cases. Depends only on Domain.
Lifeblood.Adapters.CSharp     Reference adapter. Roslyn. Left side.
Lifeblood.Adapters.JsonGraph   Universal protocol adapter. Left side.
Lifeblood.Connectors.Mcp      MCP server for AI agents. Right side.
Lifeblood.Connectors.Context   Context pack + CLAUDE.md generator. Right side.
Lifeblood.Analysis             Optional analyzers (coupling, blast radius, tiers).
Lifeblood.Reporters.*          JSON, HTML, SARIF output.
Lifeblood.CLI                  Composition root. Wires everything.
```

Domain never references Application. Application never references Adapters or Connectors. Both sides plug into Application ports. [Full architecture](docs/ARCHITECTURE.md)

---

## Status

Early stage. Architecture defined, core implemented, contracts hardened.

| Assembly | State |
|----------|-------|
| **Lifeblood.Domain** | Implemented. Graph model, GraphBuilder, GraphValidator, rules, results, capabilities. |
| **Lifeblood.Application** | Implemented. All port interfaces (left/right/graphIO/analysis/output/infrastructure), use cases. |
| **Lifeblood.Analysis** | Implemented. CouplingAnalyzer (Robert Martin metrics), RuleValidator (architecture rules). |
| **Lifeblood.Tests** | Implemented. xUnit test suite covering GraphBuilder, GraphValidator, CouplingAnalyzer, RuleValidator. |
| **Lifeblood.Adapters.CSharp** | Scaffold. Project exists, references Roslyn. No adapter code yet. |
| **Lifeblood.Reporters** | Scaffold. Project exists. No reporter code yet. |
| **Lifeblood.CLI** | Scaffold. Prints usage. Does not execute commands yet. |
| Connectors (MCP, Context) | Not started. Port interfaces defined in Application. |

**Schemas:** `graph.schema.json` (with evidence), `rules.schema.json`. Rule packs: hexagonal, clean-architecture.

**Next:** C# Roslyn adapter (reference implementation), CLI vertical slice, context pack connector.

---

## Getting Started

```bash
git clone https://github.com/user-hash/Lifeblood.git
cd Lifeblood
dotnet build
```

Read [CLAUDE.md](CLAUDE.md) for architecture rules and invariants.
Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the hexagonal design.
Read [docs/ADAPTERS.md](docs/ADAPTERS.md) to build a language adapter.

---

## Related

- [LivingDocFramework](https://github.com/user-hash/LivingDocFramework) — The methodology
- [Roslyn](https://github.com/dotnet/roslyn) — The C#/.NET compiler platform that inspired everything
- [DAWG](https://dawgtools.org) | [itch.io](https://dawg-tools.itch.io/) — The 400k LOC project where we proved these ideas

## License

AGPL v3

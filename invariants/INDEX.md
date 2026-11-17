# Lifeblood Invariants Tree

Architectural invariants live in this tree, one file per domain. The
project root `CLAUDE.md` keeps only the architecture overview, dependency
rules, naming conventions, and pointers — every formally-numbered
`INV-XXX-NNN` rule lives here.

Read the specific file before touching that area. The tree-walker
behind `lifeblood_invariant_check` aggregates every `*.md` under this
directory plus `<root>/CLAUDE.md` and `<root>/AGENTS.md`, so every
invariant in the tree is callable by id from the MCP tool.

## Tree

| File | Domain |
|------|--------|
| [architecture.md](architecture.md) | Hexagonal core: Domain purity, Application layer, Graph model, Adapters, Connectors, Analysis, Testing, Pipeline, ScriptHost isolation, composition-root allowlist |
| [resolver.md](resolver.md) | Identifier resolution (`ISymbolResolver` port, partial-type unification, kind-correction) |
| [csharp-adapter.md](csharp-adapter.md) | C# adapter: canonical symbol IDs, semantic view, BCL ownership, csproj compilation facts |
| [pipeline.md](pipeline.md) | Streaming compilation, file-edge derivation, incremental analyze |
| [usage.md](usage.md) | Analyze-pipeline usage reporting (timings, memory, GC, phases) |
| [mcp-protocol.md](mcp-protocol.md) | MCP wire protocol, tool registry, response envelope |
| [tools.md](tools.md) | Tool-specific invariants: dead-code, invariant introspection, authority report, forwarder classifier, execute robustness, Unity reachability, find-implementations |
| [governance.md](governance.md) | Doc/repo discipline: STATUS counts, CHANGELOG link refs, test patterns |

## Authoring shapes (all five recognised by the parser)

```
Shape A:  - **INV-DOMAIN-001**: body...
Shape B:  - **INV-CANONICAL-001. Title sentence.** Body paragraph...
Shape C:  **INV-WORK-001: Title.** body...                    (no bullet)
Shape D:  - **INV-DSP-012** (v1.1.566): body...               (version tag)
Shape E:  - **INV-ANIM-1:** body...                            (colon inside bold)
```

ID format: `INV-<CATEGORY>-<N>` where category is one or more uppercase
segments separated by dashes, and N is a number (1+ digits). Example:
`INV-USAGE-PROBE-001` parses with category `USAGE-PROBE`.

## Adding a new invariant

1. Pick the file that already covers the domain. Create a new file only
   if the rule starts a fresh domain not represented above.
2. Use shape A or B by default; shape D when the rule is tied to a
   specific shipping version.
3. Author the rule body inline. Don't write summaries — every
   `lifeblood_invariant_check id:<INV-...>` query returns the body
   verbatim, so it's the only place the rule lives.
4. If a new file is added, add a row to the table above and a row in
   the project-root `CLAUDE.md` invariants pointer.

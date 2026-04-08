# Contributing to Lifeblood

Lifeblood is an open hexagonal framework. Contributions plug into well-defined roles.

## Where Your Work Fits

| Role | What you build | Where |
|------|---------------|-------|
| **Language adapter** | Left-side adapter for your language | `src/Lifeblood.Adapters.{Language}/` or external JSON |
| **AI connector** | Right-side connector for your AI tool | `src/Lifeblood.Connectors.{Name}/` |
| **Pack author** | Architecture rule packs | `packs/{name}/` |
| **Analyzer** | Analysis pass (stateless, graph in, result out) | `src/Lifeblood.Analysis/` |
| **Core** | Domain model, evidence, trust | `src/Lifeblood.Domain/` |
| **Contract** | Port interfaces, use cases | `src/Lifeblood.Application/` |

## Building a Language Adapter

See [docs/ADAPTERS.md](docs/ADAPTERS.md) for the full guide. Two paths:

1. **JSON adapter** (any language): output `graph.json` conforming to `schemas/graph.schema.json`
2. **C# adapter** (in-process): implement `IWorkspaceAnalyzer`

Start with the Syntax tier (files and imports). It is already useful.

## Architecture Rules

These are non-negotiable and enforced by tests:

- `Lifeblood.Domain` has zero external dependencies
- `Lifeblood.Application` depends only on Domain
- Adapters and Connectors depend inward on Application ports, never on each other
- The graph is read-only after construction. Analyzers return separate results.
- Every adapter declares capabilities honestly via `AdapterCapability`
- Every edge carries `Evidence` (kind, adapter, confidence level)
- Output is deterministic. Same input produces the same graph.

See [docs/ARCHITECTURE_DECISIONS.md](docs/ARCHITECTURE_DECISIONS.md) for the 11 frozen ADRs.

## Code Style

- .NET 8, C# 12, nullable enabled
- Conventional commits: `feat:`, `fix:`, `refactor:`, `docs:`
- Tests in `tests/Lifeblood.Tests/` using xUnit

## Before Submitting

1. All existing tests pass: `dotnet test`
2. New code has tests
3. `GraphValidator.Validate()` returns no errors for any graph you produce
4. Architecture invariants are not violated (check CLAUDE.md)
5. Dogfood passes:

```bash
dotnet run --project src/Lifeblood.CLI -- analyze --project . --rules packs/lifeblood/rules.json
```

This should report zero violations and zero graph errors. If your change breaks the framework's ability to analyze itself, something is wrong.

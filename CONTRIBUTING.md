# Contributing to Lifeblood

Lifeblood is an open hexagonal framework. Contributions plug into well-defined roles.

## Where Your Work Fits

| Role | What you build | Where |
|------|---------------|-------|
| **Language adapter** | Left-side vein for your language | `src/Lifeblood.Adapters.{Language}/` or external JSON |
| **AI connector** | Right-side vein for your AI tool | `src/Lifeblood.Connectors.{Name}/` |
| **Pack author** | Architecture/onboarding/refactor packs | `packs/{name}/` |
| **Analyzer** | Optional analysis pass | `src/Lifeblood.Analysis/` |
| **Core** | Domain model, evidence, trust | `src/Lifeblood.Domain/` |
| **Contract** | Port interfaces, use cases | `src/Lifeblood.Application/` |

## Building a Language Adapter

See [docs/ADAPTERS.md](docs/ADAPTERS.md) for full details. Two paths:

1. **JSON adapter** (any language): output `graph.json` conforming to `schemas/graph.schema.json`
2. **C# adapter** (in-process): implement `IWorkspaceAnalyzer`

Start with Syntax tier (files + imports). It's already useful.

## Architecture Rules

These are non-negotiable:

- `Lifeblood.Domain` has **zero** external dependencies
- `Lifeblood.Application` depends only on Domain
- Adapters and Connectors depend inward (on Application ports), never on each other
- The graph is **read-only** after construction — analyzers return separate results
- Every adapter declares capabilities **honestly** via `AdapterCapability`
- Every edge carries `Evidence` (kind, adapter, confidence)

## Code Style

- .NET 8, C# 12, nullable enabled
- Conventional commits: `feat:`, `fix:`, `refactor:`, `docs:`
- Tests in `tests/Lifeblood.Tests/` using xUnit

## Running Tests

```bash
dotnet test
```

## Before Submitting

1. All existing tests pass
2. New code has tests
3. `GraphValidator.Validate()` returns no errors for any graph you produce
4. Architecture invariants are not violated (check CLAUDE.md)

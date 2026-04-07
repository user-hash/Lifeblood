# Building a Language Adapter

Lifeblood adapters translate language-specific code intelligence into the universal semantic graph. Two paths exist: JSON (any language) or C# (in-process).

## Option A: JSON Adapter (any language, recommended)

Write a parser in your language. Output JSON conforming to `schemas/graph.schema.json`. Lifeblood reads it via `JsonGraphImporter`. No C# needed.

```bash
your-parser ./project > graph.json
dotnet run --project src/Lifeblood.CLI -- analyze --graph graph.json
```

The TypeScript adapter (`adapters/typescript/`) is a working example of this approach.

### Required JSON structure

```json
{
  "version": "1.0",
  "language": "your-language",
  "adapter": {
    "name": "your-adapter",
    "version": "1.0.0",
    "capabilities": {
      "discoverSymbols": true,
      "typeResolution": "bestEffort",
      "callResolution": "bestEffort",
      "implementationResolution": "none",
      "crossModuleReferences": "none",
      "overrideResolution": "none"
    }
  },
  "symbols": [...],
  "edges": [...]
}
```

### Minimum viable adapter

Your JSON must contain:
- `version`: `"1.0"`
- `language`: your language name
- `adapter`: with honest capability declarations
- `symbols[]`: at least File and Type symbols with `id`, `name`, `kind`
- `edges[]`: at least `dependsOn` edges between modules/files

That is enough for coupling analysis, cycle detection, and architecture rules.

### Full adapter

Add these for richer analysis:
- Method and Field symbols with `parentId` for containment hierarchy
- `calls`, `references`, `implements`, `inherits` edges
- `evidence` on every edge (kind, adapterName, confidence)
- All symbols sorted by ID, all edges sorted by source+target+kind (deterministic output)

## Option B: C# Adapter (in-process)

Implement `IWorkspaceAnalyzer` from `Lifeblood.Application.Ports.Left`:

```csharp
public class MyAdapter : IWorkspaceAnalyzer
{
    public AdapterCapability Capability => new AdapterCapability
    {
        Language = "python",
        AdapterName = "python-ast",
        AdapterVersion = "1.0.0",
        CanDiscoverSymbols = true,
        TypeResolution = ConfidenceLevel.BestEffort,
        CallResolution = ConfidenceLevel.BestEffort,
    };

    public SemanticGraph AnalyzeWorkspace(string projectRoot, AnalysisConfig config)
    {
        var builder = new GraphBuilder();
        // Add symbols with ParentId — GraphBuilder synthesizes Contains edges
        return builder.Build();
    }
}
```

The Roslyn adapter (`src/Lifeblood.Adapters.CSharp/`) is the reference implementation.

## Adapter Quality Levels

| Level | What you provide | What it unlocks | Confidence |
|-------|-----------------|----------------|------------|
| **Syntax** | Files, imports, basic structure | Coupling, cycles, architecture rules | bestEffort |
| **Structural** | Types, inheritance, interfaces | Tier classification, boundary checks | high |
| **Semantic** | Methods, calls, references | Blast radius, dead code detection | high |
| **Compiler-grade** | Type resolution, overloads | Everything, full trust | proven |

Start with Syntax. It is already useful. Upgrade confidence claims as you add capabilities.

## Adapter Checklist

Before shipping an adapter, verify:

- [ ] Output conforms to `schemas/graph.schema.json`
- [ ] `version` is `"1.0"`
- [ ] `language` and `adapter` metadata are present
- [ ] Capability claims are honest (do not claim `proven` for things you guess)
- [ ] Every edge has `evidence` with `kind`, `adapterName`, and `confidence`
- [ ] No dangling edges (every sourceId and targetId exists in symbols)
- [ ] No duplicate symbols (unique IDs)
- [ ] No duplicate edges (unique source+target+kind)
- [ ] Symbols with `parentId` reference existing symbols
- [ ] Output is deterministic (same input produces same JSON)
- [ ] `IsFromSource` filtering applied (no edges to external/stdlib types)
- [ ] Self-analysis works (adapter can analyze its own source code)
- [ ] Output passes through `dotnet run --project src/Lifeblood.CLI -- analyze --graph your-output.json`

## Symbol ID Convention

Adapters should use these ID prefixes for consistency:

| Kind | Prefix | Example |
|------|--------|---------|
| Module | `mod:` | `mod:MyApp` |
| File | `file:` | `file:src/auth.ts` |
| Namespace | `ns:` | `ns:MyApp.Auth` |
| Type | `type:` | `type:MyApp.Auth.AuthService` |
| Method | `method:` | `method:MyApp.Auth.AuthService.login` |
| Field | `field:` | `field:MyApp.Auth.AuthService.token` |

## Evidence Guidelines

Every edge should carry evidence. The confidence level must be honest:

| Confidence | Meaning | Example |
|-----------|---------|---------|
| `none` | Not supported by this adapter | Override detection in a syntax-only parser |
| `bestEffort` | Inferred from patterns, may be wrong | Import-based dependency in Python |
| `high` | Resolved by language tooling, reliable | TypeChecker resolution in TypeScript |
| `proven` | Compiler-grade, guaranteed correct | Roslyn semantic model resolution |

## Testing Against Golden Repos

The `tests/GoldenRepos/` directory contains fixture projects:
- **HexagonalApp/** — 3-layer hexagonal architecture (Domain, Application, Infrastructure)
- **CycleRepo/** — Two services with circular dependencies

Run your adapter against these and verify:
- Expected types are discovered
- Inheritance and implementation edges are correct
- Circular dependencies are detectable in the output
- Graph validates cleanly through GraphValidator

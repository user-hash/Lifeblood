# Building a Language Adapter

Two options. Pick whichever fits your language.

## Option A: JSON Adapter (any language)

Write a parser in your language. Output JSON conforming to `schemas/graph.schema.json`.

```bash
# Your parser outputs the graph
python my_parser.py ./project > graph.json

# Lifeblood consumes it
lifeblood analyze --graph graph.json --rules rules.json
```

### Minimum viable adapter

Your JSON must contain:
- `version`: `"1.0"`
- `symbols[]`: at least File and Type symbols with `id`, `name`, `kind`
- `edges[]`: at least `dependsOn` edges between files

That is enough for coupling analysis, circular dependency detection, and architecture rule checking.

### Full adapter

Add these for richer context:
- Method and Field symbols with `parentId` for containment hierarchy
- `calls`, `references`, `implements`, `inherits` edges
- `evidence` on edges (how was this relationship discovered, and how confident)
- `adapter.capabilities` declaring what you can actually do

### Capability declaration

Be honest. If your parser cannot resolve types, say so:

```json
{
  "adapter": {
    "name": "python-ast-adapter",
    "version": "0.1.0",
    "capabilities": {
      "discoverSymbols": true,
      "typeResolution": "bestEffort",
      "callResolution": "bestEffort",
      "crossModuleReferences": "none"
    }
  }
}
```

Consumers know what to trust. No fake authority.

## Option B: C# Adapter (in-process)

Implement `IWorkspaceAnalyzer` from `Lifeblood.Application.Ports.Left`:

```csharp
public class MyLanguageAdapter : IWorkspaceAnalyzer
{
    public AdapterCapability Capability => new AdapterCapability
    {
        Language = "python",
        AdapterName = "python-ast",
        AdapterVersion = "0.1.0",
        CanDiscoverSymbols = true,
        TypeResolution = ConfidenceLevel.BestEffort,
        CallResolution = ConfidenceLevel.BestEffort,
    };

    public SemanticGraph AnalyzeWorkspace(string projectRoot, AnalysisConfig config)
    {
        // Discover modules, extract symbols, build edges
        var builder = new GraphBuilder();

        // Add symbols with ParentId for containment hierarchy
        // GraphBuilder will synthesize Contains edges automatically

        return builder.Build();
    }
}
```

Optionally implement `IModuleDiscovery` to provide module/package structure.

## Adapter Quality Levels

| Level | What you provide | What it unlocks |
|-------|-----------------|----------------|
| **Syntax** | Files + imports | Coupling, circular deps, architecture rules |
| **Structural** | + Types + inheritance | Tier classification, hub detection, boundary checks |
| **Semantic** | + Methods + calls + references | Dead code, blast radius, invariant verification |
| **Compiler-grade** | + Type resolution + overloads | Everything, with Proven confidence |

Start with Syntax. It is already useful. Improve from there.

## Testing Your Adapter

The `tests/GoldenRepos/` directory will contain golden repo fixtures. Every adapter should be tested against them:

- Resolves file-level dependencies
- Discovers types and methods
- Handles circular references gracefully
- Reports capabilities honestly
- Produces valid JSON conforming to the schema
- GraphValidator returns no errors for the output graph

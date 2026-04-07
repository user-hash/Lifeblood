# Building a Language Adapter

Two options. Pick whichever fits your language.

## Option A: JSON Adapter (any language)

Write a parser in your language. Output JSON conforming to `schemas/graph.schema.json`.

```bash
# Your parser outputs the graph
python my_parser.py ./project > graph.json

# Lifeblood analyzes it
lifeblood analyze --graph graph.json --rules rules.json
```

### Minimum viable adapter

Your JSON must contain:
- `version`: `"1.0"`
- `symbols[]`: at least File and Type symbols with `id`, `name`, `kind`
- `edges[]`: at least `dependsOn` edges between files

That is enough for coupling analysis, circular dependency detection, and architecture rule checking.

### Full adapter

Add these for richer analysis:
- Method and Field symbols
- `calls`, `references`, `implements`, `inherits` edges
- `confidence` on edges (how sure your parser is)
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

Analysis results will carry this confidence level. No fake authority.

## Option B: C# Adapter (in-process)

Implement `ICodeParser` from `Lifeblood.Core.Ports`:

```csharp
public class MyLanguageParser : ICodeParser
{
    public string[] SupportedExtensions => new[] { ".py" };

    public ParseResult Parse(string filePath, string sourceText)
    {
        // Parse sourceText, return symbols and edges
        return new ParseResult
        {
            Symbols = /* your symbols */,
            Edges = /* your edges */,
            Metadata = new FileMetadata { Namespace = "...", LinesOfCode = ... }
        };
    }
}
```

Optionally implement `IProjectDiscovery` to provide module/package structure.

## Adapter Quality Levels

| Level | What you provide | What analysis you unlock |
|-------|-----------------|------------------------|
| Basic | Files + imports | Coupling, circular deps, architecture rules |
| Good | + Types + inheritance | Tier classification, hub detection |
| Full | + Methods + calls + references | Dead code, blast radius, invariant verification |
| Roslyn-grade | + Type resolution + overloads | Everything, with proven confidence |

Start with Basic. It is already useful. Improve from there.

## Testing Your Adapter

The `tests/Contract/` directory contains golden repo fixtures. Every adapter should be tested against them:

- Resolves file-level dependencies
- Discovers types and methods
- Handles circular references gracefully
- Reports capabilities honestly
- Produces valid JSON conforming to the schema

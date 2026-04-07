using Lifeblood.Core.Ports;

namespace Lifeblood.Core.Graph;

/// <summary>
/// Builds a SemanticGraph from adapter ParseResults.
/// Deduplicates symbols, merges edges, builds containment hierarchy.
/// </summary>
public static class GraphBuilder
{
    /// <summary>
    /// Build a unified graph from multiple parse results (one per file).
    /// </summary>
    public static SemanticGraph Build(ParseResult[] results, ModuleInfo[]? modules = null)
    {
        var symbols = new List<Symbol>();
        var edges = new List<Edge>();
        var seenSymbols = new HashSet<string>(StringComparer.Ordinal);
        var seenEdges = new HashSet<(string, string, EdgeKind)>();

        // Add module-level symbols if provided
        if (modules != null)
        {
            for (int m = 0; m < modules.Length; m++)
            {
                string moduleId = $"module:{modules[m].Name}";
                if (seenSymbols.Add(moduleId))
                {
                    symbols.Add(new Symbol
                    {
                        Id = moduleId,
                        Name = modules[m].Name,
                        QualifiedName = modules[m].Name,
                        Kind = SymbolKind.Module,
                        Properties = modules[m].Properties ?? new(),
                    });
                }
            }
        }

        // Merge all parse results
        for (int r = 0; r < results.Length; r++)
        {
            var result = results[r];
            if (result.Symbols != null)
            {
                for (int s = 0; s < result.Symbols.Length; s++)
                {
                    if (seenSymbols.Add(result.Symbols[s].Id))
                        symbols.Add(result.Symbols[s]);
                }
            }

            if (result.Edges != null)
            {
                for (int e = 0; e < result.Edges.Length; e++)
                {
                    var edge = result.Edges[e];
                    if (seenEdges.Add((edge.SourceId, edge.TargetId, edge.Kind)))
                        edges.Add(edge);
                }
            }
        }

        return new SemanticGraph
        {
            Symbols = symbols.ToArray(),
            Edges = edges.ToArray(),
        };
    }
}

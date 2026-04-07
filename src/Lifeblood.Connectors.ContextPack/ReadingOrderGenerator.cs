using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Connectors.ContextPack;

/// <summary>
/// Topological sort by importance. Pure leaf files first, high-fan-in files early.
/// Produces a reading order that gives AI agents the right context in the right sequence.
/// </summary>
public static class ReadingOrderGenerator
{
    public static string[] Generate(SemanticGraph graph, CouplingMetrics[] coupling)
    {
        // Build lookup: symbolId → coupling metrics
        var metricsById = new Dictionary<string, CouplingMetrics>(StringComparer.Ordinal);
        foreach (var m in coupling)
            metricsById[m.SymbolId] = m;

        // Collect file symbols and their importance score
        var fileScores = new List<(string filePath, float score)>();

        foreach (var symbol in graph.Symbols)
        {
            if (symbol.Kind != SymbolKind.File) continue;
            if (string.IsNullOrEmpty(symbol.FilePath)) continue;

            // Score = sum of fan-in of all types in this file
            float score = 0;
            foreach (var child in graph.ChildrenOf(symbol.Id))
            {
                if (metricsById.TryGetValue(child.Id, out var m))
                    score += m.FanIn;
            }

            fileScores.Add((symbol.FilePath, score));
        }

        // Sort: stable files first (high fan-in, low instability = read first)
        // Then unstable files (high instability = read after understanding stable core)
        return fileScores
            .OrderByDescending(f => f.score)
            .Select(f => f.filePath)
            .ToArray();
    }
}

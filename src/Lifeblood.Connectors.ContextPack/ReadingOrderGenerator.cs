using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Connectors.ContextPack;

/// <summary>
/// Generates a reading order: stable pure-leaf files first, then their dependants.
/// Files with zero fan-in are listed last (entry points / composition roots).
/// </summary>
public static class ReadingOrderGenerator
{
    public static string[] Generate(SemanticGraph graph, CouplingMetrics[] coupling)
    {
        var metricsById = new Dictionary<string, CouplingMetrics>(StringComparer.Ordinal);
        foreach (var m in coupling)
            metricsById[m.SymbolId] = m;

        var fileScores = new List<(string filePath, int fanIn, float instability)>();

        foreach (var symbol in graph.Symbols)
        {
            if (symbol.Kind != SymbolKind.File || string.IsNullOrEmpty(symbol.FilePath))
                continue;

            int totalFanIn = 0;
            float minInstability = 1f;

            foreach (var child in graph.ChildrenOf(symbol.Id))
            {
                if (metricsById.TryGetValue(child.Id, out var m))
                {
                    totalFanIn += m.FanIn;
                    if (m.Instability < minInstability) minInstability = m.Instability;
                }
            }

            fileScores.Add((symbol.FilePath, totalFanIn, minInstability));
        }

        // Stable-first ordering: lowest instability first (pure leaves),
        // then highest fan-in (most depended on). Entry points last.
        return fileScores
            .OrderBy(f => f.instability)
            .ThenByDescending(f => f.fanIn)
            .Select(f => f.filePath)
            .ToArray();
    }
}

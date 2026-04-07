using Lifeblood.Core.Graph;

namespace Lifeblood.Core.Analysis;

/// <summary>
/// Computes blast radius: if you change this symbol, what else is affected?
/// Walks the incoming edge graph (reverse dependencies) recursively.
///
/// INV-ANALYSIS-001: Stateless. Input: graph + target symbol. Output: affected set.
/// </summary>
public static class BlastRadiusAnalyzer
{
    /// <summary>
    /// Find all symbols that would be affected by changing the target symbol.
    /// Walks reverse dependency edges (incoming) up to maxDepth.
    /// </summary>
    public static BlastRadiusResult Analyze(SemanticGraph graph, string targetSymbolId, int maxDepth = 10)
    {
        var affected = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string id, int depth)>();
        queue.Enqueue((targetSymbolId, 0));
        affected.Add(targetSymbolId);

        while (queue.Count > 0)
        {
            var (currentId, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;

            foreach (int idx in graph.GetIncomingEdgeIndexes(currentId))
            {
                var edge = graph.Edges[idx];
                if (edge.Kind == EdgeKind.Contains) continue;

                if (affected.Add(edge.SourceId))
                    queue.Enqueue((edge.SourceId, depth + 1));
            }
        }

        // Remove the target itself from the affected set
        affected.Remove(targetSymbolId);

        return new BlastRadiusResult
        {
            TargetSymbolId = targetSymbolId,
            AffectedSymbolIds = affected.ToArray(),
            AffectedCount = affected.Count,
        };
    }
}

public sealed class BlastRadiusResult
{
    public string TargetSymbolId { get; init; } = "";
    public string[] AffectedSymbolIds { get; init; } = Array.Empty<string>();
    public int AffectedCount { get; init; }
}

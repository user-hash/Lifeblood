using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Analysis;

/// <summary>
/// Computes what breaks if you change a given symbol.
/// BFS over incoming edges (excluding Contains). INV-ANALYSIS-002: Read-only.
/// </summary>
public static class BlastRadiusAnalyzer
{
    public static BlastRadiusResult Analyze(SemanticGraph graph, string targetSymbolId, int maxDepth = 10)
    {
        var affected = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string id, int depth)>();
        queue.Enqueue((targetSymbolId, 0));

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (depth > maxDepth) continue;

            foreach (int idx in graph.GetIncomingEdgeIndexes(current))
            {
                var edge = graph.Edges[idx];
                if (edge.Kind == EdgeKind.Contains) continue;

                if (affected.Add(edge.SourceId))
                    queue.Enqueue((edge.SourceId, depth + 1));
            }
        }

        affected.Remove(targetSymbolId);
        return new BlastRadiusResult
        {
            TargetSymbolId = targetSymbolId,
            AffectedSymbolIds = affected.ToArray(),
            AffectedCount = affected.Count,
        };
    }
}

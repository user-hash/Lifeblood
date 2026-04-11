using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Analysis;

/// <summary>
/// Computes what breaks if you change a given symbol.
/// BFS over incoming edges (excluding Contains). INV-ANALYSIS-002: Read-only.
///
/// Phase 6 / B7 (2026-04-11): the analyzer now also classifies each
/// directly-adjacent break via <see cref="BreakInfo"/>. The classification
/// uses the edge kind connecting the dependant to the target — that's
/// the only information the graph has, and it's enough to categorize
/// into the coarse <see cref="BreakKind"/> buckets a developer asks
/// about ("does this rename break callers? does this signature change
/// break implementers? does this deletion break anything at all?").
/// </summary>
public static class BlastRadiusAnalyzer
{
    public static BlastRadiusResult Analyze(SemanticGraph graph, string targetSymbolId, int maxDepth = 10)
    {
        var affected = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string id, int depth)>();
        queue.Enqueue((targetSymbolId, 0));

        // Break classification: one entry per DIRECTLY-adjacent dependant
        // (depth 1). Transitive breaks inherit the closest direct cause,
        // but we don't enumerate them per-entry — the transitive list
        // would duplicate AffectedSymbolIds without adding useful
        // information that a consumer couldn't compute themselves by
        // walking the graph.
        var breaks = new List<BreakInfo>();
        var directBreaks = new HashSet<string>(StringComparer.Ordinal);

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;

            foreach (int idx in graph.GetIncomingEdgeIndexes(current))
            {
                var edge = graph.Edges[idx];
                if (edge.Kind == EdgeKind.Contains) continue;

                if (affected.Add(edge.SourceId))
                    queue.Enqueue((edge.SourceId, depth + 1));

                // Record classification ONCE per direct-adjacent dependant.
                // We want the first (shallowest) edge kind we see.
                if (current == targetSymbolId && directBreaks.Add(edge.SourceId))
                {
                    breaks.Add(new BreakInfo
                    {
                        SymbolId = edge.SourceId,
                        Kind = ClassifyBreak(edge.Kind),
                        Reason = $"edge {edge.Kind} → {targetSymbolId}",
                    });
                }
            }
        }

        affected.Remove(targetSymbolId);
        return new BlastRadiusResult
        {
            TargetSymbolId = targetSymbolId,
            AffectedSymbolIds = affected.ToArray(),
            AffectedCount = affected.Count,
            Breaks = breaks.ToArray(),
        };
    }

    /// <summary>
    /// Map an edge kind to the coarse break category a developer cares
    /// about. The mapping is intentionally pessimistic — e.g.
    /// <see cref="EdgeKind.Calls"/> is classified as BindingRemoval
    /// because a deleted callee breaks callers at bind time, even though
    /// it could also be a pure signature change. The caller should read
    /// the break as "this is what COULD happen if you rename/delete
    /// the target", not "this is guaranteed to happen".
    /// </summary>
    private static BreakKind ClassifyBreak(EdgeKind edgeKind) => edgeKind switch
    {
        EdgeKind.Calls => BreakKind.BindingRemoval,
        EdgeKind.References => BreakKind.BindingRemoval,
        EdgeKind.DependsOn => BreakKind.BindingRemoval,
        EdgeKind.Implements => BreakKind.SignatureChange,
        EdgeKind.Inherits => BreakKind.SignatureChange,
        EdgeKind.Overrides => BreakKind.SignatureChange,
        _ => BreakKind.Unknown,
    };
}

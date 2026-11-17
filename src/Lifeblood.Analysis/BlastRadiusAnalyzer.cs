using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Analysis;

/// <summary>
/// Computes what breaks if you change a given symbol.
/// BFS over incoming edges (excluding Contains). INV-ANALYSIS-002: Read-only.
///
/// Each directly-adjacent break is classified via <see cref="BreakInfo"/>;
/// the classification uses the edge kind connecting the dependant to the
/// target — the only signal the graph carries, and enough to categorize
/// into the coarse <see cref="BreakKind"/> buckets a developer asks
/// about ("does this rename break callers? does this signature change
/// break implementers? does this deletion break anything at all?").
/// </summary>
public static class BlastRadiusAnalyzer
{
    public static BlastRadiusResult Analyze(SemanticGraph graph, string targetSymbolId, int maxDepth = 10)
        => AnalyzeCore(graph, targetSymbolId, maxDepth,
            new HashSet<string>(StringComparer.Ordinal),
            new Queue<(string id, int depth)>(),
            new Dictionary<string, List<EdgeKind>>(StringComparer.Ordinal));

    /// <summary>
    /// Stop incoming traversal at the selected symbols or their parent-owned
    /// descendants. Independent paths around a boundary remain traversable.
    /// The original graph and the ordinary full-impact policy are unchanged.
    /// </summary>
    public static BlastRadiusTraversalResult AnalyzeWithinBoundaries(
        SemanticGraph graph, string targetSymbolId, IReadOnlyCollection<string> stopAtSymbolIds, int maxDepth = 10)
    {
        var boundaries = new HashSet<string>(stopAtSymbolIds, StringComparer.Ordinal);
        foreach (var id in boundaries)
            if (graph.GetSymbol(id) == null)
                throw new ArgumentException($"Traversal boundary does not exist: {id}", nameof(stopAtSymbolIds));
        var membership = new Dictionary<string, bool>(StringComparer.Ordinal);
        var stoppedAt = new HashSet<string>(StringComparer.Ordinal);
        var impact = AnalyzeCore(graph, targetSymbolId, maxDepth,
            new HashSet<string>(StringComparer.Ordinal), new Queue<(string id, int depth)>(),
            new Dictionary<string, List<EdgeKind>>(StringComparer.Ordinal),
            id => IsWithinBoundary(graph, id, boundaries, membership), stoppedAt);
        return new BlastRadiusTraversalResult
        {
            Impact = impact,
            StoppedAtSymbolIds = stoppedAt.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
        };
    }

    private static bool IsWithinBoundary(SemanticGraph graph, string id,
        HashSet<string> boundaries, Dictionary<string, bool> membership)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? current = id;
        bool result = false;
        while (current != null && visited.Add(current))
        {
            if (boundaries.Contains(current)) { result = true; break; }
            if (membership.TryGetValue(current, out result)) break;
            current = graph.GetSymbol(current)?.ParentId;
        }
        foreach (var symbol in visited) membership[symbol] = result;
        return result;
    }

    internal static BlastRadiusResult[] AnalyzeMany(SemanticGraph graph, IEnumerable<string> targets, int maxDepth = 10)
    {
        var affected = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string id, int depth)>();
        var directEdgeKinds = new Dictionary<string, List<EdgeKind>>(StringComparer.Ordinal);
        return targets.Select(target => AnalyzeCore(graph, target, maxDepth, affected, queue, directEdgeKinds)).ToArray();
    }

    private static BlastRadiusResult AnalyzeCore(SemanticGraph graph, string targetSymbolId, int maxDepth,
        HashSet<string> affected, Queue<(string id, int depth)> queue,
        Dictionary<string, List<EdgeKind>> directEdgeKinds,
        Func<string, bool>? stopExpansion = null, HashSet<string>? stoppedAt = null)
    {
        affected.Clear();
        queue.Clear();
        directEdgeKinds.Clear();
        queue.Enqueue((targetSymbolId, 0));

        // Break classification: one entry per DIRECTLY-adjacent dependant
        // (depth 1). Transitive breaks inherit the closest direct cause,
        // but we don't enumerate them per-entry — the transitive list
        // would duplicate AffectedSymbolIds without adding useful
        // information that a consumer couldn't compute themselves by
        // walking the graph.
        //
        // When a dependant has MULTIPLE incoming edges to the target (e.g.
        // a class both Calls and Implements a type), we pick the MOST
        // IMPACTFUL edge kind. Precedence: SignatureChange > BindingRemoval
        // > Behavioral > Unknown. This avoids silently collapsing a
        // signature-change into a binding-removal just because the Calls
        // edge happened to be iterated first.
        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;
            if (stopExpansion?.Invoke(current) == true)
            {
                stoppedAt!.Add(current);
                continue;
            }

            foreach (int idx in graph.GetIncomingEdgeIndexes(current))
            {
                var edge = graph.Edges[idx];
                if (edge.Kind == EdgeKind.Contains) continue;

                if (affected.Add(edge.SourceId))
                    queue.Enqueue((edge.SourceId, depth + 1));

                // Record every edge kind per direct-adjacent dependant.
                if (current == targetSymbolId)
                {
                    if (!directEdgeKinds.TryGetValue(edge.SourceId, out var list))
                    {
                        list = new List<EdgeKind>(2);
                        directEdgeKinds[edge.SourceId] = list;
                    }
                    if (!list.Contains(edge.Kind))
                        list.Add(edge.Kind);
                }
            }
        }

        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
        var breaks = directEdgeKinds
            .Select(kvp =>
            {
                var dominantKind = kvp.Value
                    .Select(ClassifyBreak)
                    .OrderByDescending(BreakSeverityRank)
                    .First();
                var edgeKinds = string.Join(", ", kvp.Value);
                if (!reasons.TryGetValue(edgeKinds, out var reason))
                    reasons[edgeKinds] = reason = $"edges [{edgeKinds}] → {targetSymbolId}";
                return new BreakInfo
                {
                    SymbolId = kvp.Key,
                    Kind = dominantKind,
                    Reason = reason,
                };
            })
            .OrderBy(b => b.SymbolId, StringComparer.Ordinal)
            .ToArray();

        affected.Remove(targetSymbolId);
        return new BlastRadiusResult
        {
            TargetSymbolId = targetSymbolId,
            AffectedSymbolIds = affected.ToArray(),
            AffectedCount = affected.Count,
            Breaks = breaks,
        };
    }

    /// <summary>
    /// Severity ranking used by <see cref="Analyze"/> to collapse
    /// multiple-edge-kind dependants to a single worst-case break kind.
    /// Higher = worse. The ranking is intentionally simple and coarse:
    /// signature changes are worse than binding removals (because they
    /// propagate to every subclass), binding removals are worse than
    /// behavioral changes (because they fail at compile time, not
    /// runtime), and Unknown is the floor.
    /// </summary>
    private static int BreakSeverityRank(BreakKind kind) => kind switch
    {
        BreakKind.SignatureChange => 4,
        BreakKind.TypeRename => 3,
        BreakKind.BindingRemoval => 2,
        BreakKind.Behavioral => 1,
        _ => 0,
    };

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

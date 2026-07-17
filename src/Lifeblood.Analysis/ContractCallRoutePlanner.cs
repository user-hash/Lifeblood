using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Analysis;

/// <summary>
/// Derives bounded, profile-aware call membership from the immutable semantic
/// graph. The result is a request-local query plan, not a retained graph or a
/// second call-edge authority.
/// </summary>
public static class ContractCallRoutePlanner
{
    public static ContractCallRoutePlan Plan(
        SemanticGraph graph,
        IReadOnlyList<ContractCallRoute> routes,
        string? profileScope)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(routes);
        if (routes.Count == 0) return ContractCallRoutePlan.Empty;

        var receipts = new List<ContractCallRouteReceipt>(routes.Count);
        var matches = new List<ContractCallRouteMatch>();
        foreach (var route in routes.OrderBy(candidate => candidate.Id, StringComparer.Ordinal))
        {
            PlanOne(graph, route, profileScope, receipts, matches);
        }

        return new ContractCallRoutePlan
        {
            Routes = receipts.ToArray(),
            Matches = matches
                .OrderBy(match => match.RouteId, StringComparer.Ordinal)
                .ThenBy(match => match.RootSymbolId, StringComparer.Ordinal)
                .ThenBy(match => match.Distance)
                .ThenBy(match => match.ContainingSymbolId, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private static void PlanOne(
        SemanticGraph graph,
        ContractCallRoute route,
        string? profileScope,
        ICollection<ContractCallRouteReceipt> receipts,
        ICollection<ContractCallRouteMatch> allMatches)
    {
        var roots = route.RootSymbolIds
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        foreach (var rootId in roots)
        {
            var root = graph.GetSymbol(rootId);
            if (root == null)
                throw new ArgumentException($"Call route '{route.Id}' root '{rootId}' is not present in the selected graph.");
            if (root.Kind != SymbolKind.Method)
            {
                throw new ArgumentException(
                    $"Call route '{route.Id}' root '{rootId}' is {root.Kind}; call-route roots must be methods.");
            }
        }

        var queue = new Queue<RouteState>();
        var visited = new HashSet<(string RootId, string SymbolId)>();
        var routeMatches = new List<ContractCallRouteMatch>(Math.Min(route.MaxMembers, 256));
        foreach (var rootId in roots)
        {
            visited.Add((rootId, rootId));
            var path = new[] { rootId };
            queue.Enqueue(new RouteState(rootId, rootId, 0, path));
            routeMatches.Add(Match(route.Id, rootId, rootId, 0, path));
        }

        var truncated = false;
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current.Distance >= route.MaxDepth) continue;

            foreach (var targetId in CallTargets(graph, current.SymbolId, profileScope))
            {
                if (!visited.Add((current.RootId, targetId))) continue;
                if (routeMatches.Count >= route.MaxMembers)
                {
                    truncated = true;
                    continue;
                }

                var path = current.PathSymbolIds.Append(targetId).ToArray();
                var next = new RouteState(current.RootId, targetId, current.Distance + 1, path);
                queue.Enqueue(next);
                routeMatches.Add(Match(route.Id, next.RootId, next.SymbolId, next.Distance, path));
            }
        }

        foreach (var match in routeMatches)
            allMatches.Add(match);
        receipts.Add(new ContractCallRouteReceipt
        {
            RouteId = route.Id,
            RootSymbolIds = roots,
            MaxDepth = route.MaxDepth,
            MaxMembers = route.MaxMembers,
            ReachableMemberCount = routeMatches
                .Select(match => match.ContainingSymbolId)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            MembershipCount = routeMatches.Count,
            Truncated = truncated,
        });
    }

    private static IEnumerable<string> CallTargets(
        SemanticGraph graph,
        string sourceId,
        string? profileScope)
    {
        var targets = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var edgeIndex in graph.GetOutgoingEdgeIndexes(sourceId))
        {
            var edge = graph.Edges[edgeIndex];
            if (edge.Kind != EdgeKind.Calls || !AppliesToProfile(edge, profileScope)) continue;
            if (graph.GetSymbol(edge.TargetId)?.Kind != SymbolKind.Method) continue;
            targets.Add(edge.TargetId);
        }
        return targets;
    }

    internal static bool AppliesToProfile(Edge edge, string? profileScope)
        => string.IsNullOrWhiteSpace(profileScope)
            || edge.Profiles == null
            || edge.Profiles.Contains(profileScope, StringComparer.Ordinal);

    private static ContractCallRouteMatch Match(
        string routeId,
        string rootId,
        string containingSymbolId,
        int distance,
        string[] pathSymbolIds)
        => new()
        {
            RouteId = routeId,
            RootSymbolId = rootId,
            ContainingSymbolId = containingSymbolId,
            Distance = distance,
            Placement = distance == 0
                ? ContractCallRoutePlacement.Direct
                : ContractCallRoutePlacement.Transitive,
            PathSymbolIds = pathSymbolIds,
        };

    private sealed record RouteState(
        string RootId,
        string SymbolId,
        int Distance,
        string[] PathSymbolIds);
}

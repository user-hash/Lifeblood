using System.Collections.Generic;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Domain.Graph;

namespace Lifeblood.Connectors.Mcp;

/// <summary>
/// Reference implementation of <see cref="IPortHealthAnalyzer"/>. Walks the
/// directly-contained non-nested members of the queried type and counts how
/// many are reached by at least one non-Contains incoming edge OR an outgoing
/// <see cref="EdgeKind.Implements"/> edge (the implements branch mirrors the
/// liveness logic in <see cref="LifebloodDeadCodeAnalyzer"/> — a method
/// implementing an interface member is reachable through its contract).
///
/// Behavior is byte-equal to the pre-F3a inline <c>HandlePortHealth</c> body
/// in <c>ToolHandler.cs</c>; this atom only relocates the algorithm behind
/// a stable port interface. INV-PORT-HEALTH-ANALYZER-SEAM-001 /
/// LB-TRACK-20260519-022 / F3a atom of the 2026-05-19 plan.
///
/// Stateless per INV-ANALYSIS-001. Read-only per INV-GRAPH-004.
/// </summary>
public sealed class LifebloodPortHealthAnalyzer : IPortHealthAnalyzer
{
    public PortHealthReport? Analyze(SemanticGraph graph, string typeId)
    {
        var sym = graph.GetSymbol(typeId);
        if (sym == null || sym.Kind != SymbolKind.Type) return null;

        var directMembers = CollectDirectMembers(graph, typeId);

        // F3b: walk outgoing Inherits edges transitively to find composite
        // sub-ports, then collect their direct members. Distinct by canonical
        // id across the transitive closure — diamond inheritance does not
        // double-count. INV-PORT-HEALTH-COMPOSITE-001.
        var inheritedInterfaces = CollectInheritedInterfaces(graph, typeId);
        var inheritedMembers = new List<string>();
        var memberSet = new HashSet<string>(directMembers, System.StringComparer.Ordinal);
        foreach (var ifaceId in inheritedInterfaces)
        {
            foreach (var memberId in CollectDirectMembers(graph, ifaceId))
            {
                if (memberSet.Add(memberId)) inheritedMembers.Add(memberId);
            }
        }

        // Aggregate set drives the verdict; composite ports with zero
        // direct members but a healthy inherited surface no longer
        // mislabel as vestigial.
        var aggregate = new List<string>(memberSet.Count);
        aggregate.AddRange(directMembers);
        aggregate.AddRange(inheritedMembers);

        int liveCount = 0;
        var live = new List<string>();
        var dead = new List<string>();
        foreach (var id in aggregate)
        {
            if (IsMemberLive(graph, id))
            {
                liveCount++;
                live.Add(id);
            }
            else
            {
                dead.Add(id);
            }
        }

        double pct = aggregate.Count == 0 ? 0.0 : (double)liveCount / aggregate.Count;
        string verdict = aggregate.Count == 0
            ? "empty"
            : pct >= 0.75 ? "healthy"
            : pct >= 0.25 ? "mixed"
            : "vestigial";

        return new PortHealthReport
        {
            TypeId = typeId,
            MemberCount = aggregate.Count,
            LiveMembers = liveCount,
            DeadMembers = dead.Count,
            LivenessPct = System.Math.Round(pct, 3),
            Verdict = verdict,
            Live = live.ToArray(),
            Dead = dead.ToArray(),
            DirectMemberCount = directMembers.Count,
            InheritedMemberCount = inheritedMembers.Count,
            AggregateMemberCount = aggregate.Count,
            InheritedInterfaces = inheritedInterfaces,
            IsCompositeInterface = inheritedInterfaces.Length > 0,
        };
    }

    /// <summary>
    /// Directly-contained non-nested members of <paramref name="typeId"/>.
    /// </summary>
    private static List<string> CollectDirectMembers(SemanticGraph graph, string typeId)
    {
        var result = new List<string>();
        foreach (int idx in graph.GetOutgoingEdgeIndexes(typeId))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind != EdgeKind.Contains) continue;
            var member = graph.GetSymbol(edge.TargetId);
            if (member == null) continue;
            if (member.Kind == SymbolKind.Type) continue; // exclude nested types
            result.Add(member.Id);
        }
        return result;
    }

    /// <summary>
    /// Transitive outgoing <see cref="EdgeKind.Inherits"/> walk. Returns the
    /// distinct set of inherited type canonical ids, sorted ordinal so the
    /// wire shape is deterministic across runs. INV-PORT-HEALTH-COMPOSITE-001.
    /// </summary>
    private static string[] CollectInheritedInterfaces(SemanticGraph graph, string typeId)
    {
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(typeId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (int idx in graph.GetOutgoingEdgeIndexes(current))
            {
                var edge = graph.Edges[idx];
                if (edge.Kind != EdgeKind.Inherits) continue;
                if (!seen.Add(edge.TargetId)) continue;
                queue.Enqueue(edge.TargetId);
            }
        }
        var arr = new string[seen.Count];
        int i = 0;
        foreach (var id in seen) arr[i++] = id;
        System.Array.Sort(arr, System.StringComparer.Ordinal);
        return arr;
    }

    /// <summary>
    /// Liveness rule mirrors the dead-code analyzer: a non-Contains
    /// incoming edge marks the member live, and an outgoing
    /// <see cref="EdgeKind.Implements"/> edge marks an implementer reachable
    /// through its contract.
    /// </summary>
    private static bool IsMemberLive(SemanticGraph graph, string memberId)
    {
        foreach (int idx in graph.GetIncomingEdgeIndexes(memberId))
        {
            if (graph.Edges[idx].Kind == EdgeKind.Contains) continue;
            return true;
        }
        foreach (int idx in graph.GetOutgoingEdgeIndexes(memberId))
        {
            if (graph.Edges[idx].Kind == EdgeKind.Implements) return true;
        }
        return false;
    }
}

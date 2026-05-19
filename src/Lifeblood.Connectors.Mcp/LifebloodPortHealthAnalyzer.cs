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

        var memberIds = new List<string>();
        foreach (int idx in graph.GetOutgoingEdgeIndexes(typeId))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind != EdgeKind.Contains) continue;
            var member = graph.GetSymbol(edge.TargetId);
            if (member == null) continue;
            if (member.Kind == SymbolKind.Type) continue; // exclude nested types
            memberIds.Add(member.Id);
        }

        int liveCount = 0;
        var live = new List<string>();
        var dead = new List<string>();
        foreach (var id in memberIds)
        {
            bool hasIncoming = false;
            foreach (int idx in graph.GetIncomingEdgeIndexes(id))
            {
                var e = graph.Edges[idx];
                if (e.Kind == EdgeKind.Contains) continue;
                hasIncoming = true; break;
            }
            // Methods that implement an interface member are reachable
            // through the interface — same liveness rule the dead-code
            // analyzer uses (Implements outgoing = alive by definition).
            if (!hasIncoming)
            {
                foreach (int idx in graph.GetOutgoingEdgeIndexes(id))
                {
                    if (graph.Edges[idx].Kind == EdgeKind.Implements) { hasIncoming = true; break; }
                }
            }
            if (hasIncoming) { liveCount++; live.Add(id); }
            else dead.Add(id);
        }

        double pct = memberIds.Count == 0 ? 0.0 : (double)liveCount / memberIds.Count;
        string verdict = memberIds.Count == 0
            ? "empty"
            : pct >= 0.75 ? "healthy"
            : pct >= 0.25 ? "mixed"
            : "vestigial";

        return new PortHealthReport
        {
            TypeId = typeId,
            MemberCount = memberIds.Count,
            LiveMembers = liveCount,
            DeadMembers = dead.Count,
            LivenessPct = System.Math.Round(pct, 3),
            Verdict = verdict,
            Live = live.ToArray(),
            Dead = dead.ToArray(),
        };
    }
}

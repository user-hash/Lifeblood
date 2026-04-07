using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Analysis;

/// <summary>
/// AUDIT FIX (Hole 6): Counts DISTINCT dependants, not edge count.
/// INV-ANALYSIS-003: Follows Robert Martin's definitions.
/// </summary>
public static class CouplingAnalyzer
{
    public static CouplingMetrics[] Analyze(SemanticGraph graph, SymbolKind[] targetKinds)
    {
        var results = new List<CouplingMetrics>();

        for (int i = 0; i < graph.Symbols.Length; i++)
        {
            var symbol = graph.Symbols[i];
            bool match = false;
            for (int k = 0; k < targetKinds.Length; k++)
                if (symbol.Kind == targetKinds[k]) { match = true; break; }
            if (!match) continue;

            var distinctIncoming = new HashSet<string>(StringComparer.Ordinal);
            var distinctOutgoing = new HashSet<string>(StringComparer.Ordinal);

            foreach (int idx in graph.GetIncomingEdgeIndexes(symbol.Id))
                if (graph.Edges[idx].Kind != EdgeKind.Contains)
                    distinctIncoming.Add(graph.Edges[idx].SourceId);

            foreach (int idx in graph.GetOutgoingEdgeIndexes(symbol.Id))
                if (graph.Edges[idx].Kind != EdgeKind.Contains)
                    distinctOutgoing.Add(graph.Edges[idx].TargetId);

            int fanIn = distinctIncoming.Count;
            int fanOut = distinctOutgoing.Count;
            int total = fanIn + fanOut;

            results.Add(new CouplingMetrics
            {
                SymbolId = symbol.Id,
                FanIn = fanIn,
                FanOut = fanOut,
                Instability = total > 0 ? (float)fanOut / total : 0f,
            });
        }

        return results.ToArray();
    }
}

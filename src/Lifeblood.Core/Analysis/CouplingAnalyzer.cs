using Lifeblood.Core.Graph;

namespace Lifeblood.Core.Analysis;

/// <summary>
/// Computes coupling metrics per symbol from the semantic graph.
///
/// Metrics (Robert C. Martin, "Clean Architecture"):
///   Fan-in (Ca): how many symbols depend on this one
///   Fan-out (Ce): how many symbols this one depends on
///   Instability: Ce / (Ca + Ce)
///     0.0 = maximally stable (everything depends on it, it depends on nothing)
///     1.0 = maximally unstable (nothing depends on it, it depends on everything)
///
/// INV-ANALYSIS-001: Stateless. Input: graph. Output: metrics.
/// INV-ANALYSIS-003: Follows Martin's definitions exactly.
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
            {
                if (symbol.Kind == targetKinds[k]) { match = true; break; }
            }
            if (!match) continue;

            int fanIn = 0;
            int fanOut = 0;

            // Count incoming edges (others depending on this symbol)
            foreach (int idx in graph.GetIncomingEdgeIndexes(symbol.Id))
            {
                if (graph.Edges[idx].Kind != EdgeKind.Contains)
                    fanIn++;
            }

            // Count outgoing edges (this symbol depending on others)
            foreach (int idx in graph.GetOutgoingEdgeIndexes(symbol.Id))
            {
                if (graph.Edges[idx].Kind != EdgeKind.Contains)
                    fanOut++;
            }

            int total = fanIn + fanOut;
            float instability = total > 0 ? (float)fanOut / total : 0f;

            results.Add(new CouplingMetrics
            {
                SymbolId = symbol.Id,
                FanIn = fanIn,
                FanOut = fanOut,
                Instability = instability,
            });
        }

        return results.ToArray();
    }
}

using Lifeblood.Core.Graph;

namespace Lifeblood.Core.Analysis;

/// <summary>
/// Detects circular dependencies in the semantic graph using Tarjan's algorithm.
/// Returns all strongly connected components with more than one node.
/// </summary>
public static class CircularDependencyDetector
{
    public static string[][] Detect(SemanticGraph graph, SymbolKind[] targetKinds)
    {
        // Build adjacency for target kinds only
        var targetSymbols = new List<string>();
        var targetSet = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < graph.Symbols.Length; i++)
        {
            for (int k = 0; k < targetKinds.Length; k++)
            {
                if (graph.Symbols[i].Kind == targetKinds[k])
                {
                    targetSymbols.Add(graph.Symbols[i].Id);
                    targetSet.Add(graph.Symbols[i].Id);
                    break;
                }
            }
        }

        // Tarjan's SCC algorithm
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowlink = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var cycles = new List<string[]>();
        int currentIndex = 0;

        void StrongConnect(string v)
        {
            index[v] = currentIndex;
            lowlink[v] = currentIndex;
            currentIndex++;
            stack.Push(v);
            onStack.Add(v);

            // Visit successors
            foreach (int edgeIdx in graph.GetOutgoingEdgeIndexes(v))
            {
                var edge = graph.Edges[edgeIdx];
                if (edge.Kind == EdgeKind.Contains) continue;
                string w = edge.TargetId;
                if (!targetSet.Contains(w)) continue;

                if (!index.ContainsKey(w))
                {
                    StrongConnect(w);
                    lowlink[v] = Math.Min(lowlink[v], lowlink[w]);
                }
                else if (onStack.Contains(w))
                {
                    lowlink[v] = Math.Min(lowlink[v], index[w]);
                }
            }

            // Root of SCC
            if (lowlink[v] == index[v])
            {
                var component = new List<string>();
                string w;
                do
                {
                    w = stack.Pop();
                    onStack.Remove(w);
                    component.Add(w);
                } while (w != v);

                // Only report cycles (components with more than one node)
                if (component.Count > 1)
                    cycles.Add(component.ToArray());
            }
        }

        for (int i = 0; i < targetSymbols.Count; i++)
        {
            if (!index.ContainsKey(targetSymbols[i]))
                StrongConnect(targetSymbols[i]);
        }

        return cycles.ToArray();
    }
}

using Lifeblood.Domain.Graph;

namespace Lifeblood.Analysis;

/// <summary>
/// Detects cycles in the dependency graph using Tarjan's SCC algorithm.
/// Only considers non-Contains edges. INV-ANALYSIS-002: Read-only.
/// </summary>
public static class CircularDependencyDetector
{
    public static string[][] Detect(SemanticGraph graph)
    {
        // Build adjacency from non-Contains edges
        var adj = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var symbol in graph.Symbols)
            adj[symbol.Id] = new List<string>();

        foreach (var edge in graph.Edges)
        {
            if (edge.Kind == EdgeKind.Contains) continue;
            if (adj.ContainsKey(edge.SourceId))
                adj[edge.SourceId].Add(edge.TargetId);
        }

        // Tarjan's SCC
        int index = 0;
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowlinks = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var sccs = new List<string[]>();

        foreach (var v in adj.Keys)
        {
            if (!indices.ContainsKey(v))
                StrongConnect(v, adj, ref index, indices, lowlinks, onStack, stack, sccs);
        }

        // Only return SCCs with more than one member (actual cycles)
        return sccs.Where(scc => scc.Length > 1).ToArray();
    }

    private static void StrongConnect(
        string v,
        Dictionary<string, List<string>> adj,
        ref int index,
        Dictionary<string, int> indices,
        Dictionary<string, int> lowlinks,
        HashSet<string> onStack,
        Stack<string> stack,
        List<string[]> sccs)
    {
        indices[v] = index;
        lowlinks[v] = index;
        index++;
        stack.Push(v);
        onStack.Add(v);

        if (adj.TryGetValue(v, out var neighbors))
        {
            foreach (var w in neighbors)
            {
                if (!indices.ContainsKey(w))
                {
                    StrongConnect(w, adj, ref index, indices, lowlinks, onStack, stack, sccs);
                    lowlinks[v] = Math.Min(lowlinks[v], lowlinks[w]);
                }
                else if (onStack.Contains(w))
                {
                    lowlinks[v] = Math.Min(lowlinks[v], indices[w]);
                }
            }
        }

        if (lowlinks[v] == indices[v])
        {
            var scc = new List<string>();
            string w;
            do
            {
                w = stack.Pop();
                onStack.Remove(w);
                scc.Add(w);
            } while (w != v);

            sccs.Add(scc.ToArray());
        }
    }
}

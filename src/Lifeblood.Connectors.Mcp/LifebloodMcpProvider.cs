using Lifeblood.Application.Ports.Right;
using Lifeblood.Domain.Graph;

namespace Lifeblood.Connectors.Mcp;

/// <summary>
/// Implements IMcpGraphProvider. Serves the semantic graph to AI agents via MCP tools.
/// INV-CONN-002: Read-only. Does not modify the graph.
///
/// MCP tool surface:
///   lifeblood:symbol-lookup     — "What is AuthService?"
///   lifeblood:dependencies      — "What does Domain depend on?"
///   lifeblood:dependants        — "What depends on IUserRepository?"
///   lifeblood:blast-radius      — "What breaks if I change this?"
/// </summary>
public sealed class LifebloodMcpProvider : IMcpGraphProvider
{
    public Symbol? LookupSymbol(SemanticGraph graph, string symbolId)
    {
        return graph.GetSymbol(symbolId);
    }

    public string[] GetDependencies(SemanticGraph graph, string symbolId)
    {
        var deps = new HashSet<string>(StringComparer.Ordinal);

        foreach (int idx in graph.GetOutgoingEdgeIndexes(symbolId))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind != EdgeKind.Contains)
                deps.Add(edge.TargetId);
        }

        return deps.ToArray();
    }

    public string[] GetDependants(SemanticGraph graph, string symbolId)
    {
        var dependants = new HashSet<string>(StringComparer.Ordinal);

        foreach (int idx in graph.GetIncomingEdgeIndexes(symbolId))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind != EdgeKind.Contains)
                dependants.Add(edge.SourceId);
        }

        return dependants.ToArray();
    }

    public string[] GetBlastRadius(SemanticGraph graph, string symbolId, int maxDepth = 10)
    {
        var affected = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string id, int depth)>();
        queue.Enqueue((symbolId, 0));

        while (queue.Count > 0)
        {
            var (currentId, depth) = queue.Dequeue();
            if (depth > maxDepth) continue;

            foreach (int idx in graph.GetIncomingEdgeIndexes(currentId))
            {
                var edge = graph.Edges[idx];
                if (edge.Kind == EdgeKind.Contains) continue;

                if (affected.Add(edge.SourceId))
                    queue.Enqueue((edge.SourceId, depth + 1));
            }
        }

        affected.Remove(symbolId); // Don't include the target itself
        return affected.ToArray();
    }
}

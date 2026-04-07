using Lifeblood.Application.Ports.Right;
using Lifeblood.Domain.Graph;
using Lifeblood.Analysis;

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
        // Delegate to the canonical analyzer — single source of truth for BFS logic.
        var result = Analysis.BlastRadiusAnalyzer.Analyze(graph, symbolId, maxDepth);
        return result.AffectedSymbolIds;
    }
}

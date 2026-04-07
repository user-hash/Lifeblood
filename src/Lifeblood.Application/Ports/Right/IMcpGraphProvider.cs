using Lifeblood.Domain.Graph;

namespace Lifeblood.Application.Ports.Right;

/// <summary>
/// Right side. Serves the semantic graph over MCP protocol to AI agents.
/// INV-CONN-002: Read-only. Does not modify the graph.
/// </summary>
public interface IMcpGraphProvider
{
    Symbol? LookupSymbol(SemanticGraph graph, string symbolId);
    string[] GetDependencies(SemanticGraph graph, string symbolId);
    string[] GetDependants(SemanticGraph graph, string symbolId);
    string[] GetBlastRadius(SemanticGraph graph, string symbolId, int maxDepth = 10);
}

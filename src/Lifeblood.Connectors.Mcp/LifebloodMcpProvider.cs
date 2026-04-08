using Lifeblood.Application.Ports.Analysis;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Domain.Graph;

namespace Lifeblood.Connectors.Mcp;

/// <summary>
/// Implements IMcpGraphProvider. Serves the semantic graph to AI agents via MCP tools.
/// INV-CONN-002: Read-only. Does not modify the graph.
/// INV-CONN-001: Depends on Application ports only — never on Analysis or Adapters directly.
/// </summary>
public sealed class LifebloodMcpProvider : IMcpGraphProvider
{
    private readonly IBlastRadiusProvider _blastRadius;

    public LifebloodMcpProvider(IBlastRadiusProvider blastRadius)
    {
        _blastRadius = blastRadius;
    }

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
        var result = _blastRadius.Analyze(graph, symbolId, maxDepth);
        return result.AffectedSymbolIds;
    }

    public FileImpactResult GetFileImpact(SemanticGraph graph, string fileId)
    {
        var fileSymbol = graph.GetSymbol(fileId);
        var filePath = fileSymbol?.FilePath ?? (fileId.StartsWith("file:") ? fileId.Substring(5) : fileId);

        // Outgoing: files this file depends on (file → other via References)
        var dependsOn = new List<FileEdge>();
        foreach (int idx in graph.GetOutgoingEdgeIndexes(fileId))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind != EdgeKind.References) continue;
            var target = graph.GetSymbol(edge.TargetId);
            if (target == null || target.Kind != SymbolKind.File) continue;

            int count = 1;
            if (edge.Properties.TryGetValue("edgeCount", out var ec) && int.TryParse(ec, out var parsed))
                count = parsed;

            dependsOn.Add(new FileEdge { FileId = edge.TargetId, FilePath = target.FilePath, EdgeCount = count });
        }

        // Incoming: files that depend on this file (other → file via References)
        var dependedOnBy = new List<FileEdge>();
        foreach (int idx in graph.GetIncomingEdgeIndexes(fileId))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind != EdgeKind.References) continue;
            var source = graph.GetSymbol(edge.SourceId);
            if (source == null || source.Kind != SymbolKind.File) continue;

            int count = 1;
            if (edge.Properties.TryGetValue("edgeCount", out var ec) && int.TryParse(ec, out var parsed))
                count = parsed;

            dependedOnBy.Add(new FileEdge { FileId = edge.SourceId, FilePath = source.FilePath, EdgeCount = count });
        }

        return new FileImpactResult
        {
            FileId = fileId,
            FilePath = filePath,
            DependsOn = dependsOn.OrderByDescending(f => f.EdgeCount).ToArray(),
            DependedOnBy = dependedOnBy.OrderByDescending(f => f.EdgeCount).ToArray(),
        };
    }
}

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
    FileImpactResult GetFileImpact(SemanticGraph graph, string fileId);
}

/// <summary>
/// Result of a file-level impact query. Shows which files depend on and are depended on by a given file.
/// </summary>
public sealed class FileImpactResult
{
    public required string FileId { get; init; }
    public required string FilePath { get; init; }
    public required FileEdge[] DependsOn { get; init; }
    public required FileEdge[] DependedOnBy { get; init; }
}

public sealed class FileEdge
{
    public required string FileId { get; init; }
    public required string FilePath { get; init; }
    public required int EdgeCount { get; init; }
}

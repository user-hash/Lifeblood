using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;

namespace Lifeblood.Application.Ports.Left;

/// <summary>
/// PRIMARY adapter port. Left side. Workspace-scoped.
/// INV-PORT-001: The primary contract is workspace → graph.
/// </summary>
public interface IWorkspaceAnalyzer
{
    SemanticGraph AnalyzeWorkspace(string projectRoot, AnalysisConfig config);
    AdapterCapability Capability { get; }
}

public sealed class AnalysisConfig
{
    public string[] IncludePatterns { get; init; } = Array.Empty<string>();
    public string[] ExcludePatterns { get; init; } = Array.Empty<string>();
    public AnalysisDepth Depth { get; init; } = AnalysisDepth.Semantic;
}

public enum AnalysisDepth { Syntax, Semantic }

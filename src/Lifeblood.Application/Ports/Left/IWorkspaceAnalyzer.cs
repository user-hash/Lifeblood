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
    public string[] ExcludePatterns { get; init; } = Array.Empty<string>();

    /// <summary>
    /// When true, the adapter retains full CSharpCompilation objects after graph extraction.
    /// Required for write-side tools (FindReferences, Rename, Execute, CompileCheck).
    /// When false (default), compilations are downgraded to lightweight metadata references
    /// after extraction — dramatically reducing memory for large workspaces.
    /// </summary>
    public bool RetainCompilations { get; init; }
}

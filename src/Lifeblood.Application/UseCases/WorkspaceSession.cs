using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Lifeblood.Application.Ports.Left;

namespace Lifeblood.Application.UseCases;

/// <summary>
/// Holds the loaded workspace state: graph, analysis, and optional write-side capabilities.
/// Composition roots create adapters and attach them here. Application code queries through ports.
/// This is the single orchestration point — both CLI and Server.Mcp consume this.
/// </summary>
public sealed class WorkspaceSession
{
    public SemanticGraph? Graph { get; private set; }
    public AnalysisResult? Analysis { get; private set; }
    public AdapterCapability? Capability { get; private set; }
    public WorkspaceCapability WorkspaceOps { get; private set; } = WorkspaceCapability.None;
    public string? Language { get; private set; }

    public bool IsLoaded => Graph != null;

    /// <summary>Write-side ports. Null when loaded from JSON graph (no compilation state).</summary>
    public ICompilationHost? CompilationHost { get; private set; }
    public ICodeExecutor? CodeExecutor { get; private set; }
    public IWorkspaceRefactoring? Refactoring { get; private set; }
    public bool HasCompilationState => CompilationHost != null;

    /// <summary>
    /// Load a validated graph and optional analysis into the session.
    /// Write-side ports are attached separately via <see cref="AttachCompilationServices"/>.
    /// </summary>
    public void Load(SemanticGraph graph, AnalysisResult analysis,
        AdapterCapability? capability, string? language)
    {
        Graph = graph;
        Analysis = analysis;
        Capability = capability;
        Language = language;
    }

    /// <summary>
    /// Attach write-side compilation services. Only available when loaded via Roslyn adapter.
    /// Called by composition roots after graph analysis.
    /// </summary>
    public void AttachCompilationServices(
        ICompilationHost compilationHost,
        ICodeExecutor codeExecutor,
        IWorkspaceRefactoring refactoring,
        WorkspaceCapability? workspaceOps = null)
    {
        CompilationHost = compilationHost;
        CodeExecutor = codeExecutor;
        Refactoring = refactoring;
        WorkspaceOps = workspaceOps ?? WorkspaceCapability.RoslynFull;
    }

    /// <summary>
    /// Clear all state. Called before a new load to ensure atomic replacement.
    /// </summary>
    public void Clear()
    {
        Graph = null;
        Analysis = null;
        Capability = null;
        Language = null;
        CompilationHost = null;
        CodeExecutor = null;
        Refactoring = null;
        WorkspaceOps = WorkspaceCapability.None;
    }
}

using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Workspaces;

namespace Lifeblood.Application.UseCases;

/// <summary>
/// Publishes the loaded workspace as one immutable reference. Composition
/// roots create adapters and ports; Application consumers observe a coherent
/// <see cref="WorkspaceSnapshot"/> rather than independently mutable fields.
/// </summary>
public sealed class WorkspaceSession
{
    private WorkspaceSnapshot _current = WorkspaceSnapshot.Empty();

    public WorkspaceSnapshot Current => Volatile.Read(ref _current);

    public SemanticGraph? Graph => Current.Graph;
    public AnalysisResult? Analysis => Current.Analysis;
    public AdapterCapability? Capability => Current.Capability;
    public WorkspaceCapability WorkspaceOps => Current.WorkspaceOps;
    public string? Language => Current.Language;
    public WorkspaceContext? Context => Current.Context;
    public DateTime? AnalyzedAtUtc => Current.AnalyzedAtUtc;

    /// <summary>
    /// Monotonic counter incremented every time <see cref="Load"/> is
    /// called — covers full analyze, incremental analyze, and
    /// auto-refresh. Survives <see cref="Clear"/> so two reads taken
    /// either side of a Clear/Load pair see distinct generation values.
    /// Zero before the first Load. Surfaced through
    /// <see cref="Lifeblood.Application.Ports.Right.EnvelopeContext.AnalysisGeneration"/>
    /// onto every read-side response. INV-DIAGNOSE-FRESHNESS-001.
    /// </summary>
    public long AnalysisGeneration => Current.AnalysisGeneration;

    public bool IsLoaded => Current.IsLoaded;

    /// <summary>Write-side ports. Null when loaded from JSON graph (no compilation state).</summary>
    public ICompilationHost? CompilationHost => Current.CompilationHost;
    public ICodeExecutor? CodeExecutor => Current.CodeExecutor;
    public IWorkspaceRefactoring? Refactoring => Current.Refactoring;
    public bool HasCompilationState => Current.HasCompilationState;

    /// <summary>
    /// Load a validated graph and optional analysis into the session.
    /// Write-side ports are attached separately via <see cref="AttachCompilationServices"/>.
    /// </summary>
    public void Load(SemanticGraph graph, AnalysisResult analysis,
        AdapterCapability? capability, string? language)
    {
        var current = Current;
        Replace(WorkspaceSnapshot.Create(
            graph,
            analysis,
            capability,
            language,
            context: null,
            analyzedAtUtc: DateTime.UtcNow,
            analysisGeneration: checked(current.AnalysisGeneration + 1)));
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
        var current = Current;
        if (!current.IsLoaded || current.Graph == null || current.Analysis == null)
            throw new InvalidOperationException("Load a workspace before attaching compilation services.");
        if (current.HasCompilationState)
            throw new InvalidOperationException("Compilation services are already attached to the current snapshot.");

        Replace(WorkspaceSnapshot.Create(
            current.Graph,
            current.Analysis,
            current.Capability,
            current.Language,
            current.Context,
            current.AnalyzedAtUtc ?? DateTime.UtcNow,
            current.AnalysisGeneration,
            compilationHost,
            codeExecutor,
            refactoring,
            workspaceOps));
    }

    /// <summary>
    /// Clear all state. Called before a new load to ensure atomic replacement.
    /// Disposes write-side services if they implement IDisposable (e.g., AdhocWorkspace).
    /// Port interfaces don't extend IDisposable — the composition root checks concrete types.
    /// </summary>
    public void Clear()
    {
        var current = Current;
        Replace(WorkspaceSnapshot.Empty(current.AnalysisGeneration));
    }

    private void Replace(WorkspaceSnapshot candidate)
    {
        var replaced = Interlocked.Exchange(ref _current, candidate);
        if (!ReferenceEquals(replaced, candidate))
            replaced.Dispose();
    }
}

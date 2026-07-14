using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Lifeblood.Domain.Workspaces;

namespace Lifeblood.Application.UseCases;

/// <summary>
/// One immutable, language-neutral publication of a loaded workspace. Graph,
/// analysis, context, generation, and optional semantic ports always advance
/// together by replacing the containing reference.
/// </summary>
public sealed class WorkspaceSnapshot : IDisposable
{
    private int _retired;
    private int _portsDisposed;
    private int _leaseCount;

    private WorkspaceSnapshot(
        SemanticGraph? graph,
        AnalysisResult? analysis,
        AdapterCapability? capability,
        WorkspaceCapability workspaceOps,
        string? language,
        WorkspaceContext? context,
        DateTime? analyzedAtUtc,
        long analysisGeneration,
        SnapshotId snapshotId,
        ICompilationHost? compilationHost,
        ICodeExecutor? codeExecutor,
        IWorkspaceRefactoring? refactoring)
    {
        Graph = graph;
        Analysis = analysis;
        Capability = capability;
        WorkspaceOps = workspaceOps;
        Language = language;
        Context = context;
        AnalyzedAtUtc = analyzedAtUtc;
        AnalysisGeneration = analysisGeneration;
        SnapshotId = snapshotId;
        CompilationHost = compilationHost;
        CodeExecutor = codeExecutor;
        Refactoring = refactoring;
    }

    public SemanticGraph? Graph { get; }

    public AnalysisResult? Analysis { get; }

    public AdapterCapability? Capability { get; }

    public WorkspaceCapability WorkspaceOps { get; }

    public string? Language { get; }

    public WorkspaceContext? Context { get; }

    public DateTime? AnalyzedAtUtc { get; }

    public long AnalysisGeneration { get; }

    public SnapshotId SnapshotId { get; }

    public ICompilationHost? CompilationHost { get; }

    public ICodeExecutor? CodeExecutor { get; }

    public IWorkspaceRefactoring? Refactoring { get; }

    public bool IsLoaded => Graph != null;

    public bool HasCompilationState => CompilationHost != null;

    public static WorkspaceSnapshot Create(
        SemanticGraph graph,
        AnalysisResult analysis,
        AdapterCapability? capability,
        string? language,
        WorkspaceContext? context,
        DateTime analyzedAtUtc,
        long analysisGeneration,
        ICompilationHost? compilationHost = null,
        ICodeExecutor? codeExecutor = null,
        IWorkspaceRefactoring? refactoring = null,
        WorkspaceCapability? workspaceOps = null,
        SnapshotId? snapshotId = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(analysis);
        if (analysisGeneration <= 0)
            throw new ArgumentOutOfRangeException(nameof(analysisGeneration), "A loaded snapshot generation must be positive.");

        var compilationServiceCount =
            (compilationHost == null ? 0 : 1)
            + (codeExecutor == null ? 0 : 1)
            + (refactoring == null ? 0 : 1);
        if (compilationServiceCount is not (0 or 3))
            throw new ArgumentException("Compilation host, code executor, and refactoring ports must be published together.");

        return new WorkspaceSnapshot(
            graph,
            analysis,
            capability,
            compilationServiceCount == 0
                ? WorkspaceCapability.None
                : workspaceOps ?? WorkspaceCapability.RoslynFull,
            language,
            context,
            analyzedAtUtc,
            analysisGeneration,
            snapshotId ?? SnapshotId.New(),
            compilationHost,
            codeExecutor,
            refactoring);
    }

    public static WorkspaceSnapshot Empty(long analysisGeneration = 0)
    {
        if (analysisGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(analysisGeneration));

        return new WorkspaceSnapshot(
            graph: null,
            analysis: null,
            capability: null,
            workspaceOps: WorkspaceCapability.None,
            language: null,
            context: null,
            analyzedAtUtc: null,
            analysisGeneration: analysisGeneration,
            snapshotId: SnapshotId.None,
            compilationHost: null,
            codeExecutor: null,
            refactoring: null);
    }

    /// <summary>
    /// Pin this committed generation until the returned lease is disposed.
    /// A retired snapshot rejects new leases but existing leases remain valid.
    /// </summary>
    public WorkspaceSnapshotLease AcquireLease()
    {
        if (TryAcquireLease(out var lease))
            return lease;

        throw new ObjectDisposedException(nameof(WorkspaceSnapshot), "The snapshot has already been retired.");
    }

    public bool TryAcquireLease(out WorkspaceSnapshotLease lease)
    {
        lease = null!;
        if (Volatile.Read(ref _retired) != 0)
            return false;

        Interlocked.Increment(ref _leaseCount);
        if (Volatile.Read(ref _retired) == 0)
        {
            lease = new WorkspaceSnapshotLease(this);
            return true;
        }

        ReleaseLease();
        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _retired, 1) != 0)
            return;

        TryDisposePorts();
    }

    internal void ReleaseLease()
    {
        var remaining = Interlocked.Decrement(ref _leaseCount);
        if (remaining < 0)
            throw new InvalidOperationException("Workspace snapshot lease count became negative.");
        if (remaining == 0)
            TryDisposePorts();
    }

    private void TryDisposePorts()
    {
        if (Volatile.Read(ref _retired) == 0 || Volatile.Read(ref _leaseCount) != 0)
            return;
        if (Interlocked.Exchange(ref _portsDisposed, 1) != 0)
            return;

        var disposed = new HashSet<object>(ReferenceEqualityComparer.Instance);
        DisposeOnce(CompilationHost, disposed);
        DisposeOnce(CodeExecutor, disposed);
        DisposeOnce(Refactoring, disposed);
    }

    private static void DisposeOnce(object? value, HashSet<object> disposed)
    {
        if (value is IDisposable disposable && disposed.Add(value))
            disposable.Dispose();
    }
}

public sealed class WorkspaceSnapshotLease : IDisposable
{
    private WorkspaceSnapshot? _owner;

    internal WorkspaceSnapshotLease(WorkspaceSnapshot owner)
    {
        _owner = owner;
        Snapshot = owner;
    }

    public WorkspaceSnapshot Snapshot { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _owner, null)?.ReleaseLease();
    }
}

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
    private readonly WorkspaceCompilationServices? _compilationServices;

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
        WorkspaceAnalysisIdentity? identity,
        WorkspaceCompilationServices? compilationServices)
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
        Identity = identity;
        _compilationServices = compilationServices;
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

    public WorkspaceAnalysisIdentity? Identity { get; }

    public ICompilationHost? CompilationHost => _compilationServices?.CompilationHost;

    public ICodeExecutor? CodeExecutor => _compilationServices?.CodeExecutor;

    public IWorkspaceRefactoring? Refactoring => _compilationServices?.Refactoring;

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
        SnapshotId? snapshotId = null,
        WorkspaceAnalysisIdentity? identity = null)
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

        var compilationServices = compilationServiceCount == 0
            ? null
            : new WorkspaceCompilationServices(compilationHost!, codeExecutor!, refactoring!);
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
            identity,
            compilationServices);
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
            identity: null,
            compilationServices: null);
    }

    /// <summary>
    /// Publish a new analysis/identity over the same immutable graph and
    /// semantic service base. The service bundle is reference-counted so old
    /// snapshot leases and the new publication can coexist without rebuilding
    /// or prematurely disposing Roslyn state.
    /// </summary>
    public WorkspaceSnapshot DeriveAnalysis(
        AnalysisResult analysis,
        WorkspaceAnalysisIdentity identity,
        DateTime analyzedAtUtc,
        long analysisGeneration)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(identity);
        if (!IsLoaded || Graph == null)
            throw new InvalidOperationException("Cannot derive an unloaded workspace snapshot.");
        if (analysisGeneration <= AnalysisGeneration)
            throw new ArgumentOutOfRangeException(nameof(analysisGeneration), "A derived publication must advance generation.");

        var sharedServices = _compilationServices?.AddReference();
        try
        {
            return new WorkspaceSnapshot(
                Graph,
                analysis,
                Capability,
                WorkspaceOps,
                Language,
                Context,
                analyzedAtUtc,
                analysisGeneration,
                SnapshotId.New(),
                identity,
                sharedServices);
        }
        catch
        {
            sharedServices?.Release();
            throw;
        }
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

        _compilationServices?.Release();
    }
}

internal sealed class WorkspaceCompilationServices
{
    private int _referenceCount = 1;

    public WorkspaceCompilationServices(
        ICompilationHost compilationHost,
        ICodeExecutor codeExecutor,
        IWorkspaceRefactoring refactoring)
    {
        CompilationHost = compilationHost;
        CodeExecutor = codeExecutor;
        Refactoring = refactoring;
    }

    public ICompilationHost CompilationHost { get; }

    public ICodeExecutor CodeExecutor { get; }

    public IWorkspaceRefactoring Refactoring { get; }

    public WorkspaceCompilationServices AddReference()
    {
        while (true)
        {
            var current = Volatile.Read(ref _referenceCount);
            if (current == 0)
                throw new ObjectDisposedException(nameof(WorkspaceCompilationServices));
            if (Interlocked.CompareExchange(ref _referenceCount, checked(current + 1), current) == current)
                return this;
        }
    }

    public void Release()
    {
        var remaining = Interlocked.Decrement(ref _referenceCount);
        if (remaining < 0)
            throw new InvalidOperationException("Workspace compilation-service reference count became negative.");
        if (remaining != 0)
            return;

        var disposed = new HashSet<object>(ReferenceEqualityComparer.Instance);
        DisposeOnce(CompilationHost, disposed);
        DisposeOnce(CodeExecutor, disposed);
        DisposeOnce(Refactoring, disposed);
    }

    private static void DisposeOnce(object value, HashSet<object> disposed)
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

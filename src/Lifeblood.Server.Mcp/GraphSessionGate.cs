using Lifeblood.Application.UseCases;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// Host-edge session concurrency policy. Writers are single-flight; readers
/// lease immutable committed snapshots and do not block candidate construction.
/// </summary>
public interface ISessionGate
{
    T Read<T>(Func<T> action);

    T Read<T>(WorkspaceSnapshotPrecondition? precondition, Func<T> action);

    T Read<T>(WorkspaceSnapshotReadRequest request, Func<T> action);

    T Write<T>(Func<T> action);
}

public sealed class GraphSessionGate : ISessionGate, IDisposable
{
    private readonly object _writeSync = new();
    private readonly GraphSession? _session;
    private int _disposed;

    public GraphSessionGate(GraphSession? session = null)
    {
        _session = session;
    }

    public T Read<T>(Func<T> action)
    {
        ThrowIfDisposed();
        using var lease = _session?.AcquireReadLease();
        return action();
    }

    public T Read<T>(WorkspaceSnapshotPrecondition? precondition, Func<T> action)
        => precondition == null
            ? Read(action)
            : Read(new WorkspaceSnapshotReadRequest(selectedSnapshotId: null, precondition), action);

    public T Read<T>(WorkspaceSnapshotReadRequest request, Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        if (_session == null)
            throw new InvalidOperationException("Snapshot selection and preconditions require a bound graph session.");

        using var lease = request.SelectedSnapshotId == null
            ? _session.AcquireReadLease()
            : _session.AcquireReadLease(request.SelectedSnapshotId);
        if (request.Precondition != null)
        {
            if (request.Precondition.Compare(_session.CurrentSnapshot) is { } mismatch)
                throw new WorkspaceSnapshotPreconditionException(mismatch);
        }

        return action();
    }

    public T Write<T>(Func<T> action)
    {
        ThrowIfDisposed();
        lock (_writeSync)
        {
            ThrowIfDisposed();
            return action();
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(GraphSessionGate));
    }
}

public sealed class WorkspaceSnapshotPreconditionException : InvalidOperationException
{
    public WorkspaceSnapshotPreconditionException(WorkspaceSnapshotMismatch mismatch)
        : base("The leased workspace snapshot does not satisfy the requested optimistic-read precondition.")
    {
        Mismatch = mismatch ?? throw new ArgumentNullException(nameof(mismatch));
    }

    public WorkspaceSnapshotMismatch Mismatch { get; }
}

namespace Lifeblood.Server.Mcp;

/// <summary>
/// Host-edge session concurrency policy. Writers are single-flight; readers
/// lease immutable committed snapshots and do not block candidate construction.
/// </summary>
public interface ISessionGate
{
    T Read<T>(Func<T> action);

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

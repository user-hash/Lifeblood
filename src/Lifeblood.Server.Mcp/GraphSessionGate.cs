namespace Lifeblood.Server.Mcp;

/// <summary>
/// Host-edge session concurrency policy. Keeps future shared-server safety at
/// the MCP boundary instead of leaking locks into Domain/Application objects.
/// </summary>
public interface ISessionGate
{
    T Read<T>(Func<T> action);

    T Write<T>(Func<T> action);
}

public sealed class GraphSessionGate : ISessionGate, IDisposable
{
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    public T Read<T>(Func<T> action)
    {
        _lock.EnterReadLock();
        try
        {
            return action();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public T Write<T>(Func<T> action)
    {
        _lock.EnterWriteLock();
        try
        {
            return action();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Dispose() => _lock.Dispose();
}

using System.Collections.Concurrent;
using Lifeblood.Domain.Workspaces;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// Complete admission key for one execution request. Cold/full preparation
/// uses the live <see cref="AnalysisKey"/>; a retained same-workspace
/// incremental request uses the current publication key so concurrent callers
/// share one authoritative adapter scan instead of hashing the workspace once
/// per waiter. The policy fingerprint distinguishes every execution semantic.
/// </summary>
public sealed record AnalysisCoalescingKey(
    AnalysisKey AnalysisKey,
    ContentFingerprint ExecutionPolicy);

public sealed record AnalysisCoordinationResult<T>(
    T Value,
    string AnalysisRequestId,
    bool Coalesced,
    int WaiterCount);

/// <summary>
/// Joins exactly equal in-flight analyses. Each caller remains an independent
/// waiter: cancelling one waiter does not cancel work needed by another, while
/// cancellation of every waiter cancels the shared work token. Entries are
/// removed before completion is published so failures never poison retries.
/// </summary>
public sealed class AnalysisRequestCoordinator<T> : IDisposable
{
    private readonly ConcurrentDictionary<AnalysisCoalescingKey, Entry> _inFlight = new();
    private int _disposed;

    public int InFlightCount => _inFlight.Count;

    public AnalysisCoordinationResult<T> Execute(
        AnalysisCoalescingKey key,
        Func<CancellationToken, T> work,
        CancellationToken waiterCancellation = default)
        => ExecuteAsync(
                key,
                cancellation => Task.Run(() => work(cancellation), cancellation),
                waiterCancellation)
            .GetAwaiter()
            .GetResult();

    public async Task<AnalysisCoordinationResult<T>> ExecuteAsync(
        AnalysisCoalescingKey key,
        Func<CancellationToken, Task<T>> work,
        CancellationToken waiterCancellation = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(work);
        ThrowIfDisposed();

        Entry entry;
        bool ownsWork;
        while (true)
        {
            ThrowIfDisposed();
            entry = _inFlight.GetOrAdd(key, _ => new Entry());
            if (!entry.TryAddWaiter())
            {
                _inFlight.TryRemove(new KeyValuePair<AnalysisCoalescingKey, Entry>(key, entry));
                continue;
            }

            ownsWork = entry.TryClaimWork();
            break;
        }

        if (ownsWork)
            _ = RunWorkAsync(key, entry, work);

        try
        {
            var completion = await entry.Completion.Task.WaitAsync(waiterCancellation).ConfigureAwait(false);
            return new AnalysisCoordinationResult<T>(
                completion.Value,
                entry.RequestId,
                Coalesced: !ownsWork,
                completion.WaiterCount);
        }
        finally
        {
            entry.ReleaseWaiter();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var entry in _inFlight.Values)
            entry.CancelWork();
        _inFlight.Clear();
    }

    private async Task RunWorkAsync(
        AnalysisCoalescingKey key,
        Entry entry,
        Func<CancellationToken, Task<T>> work)
    {
        try
        {
            var value = await work(entry.WorkCancellation).ConfigureAwait(false);
            var waiterCount = entry.CloseToNewWaiters();
            _inFlight.TryRemove(new KeyValuePair<AnalysisCoalescingKey, Entry>(key, entry));
            entry.Completion.TrySetResult(new Completion(value, waiterCount));
        }
        catch (OperationCanceledException ex)
        {
            entry.CloseToNewWaiters();
            _inFlight.TryRemove(new KeyValuePair<AnalysisCoalescingKey, Entry>(key, entry));
            entry.Completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            entry.CloseToNewWaiters();
            _inFlight.TryRemove(new KeyValuePair<AnalysisCoalescingKey, Entry>(key, entry));
            entry.Completion.TrySetException(ex);
        }
        finally
        {
            entry.DisposeWorkCancellation();
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(AnalysisRequestCoordinator<T>));
    }

    private sealed class Entry
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _workCancellation = new();
        private bool _acceptingWaiters = true;
        private bool _workClaimed;
        private int _activeWaiters;
        private int _totalWaiters;

        public string RequestId { get; } = $"analysis_request_{Guid.NewGuid():N}";

        public TaskCompletionSource<Completion> Completion { get; }
            = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken WorkCancellation => _workCancellation.Token;

        public bool TryAddWaiter()
        {
            lock (_sync)
            {
                if (!_acceptingWaiters)
                    return false;

                _activeWaiters++;
                _totalWaiters++;
                return true;
            }
        }

        public bool TryClaimWork()
        {
            lock (_sync)
            {
                if (_workClaimed)
                    return false;
                _workClaimed = true;
                return true;
            }
        }

        public int CloseToNewWaiters()
        {
            lock (_sync)
            {
                _acceptingWaiters = false;
                return _totalWaiters;
            }
        }

        public void ReleaseWaiter()
        {
            var cancelWork = false;
            lock (_sync)
            {
                _activeWaiters--;
                if (_activeWaiters < 0)
                    throw new InvalidOperationException("Analysis waiter count became negative.");
                cancelWork = _activeWaiters == 0 && !Completion.Task.IsCompleted;
                if (cancelWork)
                    _acceptingWaiters = false;
            }

            if (cancelWork)
                CancelWork();
        }

        public void CancelWork()
        {
            try { _workCancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void DisposeWorkCancellation() => _workCancellation.Dispose();
    }

    private sealed record Completion(T Value, int WaiterCount);
}

internal sealed record PreparedAnalyzeRequest(
    AnalyzeToolRequest Request,
    WorkspaceAnalysisIdentity Identity,
    AnalysisCoalescingKey CoalescingKey,
    AnalysisKey? ExpectedAnalysisKey);

internal enum AnalysisPreparationMode
{
    ExactInputIdentity,
    CommittedIncrementalBase,
}

internal sealed class AnalysisInputChangedException : InvalidOperationException
{
    public AnalysisInputChangedException(AnalysisKey expected, AnalysisKey actual)
        : base($"Analysis inputs changed during candidate construction. Expected '{expected.Value}', observed '{actual.Value}'. Retry against the new source fingerprint.")
    {
        Expected = expected;
        Actual = actual;
    }

    public AnalysisKey Expected { get; }

    public AnalysisKey Actual { get; }
}

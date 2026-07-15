namespace Lifeblood.Server.Mcp;

internal interface ISharedDaemonTimeSource
{
    DateTimeOffset UtcNow { get; }

    IDisposable Schedule(TimeSpan delay, Action callback);
}

public interface ISharedDaemonStatusProvider
{
    SharedDaemonStatusSnapshot CaptureStatus();
}

public enum SharedDaemonLifecycleState
{
    Running,
    IdleWaiting,
    Draining,
    Stopped,
}

public sealed record SharedDaemonStatusSnapshot(
    string DaemonInstanceId,
    DateTimeOffset StartedAtUtc,
    TimeSpan IdleTimeout,
    SharedDaemonLifecycleState State,
    int ClientCount,
    int ActiveRequestCount,
    DateTimeOffset LastActivityUtc,
    DateTimeOffset? IdleDeadlineUtc);

public sealed record SharedMaintenanceDrainResult(
    bool Accepted,
    bool CoordinatedDrain,
    int UnrelatedClientCount,
    int OtherActiveRequestCount,
    int InFlightAnalysisCount,
    string? RejectionReason);

/// <summary>
/// Owns shared-daemon client leases and the last-client idle transition. Pipe
/// connections are transport details; this coordinator owns only lifecycle
/// state, request activity, and the shutdown signal consumed by the host.
/// </summary>
internal sealed class SharedDaemonLifecycle : IDisposable, ISharedDaemonStatusProvider
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ClientRegistration> _clients = new(StringComparer.Ordinal);
    private readonly TimeSpan _idleTimeout;
    private readonly ISharedDaemonTimeSource _timeSource;
    private readonly CancellationTokenSource _shutdown = new();
    private IDisposable? _idleSchedule;
    private SharedDaemonLifecycleState _state = SharedDaemonLifecycleState.Running;
    private DateTimeOffset _lastActivityUtc;
    private DateTimeOffset? _idleDeadlineUtc;
    private long _idleEpoch;
    private int _activeRequestCount;
    private int _disposed;

    public SharedDaemonLifecycle(
        TimeSpan idleTimeout,
        ISharedDaemonTimeSource? timeSource = null)
    {
        if (idleTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));

        _idleTimeout = idleTimeout;
        _timeSource = timeSource ?? SystemSharedDaemonTimeSource.Instance;
        StartedAtUtc = _timeSource.UtcNow;
        _lastActivityUtc = StartedAtUtc;
        DaemonInstanceId = $"daemon_{Guid.NewGuid():N}";
    }

    public string DaemonInstanceId { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public CancellationToken ShutdownToken => _shutdown.Token;

    public SharedClientLease AcquireClient(
        string clientId,
        IReadOnlyCollection<string> capabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(capabilities);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        lock (_sync)
        {
            if (_state is SharedDaemonLifecycleState.Draining or SharedDaemonLifecycleState.Stopped)
                throw new InvalidOperationException("Shared daemon is draining and cannot accept another client lease.");
            if (_clients.Values.Any(client => string.Equals(client.ClientId, clientId, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Shared client '{clientId}' already owns an active lease.");

            CancelIdleScheduleUnderLock();
            _state = SharedDaemonLifecycleState.Running;
            _idleDeadlineUtc = null;
            _lastActivityUtc = _timeSource.UtcNow;

            var leaseId = $"lease_{Guid.NewGuid():N}";
            _clients.Add(
                leaseId,
                new ClientRegistration(clientId, capabilities.ToArray()));
            return new SharedClientLease(this, leaseId);
        }
    }

    public IDisposable BeginRequest(string leaseId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);
        lock (_sync)
        {
            if (!_clients.ContainsKey(leaseId))
                throw new InvalidOperationException($"Shared client lease '{leaseId}' is not active.");
            if (_state != SharedDaemonLifecycleState.Running)
                throw new InvalidOperationException("Shared daemon is not accepting requests.");

            _activeRequestCount++;
            _lastActivityUtc = _timeSource.UtcNow;
            return new RequestActivity(this);
        }
    }

    public SharedDaemonStatusSnapshot CaptureStatus()
    {
        lock (_sync)
        {
            return new SharedDaemonStatusSnapshot(
                DaemonInstanceId,
                StartedAtUtc,
                _idleTimeout,
                _state,
                _clients.Count,
                _activeRequestCount,
                _lastActivityUtc,
                _idleDeadlineUtc);
        }
    }

    public SharedMaintenanceDrainResult RequestMaintenanceDrain(
        string requestingLeaseId,
        int inFlightAnalysisCount,
        bool coordinatedDrain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestingLeaseId);
        if (inFlightAnalysisCount < 0)
            throw new ArgumentOutOfRangeException(nameof(inFlightAnalysisCount));

        SharedMaintenanceDrainResult result;
        var requestShutdown = false;
        lock (_sync)
        {
            if (!_clients.ContainsKey(requestingLeaseId))
                throw new InvalidOperationException($"Shared client lease '{requestingLeaseId}' is not active.");

            var unrelatedClients = Math.Max(0, _clients.Count - 1);
            var otherActiveRequests = Math.Max(0, _activeRequestCount - 1);
            var hasBlockers = unrelatedClients > 0
                || otherActiveRequests > 0
                || inFlightAnalysisCount > 0;
            if (hasBlockers && !coordinatedDrain)
            {
                return new SharedMaintenanceDrainResult(
                    Accepted: false,
                    CoordinatedDrain: false,
                    UnrelatedClientCount: unrelatedClients,
                    OtherActiveRequestCount: otherActiveRequests,
                    InFlightAnalysisCount: inFlightAnalysisCount,
                    RejectionReason: "Maintenance drain requires exclusive client ownership and no in-flight analysis, or an explicit coordinated drain.");
            }

            CancelIdleScheduleUnderLock();
            _state = SharedDaemonLifecycleState.Draining;
            _idleDeadlineUtc = null;
            result = new SharedMaintenanceDrainResult(
                Accepted: true,
                CoordinatedDrain: coordinatedDrain,
                UnrelatedClientCount: unrelatedClients,
                OtherActiveRequestCount: otherActiveRequests,
                InFlightAnalysisCount: inFlightAnalysisCount,
                RejectionReason: null);
            requestShutdown = true;
        }

        if (requestShutdown)
        {
            try { _shutdown.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        return result;
    }

    public void MarkStopped()
    {
        lock (_sync)
        {
            if (_state == SharedDaemonLifecycleState.Stopped)
                return;

            CancelIdleScheduleUnderLock();
            _state = SharedDaemonLifecycleState.Stopped;
            _idleDeadlineUtc = null;
            _clients.Clear();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        MarkStopped();
        try { _shutdown.Cancel(); }
        catch (ObjectDisposedException) { }
        _shutdown.Dispose();
    }

    private void ReleaseClient(string leaseId)
    {
        long? scheduleEpoch = null;
        lock (_sync)
        {
            if (!_clients.Remove(leaseId))
                return;

            _lastActivityUtc = _timeSource.UtcNow;
            if (_clients.Count != 0
                || _state is SharedDaemonLifecycleState.Draining or SharedDaemonLifecycleState.Stopped)
            {
                return;
            }

            _state = SharedDaemonLifecycleState.IdleWaiting;
            _idleDeadlineUtc = _lastActivityUtc + _idleTimeout;
            scheduleEpoch = ++_idleEpoch;
        }

        IDisposable schedule;
        try
        {
            schedule = _timeSource.Schedule(
                _idleTimeout,
                () => OnIdleDeadline(scheduleEpoch.Value));
        }
        catch
        {
            // A daemon that cannot arm its configured eviction deadline must
            // drain instead of retaining an ownerless Roslyn heap forever.
            OnIdleDeadline(scheduleEpoch.Value);
            return;
        }
        lock (_sync)
        {
            if (_idleEpoch == scheduleEpoch.Value
                && _state == SharedDaemonLifecycleState.IdleWaiting
                && _clients.Count == 0)
            {
                _idleSchedule = schedule;
            }
            else
            {
                schedule.Dispose();
            }
        }
    }

    private void OnIdleDeadline(long epoch)
    {
        var requestShutdown = false;
        lock (_sync)
        {
            if (epoch != _idleEpoch
                || _clients.Count != 0
                || _state != SharedDaemonLifecycleState.IdleWaiting)
            {
                return;
            }

            _idleSchedule = null;
            _idleDeadlineUtc = null;
            _state = SharedDaemonLifecycleState.Draining;
            requestShutdown = true;
        }

        if (requestShutdown)
        {
            try { _shutdown.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private void CompleteRequest()
    {
        lock (_sync)
        {
            if (_activeRequestCount <= 0)
                throw new InvalidOperationException("Shared daemon active-request count became negative.");

            _activeRequestCount--;
            _lastActivityUtc = _timeSource.UtcNow;
        }
    }

    private void CancelIdleScheduleUnderLock()
    {
        _idleEpoch++;
        _idleSchedule?.Dispose();
        _idleSchedule = null;
    }

    private sealed record ClientRegistration(
        string ClientId,
        string[] Capabilities);

    internal sealed class SharedClientLease : IDisposable
    {
        private SharedDaemonLifecycle? _owner;

        public SharedClientLease(SharedDaemonLifecycle owner, string leaseId)
        {
            _owner = owner;
            LeaseId = leaseId;
        }

        public string LeaseId { get; }

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.ReleaseClient(LeaseId);
    }

    private sealed class RequestActivity : IDisposable
    {
        private SharedDaemonLifecycle? _owner;

        public RequestActivity(SharedDaemonLifecycle owner)
        {
            _owner = owner;
        }

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.CompleteRequest();
    }
}

internal sealed class SystemSharedDaemonTimeSource : ISharedDaemonTimeSource
{
    public static SystemSharedDaemonTimeSource Instance { get; } = new();

    private SystemSharedDaemonTimeSource()
    {
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return new OneShotTimer(delay, callback);
    }

    private sealed class OneShotTimer : IDisposable
    {
        private readonly Timer _timer;

        public OneShotTimer(TimeSpan delay, Action callback)
        {
            _timer = new Timer(
                static state => ((Action)state!).Invoke(),
                callback,
                delay,
                Timeout.InfiniteTimeSpan);
        }

        public void Dispose() => _timer.Dispose();
    }
}

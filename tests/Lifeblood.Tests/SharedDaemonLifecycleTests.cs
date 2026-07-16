using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

public sealed class SharedDaemonLifecycleTests
{
    [Fact]
    public void IdlePolicy_IsDisabledByDefaultAndPreservesLongExplicitIntervals()
    {
        Assert.Null(SharedMcpTransport.ParseIdleTimeout(null));
        Assert.Null(SharedMcpTransport.ParseIdleTimeout("invalid"));
        Assert.Null(SharedMcpTransport.ParseIdleTimeout("-1"));

        var requested = TimeSpan.FromDays(365);
        var rawSeconds = requested.TotalSeconds.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(requested, SharedMcpTransport.ParseIdleTimeout(rawSeconds));

        var callbackCount = 0;
        using var schedule = SystemSharedDaemonTimeSource.Instance.Schedule(
            requested,
            () => Interlocked.Increment(ref callbackCount));
        Assert.Equal(0, Volatile.Read(ref callbackCount));
    }

    [Fact]
    public void NoIdlePolicy_LastReleaseRetainsDaemonWithoutSchedulingEviction()
    {
        var clock = new ManualSharedDaemonTimeSource();
        using var lifecycle = new SharedDaemonLifecycle(idleTimeout: null, clock);
        var client = lifecycle.AcquireClient("client", Array.Empty<string>());

        client.Dispose();

        var status = lifecycle.CaptureStatus();
        Assert.Equal(SharedDaemonLifecycleState.Running, status.State);
        Assert.Equal(0, status.ClientCount);
        Assert.Null(status.IdleTimeout);
        Assert.Null(status.IdleDeadlineUtc);
        Assert.Equal(0, clock.ScheduleCount);
        Assert.False(lifecycle.ShutdownToken.IsCancellationRequested);
    }

    [Fact]
    public void ClientLeases_AreCountedAndLastReleaseStartsIdleDeadline()
    {
        var clock = new ManualSharedDaemonTimeSource();
        using var lifecycle = new SharedDaemonLifecycle(TimeSpan.FromMinutes(5), clock);
        using var first = lifecycle.AcquireClient("client-one", new[] { "status-v1" });
        using var second = lifecycle.AcquireClient("client-two", new[] { "status-v1" });

        var connected = lifecycle.CaptureStatus();
        Assert.Equal(SharedDaemonLifecycleState.Running, connected.State);
        Assert.Equal(2, connected.ClientCount);
        Assert.Null(connected.IdleDeadlineUtc);

        first.Dispose();
        Assert.Equal(1, lifecycle.CaptureStatus().ClientCount);
        Assert.Null(lifecycle.CaptureStatus().IdleDeadlineUtc);

        second.Dispose();
        var idle = lifecycle.CaptureStatus();
        Assert.Equal(SharedDaemonLifecycleState.IdleWaiting, idle.State);
        Assert.Equal(0, idle.ClientCount);
        Assert.Equal(clock.UtcNow.AddMinutes(5), idle.IdleDeadlineUtc);
        Assert.False(lifecycle.ShutdownToken.IsCancellationRequested);
    }

    [Fact]
    public void Reattach_CancelsPendingIdleExitAndNextLastReleaseRearmsIt()
    {
        var clock = new ManualSharedDaemonTimeSource();
        using var lifecycle = new SharedDaemonLifecycle(TimeSpan.FromMinutes(5), clock);
        var first = lifecycle.AcquireClient("client-one", Array.Empty<string>());
        first.Dispose();

        clock.Advance(TimeSpan.FromMinutes(4));
        using var second = lifecycle.AcquireClient("client-two", Array.Empty<string>());
        Assert.Equal(SharedDaemonLifecycleState.Running, lifecycle.CaptureStatus().State);
        Assert.Null(lifecycle.CaptureStatus().IdleDeadlineUtc);

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(lifecycle.ShutdownToken.IsCancellationRequested);

        second.Dispose();
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True(lifecycle.ShutdownToken.IsCancellationRequested);
        Assert.Equal(SharedDaemonLifecycleState.Draining, lifecycle.CaptureStatus().State);
    }

    [Fact]
    public void IdleTimerSchedulerFailure_DrainsInsteadOfRetainingHeap()
    {
        var clock = new ManualSharedDaemonTimeSource { ThrowOnSchedule = true };
        using var lifecycle = new SharedDaemonLifecycle(TimeSpan.FromMinutes(5), clock);
        var client = lifecycle.AcquireClient("client", Array.Empty<string>());

        client.Dispose();

        var status = lifecycle.CaptureStatus();
        Assert.Equal(SharedDaemonLifecycleState.Draining, status.State);
        Assert.Equal(0, status.ClientCount);
        Assert.Null(status.IdleDeadlineUtc);
        Assert.True(lifecycle.ShutdownToken.IsCancellationRequested);
    }

    [Fact]
    public void RequestActivity_UpdatesCountsAndLastActivity()
    {
        var clock = new ManualSharedDaemonTimeSource();
        using var lifecycle = new SharedDaemonLifecycle(TimeSpan.FromMinutes(5), clock);
        using var client = lifecycle.AcquireClient("client", Array.Empty<string>());
        var before = lifecycle.CaptureStatus();

        clock.Advance(TimeSpan.FromSeconds(10));
        using (lifecycle.BeginRequest(client.LeaseId))
        {
            var active = lifecycle.CaptureStatus();
            Assert.Equal(1, active.ActiveRequestCount);
            Assert.Equal(clock.UtcNow, active.LastActivityUtc);
        }

        var completed = lifecycle.CaptureStatus();
        Assert.Equal(0, completed.ActiveRequestCount);
        Assert.Equal(clock.UtcNow, completed.LastActivityUtc);
        Assert.True(completed.LastActivityUtc > before.LastActivityUtc);
    }

    [Fact]
    public void DuplicateActiveClientIdentity_IsRejectedWithoutChangingLeaseCount()
    {
        var clock = new ManualSharedDaemonTimeSource();
        using var lifecycle = new SharedDaemonLifecycle(TimeSpan.FromMinutes(5), clock);
        using var client = lifecycle.AcquireClient("client", Array.Empty<string>());

        Assert.Throws<InvalidOperationException>(() =>
            lifecycle.AcquireClient("client", Array.Empty<string>()));
        Assert.Equal(1, lifecycle.CaptureStatus().ClientCount);
    }

    [Fact]
    public void MaintenanceDrain_RejectsBlockersUnlessCoordinated()
    {
        var clock = new ManualSharedDaemonTimeSource();
        using var lifecycle = new SharedDaemonLifecycle(TimeSpan.FromMinutes(5), clock);
        using var first = lifecycle.AcquireClient("first", Array.Empty<string>());
        using var second = lifecycle.AcquireClient("second", Array.Empty<string>());
        using var request = lifecycle.BeginRequest(first.LeaseId);
        using var unrelatedRequest = lifecycle.BeginRequest(second.LeaseId);

        var rejected = lifecycle.RequestMaintenanceDrain(
            first.LeaseId,
            inFlightAnalysisCount: 1,
            coordinatedDrain: false);
        Assert.False(rejected.Accepted);
        Assert.Equal(1, rejected.UnrelatedClientCount);
        Assert.Equal(1, rejected.OtherActiveRequestCount);
        Assert.Equal(1, rejected.InFlightAnalysisCount);
        Assert.False(lifecycle.ShutdownToken.IsCancellationRequested);
        Assert.Equal(SharedDaemonLifecycleState.Running, lifecycle.CaptureStatus().State);

        var accepted = lifecycle.RequestMaintenanceDrain(
            first.LeaseId,
            inFlightAnalysisCount: 1,
            coordinatedDrain: true);
        Assert.True(accepted.Accepted);
        Assert.True(accepted.CoordinatedDrain);
        Assert.True(lifecycle.ShutdownToken.IsCancellationRequested);
        Assert.Equal(SharedDaemonLifecycleState.Draining, lifecycle.CaptureStatus().State);
    }

    private sealed class ManualSharedDaemonTimeSource : ISharedDaemonTimeSource
    {
        private readonly object _sync = new();
        private readonly List<ScheduledCallback> _scheduled = new();

        public DateTimeOffset UtcNow { get; private set; }
            = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

        public bool ThrowOnSchedule { get; init; }

        public int ScheduleCount { get; private set; }

        public IDisposable Schedule(TimeSpan delay, Action callback)
        {
            ScheduleCount++;
            if (ThrowOnSchedule)
                throw new InvalidOperationException("Test scheduler failure.");

            var scheduled = new ScheduledCallback(UtcNow + delay, callback);
            lock (_sync)
                _scheduled.Add(scheduled);
            return scheduled;
        }

        public void Advance(TimeSpan duration)
        {
            UtcNow += duration;
            while (true)
            {
                ScheduledCallback[] due;
                lock (_sync)
                {
                    due = _scheduled
                        .Where(item => !item.IsDisposed && item.DueUtc <= UtcNow)
                        .ToArray();
                    foreach (var item in due)
                    {
                        item.Dispose();
                        _scheduled.Remove(item);
                    }
                }

                if (due.Length == 0)
                    return;
                foreach (var item in due)
                    item.Callback();
            }
        }

        private sealed class ScheduledCallback : IDisposable
        {
            private int _disposed;

            public ScheduledCallback(DateTimeOffset dueUtc, Action callback)
            {
                DueUtc = dueUtc;
                Callback = callback;
            }

            public DateTimeOffset DueUtc { get; }

            public Action Callback { get; }

            public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
        }
    }
}

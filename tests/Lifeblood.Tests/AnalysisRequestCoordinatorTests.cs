using Lifeblood.Domain.Workspaces;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

public sealed class AnalysisRequestCoordinatorTests
{
    [Fact]
    public async Task ExecuteAsync_IdenticalConcurrentRequestsRunOnce()
    {
        using var coordinator = new AnalysisRequestCoordinator<int>();
        var key = CreateKey("same");
        var entered = NewSignal();
        var release = NewSignal();
        var invocations = 0;

        async Task<int> Work(CancellationToken cancellation)
        {
            Interlocked.Increment(ref invocations);
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellation);
            return 42;
        }

        var owner = coordinator.ExecuteAsync(key, Work);
        await entered.Task;
        var waiters = Enumerable.Range(0, 3)
            .Select(_ => coordinator.ExecuteAsync(key, Work))
            .ToArray();

        release.TrySetResult();
        var results = await Task.WhenAll(waiters.Prepend(owner));

        Assert.Equal(1, invocations);
        Assert.Single(results.Select(result => result.AnalysisRequestId).Distinct(StringComparer.Ordinal));
        Assert.Single(results, result => !result.Coalesced);
        Assert.Equal(3, results.Count(result => result.Coalesced));
        Assert.All(results, result =>
        {
            Assert.Equal(42, result.Value);
            Assert.Equal(4, result.WaiterCount);
        });
        Assert.Equal(0, coordinator.InFlightCount);
    }

    [Fact]
    public async Task ExecuteAsync_DifferentKeysRunIndependently()
    {
        using var coordinator = new AnalysisRequestCoordinator<int>();
        var release = NewSignal();
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = 0;

        async Task<int> Work(CancellationToken cancellation)
        {
            if (Interlocked.Increment(ref invocations) == 2)
                bothEntered.TrySetResult();
            await release.Task.WaitAsync(cancellation);
            return 1;
        }

        var first = coordinator.ExecuteAsync(CreateKey("first"), Work);
        var second = coordinator.ExecuteAsync(CreateKey("second"), Work);
        await bothEntered.Task;
        release.TrySetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(2, invocations);
        Assert.NotEqual(results[0].AnalysisRequestId, results[1].AnalysisRequestId);
        Assert.All(results, result => Assert.False(result.Coalesced));
    }

    [Fact]
    public async Task ExecuteAsync_CancellingOneWaiterKeepsSharedWorkAlive()
    {
        using var coordinator = new AnalysisRequestCoordinator<int>();
        using var cancelledWaiter = new CancellationTokenSource();
        var entered = NewSignal();
        var release = NewSignal();
        var workCancelled = 0;

        async Task<int> Work(CancellationToken cancellation)
        {
            using var registration = cancellation.Register(() => Interlocked.Exchange(ref workCancelled, 1));
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellation);
            return 7;
        }

        var owner = coordinator.ExecuteAsync(CreateKey("shared"), Work);
        await entered.Task;
        var waiter = coordinator.ExecuteAsync(CreateKey("shared"), Work, cancelledWaiter.Token);
        cancelledWaiter.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.Equal(0, Volatile.Read(ref workCancelled));

        release.TrySetResult();
        var result = await owner;
        Assert.Equal(7, result.Value);
        Assert.Equal(0, Volatile.Read(ref workCancelled));
    }

    [Fact]
    public async Task ExecuteAsync_CancellingEveryWaiterCancelsSharedWork()
    {
        using var coordinator = new AnalysisRequestCoordinator<int>();
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        var entered = NewSignal();
        var workCancelled = NewSignal();

        async Task<int> Work(CancellationToken cancellation)
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                return 0;
            }
            finally
            {
                if (cancellation.IsCancellationRequested)
                    workCancelled.TrySetResult();
            }
        }

        var first = coordinator.ExecuteAsync(CreateKey("cancel-all"), Work, firstCancellation.Token);
        await entered.Task;
        var second = coordinator.ExecuteAsync(CreateKey("cancel-all"), Work, secondCancellation.Token);
        firstCancellation.Cancel();
        secondCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        await workCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(SpinWait.SpinUntil(() => coordinator.InFlightCount == 0, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Execute_SynchronousOwnerCancellationStopsAcceptingLateWaiters()
    {
        using var coordinator = new AnalysisRequestCoordinator<int>();
        using var waiterCancellation = new CancellationTokenSource();
        using var permitOldWorkToExit = new ManualResetEventSlim();
        var key = CreateKey("sync-cancel-retry");
        var entered = NewSignal();
        var workCancellationObserved = NewSignal();
        var invocations = 0;

        var cancelledOwner = Task.Run(() => coordinator.Execute(
            key,
            cancellationToken =>
            {
                Interlocked.Increment(ref invocations);
                entered.TrySetResult();
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
                if (cancellationToken.IsCancellationRequested)
                    workCancellationObserved.TrySetResult();
                permitOldWorkToExit.Wait(TimeSpan.FromSeconds(5));
                cancellationToken.ThrowIfCancellationRequested();
                return 1;
            },
            waiterCancellation.Token));

        await entered.Task;
        waiterCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledOwner);
        await workCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var retry = await coordinator.ExecuteAsync(
            key,
            _ =>
            {
                Interlocked.Increment(ref invocations);
                return Task.FromResult(9);
            });

        Assert.Equal(9, retry.Value);
        Assert.False(retry.Coalesced);
        Assert.Equal(2, invocations);
        permitOldWorkToExit.Set();
    }

    [Fact]
    public async Task ExecuteAsync_FailureFansOutAndDoesNotPoisonRetry()
    {
        using var coordinator = new AnalysisRequestCoordinator<int>();
        var key = CreateKey("retry");
        var entered = NewSignal();
        var releaseFailure = NewSignal();
        var failure = new InvalidOperationException("boom");
        var invocations = 0;

        async Task<int> Fail(CancellationToken cancellation)
        {
            Interlocked.Increment(ref invocations);
            entered.TrySetResult();
            await releaseFailure.Task.WaitAsync(cancellation);
            throw failure;
        }

        var owner = coordinator.ExecuteAsync(key, Fail);
        await entered.Task;
        var waiter = coordinator.ExecuteAsync(key, Fail);
        releaseFailure.TrySetResult();

        var ownerFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => owner);
        var waiterFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => waiter);
        Assert.Same(failure, ownerFailure);
        Assert.Same(failure, waiterFailure);
        Assert.Equal(1, invocations);

        var retry = await coordinator.ExecuteAsync(
            key,
            _ =>
            {
                Interlocked.Increment(ref invocations);
                return Task.FromResult(9);
            });
        Assert.Equal(9, retry.Value);
        Assert.Equal(2, invocations);
        Assert.False(retry.Coalesced);
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static AnalysisCoalescingKey CreateKey(string discriminator)
    {
        var ruleSet = new RuleSetIdentity(
            RuleSetSourceKind.None,
            source: null,
            ContentFingerprint.ComputeUtf8("test.rules", "none"));
        var spec = new AnalysisSpec(
            new[] { "Editor" },
            Array.Empty<string>(),
            AnalysisRetentionMode.RetainedSemantic,
            AnalysisDescriptorPolicy.WorkspaceDiscovery,
            ruleSet);
        var source = new SourceFingerprint(
            new[]
            {
                new FingerprintEntry(
                    "Source.cs",
                    ContentFingerprint.ComputeUtf8("test.source", discriminator)),
            },
            Array.Empty<FingerprintEntry>());
        var identity = new WorkspaceAnalysisIdentity(
            WorkspaceKey.FromCanonicalIdentity("test-workspace"),
            spec,
            source);
        return new AnalysisCoalescingKey(
            identity.AnalysisKey,
            ContentFingerprint.ComputeUtf8("test.policy", discriminator));
    }
}

using Lifeblood.Application.UseCases;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Xunit;

namespace Lifeblood.Tests;

public sealed class WorkspaceSnapshotCatalogTests
{
    [Fact]
    public void Retain_UsesUnpinnedLruAndNeverExceedsHardBound()
    {
        var time = new ManualTimeProvider();
        using var catalog = new WorkspaceSnapshotCatalog(
            new WorkspaceSnapshotCatalogOptions(2, TimeSpan.Zero),
            time);
        using var first = CreateSnapshot(1);
        using var second = CreateSnapshot(2);
        using var third = CreateSnapshot(3);

        Assert.True(catalog.Retain(first).Succeeded);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(catalog.Retain(second).Succeeded);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(catalog.TryAcquire(first.SnapshotId, out var firstLease));
        firstLease.Dispose();
        time.Advance(TimeSpan.FromMinutes(1));

        var mutation = catalog.Retain(third);

        Assert.True(mutation.Succeeded);
        Assert.Equal(second.SnapshotId, mutation.EvictedSnapshotId);
        Assert.Equal(
            new[] { first.SnapshotId, third.SnapshotId }.OrderBy(id => id.Value),
            catalog.List().Select(entry => entry.Snapshot.SnapshotId).OrderBy(id => id.Value));
    }

    [Fact]
    public void PinnedCapacity_RefusesAutomaticRetentionAndReportsDrop()
    {
        using var catalog = new WorkspaceSnapshotCatalog(
            new WorkspaceSnapshotCatalogOptions(1, TimeSpan.Zero));
        using var first = CreateSnapshot(1);
        using var second = CreateSnapshot(2);

        Assert.True(catalog.Pin(first, "baseline").Succeeded);

        var blocked = catalog.Retain(second);
        var conflict = catalog.Pin(second, "BASELINE");

        Assert.Equal(WorkspaceSnapshotCatalogMutationStatus.CapacityBlocked, blocked.Status);
        Assert.Equal(1, catalog.DroppedAutomaticRetentionCount);
        Assert.Equal(WorkspaceSnapshotCatalogMutationStatus.NameConflict, conflict.Status);
        var retained = Assert.Single(catalog.List());
        Assert.Equal(first.SnapshotId, retained.Snapshot.SnapshotId);
        Assert.True(retained.IsPinned);
        Assert.Equal("baseline", retained.Name);
    }

    [Fact]
    public void AgeExpiry_RemovesOnlyUnpinnedEntries()
    {
        var time = new ManualTimeProvider();
        using var catalog = new WorkspaceSnapshotCatalog(
            new WorkspaceSnapshotCatalogOptions(3, TimeSpan.FromMinutes(10)),
            time);
        using var expiring = CreateSnapshot(1);
        using var pinned = CreateSnapshot(2);
        Assert.True(catalog.Retain(expiring).Succeeded);
        Assert.True(catalog.Pin(pinned, "investigation").Succeeded);

        time.Advance(TimeSpan.FromMinutes(11));

        var retained = Assert.Single(catalog.List());
        Assert.Equal(pinned.SnapshotId, retained.Snapshot.SnapshotId);
        Assert.True(retained.IsPinned);
    }

    [Fact]
    public void Eviction_RetiresCatalogEntryButExistingLeaseFinishes()
    {
        using var catalog = new WorkspaceSnapshotCatalog(
            new WorkspaceSnapshotCatalogOptions(1, TimeSpan.Zero));
        using var source = CreateSnapshot(1);
        Assert.True(catalog.Retain(source).Succeeded);
        Assert.True(catalog.TryAcquire(source.SnapshotId, out var lease));
        Assert.Equal(1, lease.Snapshot.ActiveLeaseCount);

        var eviction = catalog.Evict(source.SnapshotId);

        Assert.True(eviction.Succeeded);
        Assert.False(catalog.TryAcquire(source.SnapshotId, out _));
        Assert.True(lease.Snapshot.IsLoaded);
        Assert.Equal(source.SnapshotId, lease.Snapshot.SnapshotId);
        lease.Dispose();
        Assert.Equal(0, lease.Snapshot.ActiveLeaseCount);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(17)]
    public void Options_RejectHistoryLimitsOutsideHardBound(int historyLimit)
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WorkspaceSnapshotCatalogOptions(historyLimit, TimeSpan.Zero));

    private static WorkspaceSnapshot CreateSnapshot(long generation)
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol
            {
                Id = $"mod:Generation{generation}",
                Name = $"Generation{generation}",
                Kind = SymbolKind.Module,
            })
            .Build();
        return WorkspaceSnapshot.Create(
            graph,
            new AnalysisResult(),
            capability: null,
            language: "test",
            context: null,
            analyzedAtUtc: DateTime.UnixEpoch.AddMinutes(generation),
            analysisGeneration: generation);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}

using Lifeblood.Domain.Workspaces;

namespace Lifeblood.Application.UseCases;

/// <summary>
/// Transport-neutral retention policy for graph-only historical publications.
/// Pinned entries count toward the same hard bound; they are never a license
/// to grow an unbounded cache.
/// </summary>
public sealed record WorkspaceSnapshotCatalogOptions
{
    public const int DefaultHistoryLimit = 3;
    public const int HardMaximumHistoryLimit = 16;
    public static readonly TimeSpan DefaultMaximumAge = TimeSpan.FromHours(24);

    public WorkspaceSnapshotCatalogOptions(int historyLimit, TimeSpan maximumAge)
    {
        if (historyLimit is < 0 or > HardMaximumHistoryLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(historyLimit),
                $"History limit must be between 0 and {HardMaximumHistoryLimit}.");
        }
        if (maximumAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumAge));

        HistoryLimit = historyLimit;
        MaximumAge = maximumAge;
    }

    public int HistoryLimit { get; }

    /// <summary>Unpinned LRU expiry. Zero disables age-based eviction.</summary>
    public TimeSpan MaximumAge { get; }

    public static WorkspaceSnapshotCatalogOptions Default { get; } = new(
        DefaultHistoryLimit,
        DefaultMaximumAge);
}

public enum WorkspaceSnapshotCatalogMutationStatus
{
    Success,
    NotFound,
    CapacityBlocked,
    NameConflict,
    Pinned,
    Disabled,
}

public sealed record WorkspaceSnapshotCatalogMutation(
    WorkspaceSnapshotCatalogMutationStatus Status,
    SnapshotId TargetSnapshotId,
    SnapshotId? EvictedSnapshotId = null,
    string? Detail = null)
{
    public bool Succeeded => Status == WorkspaceSnapshotCatalogMutationStatus.Success;
}

public sealed record WorkspaceSnapshotCatalogEntry(
    WorkspaceSnapshot Snapshot,
    string? Name,
    bool IsPinned,
    DateTime CapturedAtUtc,
    DateTime LastAccessedAtUtc);

/// <summary>
/// Hard-bounded graph-only snapshot history. The catalog owns every retained
/// replica and retires it on eviction/disposal; existing leases may finish.
/// </summary>
public sealed class WorkspaceSnapshotCatalog : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<SnapshotId, EntryState> _entries = new();
    private readonly TimeProvider _timeProvider;
    private long _droppedAutomaticRetentionCount;
    private bool _disposed;

    public WorkspaceSnapshotCatalog(
        WorkspaceSnapshotCatalogOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        Options = options ?? WorkspaceSnapshotCatalogOptions.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public WorkspaceSnapshotCatalogOptions Options { get; }

    /// <summary>
    /// Number of automatic rollover publications that could not enter history
    /// because every configured slot was pinned. This diagnostic never relaxes
    /// the hard bound.
    /// </summary>
    public long DroppedAutomaticRetentionCount =>
        Interlocked.Read(ref _droppedAutomaticRetentionCount);

    public WorkspaceSnapshotCatalogMutation Retain(WorkspaceSnapshot source)
    {
        var mutation = RetainCore(source, isPinned: false, name: null);
        if (mutation.Status == WorkspaceSnapshotCatalogMutationStatus.CapacityBlocked)
            Interlocked.Increment(ref _droppedAutomaticRetentionCount);
        return mutation;
    }

    public WorkspaceSnapshotCatalogMutation Pin(WorkspaceSnapshot source, string? name)
        => RetainCore(source, isPinned: true, NormalizeName(name));

    public WorkspaceSnapshotCatalogMutation Pin(SnapshotId snapshotId, string? name)
    {
        ArgumentNullException.ThrowIfNull(snapshotId);
        var normalizedName = NormalizeName(name);
        lock (_sync)
        {
            ThrowIfDisposed();
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            PruneExpiredCore(now);
            if (!_entries.TryGetValue(snapshotId, out var entry))
                return Mutation(WorkspaceSnapshotCatalogMutationStatus.NotFound, snapshotId);
            if (HasNameConflict(snapshotId, normalizedName))
                return Mutation(WorkspaceSnapshotCatalogMutationStatus.NameConflict, snapshotId);

            entry.IsPinned = true;
            entry.Name = normalizedName ?? entry.Name;
            entry.LastAccessedAtUtc = now;
            return Mutation(WorkspaceSnapshotCatalogMutationStatus.Success, snapshotId);
        }
    }

    public WorkspaceSnapshotCatalogMutation Unpin(SnapshotId snapshotId)
    {
        ArgumentNullException.ThrowIfNull(snapshotId);
        lock (_sync)
        {
            ThrowIfDisposed();
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            PruneExpiredCore(now);
            if (!_entries.TryGetValue(snapshotId, out var entry))
                return Mutation(WorkspaceSnapshotCatalogMutationStatus.NotFound, snapshotId);

            entry.IsPinned = false;
            entry.LastAccessedAtUtc = now;
            EnforceLimitCore();
            return Mutation(WorkspaceSnapshotCatalogMutationStatus.Success, snapshotId);
        }
    }

    public WorkspaceSnapshotCatalogMutation Evict(SnapshotId snapshotId)
    {
        ArgumentNullException.ThrowIfNull(snapshotId);
        lock (_sync)
        {
            ThrowIfDisposed();
            PruneExpiredCore(_timeProvider.GetUtcNow().UtcDateTime);
            if (!_entries.TryGetValue(snapshotId, out var entry))
                return Mutation(WorkspaceSnapshotCatalogMutationStatus.NotFound, snapshotId);
            if (entry.IsPinned)
                return Mutation(
                    WorkspaceSnapshotCatalogMutationStatus.Pinned,
                    snapshotId,
                    detail: "Unpin the snapshot before explicit eviction.");

            RemoveCore(entry);
            return Mutation(WorkspaceSnapshotCatalogMutationStatus.Success, snapshotId);
        }
    }

    public bool TryAcquire(SnapshotId snapshotId, out WorkspaceSnapshotLease lease)
    {
        ArgumentNullException.ThrowIfNull(snapshotId);
        lock (_sync)
        {
            ThrowIfDisposed();
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            PruneExpiredCore(now);
            if (!_entries.TryGetValue(snapshotId, out var entry)
                || !entry.Snapshot.TryAcquireLease(out lease!))
            {
                lease = null!;
                return false;
            }

            entry.LastAccessedAtUtc = now;
            return true;
        }
    }

    public IReadOnlyList<WorkspaceSnapshotCatalogEntry> List()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            PruneExpiredCore(_timeProvider.GetUtcNow().UtcDateTime);
            return _entries.Values
                .OrderByDescending(entry => entry.Snapshot.AnalysisGeneration)
                .ThenBy(entry => entry.Snapshot.SnapshotId.Value, StringComparer.Ordinal)
                .Select(entry => entry.ToDescriptor())
                .ToArray();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            foreach (var entry in _entries.Values)
                entry.Snapshot.Dispose();
            _entries.Clear();
            Interlocked.Exchange(ref _droppedAutomaticRetentionCount, 0);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var entry in _entries.Values)
                entry.Snapshot.Dispose();
            _entries.Clear();
        }
    }

    private WorkspaceSnapshotCatalogMutation RetainCore(
        WorkspaceSnapshot source,
        bool isPinned,
        string? name)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsLoaded || source.SnapshotId.IsNone)
            throw new ArgumentException("Only loaded publications can enter snapshot history.", nameof(source));

        lock (_sync)
        {
            ThrowIfDisposed();
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            PruneExpiredCore(now);
            if (_entries.TryGetValue(source.SnapshotId, out var existing))
            {
                if (HasNameConflict(source.SnapshotId, name))
                    return Mutation(WorkspaceSnapshotCatalogMutationStatus.NameConflict, source.SnapshotId);
                existing.IsPinned |= isPinned;
                existing.Name = name ?? existing.Name;
                existing.LastAccessedAtUtc = now;
                return Mutation(WorkspaceSnapshotCatalogMutationStatus.Success, source.SnapshotId);
            }

            if (Options.HistoryLimit == 0)
                return Mutation(WorkspaceSnapshotCatalogMutationStatus.Disabled, source.SnapshotId);
            if (HasNameConflict(source.SnapshotId, name))
                return Mutation(WorkspaceSnapshotCatalogMutationStatus.NameConflict, source.SnapshotId);

            SnapshotId? evicted = null;
            while (_entries.Count >= Options.HistoryLimit)
            {
                var victim = FindEvictionCandidate();
                if (victim == null)
                {
                    return Mutation(
                        WorkspaceSnapshotCatalogMutationStatus.CapacityBlocked,
                        source.SnapshotId,
                        detail: "Every retained history slot is pinned.");
                }

                evicted = victim.Snapshot.SnapshotId;
                RemoveCore(victim);
            }

            var replica = source.CreateGraphOnlyReplica();
            _entries.Add(
                replica.SnapshotId,
                new EntryState(replica, name, isPinned, now, now));
            return Mutation(
                WorkspaceSnapshotCatalogMutationStatus.Success,
                source.SnapshotId,
                evicted);
        }
    }

    private void PruneExpiredCore(DateTime now)
    {
        if (Options.MaximumAge == TimeSpan.Zero)
            return;

        var expired = _entries.Values
            .Where(entry => !entry.IsPinned && now - entry.LastAccessedAtUtc >= Options.MaximumAge)
            .ToArray();
        foreach (var entry in expired)
            RemoveCore(entry);
    }

    private void EnforceLimitCore()
    {
        while (_entries.Count > Options.HistoryLimit)
        {
            var victim = FindEvictionCandidate();
            if (victim == null)
                return;
            RemoveCore(victim);
        }
    }

    private EntryState? FindEvictionCandidate()
        => _entries.Values
            .Where(entry => !entry.IsPinned)
            .OrderBy(entry => entry.LastAccessedAtUtc)
            .ThenBy(entry => entry.CapturedAtUtc)
            .ThenBy(entry => entry.Snapshot.SnapshotId.Value, StringComparer.Ordinal)
            .FirstOrDefault();

    private bool HasNameConflict(SnapshotId target, string? name)
        => name != null && _entries.Values.Any(entry =>
            entry.Snapshot.SnapshotId != target
            && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));

    private void RemoveCore(EntryState entry)
    {
        _entries.Remove(entry.Snapshot.SnapshotId);
        entry.Snapshot.Dispose();
    }

    private static string? NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var normalized = name.Trim();
        if (normalized.Length > 128)
            throw new ArgumentOutOfRangeException(nameof(name), "Snapshot names are limited to 128 characters.");
        if (normalized.Any(char.IsControl))
            throw new ArgumentException("Snapshot names cannot contain control characters.", nameof(name));
        return normalized;
    }

    private static WorkspaceSnapshotCatalogMutation Mutation(
        WorkspaceSnapshotCatalogMutationStatus status,
        SnapshotId target,
        SnapshotId? evicted = null,
        string? detail = null)
        => new(status, target, evicted, detail);

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WorkspaceSnapshotCatalog));
    }

    private sealed class EntryState
    {
        public EntryState(
            WorkspaceSnapshot snapshot,
            string? name,
            bool isPinned,
            DateTime capturedAtUtc,
            DateTime lastAccessedAtUtc)
        {
            Snapshot = snapshot;
            Name = name;
            IsPinned = isPinned;
            CapturedAtUtc = capturedAtUtc;
            LastAccessedAtUtc = lastAccessedAtUtc;
        }

        public WorkspaceSnapshot Snapshot { get; }

        public string? Name { get; set; }

        public bool IsPinned { get; set; }

        public DateTime CapturedAtUtc { get; }

        public DateTime LastAccessedAtUtc { get; set; }

        public WorkspaceSnapshotCatalogEntry ToDescriptor()
            => new(Snapshot, Name, IsPinned, CapturedAtUtc, LastAccessedAtUtc);
    }
}

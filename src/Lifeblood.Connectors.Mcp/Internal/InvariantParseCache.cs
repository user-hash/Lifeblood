using Lifeblood.Application.Ports.Infrastructure;

namespace Lifeblood.Connectors.Mcp.Internal;

/// <summary>
/// Per-project-root, timestamp-invalidated cache of parsed invariant
/// documents. Pure orchestration: file existence + timestamp lookup +
/// delegation to a caller-supplied parser function. No knowledge of
/// what is being parsed; works equally well for any future
/// <see cref="Lifeblood.Application.Ports.Right.Invariants.IInvariantProvider"/>
/// source (YAML, JSON, a REST API that returns text) as it does for
/// CLAUDE.md today.
///
/// <para>
/// Concern separation: this class knows only that
/// </para>
/// <list type="bullet">
///   <item>an invariant document lives at a filesystem path,</item>
///   <item>reading the document is expensive relative to reading its
///     modification timestamp,</item>
///   <item>a parser function can convert document text to any value
///     type the caller cares about.</item>
/// </list>
///
/// <para>
/// The provider orchestrates: it knows the path (project-root-relative
/// <c>CLAUDE.md</c>), the parser (the CLAUDE.md markdown extractor),
/// and the port shape (<see cref="Lifeblood.Application.Ports.Right.Invariants.Invariant"/>
/// results). The cache only knows paths, timestamps, and delegation.
/// </para>
///
/// <para>
/// Thread safety: a private lock guards the cache dictionary.
/// Concurrent reads for the same project root may each compute a
/// parse independently — the last writer wins and no one sees torn
/// state. Future optimisation could add per-key locks if the cold-path
/// parse ever becomes a contention bottleneck; today it is well below
/// the threshold that matters.
/// </para>
///
/// <para>
/// Stale detection is timestamp-based, not content-based. That means
/// a filesystem edit that updates the file without changing its
/// timestamp (rare but possible) will read stale cache. Timestamp
/// resolution depends on the underlying filesystem — NTFS is
/// effectively instantaneous, some network filesystems round to the
/// second. In practice the window is sub-millisecond for local edits
/// via normal tools.
/// </para>
///
/// Pinned by <c>InvariantParseCacheTests</c>.
/// </summary>
internal sealed class InvariantParseCache<T> where T : class
{
    private readonly IFileSystem _fs;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public InvariantParseCache(IFileSystem fs)
    {
        _fs = fs ?? throw new System.ArgumentNullException(nameof(fs));
    }

    /// <summary>
    /// Return the parsed result for <paramref name="sourcePath"/>,
    /// parsing fresh if the cache entry is missing or stale (its
    /// recorded timestamp diverges from the file's current
    /// last-write-time). Missing files are a first-class return: the
    /// caller gets <paramref name="emptyResult"/> and the method
    /// records the miss so repeated calls are O(stat).
    /// </summary>
    /// <param name="sourcePath">
    /// Absolute path to the document to read. Callers resolve this
    /// from whatever base they're working with — project root, module
    /// dir, workspace settings.
    /// </param>
    /// <param name="parser">
    /// Pure function that converts document text to a result value.
    /// Never called with a null or empty string in practice; the cache
    /// reads the file itself and passes the full text in.
    /// </param>
    /// <param name="emptyResult">
    /// The value to return (and cache) when the file does not exist.
    /// Reused to avoid allocating a new empty wrapper on every cold
    /// lookup for a missing file.
    /// </param>
    /// <returns>
    /// A cached entry with the parsed result, the path that was read,
    /// and the file's last-write timestamp ticks at the moment the
    /// cache entry was computed. Callers that want to surface the
    /// source path in tool output read <see cref="Entry.SourcePath"/>.
    /// </returns>
    public Entry GetOrLoad(string sourcePath, System.Func<string, T> parser, T emptyResult)
    {
        if (string.IsNullOrEmpty(sourcePath))
        {
            return new Entry("", 0, emptyResult);
        }

        long currentTimestamp = 0;
        bool exists = _fs.FileExists(sourcePath);
        if (exists)
        {
            try
            {
                currentTimestamp = _fs.GetLastWriteTimeUtc(sourcePath).Ticks;
            }
            catch
            {
                // Filesystem races (file vanished between FileExists
                // and GetLastWriteTimeUtc, permission changes, etc.)
                // degrade gracefully: zero timestamp means "always
                // reparse" which is slow but correct.
                currentTimestamp = 0;
            }
        }

        lock (_lock)
        {
            if (_cache.TryGetValue(sourcePath, out var cached)
                && cached.Timestamp == currentTimestamp
                && currentTimestamp != 0)
            {
                return cached;
            }
        }

        if (!exists)
        {
            var missingEntry = new Entry(sourcePath, 0, emptyResult);
            lock (_lock) { _cache[sourcePath] = missingEntry; }
            return missingEntry;
        }

        T result;
        try
        {
            var text = _fs.ReadAllText(sourcePath);
            result = parser(text);
        }
        catch (System.Exception)
        {
            // I/O races, permission changes, or an unexpected parser
            // throw: return a one-shot empty entry so the next call
            // retries against the (possibly-fixed) filesystem state.
            // The cache is NOT poisoned — we don't record this as the
            // entry for sourcePath.
            return new Entry(sourcePath, 0, emptyResult);
        }

        var freshEntry = new Entry(sourcePath, currentTimestamp, result);
        lock (_lock) { _cache[sourcePath] = freshEntry; }
        return freshEntry;
    }

    /// <summary>
    /// One cached parse. Value-type-like shape — all fields init-only,
    /// no mutation after construction.
    /// </summary>
    public sealed class Entry
    {
        public string SourcePath { get; }
        public long Timestamp { get; }
        public T Result { get; }

        public Entry(string sourcePath, long timestamp, T result)
        {
            SourcePath = sourcePath;
            Timestamp = timestamp;
            Result = result;
        }
    }
}

using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Connectors.Mcp.Internal;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Direct coverage for <see cref="InvariantParseCache{T}"/>. The cache is
/// the generic, reusable side of the Phase 8 invariant stack: it knows
/// only paths, timestamps, and delegation. The provider integration
/// tests in <see cref="InvariantProviderAndHandlerTests"/> exercise the
/// cache through <see cref="Lifeblood.Connectors.Mcp.LifebloodInvariantProvider"/>,
/// but they test the HAPPY path end to end, not the cache's own edge
/// cases.
///
/// These tests pin the concurrency and failure-mode claims the cache
/// makes in its XML doc: thread-safe reads, timestamp invalidation,
/// empty-path degradation, missing-file caching, and non-poisoning on
/// parser exceptions. They run against a <see cref="FakeFileSystem"/>
/// so they are deterministic, cheap, and do not touch disk.
///
/// Pinned invariants:
/// <list type="bullet">
///   <item>Empty source path returns an empty entry without invoking the parser.</item>
///   <item>Missing file is cached as a "known absent" entry.</item>
///   <item>Unchanged timestamp on hit returns the cached entry without invoking the parser.</item>
///   <item>Changed timestamp on hit invalidates and reparses.</item>
///   <item>Parser exceptions return a one-shot empty entry and do NOT poison the cache for the next call.</item>
/// </list>
/// </summary>
public class InvariantParseCacheTests
{
    private sealed class FakeFileSystem : IFileSystem
    {
        private readonly Dictionary<string, (string content, DateTime utc)> _files
            = new(StringComparer.OrdinalIgnoreCase);

        public int ReadCallCount { get; private set; }
        public int FileExistsCallCount { get; private set; }

        public void Put(string path, string content, DateTime? utc = null)
            => _files[path] = (content, utc ?? new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        public void Remove(string path) => _files.Remove(path);

        public bool FileExists(string path)
        {
            FileExistsCallCount++;
            return _files.ContainsKey(path);
        }

        public string ReadAllText(string path)
        {
            ReadCallCount++;
            return _files.TryGetValue(path, out var entry) ? entry.content : throw new FileNotFoundException(path);
        }

        public DateTime GetLastWriteTimeUtc(string path)
            => _files.TryGetValue(path, out var entry) ? entry.utc : throw new FileNotFoundException(path);

        public IEnumerable<string> ReadLines(string path) => ReadAllText(path).Split('\n');
        public System.IO.Stream OpenRead(string path) => throw new NotImplementedException();
        public bool DirectoryExists(string path) => throw new NotImplementedException();
        public string[] FindFiles(string directory, string pattern, bool recursive = true) => System.Array.Empty<string>();
    }

    private sealed class TestResult
    {
        public string Content { get; init; } = "";
    }

    private static readonly TestResult Empty = new() { Content = "" };

    [Fact]
    public void GetOrLoad_EmptyPath_ReturnsEmptyEntryWithoutInvokingParser()
    {
        var fs = new FakeFileSystem();
        var cache = new InvariantParseCache<TestResult>(fs);
        int parserCalls = 0;

        var entry = cache.GetOrLoad(
            sourcePath: "",
            parser: _ => { parserCalls++; return new TestResult { Content = "unexpected" }; },
            emptyResult: Empty);

        Assert.Equal("", entry.SourcePath);
        Assert.Equal(0, entry.Timestamp);
        Assert.Same(Empty, entry.Result);
        Assert.Equal(0, parserCalls);
        Assert.Equal(0, fs.ReadCallCount);
        Assert.Equal(0, fs.FileExistsCallCount);
    }

    [Fact]
    public void GetOrLoad_MissingFile_CachesAbsenceAndReturnsEmpty()
    {
        var fs = new FakeFileSystem();
        var cache = new InvariantParseCache<TestResult>(fs);
        int parserCalls = 0;

        var entry1 = cache.GetOrLoad(
            "/fake/CLAUDE.md",
            _ => { parserCalls++; return new TestResult(); },
            Empty);

        Assert.Same(Empty, entry1.Result);
        Assert.Equal("/fake/CLAUDE.md", entry1.SourcePath);
        Assert.Equal(0, parserCalls);
        Assert.Equal(0, fs.ReadCallCount);
    }

    [Fact]
    public void GetOrLoad_FileExists_InvokesParserAndCachesResult()
    {
        var fs = new FakeFileSystem();
        fs.Put("/fake/CLAUDE.md", "- **INV-FOO-001**: body.");
        var cache = new InvariantParseCache<TestResult>(fs);
        int parserCalls = 0;

        var entry = cache.GetOrLoad(
            "/fake/CLAUDE.md",
            text => { parserCalls++; return new TestResult { Content = text }; },
            Empty);

        Assert.Equal(1, parserCalls);
        Assert.Equal(1, fs.ReadCallCount);
        Assert.Contains("INV-FOO-001", entry.Result.Content);
        Assert.NotEqual(0, entry.Timestamp);
    }

    [Fact]
    public void GetOrLoad_CacheHit_UnchangedTimestamp_SkipsParser()
    {
        var fs = new FakeFileSystem();
        fs.Put("/fake/CLAUDE.md", "- **INV-FOO-001**: body.");
        var cache = new InvariantParseCache<TestResult>(fs);
        int parserCalls = 0;

        _ = cache.GetOrLoad("/fake/CLAUDE.md", text => { parserCalls++; return new TestResult { Content = text }; }, Empty);
        _ = cache.GetOrLoad("/fake/CLAUDE.md", text => { parserCalls++; return new TestResult { Content = text }; }, Empty);
        _ = cache.GetOrLoad("/fake/CLAUDE.md", text => { parserCalls++; return new TestResult { Content = text }; }, Empty);

        Assert.Equal(1, parserCalls);
        Assert.Equal(1, fs.ReadCallCount);
    }

    [Fact]
    public void GetOrLoad_TimestampChanged_InvalidatesAndReparses()
    {
        var fs = new FakeFileSystem();
        var path = "/fake/CLAUDE.md";
        fs.Put(path, "- **INV-FOO-001**: first.", utc: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var cache = new InvariantParseCache<TestResult>(fs);
        int parserCalls = 0;

        var first = cache.GetOrLoad(path, text => { parserCalls++; return new TestResult { Content = text }; }, Empty);

        // Same content, new timestamp.
        fs.Put(path, "- **INV-FOO-001**: first.", utc: new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        var second = cache.GetOrLoad(path, text => { parserCalls++; return new TestResult { Content = text }; }, Empty);

        Assert.Equal(2, parserCalls);
        Assert.NotEqual(first.Timestamp, second.Timestamp);
    }

    [Fact]
    public void GetOrLoad_ParserThrows_ReturnsEmptyWithoutPoisoningCache()
    {
        var fs = new FakeFileSystem();
        fs.Put("/fake/CLAUDE.md", "- **INV-FOO-001**: body.");
        var cache = new InvariantParseCache<TestResult>(fs);
        int parserCalls = 0;
        bool throwOnParse = true;

        var first = cache.GetOrLoad(
            "/fake/CLAUDE.md",
            text =>
            {
                parserCalls++;
                if (throwOnParse) throw new InvalidOperationException("parser sabotage");
                return new TestResult { Content = text };
            },
            Empty);

        Assert.Same(Empty, first.Result);

        // Next call retries. The cache was NOT poisoned with the failure.
        throwOnParse = false;
        var second = cache.GetOrLoad(
            "/fake/CLAUDE.md",
            text =>
            {
                parserCalls++;
                if (throwOnParse) throw new InvalidOperationException("parser sabotage");
                return new TestResult { Content = text };
            },
            Empty);

        Assert.Equal(2, parserCalls);
        Assert.Contains("INV-FOO-001", second.Result.Content);
    }

    [Fact]
    public async Task GetOrLoad_ConcurrentReaders_AllReturnValidEntry()
    {
        var fs = new FakeFileSystem();
        fs.Put("/fake/CLAUDE.md", "- **INV-FOO-001**: body.");
        var cache = new InvariantParseCache<TestResult>(fs);
        int parserCalls = 0;

        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            cache.GetOrLoad(
                "/fake/CLAUDE.md",
                text => { System.Threading.Interlocked.Increment(ref parserCalls); return new TestResult { Content = text }; },
                Empty))).ToArray();

        var results = await Task.WhenAll(tasks);

        // All readers return a valid entry with content.
        Assert.All(results, r => Assert.Contains("INV-FOO-001", r.Result.Content));
        // Parser MAY be called more than once due to the documented
        // cold-path race (last-writer-wins), but must be called at least
        // once and at most once per concurrent reader.
        Assert.InRange(parserCalls, 1, 16);
    }
}

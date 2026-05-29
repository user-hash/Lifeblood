using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Right.Invariants;
using Lifeblood.Connectors.Mcp.Internal;

namespace Lifeblood.Connectors.Mcp;

/// <summary>
/// Reference implementation of <see cref="IInvariantProvider"/>. Thin
/// orchestrator across three collaborators:
///
/// <list type="number">
///   <item><see cref="InvariantParseCache{T}"/> — timestamp-invalidated
///     per-source cache. Reusable across any invariant source.</item>
///   <item><see cref="ClaudeMdInvariantParser"/> — pure text-to-records
///     parser for the markdown convention. Pure function; no
///     filesystem access, no graph access, no caching.</item>
///   <item>This class itself — <b>discovers</b> invariant source files
///     dynamically per-project, hands each to the cache, then aggregates
///     parsed records into a single audit result.</item>
/// </list>
///
/// <para>
/// <b>Source discovery (no hardcoded path list).</b> Driven entirely by
/// <see cref="IFileSystem"/> probes against well-known repo conventions:
/// </para>
/// <list type="bullet">
///   <item><c>&lt;projectRoot&gt;/CLAUDE.md</c> — the canonical Claude
///     Code agent file at the project root.</item>
///   <item><c>&lt;projectRoot&gt;/AGENTS.md</c> — the agent-instruction
///     companion convention used by some repos alongside or in place of
///     CLAUDE.md.</item>
///   <item><c>&lt;projectRoot&gt;/docs/invariants/**.md</c> — the
///     invariants-tree convention for projects that have outgrown a
///     single-file authoring layout (hot-rules-stay/tree-everything-else).</item>
/// </list>
///
/// <para>
/// The conventions live in the adapter, not the port; nothing about the
/// list is encoded as a literal in <see cref="Application.Ports.Right.Invariants"/>.
/// A repo with a different layout supplies its own provider, reusing
/// <see cref="ClaudeMdInvariantParser"/> + <see cref="InvariantParseCache{T}"/>
/// without touching Application.
/// </para>
///
/// <para>
/// <b>Aggregation.</b> Each discovered file is parsed independently
/// against its own cache entry. Per-file <c>ClaudeMdParseResult</c>s are
/// merged: invariants concatenated in discovery order, warnings
/// concatenated, duplicates merged across all files (a duplicate id
/// declared in two different files is still a duplicate — the audit
/// shows every source line). The <see cref="InvariantAudit.SourcePath"/>
/// returns the first discovered source for back-compat;
/// <see cref="InvariantAudit.SourcePaths"/> returns the full list.
/// </para>
///
/// <para>
/// Instances are thread-safe for concurrent reads. The cache guards
/// its dictionary internally; this class itself is stateless beyond
/// the cache reference.
/// </para>
/// </summary>
public sealed class LifebloodInvariantProvider : IInvariantProvider
{
    /// <summary>
    /// Project-root single-file conventions. Probed in order; every
    /// existing file participates in the aggregate.
    /// </summary>
    private static readonly string[] RootSingleFileConventions =
    {
        "CLAUDE.md",
        "AGENTS.md",
    };

    /// <summary>
    /// Project-root tree conventions: a directory walked recursively
    /// for <c>*.md</c> files. Each is its own invariant source.
    /// </summary>
    private static readonly string[] RootTreeDirConventions =
    {
        Path.Combine("docs", "invariants"),
    };

    private static readonly ClaudeMdParseResult EmptyParseResult = new(
        System.Array.Empty<Invariant>(),
        System.Array.Empty<string>(),
        new Dictionary<string, int[]>(System.StringComparer.Ordinal));

    private readonly IFileSystem _fs;
    private readonly InvariantParseCache<ClaudeMdParseResult> _cache;

    public LifebloodInvariantProvider(IFileSystem fs)
    {
        _fs = fs ?? throw new System.ArgumentNullException(nameof(fs));
        _cache = new InvariantParseCache<ClaudeMdParseResult>(fs);
    }

    public Invariant[] GetAll(string projectRoot)
    {
        return Aggregate(projectRoot).Invariants;
    }

    public Invariant? GetById(string projectRoot, string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var aggregated = Aggregate(projectRoot);
        foreach (var inv in aggregated.Invariants)
        {
            if (string.Equals(inv.Id, id, System.StringComparison.Ordinal))
                return inv;
        }
        return null;
    }

    public InvariantAudit Audit(string projectRoot)
    {
        var aggregated = Aggregate(projectRoot);
        var invariants = aggregated.Invariants;

        var categoryMap = new Dictionary<string, int>(System.StringComparer.Ordinal);
        foreach (var inv in invariants)
        {
            if (string.IsNullOrEmpty(inv.Category)) continue;
            categoryMap.TryGetValue(inv.Category, out var count);
            categoryMap[inv.Category] = count + 1;
        }

        var categoryCounts = categoryMap
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, System.StringComparer.Ordinal)
            .Select(kv => new CategoryCount { Category = kv.Key, Count = kv.Value })
            .ToArray();

        var duplicates = aggregated.Duplicates
            .OrderBy(kv => kv.Key, System.StringComparer.Ordinal)
            .Select(kv => new DuplicateInvariantId { Id = kv.Key, SourceLines = kv.Value })
            .ToArray();

        return new InvariantAudit
        {
            TotalCount = invariants.Length,
            CategoryCounts = categoryCounts,
            Duplicates = duplicates,
            ParseWarnings = aggregated.Warnings,
            SourcePath = aggregated.SourcePaths.Length > 0 ? aggregated.SourcePaths[0] : "",
            SourcePaths = aggregated.SourcePaths,
            SourceCounts = aggregated.SourceCounts,
        };
    }

    /// <summary>
    /// Discover, parse, and aggregate every invariant source for the
    /// given project root. Empty result when projectRoot is empty or no
    /// known sources exist.
    /// </summary>
    private AggregatedParseResult Aggregate(string projectRoot)
    {
        if (string.IsNullOrEmpty(projectRoot))
            return AggregatedParseResult.Empty;

        var sources = DiscoverSources(projectRoot);
        if (sources.Length == 0)
            return AggregatedParseResult.Empty;

        var allInvariants = new List<Invariant>();
        var allWarnings = new List<string>();
        var sourceCounts = new List<InvariantSourceCount>(sources.Length);
        var allDuplicateLines = new Dictionary<string, List<int>>(System.StringComparer.Ordinal);
        var seenIds = new HashSet<string>(System.StringComparer.Ordinal);

        foreach (var sourcePath in sources)
        {
            var entry = _cache.GetOrLoad(sourcePath, ClaudeMdInvariantParser.Parse, EmptyParseResult);
            sourceCounts.Add(new InvariantSourceCount
            {
                SourcePath = sourcePath,
                Count = entry.Result.Invariants.Length,
            });
            foreach (var inv in entry.Result.Invariants)
            {
                if (seenIds.Add(inv.Id))
                    allInvariants.Add(inv);
            }
            foreach (var w in entry.Result.Warnings)
            {
                allWarnings.Add($"{sourcePath}: {w}");
            }
            foreach (var kv in entry.Result.Duplicates)
            {
                if (!allDuplicateLines.TryGetValue(kv.Key, out var list))
                {
                    list = new List<int>();
                    allDuplicateLines[kv.Key] = list;
                }
                list.AddRange(kv.Value);
            }
        }

        var duplicates = new Dictionary<string, int[]>(System.StringComparer.Ordinal);
        foreach (var kv in allDuplicateLines)
        {
            if (kv.Value.Count > 1)
                duplicates[kv.Key] = kv.Value.ToArray();
        }

        return new AggregatedParseResult(
            allInvariants.ToArray(),
            allWarnings.ToArray(),
            duplicates,
            sources,
            sourceCounts.ToArray());
    }

    /// <summary>
    /// Walk well-known invariant-source conventions against the live
    /// filesystem. Returns absolute paths in stable discovery order:
    /// project-root single files first, then each tree directory's
    /// <c>*.md</c> files in alphabetical order. Nothing is included
    /// unless <see cref="IFileSystem"/> reports it exists right now.
    /// </summary>
    private string[] DiscoverSources(string projectRoot)
    {
        var sources = new List<string>();

        foreach (var fileName in RootSingleFileConventions)
        {
            var path = Path.Combine(projectRoot, fileName);
            if (_fs.FileExists(path))
                sources.Add(path);
        }

        foreach (var treeDir in RootTreeDirConventions)
        {
            var dir = Path.Combine(projectRoot, treeDir);
            if (!_fs.DirectoryExists(dir)) continue;

            var found = _fs.FindFiles(dir, "*.md", recursive: true);
            System.Array.Sort(found, System.StringComparer.Ordinal);
            sources.AddRange(found);
        }

        return sources.ToArray();
    }

    private sealed class AggregatedParseResult
    {
        public static readonly AggregatedParseResult Empty = new(
            System.Array.Empty<Invariant>(),
            System.Array.Empty<string>(),
            new Dictionary<string, int[]>(System.StringComparer.Ordinal),
            System.Array.Empty<string>(),
            System.Array.Empty<InvariantSourceCount>());

        public Invariant[] Invariants { get; }
        public string[] Warnings { get; }
        public Dictionary<string, int[]> Duplicates { get; }
        public string[] SourcePaths { get; }
        public InvariantSourceCount[] SourceCounts { get; }

        public AggregatedParseResult(
            Invariant[] invariants,
            string[] warnings,
            Dictionary<string, int[]> duplicates,
            string[] sourcePaths,
            InvariantSourceCount[] sourceCounts)
        {
            Invariants = invariants;
            Warnings = warnings;
            Duplicates = duplicates;
            SourcePaths = sourcePaths;
            SourceCounts = sourceCounts;
        }
    }
}

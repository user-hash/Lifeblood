using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Right.Invariants;
using Lifeblood.Connectors.Mcp.Internal;

namespace Lifeblood.Connectors.Mcp;

/// <summary>
/// Reference implementation of <see cref="IInvariantProvider"/>. Thin
/// orchestrator across three collaborators, one per concern:
///
/// <list type="number">
///   <item><see cref="InvariantParseCache{T}"/> — timestamp-invalidated
///     per-source cache. Reusable across any invariant source because
///     the cache knows only paths + timestamps + delegation.</item>
///   <item><see cref="ClaudeMdInvariantParser"/> — pure text-to-records
///     parser for the CLAUDE.md markdown convention. Pure function; no
///     filesystem access, no graph access, no caching.</item>
///   <item>This class itself — resolves the project-root-relative
///     <c>CLAUDE.md</c> path, hands it to the cache with the parser as
///     delegate, and projects the result into the port's return shape.</item>
/// </list>
///
/// <para>
/// Hexagonal alignment: the port <see cref="IInvariantProvider"/> lives
/// in Application; the CLAUDE.md parser and cache live in
/// <c>Lifeblood.Connectors.Mcp.Internal</c> as concrete MCP-side
/// collaborators. Adding a new invariant source (YAML companion file,
/// GitHub issue tracker, external governance DB) ships as a new
/// sibling provider alongside this one, reusing the cache and
/// registering a different parser — no Application-layer changes
/// required.
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
    /// <summary>Filename convention for the project-root invariant source.</summary>
    internal const string ClaudeMdFileName = "CLAUDE.md";

    private static readonly ClaudeMdParseResult EmptyParseResult = new(
        System.Array.Empty<Invariant>(),
        System.Array.Empty<string>(),
        new Dictionary<string, int[]>(System.StringComparer.Ordinal));

    private readonly InvariantParseCache<ClaudeMdParseResult> _cache;

    public LifebloodInvariantProvider(IFileSystem fs)
    {
        if (fs == null) throw new System.ArgumentNullException(nameof(fs));
        _cache = new InvariantParseCache<ClaudeMdParseResult>(fs);
    }

    public Invariant[] GetAll(string projectRoot)
    {
        return Load(projectRoot).Result.Invariants;
    }

    public Invariant? GetById(string projectRoot, string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var entry = Load(projectRoot);
        foreach (var inv in entry.Result.Invariants)
        {
            if (string.Equals(inv.Id, id, System.StringComparison.Ordinal))
                return inv;
        }
        return null;
    }

    public InvariantAudit Audit(string projectRoot)
    {
        var entry = Load(projectRoot);
        var invariants = entry.Result.Invariants;

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

        var duplicates = entry.Result.Duplicates
            .OrderBy(kv => kv.Key, System.StringComparer.Ordinal)
            .Select(kv => new DuplicateInvariantId { Id = kv.Key, SourceLines = kv.Value })
            .ToArray();

        return new InvariantAudit
        {
            TotalCount = invariants.Length,
            CategoryCounts = categoryCounts,
            Duplicates = duplicates,
            ParseWarnings = entry.Result.Warnings,
            SourcePath = entry.SourcePath,
        };
    }

    private InvariantParseCache<ClaudeMdParseResult>.Entry Load(string projectRoot)
    {
        if (string.IsNullOrEmpty(projectRoot))
        {
            return new InvariantParseCache<ClaudeMdParseResult>.Entry("", 0, EmptyParseResult);
        }

        var sourcePath = Path.Combine(projectRoot, ClaudeMdFileName);
        return _cache.GetOrLoad(sourcePath, ClaudeMdInvariantParser.Parse, EmptyParseResult);
    }
}

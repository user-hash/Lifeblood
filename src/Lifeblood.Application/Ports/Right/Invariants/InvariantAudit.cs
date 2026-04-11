namespace Lifeblood.Application.Ports.Right.Invariants;

/// <summary>
/// Summary of all invariants declared by a project. Produced by
/// <see cref="IInvariantProvider.Audit"/> and surfaced by the
/// <c>lifeblood_invariant_check</c> MCP tool in audit mode.
///
/// The audit is a cheap read over the parsed invariant set — no graph
/// walks, no filesystem scans beyond reading <c>CLAUDE.md</c>. Callers
/// use it to spot-check coverage ("how many invariants does this
/// project declare?"), find collisions ("any duplicate ids?"), and
/// discover the category breakdown.
/// </summary>
public sealed class InvariantAudit
{
    /// <summary>Total number of unique invariant ids found.</summary>
    public int TotalCount { get; init; }

    /// <summary>
    /// Count of invariants per category, sorted by count descending then
    /// by category name ascending for stable ordering across runs.
    /// </summary>
    public CategoryCount[] CategoryCounts { get; init; } = System.Array.Empty<CategoryCount>();

    /// <summary>
    /// Ids that were declared more than once in the source. Empty when
    /// the project has no drift. Populated ids indicate the same
    /// identifier describes two different rules — a real architectural
    /// bug the tool surfaces for the maintainer to resolve (rename one,
    /// or merge the two).
    /// </summary>
    public DuplicateInvariantId[] Duplicates { get; init; } = System.Array.Empty<DuplicateInvariantId>();

    /// <summary>
    /// Parse warnings emitted during extraction (malformed bodies,
    /// missing titles, unrecognized id shapes). Non-fatal; the parser
    /// still yields whatever it could recover.
    /// </summary>
    public string[] ParseWarnings { get; init; } = System.Array.Empty<string>();

    /// <summary>
    /// Absolute path to the source file the audit was computed from
    /// (typically <c>&lt;projectRoot&gt;/CLAUDE.md</c>). Empty if the
    /// source couldn't be located.
    /// </summary>
    public string SourcePath { get; init; } = "";
}

/// <summary>
/// One entry in <see cref="InvariantAudit.CategoryCounts"/>.
/// </summary>
public sealed class CategoryCount
{
    public string Category { get; init; } = "";
    public int Count { get; init; }
}

/// <summary>
/// One entry in <see cref="InvariantAudit.Duplicates"/>. Points at the
/// id that collides and the 1-based line numbers of every occurrence.
/// </summary>
public sealed class DuplicateInvariantId
{
    public string Id { get; init; } = "";
    public int[] SourceLines { get; init; } = System.Array.Empty<int>();
}

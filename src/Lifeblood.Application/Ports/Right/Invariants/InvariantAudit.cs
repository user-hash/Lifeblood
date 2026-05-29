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
    /// Total number of invariant declarations across every source —
    /// the sum of <see cref="SourceCounts"/>. Equals <see cref="TotalCount"/>
    /// when no id is declared in more than one source; exceeds it by
    /// <see cref="DuplicateDeclarationCount"/> when sources re-declare the
    /// same id (e.g. a repo that mirrors invariants between CLAUDE.md and
    /// AGENTS.md). Reconciles the otherwise-confusing case where the
    /// per-source counts sum to more than the unique total.
    /// </summary>
    public int DeclaredCount { get; init; }

    /// <summary>
    /// <see cref="DeclaredCount"/> minus <see cref="TotalCount"/>: how many
    /// declarations are redundant because their id is already declared in
    /// an earlier source. Zero for a drift-free single-declaration project.
    /// Every redundant declaration is attributed in <see cref="Duplicates"/>
    /// via <see cref="DuplicateInvariantId.Occurrences"/>.
    /// </summary>
    public int DuplicateDeclarationCount { get; init; }

    /// <summary>
    /// Count of invariants per category, sorted by count descending then
    /// by category name ascending for stable ordering across runs.
    /// </summary>
    public CategoryCount[] CategoryCounts { get; init; } = System.Array.Empty<CategoryCount>();

    /// <summary>
    /// Ids that were declared more than once — within a single source OR
    /// across sources (e.g. the same id in both CLAUDE.md and AGENTS.md).
    /// Empty when the project has no drift. A populated entry means one
    /// identifier has multiple declaration sites; <see cref="DuplicateInvariantId.Occurrences"/>
    /// attributes each site to its file + line + title so the maintainer
    /// can tell a benign mirror (identical titles) from a real collision
    /// (same id, two different rules) and fix it.
    /// </summary>
    public DuplicateInvariantId[] Duplicates { get; init; } = System.Array.Empty<DuplicateInvariantId>();

    /// <summary>
    /// Parse warnings emitted during extraction (malformed bodies,
    /// missing titles, unrecognized id shapes). Non-fatal; the parser
    /// still yields whatever it could recover.
    /// </summary>
    public string[] ParseWarnings { get; init; } = System.Array.Empty<string>();

    /// <summary>
    /// Absolute path to the primary source file the audit was computed
    /// from. When the project uses an invariants tree (tree-style
    /// <c>docs/invariants/**.md</c>) and the audit aggregates multiple
    /// files, this is the first source in discovery order — the full
    /// list is in <see cref="SourcePaths"/>. Empty if no source could
    /// be located.
    /// </summary>
    public string SourcePath { get; init; } = "";

    /// <summary>
    /// Every source file the audit aggregated invariants from. Driven
    /// by <see cref="IInvariantProvider"/> discovery, NOT a hardcoded
    /// path list — the provider walks well-known repo conventions
    /// (project-root <c>CLAUDE.md</c>, <c>AGENTS.md</c>, and any
    /// <c>docs/invariants/**.md</c> tree) and reports back what it
    /// actually found. Empty when no source could be located.
    /// </summary>
    public string[] SourcePaths { get; init; } = System.Array.Empty<string>();

    /// <summary>
    /// Per-source declaration-site counts, aligned to the same source
    /// discovery set as <see cref="SourcePaths"/>. These sum to
    /// <see cref="DeclaredCount"/> and may exceed <see cref="TotalCount"/>
    /// when an id is declared in more than one source. Used by docs-safe
    /// evidence receipts so living docs can cite both the aggregate and
    /// where it came from without reparsing markdown themselves.
    /// </summary>
    public InvariantSourceCount[] SourceCounts { get; init; } = System.Array.Empty<InvariantSourceCount>();
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
/// One entry in <see cref="InvariantAudit.Duplicates"/>: an id with more
/// than one declaration site, and the full provenance of every site.
/// </summary>
public sealed class DuplicateInvariantId
{
    public string Id { get; init; } = "";

    /// <summary>
    /// DEPRECATED (v1 compatibility only — slated for removal in the next
    /// wire-contract version per <c>docs/SCHEMA_DEPRECATION_POLICY.md</c>).
    /// File-blind 1-based line numbers of every occurrence. Retained
    /// because v1 froze this shape, but it cannot say WHICH file each line
    /// is in — which is exactly how cross-file duplicates went unreported.
    /// Prefer <see cref="Occurrences"/>, which carries file + line + title
    /// per site. This array equals <c>Occurrences.Select(o =&gt; o.Line)</c>.
    /// </summary>
    public int[] SourceLines { get; init; } = System.Array.Empty<int>();

    /// <summary>
    /// Every declaration site for this id, in discovery order. Each site
    /// carries its source file, 1-based line, and the title parsed there,
    /// so a caller can distinguish a benign cross-file mirror (identical
    /// titles) from a genuine collision (divergent titles) — and cite the
    /// exact files to fix. Covers both within-file and cross-file repeats.
    /// </summary>
    public InvariantOccurrence[] Occurrences { get; init; } = System.Array.Empty<InvariantOccurrence>();
}

/// <summary>
/// One declaration site of an invariant id: the source file it was
/// declared in, the 1-based line, and the title parsed at that site.
/// </summary>
public sealed class InvariantOccurrence
{
    public string SourcePath { get; init; } = "";
    public int Line { get; init; }
    public string Title { get; init; } = "";
}

/// <summary>
/// Declaration-site count for one parsed source file. Counts every
/// <c>INV-*</c> declaration in the file, including within-file repeats
/// (a repeated id is a real second declaration line), so the per-source
/// counts sum to the audit's <see cref="InvariantAudit.DeclaredCount"/>.
/// </summary>
public sealed class InvariantSourceCount
{
    public string SourcePath { get; init; } = "";
    public int Count { get; init; }
}

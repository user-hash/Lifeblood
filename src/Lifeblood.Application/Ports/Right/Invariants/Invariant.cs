namespace Lifeblood.Application.Ports.Right.Invariants;

/// <summary>
/// One architectural invariant, as parsed from a project's
/// <c>CLAUDE.md</c> file by an <see cref="IInvariantProvider"/>.
///
/// Every invariant has a stable id (e.g. <c>INV-CANONICAL-001</c>) and
/// a human-readable body that describes the rule, its rationale, and
/// the enforcement sites. The data model is intentionally minimal for
/// the v1 tool surface (id / title / body / category); machine-readable
/// fields like <c>appliesTo</c> globs and enforcement declarations
/// ship in follow-up phases — see
/// <c>.claude/plans/invariant-check-spike.md</c>.
///
/// Invariants are pure value data. No graph dependency, no filesystem
/// dependency, no behavior. A connector produces them from whatever
/// source (CLAUDE.md prose, an optional companion JSON, etc.); the
/// tool handler serializes them to the MCP wire.
/// </summary>
public sealed class Invariant
{
    /// <summary>
    /// Stable unique identifier (e.g. <c>INV-CANONICAL-001</c>). Case
    /// sensitive. This is the key every tool query uses.
    /// </summary>
    public string Id { get; init; } = "";

    /// <summary>
    /// Single-line human-readable title extracted from the first bold
    /// sentence after the id in the invariant's markdown body.
    /// </summary>
    public string Title { get; init; } = "";

    /// <summary>
    /// Full markdown body of the invariant, excluding the id marker
    /// itself. May be multi-line and contain inline code, links, and
    /// references to other invariants.
    /// </summary>
    public string Body { get; init; } = "";

    /// <summary>
    /// Category derived from the id prefix (<c>INV-CANONICAL-001</c>
    /// → <c>CANONICAL</c>). Useful for grouping in audit output and
    /// filtering tool results.
    /// </summary>
    public string Category { get; init; } = "";

    /// <summary>
    /// 1-based line number in <c>CLAUDE.md</c> where this invariant
    /// was declared. Pointer for "open the source" in tool output.
    /// Zero if the source location is unknown.
    /// </summary>
    public int SourceLine { get; init; }
}

namespace Lifeblood.Application.Ports.Right.Invariants;

/// <summary>
/// Port for querying architectural invariants declared by a project
/// (typically in its <c>CLAUDE.md</c>). Every <c>lifeblood_invariant_check</c>
/// tool call routes through this port — the port is the single source
/// of truth for "what does this project declare as an invariant", the
/// same shape as <c>ISymbolResolver</c> is for symbol resolution.
///
/// <para>
/// Implementations MUST be cheap. The v1 connector
/// <c>Lifeblood.Connectors.Mcp.LifebloodInvariantProvider</c> parses
/// CLAUDE.md on the first call per project root and caches the result.
/// Subsequent calls for the same project are effectively free. There is
/// no graph dependency; the provider reads a text file and returns
/// structured data.
/// </para>
///
/// <para>
/// The port is language-agnostic. Any project that has a CLAUDE.md with
/// <c>INV-*</c> markers gets invariants for free. Projects without
/// markers get an empty result and a helpful audit message rather than
/// an error.
/// </para>
/// </summary>
public interface IInvariantProvider
{
    /// <summary>
    /// Return every invariant declared in the project rooted at
    /// <paramref name="projectRoot"/>. Order is the order of appearance
    /// in the source file so an audit walks top-to-bottom the same way
    /// a human reading CLAUDE.md would. Empty array when the project
    /// has no <c>CLAUDE.md</c> or the file has no invariants.
    /// </summary>
    Invariant[] GetAll(string projectRoot);

    /// <summary>
    /// Return the single invariant whose id matches
    /// <paramref name="id"/> exactly (case-sensitive), or <c>null</c> if
    /// the id is not declared. If the same id is declared multiple
    /// times (an error the project should fix), the first occurrence
    /// wins and the duplicate is surfaced by
    /// <see cref="Audit"/>.
    /// </summary>
    Invariant? GetById(string projectRoot, string id);

    /// <summary>
    /// Compute a summary of the declared invariant set. Cheap — iterates
    /// the parsed list exactly once. Surfaces the total count, a
    /// per-category breakdown, duplicate id collisions, and any parse
    /// warnings encountered while reading the source.
    /// </summary>
    InvariantAudit Audit(string projectRoot);
}

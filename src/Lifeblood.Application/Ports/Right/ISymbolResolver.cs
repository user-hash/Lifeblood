using Lifeblood.Domain.Graph;

namespace Lifeblood.Application.Ports.Right;

/// <summary>
/// Single resolver for user-supplied symbol identifiers. Every read-side tool
/// (lookup, dependants, dependencies, blast_radius, file_impact, etc.) MUST
/// route through this resolver before doing graph lookups. The resolver is
/// the single source of truth for "what does this symbol identifier mean" —
/// it canonicalizes truncated method IDs, resolves bare short names, and
/// produces the merged read model for partial types.
///
/// The graph itself stores raw symbols (one Symbol record per partial
/// declaration; last-write-wins remains the storage policy). Partial-type
/// unification is a READ MODEL computed by this resolver from the existing
/// file→type Contains edges in the graph. <see cref="Symbol"/> is unchanged.
///
/// See INV-RESOLVER-001..004 in the Lifeblood CLAUDE.md.
/// </summary>
public interface ISymbolResolver
{
    /// <summary>
    /// Resolve a user-supplied identifier to a canonical symbol ID and the
    /// associated merged read-model. Resolution order:
    /// <list type="number">
    ///   <item>Exact canonical match (fast path).</item>
    ///   <item>Truncated method form: <c>method:NS.Type.Name</c> with no
    ///     parens → lenient single-overload match. If exactly one method
    ///     named <c>Name</c> lives on <c>NS.Type</c>, return its canonical id.</item>
    ///   <item>Bare short name: no kind prefix and no namespace → short-name
    ///     index. If exactly one symbol matches, return its canonical id.</item>
    ///   <item>Not found / ambiguous: returns <see cref="ResolveOutcome.NotFound"/>
    ///     or one of the <c>Ambiguous*</c> outcomes with
    ///     <see cref="SymbolResolutionResult.Candidates"/> and
    ///     <see cref="SymbolResolutionResult.Diagnostic"/> populated.</item>
    /// </list>
    ///
    /// For partial types, the result's
    /// <see cref="SymbolResolutionResult.DeclarationFilePaths"/> is populated
    /// with every partial declaration file (sorted lexicographically), with
    /// <see cref="SymbolResolutionResult.PrimaryFilePath"/> chosen
    /// deterministically per INV-RESOLVER-004:
    /// filename matches type name → filename starts with
    /// <c>"&lt;TypeName&gt;."</c> (shortest first) → lexicographic first.
    /// </summary>
    SymbolResolutionResult Resolve(SemanticGraph graph, string userInput);

    /// <summary>
    /// Resolve a short name (no namespace prefix) to all matching canonical IDs.
    /// Used by the standalone <c>lifeblood_resolve_short_name</c> MCP tool and
    /// as a fallback inside <see cref="Resolve"/>.
    /// </summary>
    ShortNameMatch[] ResolveShortName(SemanticGraph graph, string shortName);
}

/// <summary>
/// Single result DTO for identifier resolution. Combines canonicalization
/// (<see cref="CanonicalId"/>), the resolution outcome
/// (<see cref="Outcome"/>), the referenced graph <see cref="Symbol"/>,
/// the partial-type read model
/// (<see cref="DeclarationFilePaths"/> + <see cref="PrimaryFilePath"/>),
/// and the diagnostic-on-miss surface
/// (<see cref="Candidates"/> + <see cref="Diagnostic"/>).
///
/// IMPORTANT — partial-type unification lives HERE, not on
/// <see cref="Lifeblood.Domain.Graph.Symbol"/>. The graph stores raw symbols
/// (one per partial declaration; last-write-wins remains the storage policy).
/// <see cref="DeclarationFilePaths"/> is computed by the resolver from the
/// existing file→type Contains edges in the graph.
/// </summary>
public sealed class SymbolResolutionResult
{
    /// <summary>
    /// The canonical symbol ID, or null if not resolved. When non-null,
    /// guaranteed to be a key in <c>graph.GetSymbol</c>.
    /// </summary>
    public string? CanonicalId { get; init; }

    /// <summary>
    /// What rule the resolver applied. <see cref="ResolveOutcome.NotFound"/>
    /// and the <c>Ambiguous*</c> outcomes have <see cref="CanonicalId"/> ==
    /// null and <see cref="Candidates"/> populated.
    /// </summary>
    public ResolveOutcome Outcome { get; init; }

    /// <summary>
    /// The raw graph <see cref="Symbol"/> record for the resolved ID, or null
    /// on miss. This is a HANDLE into graph storage — read-only, no copy. For
    /// partial types, this is whichever partial <c>GraphBuilder</c> happened
    /// to store last (the resolver does not mutate it). The merged view lives
    /// in <see cref="PrimaryFilePath"/> and <see cref="DeclarationFilePaths"/>,
    /// both computed by the resolver from the graph's Contains edges.
    /// </summary>
    public Symbol? Symbol { get; init; }

    /// <summary>
    /// Deterministic primary file path for the resolved symbol. For partial
    /// types, picked per INV-RESOLVER-004:
    /// filename matches type name → filename starts with
    /// <c>"&lt;TypeName&gt;."</c> (shortest first) → lexicographic first.
    /// For non-partial symbols, equals the symbol's only file. Empty string
    /// when <see cref="CanonicalId"/> is null.
    /// </summary>
    public string PrimaryFilePath { get; init; } = "";

    /// <summary>
    /// All declaration files for the resolved symbol. For partial types,
    /// every file containing a partial declaration, sorted lexicographically.
    /// For non-partial symbols, exactly one entry. Empty array when
    /// <see cref="CanonicalId"/> is null.
    /// </summary>
    public string[] DeclarationFilePaths { get; init; } = System.Array.Empty<string>();

    /// <summary>
    /// Candidate canonical IDs when <see cref="Outcome"/> is one of the
    /// <c>Ambiguous*</c> values — for example, a short name that matches
    /// multiple types in different namespaces, or a truncated method ID with
    /// multiple overloads. Empty when the resolution succeeded or definitively
    /// failed.
    /// </summary>
    public string[] Candidates { get; init; } = System.Array.Empty<string>();

    /// <summary>
    /// Human-readable diagnostic explaining the outcome. For
    /// <see cref="ResolveOutcome.NotFound"/>: "Symbol not found: &lt;input&gt;.
    /// Tried exact match, lenient method overload, and short-name lookup.
    /// Did you mean: A, B, C?" Closes the diagnostic-on-miss feature request
    /// from the original Lifeblood backlog.
    /// </summary>
    public string? Diagnostic { get; init; }
}

/// <summary>
/// Outcome of an <see cref="ISymbolResolver.Resolve"/> call. Tracks which
/// resolution rule applied so callers can present meaningful errors and
/// telemetry.
/// </summary>
public enum ResolveOutcome
{
    /// <summary>The input was already a canonical ID — fast-path match.</summary>
    ExactMatch,

    /// <summary>
    /// The input was a method symbol id without parens, and exactly one
    /// method with that name lives on the resolved type.
    /// </summary>
    LenientMethodOverload,

    /// <summary>
    /// The input was a bare short name (no kind prefix, no namespace), and
    /// exactly one symbol with that name exists in the graph.
    /// </summary>
    ShortNameUnique,

    /// <summary>The input did not match anything via any resolution rule.</summary>
    NotFound,

    /// <summary>
    /// The input was a bare short name that matched multiple symbols across
    /// namespaces. <see cref="SymbolResolutionResult.Candidates"/> lists them.
    /// </summary>
    AmbiguousShortName,

    /// <summary>
    /// The input was a truncated method id and the resolved type has multiple
    /// overloads of that method name. <see cref="SymbolResolutionResult.Candidates"/>
    /// lists the canonical ids of every overload.
    /// </summary>
    AmbiguousMethodOverload,
}

/// <summary>
/// One match from <see cref="ISymbolResolver.ResolveShortName"/>. Lightweight
/// shape for the <c>lifeblood_resolve_short_name</c> MCP tool's response.
/// </summary>
public sealed class ShortNameMatch
{
    public string CanonicalId { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string Kind { get; init; } = "";
}

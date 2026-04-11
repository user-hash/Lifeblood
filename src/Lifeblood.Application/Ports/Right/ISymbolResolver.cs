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
    ///
    /// The <paramref name="mode"/> controls how the short name is compared
    /// against the graph's short-name index:
    /// <list type="bullet">
    ///   <item><see cref="ResolutionMode.Exact"/> (default) — literal case-insensitive match.</item>
    ///   <item><see cref="ResolutionMode.Contains"/> — substring match against the candidate's simple name.</item>
    ///   <item><see cref="ResolutionMode.Fuzzy"/> — ranked fuzzy search (token prefixes, CamelCase split, Levenshtein distance).</item>
    /// </list>
    /// When the literal/substring search yields no results, the implementation
    /// MUST also surface ranked suggestions by calling
    /// <see cref="SuggestNearMatches"/>. That guarantee is what makes a
    /// zero-result response useful instead of a dead end.
    /// </summary>
    ShortNameMatch[] ResolveShortName(SemanticGraph graph, string shortName, ResolutionMode mode = ResolutionMode.Exact);

    /// <summary>
    /// Return the top-N ranked near-matches for a short name query. Used as
    /// a zero-result fallback from <see cref="ResolveShortName"/> in every
    /// mode so no dead-end response leaves the caller without next-steps,
    /// and as the backing implementation of <see cref="ResolutionMode.Fuzzy"/>.
    /// Ranking is deterministic for a given input; see the resolver
    /// implementation for scoring weights.
    /// </summary>
    ShortNameMatch[] SuggestNearMatches(SemanticGraph graph, string shortName, int limit = 5);
}

/// <summary>
/// How <see cref="ISymbolResolver.ResolveShortName"/> compares the query
/// against the graph's short-name index. Added 2026-04-11 as part of
/// Phase 3 of the improvement-master plan (LB-INBOX-002). Every resolver
/// implementation MUST handle every enum value; adding a new value is a
/// breaking change to every resolver adapter, which is the intended
/// contract (the enum deliberately has no <c>Unknown = 0</c> fallback).
/// </summary>
public enum ResolutionMode
{
    /// <summary>Case-insensitive literal match against the candidate's simple name. Default.</summary>
    Exact,
    /// <summary>Case-insensitive substring match against the candidate's simple name.</summary>
    Contains,
    /// <summary>Ranked fuzzy search backed by the same scorer as <see cref="ISymbolResolver.SuggestNearMatches"/>.</summary>
    Fuzzy,
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

    /// <summary>
    /// Sibling overloads of the resolved symbol when it is a method.
    /// Populated by <see cref="ISymbolResolver.Resolve"/> when the input
    /// resolved to a method that has more than one overload on the
    /// containing type. Each entry is one overload of the same simple name
    /// on the same containing type, including the one this result resolved
    /// to. Empty for non-method symbols, empty when the method has no
    /// other overloads, empty on miss. Closes LB-INBOX-004.
    /// </summary>
    public OverloadInfo[] Overloads { get; init; } = System.Array.Empty<OverloadInfo>();
}

/// <summary>
/// One overload of a method, surfaced on <see cref="SymbolResolutionResult.Overloads"/>.
/// Carries the canonical id so a caller can feed it back into any read-side
/// tool, the param display string for human-readable disambiguation, and
/// the declaration location. Added 2026-04-11 for LB-INBOX-004.
/// </summary>
public sealed class OverloadInfo
{
    public required string CanonicalId { get; init; }
    public string ParamDisplay { get; init; } = "";
    public string FilePath { get; init; } = "";
    public int Line { get; init; }
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

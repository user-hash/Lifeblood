using Lifeblood.Domain.Graph;

namespace Lifeblood.Application.Ports.Right;

/// <summary>
/// Right-side port: ranked keyword search across the semantic graph's
/// symbol names and persisted XML documentation summaries. Unlike
/// <see cref="ISymbolResolver"/>, which is a canonical-ID lookup with
/// a narrow contract, this port is a general-purpose content search
/// that treats <c>Symbol.Name</c>, <c>Symbol.QualifiedName</c>, and
/// <c>Symbol.Properties["xmlDocSummary"]</c> as a searchable corpus.
///
/// Added 2026-04-11 (Phase 5) to close LB-INBOX-003. The <c>lifeblood_search</c>
/// MCP tool surfaces this port to AI agents who need to find symbols by
/// what they DO, not by what they're NAMED. It is distinct from
/// <c>ResolutionMode.Fuzzy</c> (which ranks short names) because search
/// looks inside the xmldoc corpus as well, and is distinct from
/// <see cref="IMcpGraphProvider"/> (which is a graph walker), because
/// search does not traverse edges.
///
/// Keeping search as its own port — not a method on
/// <see cref="IMcpGraphProvider"/> — was deliberate: that port is
/// already five-methods heavy and serves a different concern (symbol
/// lookup + graph walk). Merging them would violate single-responsibility
/// and make future search-only adapters (e.g., a BM25 provider or a
/// vector-embedding provider) harder to plug in.
/// </summary>
public interface ISemanticSearchProvider
{
    SearchResult[] Search(SemanticGraph graph, SearchQuery query);
}

/// <summary>
/// Input to <see cref="ISemanticSearchProvider.Search"/>. Query text is
/// matched against <see cref="Symbol.Name"/>, <see cref="Symbol.QualifiedName"/>,
/// and any persisted <c>xmlDocSummary</c> property. <paramref name="Kinds"/>
/// optionally restricts the search to a subset of symbol kinds (e.g.
/// methods only). <paramref name="Limit"/> caps the result count — the
/// default serves a comfortable pager page.
/// </summary>
public sealed record SearchQuery(string Query, SymbolKind[]? Kinds = null, int Limit = 20);

/// <summary>
/// One ranked hit from the semantic search corpus. Carries the symbol's
/// canonical ID (so the caller can feed it straight into any other
/// read-side tool), the kind, the file path, the line, and the relative
/// score (higher = better). The <see cref="MatchSnippets"/> array carries
/// up to three short snippets showing WHERE in the searched corpus the
/// query matched, for human-readable result rendering. The
/// <see cref="MatchKind"/> field STRUCTURALLY reports which scoring
/// bucket(s) drove the rank so callers can filter without parsing the
/// snippet strings. INV-SEARCH-MATCHKIND-001.
/// </summary>
public sealed record SearchResult(
    string CanonicalId,
    SymbolKind Kind,
    string Name,
    string FilePath,
    int Line,
    double Score,
    string[] MatchSnippets,
    MatchKind MatchKind);

/// <summary>
/// Which scoring bucket drove a <see cref="SearchResult"/>'s rank. Lifted
/// from the adapter's existing first-hit-token tracking so callers can
/// filter / sort by signal type without parsing the (human-rendered)
/// <see cref="SearchResult.MatchSnippets"/> strings. INV-SEARCH-MATCHKIND-001.
/// Adapter-agnostic; future TS / Python adapters reuse the same taxonomy.
/// </summary>
public enum MatchKind
{
    /// <summary>The query matched the symbol's bare <c>Name</c>.</summary>
    Name,

    /// <summary>The query matched the symbol's <c>QualifiedName</c> but
    /// not the bare <c>Name</c>. (Bare-name matches always take precedence
    /// per the C# adapter's scoring guard, so QualifiedName is exclusive
    /// of Name in single-bucket results.)</summary>
    QualifiedName,

    /// <summary>The query matched only the symbol's persisted
    /// <c>xmlDocSummary</c> property — the by-what-it-DOES path that
    /// distinguishes <c>lifeblood_search</c> from name-only resolution.</summary>
    XmlDoc,

    /// <summary>More than one bucket fired (e.g. Name + XmlDoc, or
    /// QualifiedName + XmlDoc). Adapter does not enumerate which combo —
    /// the score reflects the cumulative weight, and the
    /// <see cref="SearchResult.MatchSnippets"/> array enumerates the
    /// per-bucket evidence for human consumption.</summary>
    Multiple,
}

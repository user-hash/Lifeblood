using Lifeblood.Application.Ports.Right;
using Lifeblood.Domain.Graph;

namespace Lifeblood.Connectors.Mcp;

/// <summary>
/// Reference implementation of <see cref="ISemanticSearchProvider"/>.
/// Ranks graph symbols against a query string using a weighted combination
/// of name, qualified name, and persisted xml-documentation summary
/// matches. Purely in-memory: no Roslyn, no file I/O, no edges walked.
/// Deterministic for a given graph + query pair.
///
/// Query model: the query is tokenized on whitespace, deduplicated
/// case-insensitively, and short tokens (&lt; 3 chars) are dropped. Each
/// surviving token is an independent scoring signal — scores accumulate
/// across tokens (ranked OR), never AND-gate. If every token is
/// sub-threshold, the whole trimmed query is treated as one literal so
/// terse queries like "id" still work.
///
/// Per-token scoring weights (relative, not absolute):
///   name       substring match   : +10 when the token appears in the bare name
///   name       token-prefix      : +5 when any CamelCase-split token of the name starts with the query token
///   qualifiedName substring      : +3 when the token appears in the FQN but not the bare name (so FQ hits score, but below bare names)
///   xmlDocSummary substring      : +5 when the token appears in the persisted xmldoc summary —
///                                   docstring matches are the whole point of this tool vs. the short-name resolver
///   kind filter bonus            : +2 once per symbol if the caller supplied a Kinds filter and the symbol matches
///
/// Ties are broken lexicographically on the canonical ID for stable
/// ordering across test runs. Added 2026-04-11 (Phase 5); tokenized
/// 2026-04-11 after dogfood found multi-word queries collapsed to zero.
/// </summary>
public sealed class LifebloodSemanticSearchProvider : ISemanticSearchProvider
{
    private const int MinTokenLength = 3;
    private static readonly char[] QueryTokenSeparators = new[] { ' ', '\t', '\r', '\n' };

    public SearchResult[] Search(SemanticGraph graph, SearchQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.Query)) return System.Array.Empty<SearchResult>();

        var limit = query.Limit > 0 ? query.Limit : 20;
        var kindFilter = query.Kinds != null && query.Kinds.Length > 0
            ? new HashSet<SymbolKind>(query.Kinds)
            : null;

        var tokens = TokenizeQuery(query.Query);
        if (tokens.Length == 0) return System.Array.Empty<SearchResult>();

        var scored = new List<(double score, Symbol sym, List<string> snippets)>(capacity: 64);

        foreach (var sym in graph.Symbols)
        {
            if (kindFilter != null && !kindFilter.Contains(sym.Kind)) continue;
            if (string.IsNullOrEmpty(sym.Name)) continue;

            sym.Properties.TryGetValue("xmlDocSummary", out var docSummary);
            var hasDoc = !string.IsNullOrEmpty(docSummary);
            var camelTokens = SplitCamelCase(sym.Name);

            double score = 0;
            string? firstNameHitToken = null;
            string? firstQNameHitToken = null;
            string? firstXmlDocHitToken = null;

            foreach (var token in tokens)
            {
                var nameHit = sym.Name.Contains(token, StringComparison.OrdinalIgnoreCase);
                if (nameHit)
                {
                    score += 10;
                    firstNameHitToken ??= token;
                }

                foreach (var camel in camelTokens)
                {
                    if (camel.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 5;
                        break;
                    }
                }

                if (!nameHit
                    && !string.IsNullOrEmpty(sym.QualifiedName)
                    && sym.QualifiedName!.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    score += 3;
                    firstQNameHitToken ??= token;
                }

                if (hasDoc && docSummary!.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    score += 5;
                    firstXmlDocHitToken ??= token;
                }
            }

            if (score <= 0) continue;
            if (kindFilter != null) score += 2;

            var snippets = new List<string>(3);
            if (firstNameHitToken != null)
            {
                snippets.Add($"name: {sym.Name}");
            }
            if (firstQNameHitToken != null)
            {
                snippets.Add($"qualifiedName: {sym.QualifiedName}");
            }
            if (firstXmlDocHitToken != null && hasDoc)
            {
                snippets.Add($"xmlDoc: {TruncateSnippet(docSummary!, firstXmlDocHitToken)}");
            }

            scored.Add((score, sym, snippets));
        }

        return scored
            .OrderByDescending(t => t.score)
            .ThenBy(t => t.sym.Id, StringComparer.Ordinal)
            .Take(limit)
            .Select(t => new SearchResult(
                CanonicalId: t.sym.Id,
                Kind: t.sym.Kind,
                Name: t.sym.Name,
                FilePath: t.sym.FilePath,
                Line: t.sym.Line,
                Score: t.score,
                MatchSnippets: t.snippets.ToArray()))
            .ToArray();
    }

    /// <summary>
    /// Split the raw query into distinct case-insensitive tokens and drop
    /// anything below <see cref="MinTokenLength"/> to keep noise from
    /// saturating scores. If the filter kills every token, fall back to
    /// the whole trimmed query as a single literal so terse queries like
    /// "id" or "db" still work exactly as they did pre-tokenization.
    /// </summary>
    private static string[] TokenizeQuery(string rawQuery)
    {
        var split = rawQuery.Split(QueryTokenSeparators, StringSplitOptions.RemoveEmptyEntries);
        var filtered = new List<string>(split.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var piece in split)
        {
            if (piece.Length < MinTokenLength) continue;
            if (!seen.Add(piece)) continue;
            filtered.Add(piece);
        }

        if (filtered.Count > 0) return filtered.ToArray();

        var trimmed = rawQuery.Trim();
        return trimmed.Length == 0 ? System.Array.Empty<string>() : new[] { trimmed };
    }

    /// <summary>
    /// Return a short snippet of the doc text centered on the first
    /// case-insensitive occurrence of the anchor token. Cap ~80
    /// characters so the MCP response stays lean.
    /// </summary>
    private static string TruncateSnippet(string text, string anchor)
    {
        const int windowRadius = 40;
        var idx = text.IndexOf(anchor, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return text.Length <= 80 ? text : text.Substring(0, 80) + "…";
        var start = System.Math.Max(0, idx - windowRadius);
        var end = System.Math.Min(text.Length, idx + anchor.Length + windowRadius);
        var prefix = start > 0 ? "…" : "";
        var suffix = end < text.Length ? "…" : "";
        return prefix + text.Substring(start, end - start) + suffix;
    }

    private static List<string> SplitCamelCase(string name)
    {
        var result = new List<string>(4);
        if (string.IsNullOrEmpty(name)) return result;
        int start = 0;
        for (int i = 1; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                result.Add(name.Substring(start, i - start));
                start = i;
            }
        }
        result.Add(name.Substring(start));
        return result;
    }
}

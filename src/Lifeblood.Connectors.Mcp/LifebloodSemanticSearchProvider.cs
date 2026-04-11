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
/// Scoring weights (relative, not absolute):
///   name       substring match   : +10 per occurrence (capped at 1)
///   name       token-prefix      : +5 when the query is a CamelCase token prefix of the name
///   qualifiedName substring      : +3 per occurrence (so fully-qualified hits score, but below bare names)
///   xmlDocSummary substring      : +5 per occurrence (capped at 3) — docstring matches are the
///                                   whole point of this tool vs. the short-name resolver
///   kind filter bonus            : +2 if the caller supplied a Kinds filter and the symbol matches
///
/// Ties are broken lexicographically on the canonical ID for stable
/// ordering across test runs. Added 2026-04-11 (Phase 5).
/// </summary>
public sealed class LifebloodSemanticSearchProvider : ISemanticSearchProvider
{
    public SearchResult[] Search(SemanticGraph graph, SearchQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.Query)) return System.Array.Empty<SearchResult>();

        var limit = query.Limit > 0 ? query.Limit : 20;
        var kindFilter = query.Kinds != null && query.Kinds.Length > 0
            ? new HashSet<SymbolKind>(query.Kinds)
            : null;

        var scored = new List<(double score, Symbol sym, List<string> snippets)>(capacity: 64);

        foreach (var sym in graph.Symbols)
        {
            if (kindFilter != null && !kindFilter.Contains(sym.Kind)) continue;
            if (string.IsNullOrEmpty(sym.Name)) continue;

            double score = 0;
            var snippets = new List<string>(3);

            // Name match (short bare name — the strongest signal).
            if (sym.Name.Contains(query.Query, StringComparison.OrdinalIgnoreCase))
            {
                score += 10;
                snippets.Add($"name: {sym.Name}");
            }

            // Token-prefix bonus.
            foreach (var token in SplitCamelCase(sym.Name))
            {
                if (token.StartsWith(query.Query, StringComparison.OrdinalIgnoreCase))
                {
                    score += 5;
                    break;
                }
            }

            // Qualified name match (FQ hits).
            if (!string.IsNullOrEmpty(sym.QualifiedName)
                && sym.QualifiedName.Contains(query.Query, StringComparison.OrdinalIgnoreCase)
                && !sym.Name.Contains(query.Query, StringComparison.OrdinalIgnoreCase))
            {
                score += 3;
                snippets.Add($"qualifiedName: {sym.QualifiedName}");
            }

            // XML documentation summary search. This is the feature that makes
            // lifeblood_search qualitatively different from resolve_short_name —
            // the user can ask "what searches the graph for a symbol by XMLdoc?"
            // and hit THIS symbol even though its name doesn't contain "search".
            if (sym.Properties.TryGetValue("xmlDocSummary", out var docSummary)
                && !string.IsNullOrEmpty(docSummary)
                && docSummary.Contains(query.Query, StringComparison.OrdinalIgnoreCase))
            {
                score += 5;
                snippets.Add($"xmlDoc: {TruncateSnippet(docSummary, query.Query)}");
            }

            if (kindFilter != null && score > 0) score += 2;
            if (score <= 0) continue;

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
    /// Return a short snippet of the doc text centered on the first
    /// case-insensitive occurrence of the query. Cap ~80 characters so
    /// the MCP response stays lean.
    /// </summary>
    private static string TruncateSnippet(string text, string query)
    {
        const int windowRadius = 40;
        var idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return text.Length <= 80 ? text : text.Substring(0, 80) + "…";
        var start = System.Math.Max(0, idx - windowRadius);
        var end = System.Math.Min(text.Length, idx + query.Length + windowRadius);
        var prefix = start > 0 ? "…" : "";
        var suffix = end < text.Length ? "…" : "";
        return prefix + text.Substring(start, end - start) + suffix;
    }

    private static IEnumerable<string> SplitCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) yield break;
        int start = 0;
        for (int i = 1; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                yield return name.Substring(start, i - start);
                start = i;
            }
        }
        yield return name.Substring(start);
    }
}

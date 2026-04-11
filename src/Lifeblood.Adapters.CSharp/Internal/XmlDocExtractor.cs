using Microsoft.CodeAnalysis;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Single source of truth for extracting the <c>&lt;summary&gt;</c> text
/// from a Roslyn <see cref="ISymbol"/>'s XML documentation comment. Used
/// during symbol extraction to persist a short doc string on
/// <c>Symbol.Properties["xmlDocSummary"]</c> so that search and context
/// tools can key off the documentation WITHOUT needing live Roslyn
/// compilations (read-side, streaming-mode workspaces included).
///
/// Historically this logic lived inside a private helper on
/// <c>RoslynCompilationHost</c>. Phase 5 (2026-04-11) promoted it to an
/// internal shared helper so extraction and write-side tools can both
/// route through it.
/// </summary>
internal static class XmlDocExtractor
{
    /// <summary>
    /// Return the inner text of the symbol's <c>&lt;summary&gt;</c> XML
    /// element with whitespace and newlines collapsed to single spaces.
    /// Returns the raw documentation XML when no <c>&lt;summary&gt;</c>
    /// element is present, and the empty string when the symbol has no
    /// documentation at all.
    /// </summary>
    public static string ExtractSummary(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml)) return "";
        var start = xml.IndexOf("<summary>", System.StringComparison.Ordinal);
        var end = xml.IndexOf("</summary>", System.StringComparison.Ordinal);
        if (start >= 0 && end > start)
        {
            var inner = xml.Substring(start + 9, end - start - 9).Trim();
            return string.Join(" ", inner.Split(
                new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()));
        }
        return xml;
    }
}

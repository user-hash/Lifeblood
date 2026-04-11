using System.Text;
using System.Text.RegularExpressions;
using Lifeblood.Application.Ports.Right.Invariants;

namespace Lifeblood.Connectors.Mcp.Internal;

/// <summary>
/// Parses <c>CLAUDE.md</c> prose into a list of <see cref="Invariant"/>
/// records. The source-of-truth contract for the
/// <c>lifeblood_invariant_check</c> tool.
///
/// Two bullet shapes are recognised, both common in the Lifeblood
/// CLAUDE.md authoring history:
///
/// <para>
/// <b>Shape A — short, single-line body.</b> The bold contains only
/// the id, everything after the colon is body:
/// </para>
/// <code>
/// - **INV-DOMAIN-001**: `Lifeblood.Domain` has ZERO dependencies. Not Roslyn, not JSON, …
/// </code>
///
/// <para>
/// <b>Shape B — multi-paragraph body.</b> The bold contains the id
/// PLUS a single title sentence ending in a period, then the body
/// continues after the closing <c>**</c>:
/// </para>
/// <code>
/// - **INV-CANONICAL-001. Roslyn compilations receive the full transitive
///   dependency closure, not just direct `ModuleInfo.Dependencies`.** Unlike
///   MSBuild's ProjectReference flow, Roslyn's `CSharpCompilation.Create`
///   does NOT walk references transitively. …
/// </code>
///
/// <para>
/// The parser uses a line-by-line walker. A line whose trimmed prefix
/// matches <c>- **INV-</c> opens a new invariant block. The block
/// continues until the next invariant-opening line or a markdown header
/// (<c># ... ##### </c>). Body is the verbatim text of the block with
/// the opening marker preserved. Title is extracted from the bold
/// section: shape B yields the bold-enclosed title sentence; shape A
/// falls back to the first sentence after the colon, capped at
/// <see cref="MaxTitleLength"/> chars.
/// </para>
///
/// <para>
/// Duplicate ids are kept — the first occurrence wins for
/// <see cref="IInvariantProvider.GetById"/>, but every occurrence's
/// source line is reported in <see cref="ClaudeMdParseResult.Duplicates"/>
/// so <c>InvariantAudit</c> can surface the drift.
/// </para>
///
/// <para>
/// Malformed invariants (unclosed bold, missing id shape, body-only
/// with no id) produce a warning string in
/// <see cref="ClaudeMdParseResult.Warnings"/> and are dropped from the
/// returned list. The parser never throws on bad input; the worst case
/// is an empty result with a descriptive warning.
/// </para>
///
/// <para>
/// Deterministic for a given input. Pure function. No filesystem
/// access, no graph access. Pinned by <c>ClaudeMdInvariantParserTests</c>.
/// </para>
/// </summary>
internal static class ClaudeMdInvariantParser
{
    /// <summary>
    /// Cap for shape-A titles (inline bodies). A title longer than
    /// this is truncated with an ellipsis so tool responses stay lean.
    /// </summary>
    private const int MaxTitleLength = 140;

    /// <summary>
    /// Regex that recognises an invariant-opening bullet. Anchored at
    /// the start of a trimmed line to avoid matching inline references
    /// like <c>see INV-FOO-001</c> in a body paragraph.
    /// </summary>
    private static readonly Regex InvariantBulletStart = new(
        @"^-\s+\*\*(?<id>INV-[A-Z][A-Z0-9]*-\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Extracts the bold-enclosed title sentence of shape B — the text
    /// between the first <c>**INV-X-N.</c> and the closing <c>**</c>.
    /// Multi-line bold spans are handled by the block reconstruction
    /// below; the regex operates on a single joined string.
    /// </summary>
    private static readonly Regex BoldTitleCapture = new(
        @"\*\*(?<id>INV-[A-Z][A-Z0-9]*-\d+)\.\s+(?<title>.+?)\*\*",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>
    /// Extracts a shape-A bullet's id and the colon-separated body
    /// prefix on the same line. Used when no BoldTitleCapture match
    /// was found inside the block.
    /// </summary>
    private static readonly Regex ShapeAColonBody = new(
        @"\*\*(?<id>INV-[A-Z][A-Z0-9]*-\d+)\*\*\s*:\s*(?<rest>.+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Matches a markdown heading line (<c>#</c> through <c>######</c>).
    /// Headings terminate an in-progress invariant block.
    /// </summary>
    private static readonly Regex MarkdownHeading = new(
        @"^#{1,6}\s",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parse the full text of a <c>CLAUDE.md</c> file into a structured
    /// result. The returned list preserves source order so callers can
    /// present an audit that mirrors the document's narrative flow.
    /// </summary>
    public static ClaudeMdParseResult Parse(string claudeMdText)
    {
        if (string.IsNullOrEmpty(claudeMdText))
        {
            return new ClaudeMdParseResult(
                System.Array.Empty<Invariant>(),
                System.Array.Empty<string>(),
                new Dictionary<string, int[]>(StringComparer.Ordinal));
        }

        var lines = claudeMdText.Split('\n');
        var invariants = new List<Invariant>(64);
        var warnings = new List<string>();
        // Map id → list of 1-based line numbers it was declared on.
        var idOccurrences = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        int i = 0;
        while (i < lines.Length)
        {
            var trimmedLine = TrimCarriageReturn(lines[i]).TrimStart();
            var match = InvariantBulletStart.Match(trimmedLine);
            if (!match.Success)
            {
                i++;
                continue;
            }

            var openingLineNumber = i + 1; // 1-based for human-friendly output

            // Collect the invariant block: from the opening bullet down
            // to the line BEFORE the next opening bullet or markdown
            // heading. Blank lines are allowed inside the block (they
            // separate paragraphs); what terminates is another
            // `- **INV-` or a `## ` heading.
            var blockBuilder = new StringBuilder();
            blockBuilder.Append(TrimCarriageReturn(lines[i]));
            int j = i + 1;
            while (j < lines.Length)
            {
                var lookAhead = TrimCarriageReturn(lines[j]);
                var lookAheadTrimmed = lookAhead.TrimStart();
                if (InvariantBulletStart.IsMatch(lookAheadTrimmed)) break;
                if (MarkdownHeading.IsMatch(lookAhead)) break;
                blockBuilder.Append('\n').Append(lookAhead);
                j++;
            }
            i = j;

            var blockText = blockBuilder.ToString();
            var parsed = BuildInvariant(blockText, openingLineNumber, warnings);
            if (parsed == null) continue;

            // Record occurrence for duplicate detection BEFORE filtering
            // to first-win. Duplicates still audit; only the first wins
            // for GetById.
            if (!idOccurrences.TryGetValue(parsed.Id, out var lineList))
            {
                lineList = new List<int>(1);
                idOccurrences[parsed.Id] = lineList;
                invariants.Add(parsed);
            }
            lineList.Add(openingLineNumber);
        }

        var duplicates = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var kv in idOccurrences)
        {
            if (kv.Value.Count > 1)
            {
                duplicates[kv.Key] = kv.Value.ToArray();
            }
        }

        return new ClaudeMdParseResult(
            invariants.ToArray(),
            warnings.ToArray(),
            duplicates);
    }

    /// <summary>
    /// Assemble one <see cref="Invariant"/> from a block of text that
    /// has already been identified as opening with an invariant bullet.
    /// Returns <c>null</c> when the block is malformed (the bold marker
    /// couldn't be parsed, the id is unrecognisable, etc.); a warning
    /// is appended to <paramref name="warnings"/> in that case.
    /// </summary>
    private static Invariant? BuildInvariant(string blockText, int openingLineNumber, List<string> warnings)
    {
        var firstLineMatch = InvariantBulletStart.Match(blockText.TrimStart());
        if (!firstLineMatch.Success)
        {
            warnings.Add($"line {openingLineNumber}: block does not open with a recognised invariant bullet");
            return null;
        }

        var id = firstLineMatch.Groups["id"].Value;
        var category = ExtractCategoryFromId(id);
        if (string.IsNullOrEmpty(category))
        {
            warnings.Add($"line {openingLineNumber}: could not derive category from id '{id}'");
            return null;
        }

        // Try shape B first — the richer form. The regex is singleline
        // so the title sentence can span multiple physical lines.
        var boldTitleMatch = BoldTitleCapture.Match(blockText);
        string title;
        if (boldTitleMatch.Success && boldTitleMatch.Groups["id"].Value == id)
        {
            // Collapse internal whitespace so a wrapped title reads
            // naturally as a single sentence.
            title = CollapseWhitespace(boldTitleMatch.Groups["title"].Value).TrimEnd('.', ' ');
        }
        else
        {
            // Shape A: look for the colon-separated body and extract
            // the first sentence from it.
            var shapeAMatch = ShapeAColonBody.Match(blockText);
            if (shapeAMatch.Success && shapeAMatch.Groups["id"].Value == id)
            {
                var rest = CollapseWhitespace(shapeAMatch.Groups["rest"].Value);
                title = ExtractFirstSentence(rest);
            }
            else
            {
                // Last-resort fallback: title is the trimmed first line
                // minus the bullet prefix. This keeps the tool usable
                // even for invariants authored in a slightly different
                // shape.
                var firstPhysicalLine = blockText.Split('\n', 2)[0];
                title = firstPhysicalLine
                    .TrimStart('-', ' ', '*')
                    .TrimEnd('*', ' ');
                warnings.Add(
                    $"line {openingLineNumber}: id '{id}' did not match shape A or B; " +
                    "fell back to bullet-prefix title extraction");
            }
        }

        if (title.Length > MaxTitleLength)
        {
            title = title.Substring(0, MaxTitleLength - 1) + "…";
        }

        return new Invariant
        {
            Id = id,
            Title = title,
            Body = blockText.Trim(),
            Category = category,
            SourceLine = openingLineNumber,
        };
    }

    /// <summary>
    /// <c>INV-CANONICAL-001</c> → <c>CANONICAL</c>. Returns empty string
    /// for malformed ids (the parser warns on those separately).
    /// Supports multi-segment category prefixes the codebase already
    /// uses such as <c>INV-USAGE-PROBE-002</c> → <c>USAGE-PROBE</c>.
    /// </summary>
    internal static string ExtractCategoryFromId(string id)
    {
        if (string.IsNullOrEmpty(id) || !id.StartsWith("INV-", StringComparison.Ordinal)) return string.Empty;
        var withoutPrefix = id.Substring("INV-".Length);
        // Find the LAST dash before the numeric tail; everything
        // between the first position and that dash is the category.
        var lastDash = withoutPrefix.LastIndexOf('-');
        if (lastDash <= 0) return string.Empty;
        var tail = withoutPrefix.Substring(lastDash + 1);
        foreach (var ch in tail) if (!char.IsDigit(ch)) return string.Empty;
        return withoutPrefix.Substring(0, lastDash);
    }

    /// <summary>
    /// Return the first sentence of <paramref name="text"/>. The sentence
    /// boundary is a period followed by whitespace or end-of-string, NOT
    /// any period — which correctly preserves dotted identifiers inside
    /// backtick code spans (<c>`Lifeblood.Domain`</c>) and dotted symbol
    /// names (<c>Foo.Bar</c>) that would otherwise truncate the title
    /// mid-identifier.
    /// </summary>
    private static string ExtractFirstSentence(string text)
    {
        var trimmed = text.TrimStart();
        for (int i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] != '.') continue;
            var nextIsWhitespaceOrEnd = i == trimmed.Length - 1 || char.IsWhiteSpace(trimmed[i + 1]);
            if (nextIsWhitespaceOrEnd)
            {
                return trimmed.Substring(0, i).Trim();
            }
        }
        return trimmed;
    }

    private static string CollapseWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var sb = new StringBuilder(text.Length);
        bool lastWasSpace = false;
        foreach (var ch in text)
        {
            if (ch == '\r' || ch == '\n' || ch == '\t' || ch == ' ')
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
        }
        return sb.ToString().Trim();
    }

    private static string TrimCarriageReturn(string line)
        => line.Length > 0 && line[line.Length - 1] == '\r' ? line.Substring(0, line.Length - 1) : line;
}

/// <summary>
/// Parser output. Invariants are in source order; duplicates map id
/// → every line where that id was declared; warnings are any
/// non-fatal parse issues surfaced to the audit.
/// </summary>
internal sealed class ClaudeMdParseResult
{
    public Invariant[] Invariants { get; }
    public string[] Warnings { get; }
    public Dictionary<string, int[]> Duplicates { get; }

    public ClaudeMdParseResult(Invariant[] invariants, string[] warnings, Dictionary<string, int[]> duplicates)
    {
        Invariants = invariants;
        Warnings = warnings;
        Duplicates = duplicates;
    }
}

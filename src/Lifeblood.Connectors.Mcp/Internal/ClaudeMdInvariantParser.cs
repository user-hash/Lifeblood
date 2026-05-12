using System.Text;
using System.Text.RegularExpressions;
using Lifeblood.Application.Ports.Right.Invariants;

namespace Lifeblood.Connectors.Mcp.Internal;

/// <summary>
/// Parses <c>CLAUDE.md</c> prose into a list of <see cref="Invariant"/>
/// records. The source-of-truth contract for the
/// <c>lifeblood_invariant_check</c> tool.
///
/// Three shapes are recognised, all common across the Lifeblood and
/// real-world CLAUDE.md authoring histories:
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
/// <b>Shape C — bare bold paragraph (flat-hot-rules authoring style).</b> No
/// bullet prefix; the bold encloses both the id and a title sentence
/// separated by a colon, body continues after the closing <c>**</c>:
/// </para>
/// <code>
/// **INV-WORK-001: Read before writing.** Read every file and method
/// you reference end-to-end before writing about it. …
/// </code>
///
/// <para>
/// <b>Shape D — shape-A + parenthesized version tag.</b> A bullet with
/// a version annotation between the bold close and the colon:
/// </para>
/// <code>
/// - **INV-DSP-012** (v1.1.566): POST_SAT_LP coefficient must be …
/// </code>
///
/// <para>
/// <b>Shape E — colon inside the bold (INDEX-style summary).</b> A
/// bullet whose colon sits before the closing <c>**</c>:
/// </para>
/// <code>
/// - **INV-ANIM-1:** BPM synchronization — Derive timing from _bpm …
/// </code>
///
/// <para>
/// The parser uses a line-by-line walker. A line whose trimmed prefix
/// matches <c>- **INV-</c> (shapes A/B) OR opens with a bare
/// <c>**INV-XXX-NNN:</c> (shape C) opens a new invariant block. The
/// block continues until the next invariant-opening line or a markdown
/// header (<c># ... ##### </c>). Body is the verbatim text of the block
/// with the opening marker preserved. Title is extracted from the bold
/// section: shape B yields the bold-enclosed title sentence; shape C
/// yields the colon-separated title from inside the bold; shape A falls
/// back to the first sentence after the colon. Capped at
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
    /// Regex that recognises an invariant-opening bullet (shapes A and
    /// B). Anchored at the start of a trimmed line to avoid matching
    /// inline references like <c>see INV-FOO-001</c> in a body
    /// paragraph.
    /// </summary>
    private static readonly Regex InvariantBulletStart = new(
        @"^-\s+\*\*(?<id>INV-[A-Z][A-Z0-9]*(?:-[A-Z][A-Z0-9]*)*-\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Shape C: bare bold paragraph with id-and-title inside the bold,
    /// body text following the closing <c>**</c>. Recognised when a
    /// line's trimmed prefix is <c>**INV-XXX-NNN:</c> (id, colon, then
    /// title sentence still inside the bold). the dogfood project's CLAUDE.md hot-rule
    /// section uses this shape — many INVs in a row, no bullets, no
    /// section headers between them.
    /// </summary>
    private static readonly Regex InvariantBareBoldStart = new(
        @"^\*\*(?<id>INV-[A-Z][A-Z0-9]*(?:-[A-Z][A-Z0-9]*)*-\d+)\s*:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Shape C title capture: the bold begins with the id, colon, then
    /// the title sentence, then closes with <c>**</c>. The title may
    /// span multiple physical lines if the bold wraps.
    /// </summary>
    private static readonly Regex BareBoldTitleCapture = new(
        @"\*\*(?<id>INV-[A-Z][A-Z0-9]*(?:-[A-Z][A-Z0-9]*)*-\d+)\s*:\s*(?<title>.+?)\*\*",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>
    /// Extracts the bold-enclosed title sentence of shape B — the text
    /// between the first <c>**INV-X-N.</c> and the closing <c>**</c>.
    /// Multi-line bold spans are handled by the block reconstruction
    /// below; the regex operates on a single joined string.
    /// </summary>
    private static readonly Regex BoldTitleCapture = new(
        @"\*\*(?<id>INV-[A-Z][A-Z0-9]*(?:-[A-Z][A-Z0-9]*)*-\d+)\.\s+(?<title>.+?)\*\*",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>
    /// Extracts a shape-A bullet's id and the colon-separated body
    /// prefix on the same line. Used when no BoldTitleCapture match
    /// was found inside the block.
    /// </summary>
    private static readonly Regex ShapeAColonBody = new(
        @"\*\*(?<id>INV-[A-Z][A-Z0-9]*(?:-[A-Z][A-Z0-9]*)*-\d+)\*\*\s*:\s*(?<rest>.+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Shape D: shape-A with an optional parenthesized version/date tag
    /// between the bold close and the colon. that authoring style:
    /// <c>- **INV-DSP-012** (v1.1.566): body...</c>. The parens contents
    /// are not captured into the title — they're a version annotation,
    /// not part of the rule.
    /// </summary>
    private static readonly Regex ShapeDColonBody = new(
        @"\*\*(?<id>INV-[A-Z][A-Z0-9]*(?:-[A-Z][A-Z0-9]*)*-\d+)\*\*\s*\([^)]*\)\s*:\s*(?<rest>.+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Shape E: colon-inside-the-bold variant. INDEX-style listings use
    /// this for terse summaries: <c>- **INV-ANIM-1:** BPM synchronization
    /// - Derive timing from _bpm</c>. The colon is the LAST character
    /// before the closing <c>**</c>; whitespace between id and colon is
    /// allowed but not required.
    /// </summary>
    private static readonly Regex ShapeEColonBody = new(
        @"\*\*(?<id>INV-[A-Z][A-Z0-9]*(?:-[A-Z][A-Z0-9]*)*-\d+)\s*:\*\*\s*(?<rest>.+)",
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
            var bulletMatch = InvariantBulletStart.Match(trimmedLine);
            var bareBoldMatch = bulletMatch.Success ? Match.Empty : InvariantBareBoldStart.Match(trimmedLine);
            if (!bulletMatch.Success && !bareBoldMatch.Success)
            {
                i++;
                continue;
            }

            var openingLineNumber = i + 1; // 1-based for human-friendly output

            // Collect the invariant block: from the opening line down
            // to the line BEFORE the next opening line or markdown
            // heading. Blank lines are allowed inside the block (they
            // separate paragraphs); what terminates is another opening
            // (`- **INV-` bullet or `**INV-XXX:` bare bold) or a `## `
            // heading.
            var blockBuilder = new StringBuilder();
            blockBuilder.Append(TrimCarriageReturn(lines[i]));
            int j = i + 1;
            while (j < lines.Length)
            {
                var lookAhead = TrimCarriageReturn(lines[j]);
                var lookAheadTrimmed = lookAhead.TrimStart();
                if (InvariantBulletStart.IsMatch(lookAheadTrimmed)) break;
                if (InvariantBareBoldStart.IsMatch(lookAheadTrimmed)) break;
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
        var trimmedBlock = blockText.TrimStart();
        var bulletMatch = InvariantBulletStart.Match(trimmedBlock);
        var bareBoldMatch = bulletMatch.Success ? Match.Empty : InvariantBareBoldStart.Match(trimmedBlock);
        if (!bulletMatch.Success && !bareBoldMatch.Success)
        {
            warnings.Add($"line {openingLineNumber}: block does not open with a recognised invariant marker");
            return null;
        }

        var id = bulletMatch.Success ? bulletMatch.Groups["id"].Value : bareBoldMatch.Groups["id"].Value;
        var category = ExtractCategoryFromId(id);
        if (string.IsNullOrEmpty(category))
        {
            warnings.Add($"line {openingLineNumber}: could not derive category from id '{id}'");
            return null;
        }

        string title;
        if (bareBoldMatch.Success)
        {
            // Shape C: bold encloses `INV-XXX-NNN: <title>` with body
            // following the closing `**`. The capture is singleline so
            // a wrapped bold reads as one sentence.
            var bareTitleMatch = BareBoldTitleCapture.Match(blockText);
            if (bareTitleMatch.Success && bareTitleMatch.Groups["id"].Value == id)
            {
                title = CollapseWhitespace(bareTitleMatch.Groups["title"].Value).TrimEnd('.', ' ');
            }
            else
            {
                // Bold not closed on this block — first physical line
                // minus markers as last-resort title.
                var firstPhysicalLine = blockText.Split('\n', 2)[0];
                title = firstPhysicalLine.TrimStart('*', ' ').TrimEnd('*', ' ');
                warnings.Add(
                    $"line {openingLineNumber}: id '{id}' opened as shape C but bold was not closed; " +
                    "fell back to first-line title extraction");
            }
        }
        else
        {
            // Try shape B first — the richer form. The regex is singleline
            // so the title sentence can span multiple physical lines.
            var boldTitleMatch = BoldTitleCapture.Match(blockText);
            if (boldTitleMatch.Success && boldTitleMatch.Groups["id"].Value == id)
            {
                title = CollapseWhitespace(boldTitleMatch.Groups["title"].Value).TrimEnd('.', ' ');
            }
            else
            {
                // Try shape D first (parenthesized version tag between
                // bold and colon) — strictly more specific than shape A,
                // so it must be checked first to avoid the parens text
                // leaking into the body capture.
                var shapeDMatch = ShapeDColonBody.Match(blockText);
                if (shapeDMatch.Success && shapeDMatch.Groups["id"].Value == id)
                {
                    var rest = CollapseWhitespace(shapeDMatch.Groups["rest"].Value);
                    title = ExtractFirstSentence(rest);
                }
                else
                {
                    var shapeAMatch = ShapeAColonBody.Match(blockText);
                    if (shapeAMatch.Success && shapeAMatch.Groups["id"].Value == id)
                    {
                        var rest = CollapseWhitespace(shapeAMatch.Groups["rest"].Value);
                        title = ExtractFirstSentence(rest);
                    }
                    else
                    {
                        var shapeEMatch = ShapeEColonBody.Match(blockText);
                        if (shapeEMatch.Success && shapeEMatch.Groups["id"].Value == id)
                        {
                            var rest = CollapseWhitespace(shapeEMatch.Groups["rest"].Value);
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
                                $"line {openingLineNumber}: id '{id}' did not match shape A, B, D, or E; " +
                                "fell back to bullet-prefix title extraction");
                        }
                    }
                }
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

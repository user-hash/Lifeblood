using Lifeblood.Connectors.Mcp.Internal;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Unit tests for <see cref="ClaudeMdInvariantParser"/>. The parser is a
/// pure function — text in, structured result out — so every test
/// builds a synthetic CLAUDE.md snippet and asserts against the
/// extracted records. No filesystem, no graph, no mocks.
///
/// The parser is the load-bearing boundary between CLAUDE.md prose
/// and the <c>lifeblood_invariant_check</c> tool. Breaking it means
/// the tool surface silently returns wrong data, so coverage here
/// needs to be thorough — every invariant bullet shape the codebase
/// has used (shape A, shape B, nested bodies, duplicate ids) gets a
/// dedicated pin.
/// </summary>
public class ClaudeMdInvariantParserTests
{
    // ─────────────────────────────────────────────────────────────────────
    // Shape A: `- **INV-X-N**: body`. Short, single-line body.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_ShapeA_SingleInvariant_ExtractsIdAndFirstSentenceTitle()
    {
        var text = "- **INV-DOMAIN-001**: Lifeblood.Domain has ZERO dependencies. Not Roslyn, not JSON.\n";

        var result = ClaudeMdInvariantParser.Parse(text);

        Assert.Single(result.Invariants);
        var inv = result.Invariants[0];
        Assert.Equal("INV-DOMAIN-001", inv.Id);
        Assert.Equal("DOMAIN", inv.Category);
        // First sentence ends at the period that precedes whitespace.
        // Dotted identifiers like "Lifeblood.Domain" are preserved
        // because the period after "Domain" is followed by a letter,
        // not whitespace.
        Assert.Equal("Lifeblood.Domain has ZERO dependencies", inv.Title);
        Assert.Equal(1, inv.SourceLine);
        Assert.Contains("ZERO dependencies", inv.Body);
    }

    [Fact]
    public void Parse_ShapeA_BacktickCodeSpanInTitle_NotTruncatedAtInnerPeriod()
    {
        // Regression pin for the Shape-A title extractor. The body
        // starts with `Lifeblood.Domain` in a code span — the period
        // inside the backticks must NOT terminate the title. The real
        // sentence ends at the period after "dependencies".
        var text = "- **INV-DOMAIN-001**: `Lifeblood.Domain` has ZERO dependencies. Not Roslyn.\n";

        var result = ClaudeMdInvariantParser.Parse(text);

        Assert.Single(result.Invariants);
        Assert.Equal("`Lifeblood.Domain` has ZERO dependencies", result.Invariants[0].Title);
    }

    [Fact]
    public void Parse_ShapeA_MultipleInvariants_PreservesOrderAndLineNumbers()
    {
        var text =
            "- **INV-APP-001**: Application depends only on Domain.\n" +
            "- **INV-APP-002**: Application never references concrete adapters.\n";

        var result = ClaudeMdInvariantParser.Parse(text);

        Assert.Equal(2, result.Invariants.Length);
        Assert.Equal("INV-APP-001", result.Invariants[0].Id);
        Assert.Equal(1, result.Invariants[0].SourceLine);
        Assert.Equal("INV-APP-002", result.Invariants[1].Id);
        Assert.Equal(2, result.Invariants[1].SourceLine);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Shape B: `- **INV-X-N. Title sentence.** Body...`. Bold contains
    // id + title; body continues after the closing **.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_ShapeB_SingleLine_ExtractsBoldTitleAsTitle()
    {
        var text = "- **INV-CANONICAL-001. Roslyn compilations receive the full transitive dependency closure.** Unlike MSBuild, Roslyn does NOT walk references transitively.\n";

        var result = ClaudeMdInvariantParser.Parse(text);

        Assert.Single(result.Invariants);
        var inv = result.Invariants[0];
        Assert.Equal("INV-CANONICAL-001", inv.Id);
        Assert.Equal("CANONICAL", inv.Category);
        Assert.Equal("Roslyn compilations receive the full transitive dependency closure", inv.Title);
    }

    [Fact]
    public void Parse_ShapeB_MultiLineTitle_CollapsesWhitespace()
    {
        // The title sentence wraps across multiple physical lines —
        // common in CLAUDE.md for long invariant descriptions. The
        // parser must collapse internal whitespace so the title reads
        // as a single sentence.
        var text =
            "- **INV-CANONICAL-001. Roslyn compilations receive the full transitive\n" +
            "  dependency closure, not just direct `ModuleInfo.Dependencies`.** Unlike\n" +
            "  MSBuild's ProjectReference flow, Roslyn's `CSharpCompilation.Create`\n" +
            "  does NOT walk references transitively.\n";

        var result = ClaudeMdInvariantParser.Parse(text);

        Assert.Single(result.Invariants);
        var inv = result.Invariants[0];
        Assert.Equal("INV-CANONICAL-001", inv.Id);
        // Title is the full bold sentence collapsed to one line, no trailing period.
        Assert.Equal(
            "Roslyn compilations receive the full transitive dependency closure, not just direct `ModuleInfo.Dependencies`",
            inv.Title);
    }

    [Fact]
    public void Parse_ShapeB_MultiParagraphBody_PreservesFullBody()
    {
        var text =
            "- **INV-BCL-001. Single BCL per compilation.** Two BCLs causes CS0433.\n" +
            "\n" +
            "  Some modules ship their own BCL. Others rely on the host.\n" +
            "\n" +
            "  The detection lives in RoslynModuleDiscovery.\n";

        var result = ClaudeMdInvariantParser.Parse(text);

        Assert.Single(result.Invariants);
        var body = result.Invariants[0].Body;
        Assert.Contains("Single BCL per compilation", body);
        Assert.Contains("CS0433", body);
        Assert.Contains("RoslynModuleDiscovery", body);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Block termination: an invariant block ends at the next INV bullet
    // or the next markdown heading. Prose lines in between belong to
    // the current invariant.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_BlockTerminatesAtNextInvariantBullet()
    {
        var text =
            "- **INV-GRAPH-001**: SymbolKind is language-agnostic.\n" +
            "\n" +
            "  Some extra prose that belongs to GRAPH-001.\n" +
            "\n" +
            "- **INV-GRAPH-002**: Language-specific metadata goes in Properties.\n";

        var result = ClaudeMdInvariantParser.Parse(text);

        Assert.Equal(2, result.Invariants.Length);
        Assert.Contains("extra prose that belongs to GRAPH-001", result.Invariants[0].Body);
        Assert.DoesNotContain("Language-specific metadata", result.Invariants[0].Body);
        Assert.Contains("Language-specific metadata", result.Invariants[1].Body);
    }

    [Fact]
    public void Parse_BlockTerminatesAtMarkdownHeading()
    {
        var text =
            "- **INV-FOO-001**: first invariant body.\n" +
            "\n" +
            "## Next Section\n" +
            "\n" +
            "Not an invariant.\n";

        var result = ClaudeMdInvariantParser.Parse(text);

        Assert.Single(result.Invariants);
        Assert.DoesNotContain("Next Section", result.Invariants[0].Body);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Duplicate-id detection. First occurrence wins for GetById; every
    // occurrence appears in Duplicates so the audit can surface the
    // drift for the maintainer to resolve.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_DuplicateId_FirstWins_ButBothLinesRecorded()
    {
        var text =
            "- **INV-TEST-001**: first occurrence body.\n" +
            "\n" +
            "## Some Heading\n" +
            "\n" +
            "- **INV-TEST-001**: SECOND occurrence different body.\n";

        var result = ClaudeMdInvariantParser.Parse(text);

        // Only one entry in Invariants — first wins for GetById.
        Assert.Single(result.Invariants);
        Assert.Contains("first occurrence", result.Invariants[0].Body);

        // Duplicates map carries the id with both line numbers.
        Assert.True(result.Duplicates.ContainsKey("INV-TEST-001"));
        Assert.Equal(2, result.Duplicates["INV-TEST-001"].Length);
    }

    [Fact]
    public void Parse_NoDuplicates_EmptyDuplicatesMap()
    {
        var text = "- **INV-FOO-001**: unique one.\n- **INV-FOO-002**: unique two.\n";

        var result = ClaudeMdInvariantParser.Parse(text);

        Assert.Empty(result.Duplicates);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Ignored shapes. In-line references to INV ids, content without
    // the right bullet prefix, and non-invariant lines must NOT be
    // extracted as invariants.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_InlineInvariantReference_IsNotExtracted()
    {
        var text =
            "This paragraph mentions INV-FOO-001 in running prose.\n" +
            "The reference should not be extracted.\n";

        var result = ClaudeMdInvariantParser.Parse(text);

        Assert.Empty(result.Invariants);
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsEmpty()
    {
        var result = ClaudeMdInvariantParser.Parse("");

        Assert.Empty(result.Invariants);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.Duplicates);
    }

    [Fact]
    public void Parse_OnlyHeadings_ReturnsEmpty()
    {
        var result = ClaudeMdInvariantParser.Parse("# Title\n\n## Subtitle\n\nSome prose.\n");

        Assert.Empty(result.Invariants);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Category extraction. Pins the id-prefix → category derivation
    // logic, including the multi-segment prefixes the Lifeblood
    // codebase already uses (INV-USAGE-PROBE-001).
    // ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("INV-DOMAIN-001", "DOMAIN")]
    [InlineData("INV-CANONICAL-001", "CANONICAL")]
    [InlineData("INV-USAGE-PORT-001", "USAGE-PORT")]
    [InlineData("INV-USAGE-PROBE-002", "USAGE-PROBE")]
    [InlineData("INV-FILE-EDGE-001", "FILE-EDGE")]
    [InlineData("INV-SCRIPTHOST-001", "SCRIPTHOST")]
    [InlineData("INV-TOOLREG-001", "TOOLREG")]
    public void ExtractCategoryFromId_HandlesMultiSegmentPrefixes(string id, string expectedCategory)
    {
        var actual = ClaudeMdInvariantParser.ExtractCategoryFromId(id);
        Assert.Equal(expectedCategory, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("NOT-AN-INVARIANT")]
    [InlineData("INV-FOO")]       // missing numeric tail
    [InlineData("INV-FOO-BAR")]   // tail is not numeric
    public void ExtractCategoryFromId_MalformedId_ReturnsEmpty(string id)
    {
        var actual = ClaudeMdInvariantParser.ExtractCategoryFromId(id);
        Assert.Equal(string.Empty, actual);
    }

    // ───────────────────────────────────────────────────────────────────
    // Shape C — DAWG hot-rules style. Bare bold paragraph, no bullet,
    // id-and-title inside the bold separated by a colon. Multiple INVs
    // appear in a row with body paragraphs in between, no headers.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_ShapeC_BareBoldParagraph_ExtractsIdAndTitle()
    {
        const string md = "**INV-WORK-001: Read before writing.** Read every file and method " +
                          "you reference end-to-end before writing about it.";
        var result = ClaudeMdInvariantParser.Parse(md);
        Assert.Empty(result.Warnings);
        var inv = Assert.Single(result.Invariants);
        Assert.Equal("INV-WORK-001", inv.Id);
        Assert.Equal("Read before writing", inv.Title);
        Assert.Equal("WORK", inv.Category);
        Assert.Contains("Read every file", inv.Body);
    }

    [Fact]
    public void Parse_ShapeC_MultipleConsecutive_AllExtracted()
    {
        const string md =
            "**INV-WORK-001: Read before writing.** Read every file end-to-end.\n" +
            "\n" +
            "**INV-WORK-002: Verify every claim.** Before stating something is done.\n" +
            "\n" +
            "**INV-WORK-003: Find simplifications.** When you read code and notice 6 identical methods.";
        var result = ClaudeMdInvariantParser.Parse(md);
        Assert.Empty(result.Warnings);
        Assert.Equal(3, result.Invariants.Length);
        Assert.Equal("INV-WORK-001", result.Invariants[0].Id);
        Assert.Equal("INV-WORK-002", result.Invariants[1].Id);
        Assert.Equal("INV-WORK-003", result.Invariants[2].Id);
        Assert.Equal("Verify every claim", result.Invariants[1].Title);
    }

    [Fact]
    public void Parse_ShapeC_BodyTerminatesAtNextOpener()
    {
        const string md =
            "**INV-FOO-001: First rule.** Body of first.\n" +
            "Continued body of first.\n" +
            "\n" +
            "**INV-BAR-002: Second rule.** Body of second.";
        var result = ClaudeMdInvariantParser.Parse(md);
        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.Invariants.Length);
        Assert.Contains("Continued body of first", result.Invariants[0].Body);
        Assert.DoesNotContain("Body of second", result.Invariants[0].Body);
    }

    [Fact]
    public void Parse_ShapeC_HeadingTerminatesBlock()
    {
        const string md =
            "**INV-FOO-001: First.** Body of first.\n" +
            "## Some Section\n" +
            "Not part of the invariant.";
        var result = ClaudeMdInvariantParser.Parse(md);
        var inv = Assert.Single(result.Invariants);
        Assert.Contains("Body of first", inv.Body);
        Assert.DoesNotContain("Not part of the invariant", inv.Body);
    }

    [Fact]
    public void Parse_MixedShapes_AllRecognised()
    {
        const string md =
            "- **INV-DOMAIN-001**: shape A inline body.\n" +
            "\n" +
            "- **INV-CANONICAL-001. Shape B title sentence.** Shape B body paragraph.\n" +
            "\n" +
            "**INV-WORK-001: Shape C bare bold.** Shape C body paragraph.";
        var result = ClaudeMdInvariantParser.Parse(md);
        Assert.Empty(result.Warnings);
        Assert.Equal(3, result.Invariants.Length);
        Assert.Equal("INV-DOMAIN-001", result.Invariants[0].Id);
        Assert.Equal("INV-CANONICAL-001", result.Invariants[1].Id);
        Assert.Equal("INV-WORK-001", result.Invariants[2].Id);
        Assert.Equal("Shape B title sentence", result.Invariants[1].Title);
        Assert.Equal("Shape C bare bold", result.Invariants[2].Title);
    }

    // ───────────────────────────────────────────────────────────────────
    // Shape D — DAWG bullet with parenthesized version tag between the
    // bold close and the colon: `- **INV-DSP-012** (v1.1.566): body...`
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_ShapeD_ParenthesizedVersionTag_TitleExtractedFromBody()
    {
        const string md = "- **INV-DSP-012** (v1.1.566): POST_SAT_LP coefficient must be identical " +
                          "in mono and stereo paths. Ratchet-tested.";
        var result = ClaudeMdInvariantParser.Parse(md);
        Assert.Empty(result.Warnings);
        var inv = Assert.Single(result.Invariants);
        Assert.Equal("INV-DSP-012", inv.Id);
        Assert.Equal("DSP", inv.Category);
        Assert.Equal(
            "POST_SAT_LP coefficient must be identical in mono and stereo paths",
            inv.Title);
    }

    [Fact]
    public void Parse_ShapeD_VersionTagDoesNotLeakIntoTitle()
    {
        const string md = "- **INV-DSP-016** (v1.1.695): Production code may not call X directly.";
        var result = ClaudeMdInvariantParser.Parse(md);
        Assert.Empty(result.Warnings);
        var inv = Assert.Single(result.Invariants);
        Assert.DoesNotContain("v1.1.695", inv.Title);
        Assert.DoesNotContain("(", inv.Title);
        Assert.Equal("Production code may not call X directly", inv.Title);
    }

    // ───────────────────────────────────────────────────────────────────
    // Shape E — colon inside the bold: `- **INV-ANIM-1:** body...`
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_ShapeE_ColonInsideBold_TitleExtractedFromBody()
    {
        const string md = "- **INV-ANIM-1:** BPM synchronization — Derive timing from _bpm, " +
                          "use modulo phase.";
        var result = ClaudeMdInvariantParser.Parse(md);
        Assert.Empty(result.Warnings);
        var inv = Assert.Single(result.Invariants);
        Assert.Equal("INV-ANIM-1", inv.Id);
        Assert.Equal("ANIM", inv.Category);
        Assert.Equal("BPM synchronization — Derive timing from _bpm, use modulo phase", inv.Title);
    }

    [Fact]
    public void Parse_ShapeE_SingleDigitNumericTail_Recognised()
    {
        const string md = "- **INV-PREVIEW-001:** Atomic state transitions - Use TryCaptureTransitionState().";
        var result = ClaudeMdInvariantParser.Parse(md);
        Assert.Empty(result.Warnings);
        var inv = Assert.Single(result.Invariants);
        Assert.Equal("INV-PREVIEW-001", inv.Id);
        Assert.Contains("Atomic state transitions", inv.Title);
    }

    [Fact]
    public void Parse_AllFiveShapes_NoWarnings()
    {
        const string md =
            "- **INV-DOMAIN-001**: shape A.\n" +
            "\n" +
            "- **INV-CANONICAL-001. Shape B sentence.** Shape B body.\n" +
            "\n" +
            "**INV-WORK-001: Shape C.** Shape C body.\n" +
            "\n" +
            "- **INV-DSP-012** (v1.1.566): Shape D body sentence.\n" +
            "\n" +
            "- **INV-ANIM-1:** Shape E body sentence.";
        var result = ClaudeMdInvariantParser.Parse(md);
        Assert.Empty(result.Warnings);
        Assert.Equal(5, result.Invariants.Length);
    }
}

using Lifeblood.Application.Ports.Right;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Pins the Rule 4 extracted-short-name fallback added to
/// <see cref="LifebloodSymbolResolver"/>. Two real dogfood reports landed
/// the same failure: user types a kind-prefixed, namespaced symbol id
/// with the wrong namespace (<c>type:Nebulae.BeatGrid.Audio.DSP.VoicePatchAdapter</c>
/// when the actual symbol lives in <c>Audio.Tuning</c>), the resolver's
/// rules 1-3 all miss because of the kind prefix and namespace dots, and
/// the old suggestion ranker surfaced three unrelated long-named symbols
/// (MixerScreenAdapter properties) that beat the real target on
/// Levenshtein closeness because
/// <c>closeness = candidateLength - distance</c> grows with candidate
/// length.
///
/// The fix: extract the trailing short-name segment from qualified inputs
/// and look it up in the same short-name index rule 3 uses. Single hit →
/// resolve via <see cref="ResolveOutcome.ShortNameFromQualifiedInput"/>;
/// multiple hits → surface every candidate via
/// <see cref="ResolveOutcome.AmbiguousShortNameFromQualifiedInput"/>;
/// zero hits → fall through to a honest not-found diagnostic with a
/// ranker that ALSO routes through the extractor so the fuzzy fallback
/// is scoring short names against short names.
///
/// See INV-RESOLVER-005.
/// </summary>
public class ResolverShortNameFallbackTests
{
    private static readonly Evidence Ev = new()
    {
        Kind = EvidenceKind.Semantic,
        AdapterName = "test",
        Confidence = ConfidenceLevel.Proven,
    };

    private static readonly LifebloodSymbolResolver Resolver = new();

    // ─────────────────────────────────────────────────────────────────────
    // ExtractLikelyShortName — pure function. Unit tests cover every
    // input shape the resolver can see in production.
    // ─────────────────────────────────────────────────────────────────────

    [Theory]
    // Kind-prefixed with namespace (the dogfood case).
    [InlineData("type:Nebulae.BeatGrid.Audio.DSP.VoicePatchAdapter", "VoicePatchAdapter")]
    [InlineData("type:Nebulae.BeatGrid.Audio.Tuning.DspPolicy", "DspPolicy")]
    [InlineData("type:App.Foo", "Foo")]
    // Kind-prefixed single-segment.
    [InlineData("type:Foo", "Foo")]
    // Method ids with and without parens.
    [InlineData("method:App.Svc.Do(int)", "Do")]
    [InlineData("method:App.Svc.Do(int,string)", "Do")]
    [InlineData("method:App.Svc.Do", "Do")]
    // Field / property / namespace shapes.
    [InlineData("field:App.Foo._count", "_count")]
    [InlineData("property:App.Foo.Name", "Name")]
    [InlineData("ns:App.Services", "Services")]
    // Qualified without kind prefix.
    [InlineData("App.Svc.Do", "Do")]
    [InlineData("App.Foo", "Foo")]
    // Bare short name (extraction is a no-op).
    [InlineData("MidiLearnManager", "MidiLearnManager")]
    [InlineData("Foo", "Foo")]
    // Pathological / edge.
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void ExtractLikelyShortName_HandlesEveryShape(string input, string expected)
    {
        var actual = LifebloodSymbolResolver.ExtractLikelyShortName(input);
        Assert.Equal(expected, actual);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Rule 4 — qualified-input short-name fallback. The two user-reported
    // cases reproduced exactly in a synthetic graph so the regression is
    // pinned without a DAWG dependency.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_VoicePatchAdapter_WrongNamespace_ResolvesViaShortNameFallback()
    {
        // Dogfood case 1: user typed type:Nebulae.BeatGrid.Audio.DSP.VoicePatchAdapter
        // when the real symbol is in Audio.Tuning. Short name is unique.
        var graph = BuildGraphWithNoiseAndSingle(
            shortName: "VoicePatchAdapter",
            realId: "type:Nebulae.BeatGrid.Audio.Tuning.VoicePatchAdapter",
            realFilePath: "Audio/Tuning/VoicePatchAdapter.cs");

        var result = Resolver.Resolve(
            graph, "type:Nebulae.BeatGrid.Audio.DSP.VoicePatchAdapter");

        Assert.Equal(ResolveOutcome.ShortNameFromQualifiedInput, result.Outcome);
        Assert.Equal("type:Nebulae.BeatGrid.Audio.Tuning.VoicePatchAdapter", result.CanonicalId);
        Assert.NotNull(result.Symbol);
        Assert.Contains("short-name fallback", result.Diagnostic ?? "");
        Assert.Contains("VoicePatchAdapter", result.Diagnostic ?? "");
    }

    [Fact]
    public void Resolve_DspPolicy_WrongNamespace_ResolvesViaShortNameFallback()
    {
        // Dogfood case 2: user typed type:Nebulae.BeatGrid.Audio.Tuning.DspPolicy
        // when the real symbol is in Infrastructure.Audio.Synthesis.Recipes.
        var graph = BuildGraphWithNoiseAndSingle(
            shortName: "DspPolicy",
            realId: "type:Nebulae.BeatGrid.Infrastructure.Audio.Synthesis.Recipes.DspPolicy",
            realFilePath: "Audio/Generation/Recipes/DspPolicy.cs");

        var result = Resolver.Resolve(
            graph, "type:Nebulae.BeatGrid.Audio.Tuning.DspPolicy");

        Assert.Equal(ResolveOutcome.ShortNameFromQualifiedInput, result.Outcome);
        Assert.Equal(
            "type:Nebulae.BeatGrid.Infrastructure.Audio.Synthesis.Recipes.DspPolicy",
            result.CanonicalId);
        Assert.NotNull(result.Symbol);
    }

    [Fact]
    public void Resolve_QualifiedInput_MultipleShortNameMatches_SurfacesAllCandidates()
    {
        // Ambiguous case: the extracted short name hits two symbols in
        // different namespaces. The resolver must NOT silently pick one —
        // it should surface every candidate via
        // AmbiguousShortNameFromQualifiedInput so the caller can choose.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol
            {
                Id = "type:A.B.Widget",
                Name = "Widget",
                Kind = SymbolKind.Type,
                FilePath = "A/B/Widget.cs",
            })
            .AddSymbol(new Symbol
            {
                Id = "type:C.D.Widget",
                Name = "Widget",
                Kind = SymbolKind.Type,
                FilePath = "C/D/Widget.cs",
            })
            .Build();

        var result = Resolver.Resolve(graph, "type:Wrong.Namespace.Widget");

        Assert.Equal(ResolveOutcome.AmbiguousShortNameFromQualifiedInput, result.Outcome);
        Assert.Null(result.Symbol);
        Assert.Contains("type:A.B.Widget", result.Candidates);
        Assert.Contains("type:C.D.Widget", result.Candidates);
    }

    [Fact]
    public void Resolve_QualifiedInput_NoShortNameMatches_FallsThroughToNotFound()
    {
        // Extraction yields a short name that nothing in the graph has.
        // We must fall through to NotFound with a honest diagnostic and
        // the extraction attempt surfaced in the "tried" description.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:A.Foo", Name = "Foo", Kind = SymbolKind.Type, FilePath = "Foo.cs" })
            .Build();

        var result = Resolver.Resolve(graph, "type:Bogus.Namespace.Nonexistent");

        Assert.Equal(ResolveOutcome.NotFound, result.Outcome);
        Assert.Contains("extracted short-name fallback", result.Diagnostic ?? "");
        Assert.Contains("Nonexistent", result.Diagnostic ?? "");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Suggestion ranker. Proves the fuzzy fallback ALSO routes through
    // the extractor so the bias-toward-long-candidate-names bug can't
    // resurface.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Suggestions_PrefixedInput_ShortNameIndexHitRanksAboveLongLevenshteinCandidate()
    {
        // Build a graph where one symbol is a literal short-name hit and
        // another is a very long noise symbol whose name has zero semantic
        // connection to the query but happens to be long enough to win
        // the old Levenshtein-closeness ranking. Under the fix, the
        // short-name hit must sort first.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol
            {
                Id = "type:Real.Namespace.VoicePatchAdapter",
                Name = "VoicePatchAdapter",
                Kind = SymbolKind.Type,
                FilePath = "Real/VoicePatchAdapter.cs",
            })
            // Noise: a symbol whose name bears no resemblance to
            // "VoicePatchAdapter" but whose length makes old Levenshtein
            // closeness ~45 anyway.
            .AddSymbol(new Symbol
            {
                Id = "property:Noise.MixerScreenAdapter.ActivePresetDisplayNameLongEnoughToBiasScoring",
                Name = "ActivePresetDisplayNameLongEnoughToBiasScoring",
                Kind = SymbolKind.Property,
                FilePath = "Noise/MixerScreenAdapter.cs",
            })
            .Build();

        var matches = Resolver.SuggestNearMatches(
            graph, "type:Nebulae.BeatGrid.Audio.DSP.VoicePatchAdapter", limit: 5);

        Assert.NotEmpty(matches);
        Assert.Equal("type:Real.Namespace.VoicePatchAdapter", matches[0].CanonicalId);
    }

    [Fact]
    public void Suggestions_BareShortName_StillRanksExactHitFirst()
    {
        // Pre-fix behavior — bare short names still work exactly as they
        // did. The extractor is a no-op on "Widget" and the short-name
        // index hit sorts first.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:A.Widget", Name = "Widget", Kind = SymbolKind.Type, FilePath = "W.cs" })
            .AddSymbol(new Symbol { Id = "type:B.WidgetFactoryBuilder", Name = "WidgetFactoryBuilder", Kind = SymbolKind.Type, FilePath = "WFB.cs" })
            .Build();

        var matches = Resolver.SuggestNearMatches(graph, "Widget", limit: 5);

        Assert.NotEmpty(matches);
        Assert.Equal("type:A.Widget", matches[0].CanonicalId);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Guard: the fallback MUST NOT clobber the existing rules. Valid
    // canonical ids still hit rule 1, bare short names still hit rule 3,
    // etc. These tests run the same resolver against inputs that would
    // have resolved before the fix and confirm nothing changed.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_ExactCanonical_StillTakesFastPath_EvenWithExtractionAvailable()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:A.Foo", Name = "Foo", Kind = SymbolKind.Type, FilePath = "Foo.cs" })
            .Build();

        var result = Resolver.Resolve(graph, "type:A.Foo");

        Assert.Equal(ResolveOutcome.ExactMatch, result.Outcome);
        Assert.Equal("type:A.Foo", result.CanonicalId);
    }

    [Fact]
    public void Resolve_BareShortName_StillTakesRule3_NotRule4()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:A.Foo", Name = "Foo", Kind = SymbolKind.Type, FilePath = "Foo.cs" })
            .Build();

        var result = Resolver.Resolve(graph, "Foo");

        Assert.Equal(ResolveOutcome.ShortNameUnique, result.Outcome);
        Assert.Equal("type:A.Foo", result.CanonicalId);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    private static SemanticGraph BuildGraphWithNoiseAndSingle(
        string shortName, string realId, string realFilePath)
    {
        // Build a graph with: the real symbol keyed by its actual namespace +
        // 3 noise symbols whose names are "long and bias-inducing" but have
        // nothing to do with the short name. The old ranker used to surface
        // the noise; the new ranker must surface the real one.
        var builder = new GraphBuilder()
            .AddSymbol(new Symbol
            {
                Id = realId,
                Name = shortName,
                Kind = SymbolKind.Type,
                FilePath = realFilePath,
            })
            .AddSymbol(new Symbol
            {
                Id = "property:Noise.Alpha.VeryLongPropertyNameThatConfusesLevenshteinClosenessRanking",
                Name = "VeryLongPropertyNameThatConfusesLevenshteinClosenessRanking",
                Kind = SymbolKind.Property,
                FilePath = "Noise/Alpha.cs",
            })
            .AddSymbol(new Symbol
            {
                Id = "property:Noise.Beta.AnotherExtremelyLongPropertyNameForPaddedDistanceBonusAbuse",
                Name = "AnotherExtremelyLongPropertyNameForPaddedDistanceBonusAbuse",
                Kind = SymbolKind.Property,
                FilePath = "Noise/Beta.cs",
            })
            .AddSymbol(new Symbol
            {
                Id = "property:Noise.Gamma.YetAnotherPaddedNameWhoseLengthSaturatesClosenessMetric",
                Name = "YetAnotherPaddedNameWhoseLengthSaturatesClosenessMetric",
                Kind = SymbolKind.Property,
                FilePath = "Noise/Gamma.cs",
            });
        return builder.Build();
    }
}

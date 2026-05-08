using Lifeblood.Application.Ports.Right;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-RESOLVER-007. Pins the type-aware tightening of the Rule 4 short-name
/// fallback added in <see cref="LifebloodSymbolResolver"/>. The dogfood case:
/// a Unity workspace contained both <c>FieldMask.ShimmerPhase</c> (an enum
/// member) and <c>BurstVoiceState.ShimmerPhase</c> (a struct field). Asking
/// for <c>field:NS.FieldMask.ShimmerPhase</c> previously fell through to a
/// global short-name lookup that silently returned the struct field — wrong
/// containing type, wrong concept, no diagnostic surface.
///
/// The fix preserves the documented intent of Rule 4 (namespace was wrong but
/// the symbol is uniquely identified) while killing the cross-type and
/// cross-kind silent substitution class:
/// <list type="bullet">
///   <item>Same containing-type SHORT name + different namespace → still
///         resolves cleanly via <see cref="ResolveOutcome.ShortNameFromQualifiedInput"/>.</item>
///   <item>Different containing type → <see cref="ResolveOutcome.NotFound"/>
///         with the unfiltered short-name hits as <c>Candidates</c>.</item>
///   <item>Different symbol kind on the same name → <see cref="ResolveOutcome.NotFound"/>.</item>
///   <item><c>type:</c> / <c>ns:</c> inputs are unaffected — the gate fires
///         only for member-kind prefixes (<c>field:</c>, <c>property:</c>,
///         <c>method:</c>).</item>
/// </list>
/// </summary>
public class ResolverTypeAwareFallbackTests
{
    private static readonly LifebloodSymbolResolver Resolver = new();

    private static readonly Evidence Ev = new()
    {
        Kind = EvidenceKind.Semantic,
        AdapterName = "test",
        Confidence = ConfidenceLevel.Proven,
    };

    // ─────────────────────────────────────────────────────────────────────
    // Helper builders
    // ─────────────────────────────────────────────────────────────────────

    private static Symbol Type(string id, string name) => new()
    {
        Id = id,
        Name = name,
        QualifiedName = id.StartsWith("type:") ? id["type:".Length..] : id,
        Kind = SymbolKind.Type,
        FilePath = "test.cs",
    };

    private static Symbol Field(string id, string name, string parentId) => new()
    {
        Id = id,
        Name = name,
        QualifiedName = id.StartsWith("field:") ? id["field:".Length..] : id,
        Kind = SymbolKind.Field,
        ParentId = parentId,
        FilePath = "test.cs",
    };

    private static Symbol Property(string id, string name, string parentId) => new()
    {
        Id = id,
        Name = name,
        QualifiedName = id.StartsWith("property:") ? id["property:".Length..] : id,
        Kind = SymbolKind.Property,
        ParentId = parentId,
        FilePath = "test.cs",
    };

    private static Symbol Method(string id, string name, string parentId) => new()
    {
        Id = id,
        Name = name,
        QualifiedName = id.StartsWith("method:") ? id["method:".Length..] : id,
        Kind = SymbolKind.Method,
        ParentId = parentId,
        FilePath = "test.cs",
    };

    private static SemanticGraph Build(params Symbol[] symbols)
    {
        var b = new GraphBuilder();
        foreach (var s in symbols) b.AddSymbol(s);
        return b.Build();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Pure helpers — TryParseMemberInput / ShortNameOf / StripTypePrefix /
    // SymbolKindForPrefix. Unit-test these so the gate's parsing semantics
    // are pinned independent of resolver wiring.
    // ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("field:A.B.X",                "field",    "A.B",    "X")]
    [InlineData("property:NS.T.P",            "property", "NS.T",   "P")]
    [InlineData("method:A.B.Run(int,string)", "method",   "A.B",    "Run")]
    [InlineData("method:A.B.Run",             "method",   "A.B",    "Run")]
    [InlineData("field:Outer.Inner.Member",   "field",    "Outer.Inner", "Member")]
    public void TryParseMemberInput_ParsesValidMemberIds(
        string input, string expectPrefix, string expectType, string expectName)
    {
        Assert.True(LifebloodSymbolResolver.TryParseMemberInput(
            input, out var p, out var t, out var n));
        Assert.Equal(expectPrefix, p);
        Assert.Equal(expectType, t);
        Assert.Equal(expectName, n);
    }

    [Theory]
    [InlineData("type:A.Foo")]                  // type prefix not a member
    [InlineData("ns:A.B")]                      // namespace not a member
    [InlineData("file:src/foo.cs")]             // file not a member
    [InlineData("mod:Asm")]                     // module not a member
    [InlineData("Foo")]                         // no colon
    [InlineData("field:")]                      // empty tail
    [InlineData("field:Foo")]                   // no dot — type-only
    [InlineData("field:Foo.")]                  // trailing dot
    [InlineData("")]                            // empty
    public void TryParseMemberInput_RejectsNonMemberOrMalformedIds(string input)
    {
        Assert.False(LifebloodSymbolResolver.TryParseMemberInput(input, out _, out _, out _));
    }

    [Theory]
    [InlineData("A.B.C", "C")]
    [InlineData("Foo", "Foo")]
    [InlineData("",     "")]
    [InlineData("Outer.Inner", "Inner")]
    public void ShortNameOf_ReturnsFinalSegment(string fqn, string expected)
        => Assert.Equal(expected, LifebloodSymbolResolver.ShortNameOf(fqn));

    [Theory]
    [InlineData("type:A.B.C", "A.B.C")]
    [InlineData("A.B.C",      "A.B.C")] // no prefix, no-op
    [InlineData("type:Foo",   "Foo")]
    public void StripTypePrefix_RemovesTypeColon(string parentId, string expected)
        => Assert.Equal(expected, LifebloodSymbolResolver.StripTypePrefix(parentId));

    // ─────────────────────────────────────────────────────────────────────
    // Cross-type substitution — the R2-3 dogfood case. Must NOT silently
    // resolve to a different containing type's same-named member.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_FieldOnMissingContainingType_RefusesCrossTypeSubstitution()
    {
        // The user asks for FieldMask.ShimmerPhase. Only BurstVoiceState
        // has a ShimmerPhase field in the graph. Pre-fix the resolver
        // silently substituted the unrelated field. INV-RESOLVER-007: refuse.
        var graph = Build(
            Type("type:NS.BurstVoiceState", "BurstVoiceState"),
            Field("field:NS.BurstVoiceState.ShimmerPhase", "ShimmerPhase",
                  parentId: "type:NS.BurstVoiceState"));

        var result = Resolver.Resolve(graph, "field:NS.FieldMask.ShimmerPhase");

        Assert.Equal(ResolveOutcome.NotFound, result.Outcome);
        Assert.Contains("FieldMask", result.Diagnostic);
        Assert.Contains("INV-RESOLVER-007", result.Diagnostic);
        Assert.Contains("field:NS.BurstVoiceState.ShimmerPhase", result.Candidates);
    }

    [Fact]
    public void Resolve_FieldOnSameTypeShortName_DifferentNamespace_StillResolves()
    {
        // Documented intent of Rule 4: "namespace was wrong but symbol uniquely
        // identified." Same containing-type short name + different namespace
        // is the canonical accept case.
        var graph = Build(
            Type("type:Real.NS.MyType", "MyType"),
            Field("field:Real.NS.MyType.Counter", "Counter",
                  parentId: "type:Real.NS.MyType"));

        var result = Resolver.Resolve(graph, "field:Stale.NS.MyType.Counter");

        Assert.Equal(ResolveOutcome.ShortNameFromQualifiedInput, result.Outcome);
        Assert.Equal("field:Real.NS.MyType.Counter", result.CanonicalId);
    }

    [Fact]
    public void Resolve_PropertyAskedAsField_RefusesCrossKindSubstitution()
    {
        // User asked for field:A.B.X. Only property:A.B.X exists. Same type,
        // different kind. INV-RESOLVER-007 refuses cross-kind substitution.
        var graph = Build(
            Type("type:A.B", "B"),
            Property("property:A.B.X", "X", parentId: "type:A.B"));

        var result = Resolver.Resolve(graph, "field:A.B.X");

        Assert.Equal(ResolveOutcome.NotFound, result.Outcome);
        Assert.Contains("kind", (result.Diagnostic ?? "").ToLowerInvariant());
        Assert.Contains("property:A.B.X", result.Candidates);
    }

    [Fact]
    public void Resolve_MethodOnDifferentContainingType_RefusesCrossTypeSubstitution()
    {
        // Methods carry param sigs but the cross-type rule still applies.
        var graph = Build(
            Type("type:A.Bar", "Bar"),
            Method("method:A.Bar.Run(int)", "Run", parentId: "type:A.Bar"));

        var result = Resolver.Resolve(graph, "method:A.Foo.Run(int)");

        Assert.Equal(ResolveOutcome.NotFound, result.Outcome);
        Assert.Contains("Foo", result.Diagnostic);
        Assert.Contains("method:A.Bar.Run(int)", result.Candidates);
    }

    [Fact]
    public void Resolve_FieldExactIdInGraph_HitsRule1FastPath()
    {
        // Sanity guard: the type-aware gate runs in Rule 4. Exact-ID hits
        // (Rule 1) must short-circuit before Rule 4 ever fires. With C1's
        // enum-member extraction landed, this is the production path for
        // every well-formed enum-member lookup.
        var graph = Build(
            Type("type:NS.FieldMask", "FieldMask"),
            Field("field:NS.FieldMask.ShimmerPhase", "ShimmerPhase",
                  parentId: "type:NS.FieldMask"),
            Type("type:NS.BurstVoiceState", "BurstVoiceState"),
            Field("field:NS.BurstVoiceState.ShimmerPhase", "ShimmerPhase",
                  parentId: "type:NS.BurstVoiceState"));

        var result = Resolver.Resolve(graph, "field:NS.FieldMask.ShimmerPhase");

        Assert.Equal(ResolveOutcome.ExactMatch, result.Outcome);
        Assert.Equal("field:NS.FieldMask.ShimmerPhase", result.CanonicalId);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Type-input behavior unchanged — gate fires only for member kinds.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_TypePrefix_NamespaceSubstitutionStillWorks()
    {
        // type:Stale.NS.Adapter → type:Real.NS.Adapter is the original
        // INV-RESOLVER-005 dogfood case. The new gate must NOT regress it.
        var graph = Build(
            Type("type:Real.NS.VoicePatchAdapter", "VoicePatchAdapter"));

        var result = Resolver.Resolve(graph, "type:Stale.NS.VoicePatchAdapter");

        Assert.Equal(ResolveOutcome.ShortNameFromQualifiedInput, result.Outcome);
        Assert.Equal("type:Real.NS.VoicePatchAdapter", result.CanonicalId);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Cross-type AND ambiguous: report all unfiltered candidates so the
    // caller can pick. Avoids "unique-but-wrong" silent answers.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_FieldOnMissingType_MultipleCrossTypeCandidates_ReturnsNotFoundWithAllCandidates()
    {
        var graph = Build(
            Type("type:NS.A", "A"),
            Field("field:NS.A.X", "X", parentId: "type:NS.A"),
            Type("type:NS.B", "B"),
            Field("field:NS.B.X", "X", parentId: "type:NS.B"));

        var result = Resolver.Resolve(graph, "field:NS.MissingType.X");

        Assert.Equal(ResolveOutcome.NotFound, result.Outcome);
        Assert.Equal(2, result.Candidates.Length);
        Assert.Contains("field:NS.A.X", result.Candidates);
        Assert.Contains("field:NS.B.X", result.Candidates);
    }
}

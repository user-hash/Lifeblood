using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Phase 3 regression tests for the resolver capability layer (2026-04-11):
/// primitive-alias canonicalization at step 0, <see cref="ResolutionMode"/>
/// semantics, fuzzy scoring, zero-result suggestion fallback, and overload
/// surfacing on <see cref="SymbolResolutionResult"/>.
///
/// Each test builds a minimal in-memory graph via <see cref="GraphBuilder"/>
/// and exercises the resolver through its public port contract. These tests
/// pin INV-RESOLVER-001 ("every read-side tool routes through ISymbolResolver")
/// plus the Phase 3 additions from
/// <c>.claude/plans/improvement-master-2026-04-11.md</c> Part 4 Group B.
/// </summary>
public class ResolverPhase3Tests
{
    private static readonly Evidence Evidence = new()
    {
        Kind = EvidenceKind.Semantic,
        AdapterName = "test",
        Confidence = ConfidenceLevel.Proven,
    };

    private static ISymbolResolver CSharpResolver() => new LifebloodSymbolResolver(new CSharpUserInputCanonicalizer());

    // ─────────────────────────────────────────────────────────────────────
    // Step 0: input canonicalization
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_SystemStringAlias_ResolvesToCanonicalStringForm()
    {
        // User writes `System.String` in the parameter signature; the graph
        // stores the canonical `string` form. Step 0 canonicalization
        // rewrites the input before any lookup runs, so the exact-match path
        // succeeds on the first try with no alias-retry fallback.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Foo", Name = "Foo", Kind = SymbolKind.Type, FilePath = "Foo.cs" })
            .AddSymbol(new Symbol
            {
                Id = "method:N.Foo.Bar(string)",
                Name = "Bar",
                Kind = SymbolKind.Method,
                FilePath = "Foo.cs",
                ParentId = "type:N.Foo",
            })
            .Build();

        var resolver = CSharpResolver();
        var result = resolver.Resolve(graph, "method:N.Foo.Bar(System.String)");

        Assert.Equal(ResolveOutcome.ExactMatch, result.Outcome);
        Assert.Equal("method:N.Foo.Bar(string)", result.CanonicalId);
    }

    [Fact]
    public void Resolve_SystemInt32Alias_ResolvesToInt()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Foo", Name = "Foo", Kind = SymbolKind.Type, FilePath = "Foo.cs" })
            .AddSymbol(new Symbol
            {
                Id = "method:N.Foo.Bar(int,int)",
                Name = "Bar",
                Kind = SymbolKind.Method,
                FilePath = "Foo.cs",
                ParentId = "type:N.Foo",
            })
            .Build();

        var result = CSharpResolver().Resolve(graph, "method:N.Foo.Bar(System.Int32,System.Int32)");

        Assert.Equal(ResolveOutcome.ExactMatch, result.Outcome);
    }

    [Fact]
    public void Resolve_GlobalPrefix_IsStripped()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Foo", Name = "Foo", Kind = SymbolKind.Type, FilePath = "Foo.cs" })
            .Build();

        var result = CSharpResolver().Resolve(graph, "type:global::N.Foo");

        Assert.Equal(ResolveOutcome.ExactMatch, result.Outcome);
    }

    [Fact]
    public void Resolve_DiagnosticsReferenceCanonicalForm_NotRawInput()
    {
        // Not-found diagnostics must quote the canonical form, not the user's
        // original input. Ground Rule 7: "every diagnostic surfaces the
        // canonical grammar, never the user's raw form."
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Other", Name = "Other", Kind = SymbolKind.Type, FilePath = "Other.cs" })
            .Build();

        var result = CSharpResolver().Resolve(graph, "method:N.Missing.Foo(System.String)");

        Assert.Equal(ResolveOutcome.NotFound, result.Outcome);
        Assert.NotNull(result.Diagnostic);
        Assert.Contains("method:N.Missing.Foo(string)", result.Diagnostic!);
        Assert.DoesNotContain("System.String", result.Diagnostic!);
    }

    // ─────────────────────────────────────────────────────────────────────
    // PrimitiveAliasTable token-awareness
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void PrimitiveAliasTable_DoesNotCorruptIdentifiersStartingWithBclName()
    {
        // The token-aware rewriter MUST NOT rewrite `System.StringBuilder`
        // or `MyApp.System.Stringent` — only complete identifier-token
        // occurrences of `System.String` bounded by delimiters. Otherwise
        // a user type named "Stringent" in a "System" namespace would be
        // silently renamed.
        Assert.Equal("type:MyApp.System.StringBuilder",
            PrimitiveAliasTable.Rewrite("type:MyApp.System.StringBuilder"));
        Assert.Equal("type:N.SystemManager",
            PrimitiveAliasTable.Rewrite("type:N.SystemManager"));
    }

    [Fact]
    public void PrimitiveAliasTable_RewritesTokenBoundedBcl()
    {
        Assert.Equal("method:N.Foo.Bar(string,int)",
            PrimitiveAliasTable.Rewrite("method:N.Foo.Bar(System.String,System.Int32)"));
    }

    [Fact]
    public void PrimitiveAliasTable_Idempotent()
    {
        var once = PrimitiveAliasTable.Rewrite("method:N.Foo.Bar(System.String)");
        var twice = PrimitiveAliasTable.Rewrite(once);
        Assert.Equal(once, twice);
    }

    // ─────────────────────────────────────────────────────────────────────
    // ResolutionMode behavior
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveShortName_ContainsMode_ReturnsSubstringMatches()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.CustomerRepository", Name = "CustomerRepository", Kind = SymbolKind.Type, FilePath = "CR.cs" })
            .AddSymbol(new Symbol { Id = "type:N.CustomerService", Name = "CustomerService", Kind = SymbolKind.Type, FilePath = "CS.cs" })
            .AddSymbol(new Symbol { Id = "type:N.Unrelated", Name = "Unrelated", Kind = SymbolKind.Type, FilePath = "U.cs" })
            .Build();

        var matches = CSharpResolver().ResolveShortName(graph, "Customer", ResolutionMode.Contains);

        Assert.Equal(2, matches.Length);
        Assert.All(matches, m => Assert.Contains("Customer", m.CanonicalId));
    }

    [Fact]
    public void ResolveShortName_FuzzyMode_RanksScoredResults()
    {
        // CamelCase token-prefix scoring should rank "CustomerRepo" ahead
        // of "OrderProcessor" for query "Cust".
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.CustomerRepo", Name = "CustomerRepo", Kind = SymbolKind.Type, FilePath = "C.cs" })
            .AddSymbol(new Symbol { Id = "type:N.OrderProcessor", Name = "OrderProcessor", Kind = SymbolKind.Type, FilePath = "O.cs" })
            .Build();

        var matches = CSharpResolver().ResolveShortName(graph, "Cust", ResolutionMode.Fuzzy);

        Assert.True(matches.Length > 0);
        Assert.Equal("type:N.CustomerRepo", matches[0].CanonicalId);
    }

    [Fact]
    public void ResolveShortName_ZeroResultExact_SurfacesSuggestions()
    {
        // Typo: user types "Customre" instead of "Customer". Exact mode finds
        // nothing but the zero-result fallback runs the scorer and returns
        // ranked suggestions so the caller is never stuck.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Customer", Name = "Customer", Kind = SymbolKind.Type, FilePath = "C.cs" })
            .Build();

        var matches = CSharpResolver().ResolveShortName(graph, "Customre");

        Assert.NotEmpty(matches);
        Assert.Contains(matches, m => m.CanonicalId == "type:N.Customer");
    }

    // ─────────────────────────────────────────────────────────────────────
    // SuggestNearMatches directly
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void SuggestNearMatches_RespectsLimit()
    {
        var builder = new GraphBuilder();
        for (int i = 0; i < 20; i++)
        {
            builder.AddSymbol(new Symbol
            {
                Id = $"type:N.Foo{i}",
                Name = $"Foo{i}",
                Kind = SymbolKind.Type,
                FilePath = $"Foo{i}.cs",
            });
        }
        var graph = builder.Build();

        var matches = CSharpResolver().SuggestNearMatches(graph, "Foo", limit: 3);

        Assert.Equal(3, matches.Length);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Overload surfacing on SymbolResolutionResult (LB-INBOX-004)
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_ResolvedMethod_SurfacesSiblingOverloadsOnResult()
    {
        // The user queries by a specific overload; the result should carry
        // every sibling overload on the same type so a picker UI can show
        // the full surface in one round-trip.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Svc", Name = "Svc", Kind = SymbolKind.Type, FilePath = "Svc.cs" })
            .AddSymbol(new Symbol
            {
                Id = "method:N.Svc.Do(int)", Name = "Do", Kind = SymbolKind.Method,
                FilePath = "Svc.cs", ParentId = "type:N.Svc", Line = 10,
            })
            .AddSymbol(new Symbol
            {
                Id = "method:N.Svc.Do(string)", Name = "Do", Kind = SymbolKind.Method,
                FilePath = "Svc.cs", ParentId = "type:N.Svc", Line = 14,
            })
            .AddSymbol(new Symbol
            {
                Id = "method:N.Svc.Do(int,int)", Name = "Do", Kind = SymbolKind.Method,
                FilePath = "Svc.cs", ParentId = "type:N.Svc", Line = 18,
            })
            .AddEdge(new Edge { SourceId = "type:N.Svc", TargetId = "method:N.Svc.Do(int)", Kind = EdgeKind.Contains, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "type:N.Svc", TargetId = "method:N.Svc.Do(string)", Kind = EdgeKind.Contains, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "type:N.Svc", TargetId = "method:N.Svc.Do(int,int)", Kind = EdgeKind.Contains, Evidence = Evidence })
            .Build();

        var result = CSharpResolver().Resolve(graph, "method:N.Svc.Do(int)");

        Assert.Equal(ResolveOutcome.ExactMatch, result.Outcome);
        Assert.Equal(3, result.Overloads.Length);
        Assert.Contains(result.Overloads, o => o.CanonicalId == "method:N.Svc.Do(int)");
        Assert.Contains(result.Overloads, o => o.CanonicalId == "method:N.Svc.Do(string)");
        Assert.Contains(result.Overloads, o => o.CanonicalId == "method:N.Svc.Do(int,int)");

        // Each overload carries its own param display for human picker UIs.
        var intOverload = result.Overloads.First(o => o.CanonicalId == "method:N.Svc.Do(int)");
        Assert.Equal("int", intOverload.ParamDisplay);
    }

    [Fact]
    public void Resolve_SingleOverloadMethod_LeavesOverloadsEmpty()
    {
        // A method with no siblings doesn't populate Overloads — the caller
        // already has everything it needs and we avoid an N=1 picker.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Svc", Name = "Svc", Kind = SymbolKind.Type, FilePath = "Svc.cs" })
            .AddSymbol(new Symbol
            {
                Id = "method:N.Svc.Only(int)", Name = "Only", Kind = SymbolKind.Method,
                FilePath = "Svc.cs", ParentId = "type:N.Svc",
            })
            .AddEdge(new Edge { SourceId = "type:N.Svc", TargetId = "method:N.Svc.Only(int)", Kind = EdgeKind.Contains, Evidence = Evidence })
            .Build();

        var result = CSharpResolver().Resolve(graph, "method:N.Svc.Only(int)");

        Assert.Equal(ResolveOutcome.ExactMatch, result.Outcome);
        Assert.Empty(result.Overloads);
    }

    [Fact]
    public void Resolve_TypeSymbol_LeavesOverloadsEmpty()
    {
        // Non-method resolution — Overloads stays empty regardless of what
        // members the containing type has.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Svc", Name = "Svc", Kind = SymbolKind.Type, FilePath = "Svc.cs" })
            .Build();

        var result = CSharpResolver().Resolve(graph, "type:N.Svc");

        Assert.Equal(ResolveOutcome.ExactMatch, result.Outcome);
        Assert.Empty(result.Overloads);
    }
}

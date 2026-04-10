using Lifeblood.Application.Ports.Right;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Unit tests for <see cref="LifebloodSymbolResolver"/>. Each test builds a
/// minimal in-memory <see cref="SemanticGraph"/> via the public
/// <see cref="GraphBuilder"/> API and asserts the resolver returns the
/// expected outcome.
///
/// Coverage matrix (Plan v4 §5 Seam #1 step 4):
///   - Exact canonical match (fast path)
///   - Truncated method, single overload (lenient match)
///   - Truncated method, multiple overloads (ambiguous)
///   - Bare short name, unique
///   - Bare short name, ambiguous across namespaces
///   - Not found with helpful diagnostic
///   - Partial type primary picker — filename match
///   - Partial type primary picker — prefix match fallback
/// </summary>
public class SymbolResolverTests
{
    private static readonly LifebloodSymbolResolver Resolver = new();

    private static readonly Evidence Evidence = new()
    {
        Kind = EvidenceKind.Semantic,
        AdapterName = "test",
        Confidence = ConfidenceLevel.Proven,
    };

    [Fact]
    public void Resolve_ExactCanonical_FastPath()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol
            {
                Id = "type:N.Foo",
                Name = "Foo",
                QualifiedName = "N.Foo",
                Kind = SymbolKind.Type,
                FilePath = "Foo.cs",
            })
            .Build();

        var result = Resolver.Resolve(graph, "type:N.Foo");

        Assert.Equal(ResolveOutcome.ExactMatch, result.Outcome);
        Assert.Equal("type:N.Foo", result.CanonicalId);
        Assert.NotNull(result.Symbol);
        Assert.Equal("Foo", result.Symbol!.Name);
    }

    [Fact]
    public void Resolve_TruncatedMethod_SingleOverload_Lenient()
    {
        // Type N.Foo with one method Bar(int). Truncated input "method:N.Foo.Bar"
        // should resolve to the canonical id with parens.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol
            {
                Id = "type:N.Foo",
                Name = "Foo",
                QualifiedName = "N.Foo",
                Kind = SymbolKind.Type,
                FilePath = "Foo.cs",
            })
            .AddSymbol(new Symbol
            {
                Id = "method:N.Foo.Bar(int)",
                Name = "Bar",
                QualifiedName = "N.Foo.Bar",
                Kind = SymbolKind.Method,
                FilePath = "Foo.cs",
                ParentId = "type:N.Foo",
            })
            // Contains edge so the resolver can walk type → methods.
            .AddEdge(new Edge
            {
                SourceId = "type:N.Foo",
                TargetId = "method:N.Foo.Bar(int)",
                Kind = EdgeKind.Contains,
                Evidence = Evidence,
            })
            .Build();

        var result = Resolver.Resolve(graph, "method:N.Foo.Bar");

        Assert.Equal(ResolveOutcome.LenientMethodOverload, result.Outcome);
        Assert.Equal("method:N.Foo.Bar(int)", result.CanonicalId);
    }

    [Fact]
    public void Resolve_TruncatedMethod_AmbiguousOverloads()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol
            {
                Id = "type:N.Foo",
                Name = "Foo",
                QualifiedName = "N.Foo",
                Kind = SymbolKind.Type,
                FilePath = "Foo.cs",
            })
            .AddSymbol(new Symbol
            {
                Id = "method:N.Foo.Bar(int)",
                Name = "Bar",
                QualifiedName = "N.Foo.Bar",
                Kind = SymbolKind.Method,
                FilePath = "Foo.cs",
                ParentId = "type:N.Foo",
            })
            .AddSymbol(new Symbol
            {
                Id = "method:N.Foo.Bar(string)",
                Name = "Bar",
                QualifiedName = "N.Foo.Bar",
                Kind = SymbolKind.Method,
                FilePath = "Foo.cs",
                ParentId = "type:N.Foo",
            })
            .AddEdge(new Edge
            {
                SourceId = "type:N.Foo",
                TargetId = "method:N.Foo.Bar(int)",
                Kind = EdgeKind.Contains,
                Evidence = Evidence,
            })
            .AddEdge(new Edge
            {
                SourceId = "type:N.Foo",
                TargetId = "method:N.Foo.Bar(string)",
                Kind = EdgeKind.Contains,
                Evidence = Evidence,
            })
            .Build();

        var result = Resolver.Resolve(graph, "method:N.Foo.Bar");

        Assert.Equal(ResolveOutcome.AmbiguousMethodOverload, result.Outcome);
        Assert.Null(result.CanonicalId);
        Assert.Equal(2, result.Candidates.Length);
        Assert.Contains("method:N.Foo.Bar(int)", result.Candidates);
        Assert.Contains("method:N.Foo.Bar(string)", result.Candidates);
        Assert.NotNull(result.Diagnostic);
    }

    [Fact]
    public void Resolve_BareShortName_Unique()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol
            {
                Id = "type:N.MidiLearnManager",
                Name = "MidiLearnManager",
                QualifiedName = "N.MidiLearnManager",
                Kind = SymbolKind.Type,
                FilePath = "Adapters/Midi/MidiLearnManager.cs",
            })
            .Build();

        var result = Resolver.Resolve(graph, "MidiLearnManager");

        Assert.Equal(ResolveOutcome.ShortNameUnique, result.Outcome);
        Assert.Equal("type:N.MidiLearnManager", result.CanonicalId);
    }

    [Fact]
    public void Resolve_BareShortName_Ambiguous()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol
            {
                Id = "type:NsA.Helper",
                Name = "Helper",
                QualifiedName = "NsA.Helper",
                Kind = SymbolKind.Type,
                FilePath = "A/Helper.cs",
            })
            .AddSymbol(new Symbol
            {
                Id = "type:NsB.Helper",
                Name = "Helper",
                QualifiedName = "NsB.Helper",
                Kind = SymbolKind.Type,
                FilePath = "B/Helper.cs",
            })
            .Build();

        var result = Resolver.Resolve(graph, "Helper");

        Assert.Equal(ResolveOutcome.AmbiguousShortName, result.Outcome);
        Assert.Null(result.CanonicalId);
        Assert.Equal(2, result.Candidates.Length);
        Assert.NotNull(result.Diagnostic);
    }

    [Fact]
    public void Resolve_NotFound_HelpfulDiagnostic()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol
            {
                Id = "type:N.Foo",
                Name = "Foo",
                QualifiedName = "N.Foo",
                Kind = SymbolKind.Type,
                FilePath = "Foo.cs",
            })
            .Build();

        var result = Resolver.Resolve(graph, "type:N.DoesNotExist");

        Assert.Equal(ResolveOutcome.NotFound, result.Outcome);
        Assert.Null(result.CanonicalId);
        Assert.NotNull(result.Diagnostic);
        Assert.Contains("type:N.DoesNotExist", result.Diagnostic);
    }

    [Fact]
    public void Resolve_PartialType_FindsAllPartialFilesViaContainsEdges()
    {
        // The full multi-partial regression: 5 declaration files for the same
        // type. GraphBuilder must synthesize one Contains edge per file (via
        // the per-id parent-set tracking), and the resolver must walk those
        // edges to recover all 5 file paths in DeclarationFilePaths.
        //
        // This pins down the LB-BUG-004 fix end-to-end: even though
        // GraphBuilder.AddSymbol is last-write-wins on the Symbol record,
        // every observed ParentId is preserved and reachable via Contains
        // edges in the final graph.
        var graph = BuildPartialTypeGraph(
            "AdaptiveBeatGrid",
            new[]
            {
                "AdaptiveBeatGrid.Audio.cs",
                "AdaptiveBeatGrid.Mixer.cs",
                "AdaptiveBeatGrid.cs",
                "AdaptiveBeatGrid.Init.cs",
                "AdaptiveBeatGrid.PadInput.cs",
            });

        var result = Resolver.Resolve(graph, "type:N.AdaptiveBeatGrid");

        Assert.Equal(ResolveOutcome.ExactMatch, result.Outcome);
        // All 5 partial files must appear in DeclarationFilePaths.
        Assert.Equal(5, result.DeclarationFilePaths.Length);
        // Sorted lexicographically (OrdinalIgnoreCase): the bare ".cs" sorts
        // before ".Init.cs" because "C" < "I" in OrdinalIgnoreCase.
        Assert.Equal(new[]
        {
            "AdaptiveBeatGrid.Audio.cs",
            "AdaptiveBeatGrid.cs",
            "AdaptiveBeatGrid.Init.cs",
            "AdaptiveBeatGrid.Mixer.cs",
            "AdaptiveBeatGrid.PadInput.cs",
        }, result.DeclarationFilePaths);
        // Primary picker rule 1 — filename matches type name exactly.
        Assert.Equal("AdaptiveBeatGrid.cs", result.PrimaryFilePath);
    }

    [Fact]
    public void Resolve_PartialType_PrimaryFilePathMatchesTypeName()
    {
        // Three partial declarations of N.Foo across files Foo.cs, Foo.Bar.cs,
        // Foo.Baz.cs. The graph stores last-write-wins for the type symbol's
        // FilePath; the resolver must pick "Foo.cs" as the deterministic
        // primary regardless of which partial GraphBuilder kept.
        var graph = BuildPartialTypeGraph(
            "Foo",
            new[]
            {
                "Foo.Baz.cs",   // some other partial
                "Foo.Bar.cs",   // another partial
                "Foo.cs",       // canonical home (filename matches type name)
            });

        var result = Resolver.Resolve(graph, "type:N.Foo");

        Assert.Equal(ResolveOutcome.ExactMatch, result.Outcome);
        Assert.Equal("Foo.cs", result.PrimaryFilePath);
        Assert.Equal(3, result.DeclarationFilePaths.Length);
        // DeclarationFilePaths is sorted lexicographically.
        Assert.Equal(new[] { "Foo.Bar.cs", "Foo.Baz.cs", "Foo.cs" }, result.DeclarationFilePaths);
    }

    [Fact]
    public void Resolve_TruncatedMethodId_DependantsRegression_LB_BUG_002()
    {
        // The LB-BUG-002 misdiagnosis: a reviewer queried lifeblood_dependants
        // with a truncated method id (no parens). The pure-dict graph lookup
        // returned []. After Plan v4 Seam #1, the resolver canonicalizes the
        // truncated form to the full id, which DOES have incoming Calls edges
        // from the source.
        //
        // This test pins down the resolver path: given a graph with a method
        // and a Calls edge to it, querying with the TRUNCATED id resolves to
        // the canonical id and the resolved Symbol's incoming-edge index
        // contains the caller. This is what the MCP HandleDependants handler
        // now does — resolve first, then graph-lookup against the canonical id.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol
            {
                Id = "type:N.Voice",
                Name = "Voice",
                QualifiedName = "N.Voice",
                Kind = SymbolKind.Type,
                FilePath = "Voice.cs",
            })
            .AddSymbol(new Symbol
            {
                Id = "method:N.Voice.SetPatch(N.VoicePatch)",
                Name = "SetPatch",
                QualifiedName = "N.Voice.SetPatch",
                Kind = SymbolKind.Method,
                FilePath = "Voice.cs",
                ParentId = "type:N.Voice",
            })
            .AddSymbol(new Symbol
            {
                Id = "type:N.Caller",
                Name = "Caller",
                QualifiedName = "N.Caller",
                Kind = SymbolKind.Type,
                FilePath = "Caller.cs",
            })
            .AddSymbol(new Symbol
            {
                Id = "method:N.Caller.Use()",
                Name = "Use",
                QualifiedName = "N.Caller.Use",
                Kind = SymbolKind.Method,
                FilePath = "Caller.cs",
                ParentId = "type:N.Caller",
            })
            .AddEdge(new Edge
            {
                SourceId = "type:N.Voice",
                TargetId = "method:N.Voice.SetPatch(N.VoicePatch)",
                Kind = EdgeKind.Contains,
                Evidence = Evidence,
            })
            .AddEdge(new Edge
            {
                // The cross-method Calls edge that dependants on SetPatch must find.
                SourceId = "method:N.Caller.Use()",
                TargetId = "method:N.Voice.SetPatch(N.VoicePatch)",
                Kind = EdgeKind.Calls,
                Evidence = Evidence,
            })
            .Build();

        // Truncated input — what the reviewer originally tried.
        var resolved = Resolver.Resolve(graph, "method:N.Voice.SetPatch");

        Assert.Equal(ResolveOutcome.LenientMethodOverload, resolved.Outcome);
        Assert.Equal("method:N.Voice.SetPatch(N.VoicePatch)", resolved.CanonicalId);

        // Now the dependants query against the canonical id finds the caller.
        var incomingCallers = graph.GetIncomingEdgeIndexes(resolved.CanonicalId!).ToArray();
        var hasUseCaller = incomingCallers.Any(idx =>
            graph.Edges[idx].Kind == EdgeKind.Calls &&
            graph.Edges[idx].SourceId == "method:N.Caller.Use()");
        Assert.True(hasUseCaller,
            "Resolver canonicalized the truncated id, but dependants still " +
            "missed the Calls edge — graph indexing or routing is broken.");
    }

    [Fact]
    public void Resolve_PartialType_PrimaryFallsBackToShortestPrefixMatch()
    {
        // No bare "Foo.cs" — only Foo.A.cs, Foo.Audio.cs, Foo.B.cs.
        // Primary picker rule 2: shortest filename length wins among prefix
        // matches. "Foo.A.cs" (length 8) beats "Foo.B.cs" (also length 8 →
        // tie-broken lexicographically) and "Foo.Audio.cs" (length 12).
        var graph = BuildPartialTypeGraph(
            "Foo",
            new[]
            {
                "Foo.Audio.cs",
                "Foo.A.cs",
                "Foo.B.cs",
            });

        var result = Resolver.Resolve(graph, "type:N.Foo");

        Assert.Equal(ResolveOutcome.ExactMatch, result.Outcome);
        Assert.Equal("Foo.A.cs", result.PrimaryFilePath);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build a graph with one type N.<paramref name="typeName"/> declared
    /// across <paramref name="filePaths"/> partial files. Each partial file
    /// produces a <c>file:X Contains type:N.TypeName</c> edge so the resolver
    /// can walk them and discover all declaration sites.
    /// </summary>
    private static SemanticGraph BuildPartialTypeGraph(string typeName, string[] filePaths)
    {
        var typeId = $"type:N.{typeName}";
        var builder = new GraphBuilder();

        // The type symbol stores last-write-wins by Id. Different partials may
        // record different FilePath values; whichever is added last wins —
        // exactly mirroring the production graph build behavior. The resolver
        // must produce a deterministic answer regardless.
        for (int i = 0; i < filePaths.Length; i++)
        {
            var filePath = filePaths[i];
            var fileId = $"file:{filePath}";

            builder.AddSymbol(new Symbol
            {
                Id = fileId,
                Name = Path.GetFileName(filePath),
                QualifiedName = filePath,
                Kind = SymbolKind.File,
                FilePath = filePath,
            });

            builder.AddSymbol(new Symbol
            {
                Id = typeId,
                Name = typeName,
                QualifiedName = $"N.{typeName}",
                Kind = SymbolKind.Type,
                FilePath = filePath,    // last-write-wins; resolver must override
                ParentId = fileId,
            });

            builder.AddEdge(new Edge
            {
                SourceId = fileId,
                TargetId = typeId,
                Kind = EdgeKind.Contains,
                Evidence = Evidence,
            });
        }

        return builder.Build();
    }
}

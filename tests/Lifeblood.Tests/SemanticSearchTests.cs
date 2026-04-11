using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Phase 5 tests for <see cref="LifebloodSemanticSearchProvider"/> and the
/// xmldoc-summary persistence path. Pins:
///   - Name match scoring
///   - XmlDoc-only match (the killer feature — find symbols by WHAT they do)
///   - Kind filter narrowing
///   - Limit respected
///   - XmlDoc summaries are persisted on Symbol.Properties during extraction
/// </summary>
public class SemanticSearchTests
{
    private static readonly Evidence Evidence = new()
    {
        Kind = EvidenceKind.Semantic,
        AdapterName = "test",
        Confidence = ConfidenceLevel.Proven,
    };

    private static readonly LifebloodSemanticSearchProvider Provider = new();

    // ─────────────────────────────────────────────────────────────────────
    // Scoring unit tests (in-memory graph, no Roslyn)
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Search_NameMatch_ReturnsRankedHit()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.UserRepository", Name = "UserRepository", Kind = SymbolKind.Type, FilePath = "UR.cs" })
            .AddSymbol(new Symbol { Id = "type:N.Unrelated", Name = "Unrelated", Kind = SymbolKind.Type, FilePath = "U.cs" })
            .Build();

        var results = Provider.Search(graph, new SearchQuery("User"));

        Assert.NotEmpty(results);
        Assert.Equal("type:N.UserRepository", results[0].CanonicalId);
    }

    [Fact]
    public void Search_XmlDocOnly_FindsSymbolByDocText()
    {
        // The symbol's NAME has nothing to do with "canonicalize" but its
        // xmldoc summary does. Before Phase 5, this was only discoverable
        // via a full-text search tool; now lifeblood_search finds it.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol
            {
                Id = "type:N.UserRepository",
                Name = "UserRepository",
                Kind = SymbolKind.Type,
                FilePath = "UR.cs",
                Properties = new Dictionary<string, string>
                {
                    ["xmlDocSummary"] = "Canonicalizes user input before persisting.",
                },
            })
            .AddSymbol(new Symbol
            {
                Id = "type:N.Unrelated",
                Name = "Unrelated",
                Kind = SymbolKind.Type,
                FilePath = "U.cs",
            })
            .Build();

        var results = Provider.Search(graph, new SearchQuery("canonicaliz"));

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.CanonicalId == "type:N.UserRepository");
        // The snippet field carries the matching doc text so the user
        // can render the context.
        var hit = results.First(r => r.CanonicalId == "type:N.UserRepository");
        Assert.Contains(hit.MatchSnippets, s => s.StartsWith("xmlDoc:", StringComparison.Ordinal));
    }

    [Fact]
    public void Search_KindFilter_RestrictsToRequestedKinds()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.UserType", Name = "UserType", Kind = SymbolKind.Type, FilePath = "U.cs" })
            .AddSymbol(new Symbol { Id = "method:N.Foo.User()", Name = "User", Kind = SymbolKind.Method, FilePath = "F.cs" })
            .Build();

        var results = Provider.Search(graph, new SearchQuery("User", new[] { SymbolKind.Method }));

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal(SymbolKind.Method, r.Kind));
    }

    [Fact]
    public void Search_RespectsLimit()
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

        var results = Provider.Search(graph, new SearchQuery("Foo", Limit: 5));

        Assert.Equal(5, results.Length);
    }

    [Fact]
    public void Search_EmptyQuery_ReturnsEmpty()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Foo", Name = "Foo", Kind = SymbolKind.Type, FilePath = "Foo.cs" })
            .Build();

        Assert.Empty(Provider.Search(graph, new SearchQuery("")));
        Assert.Empty(Provider.Search(graph, new SearchQuery("   ")));
    }

    // ─────────────────────────────────────────────────────────────────────
    // End-to-end: xmldoc summaries persisted during real extraction
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnalyzeWorkspace_ExtractsXmlDocSummariesOntoSymbolProperties()
    {
        // Real csproj on disk. A method with an XML doc summary. Assert
        // that after AnalyzeWorkspace runs, the graph Symbol for that
        // method carries the summary in its Properties bag under the
        // "xmlDocSummary" key.
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-xmldoc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Svc.cs"), @"
namespace N
{
    /// <summary>Top-level service class that canonicalizes inputs.</summary>
    public class Svc
    {
        /// <summary>Normalize the input and persist it.</summary>
        public void Run(string input) { }
    }
}");
            File.WriteAllText(Path.Combine(tempDir, "TestProject.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>");

            var fs = new PhysicalFileSystem();
            var analyzer = new RoslynWorkspaceAnalyzer(fs);
            var graph = analyzer.AnalyzeWorkspace(tempDir, new AnalysisConfig());

            var svc = graph.GetSymbol("type:N.Svc");
            Assert.NotNull(svc);
            Assert.True(svc!.Properties.ContainsKey("xmlDocSummary"),
                "Type symbol missing xmlDocSummary. " +
                "Properties keys: " + string.Join(",", svc.Properties.Keys));
            Assert.Contains("canonicalizes", svc.Properties["xmlDocSummary"], StringComparison.OrdinalIgnoreCase);

            var run = graph.GetSymbol("method:N.Svc.Run(string)");
            Assert.NotNull(run);
            Assert.True(run!.Properties.ContainsKey("xmlDocSummary"));
            Assert.Contains("Normalize", run.Properties["xmlDocSummary"]);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
    }
}

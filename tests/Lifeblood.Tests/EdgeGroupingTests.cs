using Lifeblood.Application.Ports.Right;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.PathClassification;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Pins the dependants/dependencies edge grouping + filtering contract
/// (`INV-EDGE-GROUP-001`). A flat 21-caller list is a bookkeeping chore;
/// grouping by path bucket answers "production-live or test-only?" and the
/// filters narrow the flat list to the buckets the caller cares about.
/// Bucket/module classification reuses the same SSoT as blast-radius grouping.
/// </summary>
public class EdgeGroupingTests
{
    private readonly LifebloodMcpProvider _provider = new(new TestBlastRadiusBridge());

    // Target type with one production caller, one test caller, one editor
    // caller — all under module "ModA".
    private static SemanticGraph Graph() => new GraphBuilder()
        .AddSymbol(new Symbol { Id = "mod:ModA", Name = "ModA", Kind = SymbolKind.Module })
        .AddSymbol(new Symbol { Id = "type:Target", Name = "Target", Kind = SymbolKind.Type, FilePath = "src/Target.cs", ParentId = "mod:ModA" })
        .AddSymbol(new Symbol { Id = "type:ProdCaller", Name = "ProdCaller", Kind = SymbolKind.Type, FilePath = "src/ProdCaller.cs", ParentId = "mod:ModA" })
        .AddSymbol(new Symbol { Id = "type:TestCaller", Name = "TestCaller", Kind = SymbolKind.Type, FilePath = "Tests/TestCallerTests.cs", ParentId = "mod:ModA" })
        .AddSymbol(new Symbol { Id = "type:EditorCaller", Name = "EditorCaller", Kind = SymbolKind.Type, FilePath = "src/Editor/EditorCaller.cs", ParentId = "mod:ModA" })
        .Build();

    private static EdgeDetail[] CallerEdges() => new[]
    {
        new EdgeDetail { OtherEndId = "type:ProdCaller", Kind = EdgeKind.Calls },
        new EdgeDetail { OtherEndId = "type:TestCaller", Kind = EdgeKind.Calls },
        new EdgeDetail { OtherEndId = "type:EditorCaller", Kind = EdgeKind.Calls },
    };

    [Fact]
    public void ClassifyEdges_GroupByBucket_SplitsProductionTestEditor()
    {
        var result = _provider.ClassifyEdges(Graph(), CallerEdges(),
            new EdgeGroupOptions { GroupByBucket = true });

        Assert.Equal(3, result.Edges.Length);          // nothing filtered
        Assert.Equal(3, result.TotalBeforeFilter);
        Assert.NotNull(result.ByBucket);
        Assert.Null(result.ByModule);                  // not requested
        Assert.Equal(1, result.ByBucket!["Production"].Count);
        Assert.Equal(1, result.ByBucket["Test"].Count);
        Assert.Equal(1, result.ByBucket["Editor"].Count);
    }

    [Fact]
    public void ClassifyEdges_GroupByModule_AttributesToContainingModule()
    {
        var result = _provider.ClassifyEdges(Graph(), CallerEdges(),
            new EdgeGroupOptions { GroupByModule = true });

        Assert.NotNull(result.ByModule);
        Assert.Equal(3, result.ByModule!["ModA"].Count);
    }

    [Fact]
    public void ClassifyEdges_ExcludeTests_DropsTestEndpointsFromFlatList()
    {
        var result = _provider.ClassifyEdges(Graph(), CallerEdges(),
            new EdgeGroupOptions { ExcludeTests = true, GroupByBucket = true });

        Assert.Equal(2, result.Edges.Length);
        Assert.DoesNotContain(result.Edges, e => e.OtherEndId == "type:TestCaller");
        Assert.False(result.ByBucket!.ContainsKey("Test"));
    }

    [Fact]
    public void ClassifyEdges_IncludeBuckets_KeepsOnlyAllowlistedBuckets()
    {
        var result = _provider.ClassifyEdges(Graph(), CallerEdges(),
            new EdgeGroupOptions { IncludeBuckets = new[] { "Production" } });

        Assert.Single(result.Edges);
        Assert.Equal("type:ProdCaller", result.Edges[0].OtherEndId);
        Assert.Equal(3, result.TotalBeforeFilter);     // filter does not change the pre-filter total
    }

    [Fact]
    public void ClassifyEdges_IncludeBuckets_IsCaseInsensitive()
    {
        var result = _provider.ClassifyEdges(Graph(), CallerEdges(),
            new EdgeGroupOptions { IncludeBuckets = new[] { "production" } });

        Assert.Single(result.Edges);
    }

    [Fact]
    public void ClassifyEdges_PreviewPerGroupZero_OmitsPreviewKeepsCount()
    {
        var result = _provider.ClassifyEdges(Graph(), CallerEdges(),
            new EdgeGroupOptions { GroupByBucket = true, PreviewPerGroup = 0 });

        Assert.Equal(1, result.ByBucket!["Production"].Count);
        Assert.Empty(result.ByBucket["Production"].Preview);
    }

    [Fact]
    public void ClassifyEdges_NoOptions_ReturnsAllEdgesUngrouped()
    {
        var result = _provider.ClassifyEdges(Graph(), CallerEdges(), new EdgeGroupOptions());

        Assert.Equal(3, result.Edges.Length);
        Assert.Null(result.ByBucket);
        Assert.Null(result.ByModule);
    }

    [Theory]
    [InlineData("src/Caller.cs", PathBucket.Production)]
    [InlineData("Tests/CallerTests.cs", PathBucket.Test)]
    [InlineData("src/Editor/Caller.cs", PathBucket.Editor)]
    [InlineData("obj/Caller.g.cs", PathBucket.Generated)]
    [InlineData("Packages/Library/Caller.cs", PathBucket.Vendored)]
    [InlineData("ThirdParty/Editor/Caller.cs", PathBucket.Vendored)]
    public void ClassifyEdges_CanonicalBuckets_PreserveCountsAndPreviews(string path, PathBucket bucket)
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:Caller", Name = "Caller", Kind = SymbolKind.Type, FilePath = path })
            .Build();
        var edges = new[] { new EdgeDetail { OtherEndId = "type:Caller", Kind = EdgeKind.Calls } };

        var result = _provider.ClassifyEdges(graph, edges,
            new EdgeGroupOptions { GroupByBucket = true });

        Assert.Single(result.Edges);
        Assert.Equal(1, result.TotalBeforeFilter);
        var group = Assert.Single(result.ByBucket!);
        Assert.Equal(bucket.ToString(), group.Key);
        Assert.Equal(1, group.Value.Count);
        Assert.Equal(new[] { "type:Caller" }, group.Value.Preview);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ClassifyEdges_VendoredAllowlist_PreservesFilteringAndPreviewLimit(int preview)
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:Vendor", Name = "Vendor", Kind = SymbolKind.Type, FilePath = "Vendor/Caller.cs" })
            .AddSymbol(new Symbol { Id = "type:App", Name = "App", Kind = SymbolKind.Type, FilePath = "src/Caller.cs" })
            .Build();
        var edges = new[]
        {
            new EdgeDetail { OtherEndId = "type:Vendor", Kind = EdgeKind.Calls },
            new EdgeDetail { OtherEndId = "type:App", Kind = EdgeKind.Calls },
        };

        var result = _provider.ClassifyEdges(graph, edges, new EdgeGroupOptions
        {
            GroupByBucket = true, IncludeBuckets = new[] { "vendored" }, PreviewPerGroup = preview,
        });

        Assert.Equal("type:Vendor", Assert.Single(result.Edges).OtherEndId);
        Assert.Equal(2, result.TotalBeforeFilter);
        var group = Assert.Single(result.ByBucket!);
        Assert.Equal("Vendored", group.Key);
        Assert.Equal(1, group.Value.Count);
        Assert.Equal(preview, group.Value.Preview.Length);
    }
}

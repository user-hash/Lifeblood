using System.Text.Json;
using Lifeblood.Adapters.JsonGraph;
using Lifeblood.Analysis;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Graph;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

public class BlastRadiusBoundaryTests
{
    private const string Target = "type:Example.Target";
    private const string Hub = "type:Example.Hub";
    private const string HubMethod = "method:Example.Hub.Run()";

    private static SemanticGraph Graph(bool qualifiedIds = false)
    {
        string Id(string value) => qualifiedIds ? value.Replace(":", ":v2|Fixture|", StringComparison.Ordinal) : value;
        var graph = new GraphBuilder();
        foreach (var id in new[] { Target, Hub, HubMethod, "type:Example.Consumer", "type:Example.Far", "type:Example.Independent", "type:Example.Dual" })
            graph.AddSymbol(new Symbol
            {
                Id = Id(id), Name = id == Hub ? "Hub" : id.Split('.').Last(),
                Kind = id == HubMethod ? SymbolKind.Method : SymbolKind.Type,
                ParentId = id == HubMethod ? Id(Hub) : "mod:Example",
                FilePath = id is Hub or HubMethod ? "Packages/Example/Hub.cs" : "src/Example.cs",
                Properties = qualifiedIds
                    ? new Dictionary<string, string> { [SymbolPropertyKeys.LegacyCanonicalId] = id }
                    : new Dictionary<string, string>(),
            });
        graph.AddSymbol(new Symbol { Id = "mod:Example", Name = "Example", Kind = SymbolKind.Module });
        foreach (var (source, target) in new[]
        {
            (Hub, Target), (HubMethod, Target), ("type:Example.Consumer", HubMethod),
            ("type:Example.Far", Hub), ("type:Example.Independent", Target),
            ("type:Example.Dual", HubMethod), ("type:Example.Dual", "type:Example.Independent"),
        })
            graph.AddEdge(new Edge { SourceId = Id(source), TargetId = Id(target), Kind = EdgeKind.References });
        graph.AddEdge(new Edge { SourceId = Id(HubMethod), TargetId = Id(Target), Kind = EdgeKind.Calls });
        return graph.Build();
    }

    [Fact]
    public void Boundary_StopsHubAndOwnedMembers_PreservesIndependentPathsAndInput()
    {
        var graph = Graph();
        var original = JsonSerializer.Serialize(graph);
        var full = BlastRadiusAnalyzer.Analyze(graph, Target);
        var scoped = BlastRadiusAnalyzer.AnalyzeWithinBoundaries(graph, Target, new[] { Hub });

        Assert.Equal(6, full.AffectedCount);
        Assert.Equal(4, scoped.Impact.AffectedCount);
        Assert.Equal(new[] { HubMethod, Hub }, scoped.StoppedAtSymbolIds);
        Assert.Contains("type:Example.Dual", scoped.Impact.AffectedSymbolIds);
        Assert.DoesNotContain("type:Example.Consumer", scoped.Impact.AffectedSymbolIds);
        Assert.DoesNotContain("type:Example.Far", scoped.Impact.AffectedSymbolIds);
        Assert.Equal(3, scoped.Impact.Breaks.Length);
        Assert.Equal(original, JsonSerializer.Serialize(graph));
        Assert.Equal(JsonSerializer.Serialize(full), JsonSerializer.Serialize(BlastRadiusAnalyzer.Analyze(graph, Target)));
    }

    [Fact]
    public void EmptyBoundaries_PreserveOrdinaryTraversal()
    {
        var graph = Graph();
        var result = BlastRadiusAnalyzer.AnalyzeWithinBoundaries(graph, Target, Array.Empty<string>());
        Assert.Empty(result.StoppedAtSymbolIds);
        Assert.Equal(JsonSerializer.Serialize(BlastRadiusAnalyzer.Analyze(graph, Target)), JsonSerializer.Serialize(result.Impact));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 3)]
    public void DepthLimit_RemainsBinding(int depth, int expected)
    {
        var result = BlastRadiusAnalyzer.AnalyzeWithinBoundaries(Graph(), Target, new[] { Hub }, depth);
        Assert.Equal(expected, result.Impact.AffectedCount);
        Assert.Empty(result.StoppedAtSymbolIds);
    }

    [Fact]
    public void ExactMemberBoundary_DoesNotStopItsUnselectedContainingType()
    {
        var result = BlastRadiusAnalyzer.AnalyzeWithinBoundaries(Graph(), Target, new[] { HubMethod });
        Assert.Equal(5, result.Impact.AffectedCount);
        Assert.Contains("type:Example.Far", result.Impact.AffectedSymbolIds);
        Assert.Equal(HubMethod, Assert.Single(result.StoppedAtSymbolIds));
    }

    [Fact]
    public void TargetAsBoundary_StopsBeforeFirstExpansion()
    {
        var result = BlastRadiusAnalyzer.AnalyzeWithinBoundaries(Graph(), Target, new[] { Target });
        Assert.Empty(result.Impact.AffectedSymbolIds);
        Assert.Equal(Target, Assert.Single(result.StoppedAtSymbolIds));
    }

    [Fact]
    public void UnknownBoundary_IsRejected()
        => Assert.Throws<ArgumentException>(() => BlastRadiusAnalyzer.AnalyzeWithinBoundaries(Graph(), Target, new[] { "type:Missing" }));

    [Fact]
    public void NestedOwnershipAndParentCycles_AreBounded()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = Target, Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = Hub, Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:Nested", Kind = SymbolKind.Type, ParentId = Hub })
            .AddSymbol(new Symbol { Id = "method:Nested.Run", Kind = SymbolKind.Method, ParentId = "type:Nested" })
            .AddSymbol(new Symbol { Id = "type:CycleA", Kind = SymbolKind.Type, ParentId = "type:CycleB" })
            .AddSymbol(new Symbol { Id = "type:CycleB", Kind = SymbolKind.Type, ParentId = "type:CycleA" })
            .AddEdge(new Edge { SourceId = "method:Nested.Run", TargetId = Target, Kind = EdgeKind.Calls })
            .AddEdge(new Edge { SourceId = "type:CycleA", TargetId = Target, Kind = EdgeKind.References })
            .AddEdge(new Edge { SourceId = "type:CycleB", TargetId = "type:CycleA", Kind = EdgeKind.References })
            .Build();
        var result = BlastRadiusAnalyzer.AnalyzeWithinBoundaries(graph, Target, new[] { Hub });
        Assert.Equal("method:Nested.Run", Assert.Single(result.StoppedAtSymbolIds));
        Assert.Equal(3, result.Impact.AffectedCount);
    }

    [Theory]
    [InlineData("none", false)]
    [InlineData("none", true)]
    [InlineData("bucket", false)]
    [InlineData("module", false)]
    [InlineData("both", false)]
    public void Wire_AllShapesUseTheSameScopedImpactAndTrueDirectCount(string grouping, bool summarize)
    {
        using var fixture = new WireFixture();
        var result = fixture.Tool(new { symbolId = Target, groupBy = grouping, summarize, stopAtSymbolIds = new[] { "Hub" }, previewPerGroup = 0, maxResults = 1 });
        Assert.NotEqual(true, result.Result.IsError);
        Assert.Equal(4, result.Payload.GetProperty("affectedCount").GetInt32());
        Assert.Equal(3, result.Payload.GetProperty("directDependants").GetInt32());
        var traversal = result.Payload.GetProperty("traversal");
        Assert.True(traversal.GetProperty("scopeLimited").GetBoolean());
        Assert.Equal(Hub, traversal.GetProperty("stopAtSymbolIds")[0].GetString());
        Assert.Equal(2, traversal.GetProperty("stoppedAtCount").GetInt32());
        if (grouping is "bucket" or "both")
        {
            Assert.Equal(2, result.Payload.GetProperty("byBucket").GetProperty("Vendored").GetProperty("count").GetInt32());
            Assert.Empty(result.Payload.GetProperty("byBucket").GetProperty("Vendored").GetProperty("preview").EnumerateArray());
        }
        if (grouping is "module" or "both")
            Assert.Equal(4, result.Payload.GetProperty("byModule").GetProperty("Example").GetProperty("count").GetInt32());
        if (grouping == "none") Assert.True(result.Payload.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Wire_OmittedAndEmptyBoundariesPreserveLegacyShape()
    {
        using var fixture = new WireFixture();
        var ordinary = fixture.Tool(new { symbolId = Target }).Payload;
        var empty = fixture.Tool(new { symbolId = Target, stopAtSymbolIds = Array.Empty<string>() }).Payload;
        Assert.Equal(6, ordinary.GetProperty("affectedCount").GetInt32());
        Assert.False(ordinary.TryGetProperty("traversal", out _));
        Assert.False(empty.TryGetProperty("traversal", out _));
        Assert.Equal(ordinary.GetProperty("affected").GetRawText(), empty.GetProperty("affected").GetRawText());
    }

    [Theory]
    [InlineData(McpProtocolSpec.SchemaVersions.Legacy)]
    [InlineData(McpProtocolSpec.SchemaVersions.Canonical)]
    public void Wire_BoundaryAndStoppedIdsRespectNegotiatedSchema(string schema)
    {
        using var fixture = new WireFixture(qualifiedIds: true, schema);
        var result = fixture.Tool(new { symbolId = Target, stopAtSymbolIds = new[] { Hub } });
        Assert.NotEqual(true, result.Result.IsError);
        var ids = result.Payload.GetProperty("traversal").GetProperty("stoppedAtSymbolIds").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(2, ids.Length);
        Assert.All(ids, id => Assert.Equal(schema == McpProtocolSpec.SchemaVersions.Canonical, id.Contains("v2|", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[7]")]
    [InlineData("[\"\"]")]
    [InlineData("[\"type:Missing\"]")]
    public void Wire_InvalidBoundaryInputReturnsAnError(string value)
    {
        using var fixture = new WireFixture();
        var result = fixture.Call(JsonSerializer.Deserialize<JsonElement>($$"""{"symbolId":"{{Target}}","stopAtSymbolIds":{{value}}} """));
        Assert.True(result.IsError);
    }

    [Fact]
    public void Wire_RejectsOverLimitInputBeforeResolvingBoundaries()
    {
        using var fixture = new WireFixture();
        var result = fixture.Call(new { symbolId = Target, stopAtSymbolIds = Enumerable.Repeat(Hub, 33).ToArray() });
        Assert.True(result.IsError);
        Assert.Contains("at most 32", result.Content[0].Text);
    }

    [Fact]
    public void Wire_StoppedMemberReceiptIsBoundedWithoutClippingCounts()
    {
        var builder = new GraphBuilder()
            .AddSymbol(new Symbol { Id = Target, Name = "Target", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = Hub, Name = "Hub", Kind = SymbolKind.Type });
        for (var i = 0; i < 40; i++)
        {
            var id = $"method:Example.Hub.Run{i}()";
            builder.AddSymbol(new Symbol { Id = id, Name = $"Run{i}", Kind = SymbolKind.Method, ParentId = Hub });
            builder.AddEdge(new Edge { SourceId = id, TargetId = Target, Kind = EdgeKind.Calls });
        }
        using var fixture = new WireFixture(graph: builder.Build());
        var result = fixture.Tool(new { symbolId = Target, stopAtSymbolIds = new[] { Hub } }).Payload;
        Assert.Equal(40, result.GetProperty("affectedCount").GetInt32());
        var traversal = result.GetProperty("traversal");
        Assert.Equal(40, traversal.GetProperty("stoppedAtCount").GetInt32());
        Assert.Equal(32, traversal.GetProperty("stoppedAtSymbolIds").GetArrayLength());
        Assert.True(traversal.GetProperty("stoppedAtTruncated").GetBoolean());
    }

    private sealed class WireFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "lifeblood-boundary-wire-" + Guid.NewGuid().ToString("N"));
        private readonly McpServerHost _host;
        private readonly McpDispatcher _dispatcher;

        internal WireFixture(bool qualifiedIds = false, string schema = McpProtocolSpec.SchemaVersions.Legacy, SemanticGraph? graph = null)
        {
            Directory.CreateDirectory(_root);
            var graphPath = Path.Combine(_root, "graph.json");
            using (var output = File.Create(graphPath))
                new JsonGraphExporter().Export(new GraphDocument { Language = "synthetic", Graph = graph ?? Graph(qualifiedIds) }, output);
            _host = McpServerHost.Create(ToolJsonCompatibilityMode.Strict);
            _host.Session.Load(null, graphPath, null);
            Assert.NotNull(_host.Session.Graph);
            _dispatcher = _host.CreateDispatcher();
            Assert.Null(_dispatcher.Dispatch(new JsonRpcRequest
            {
                Id = JsonSerializer.SerializeToElement(1), Method = McpProtocolSpec.Methods.Initialize,
                Params = JsonSerializer.SerializeToElement(new Dictionary<string, string> { [McpProtocolSpec.SchemaVersions.InitializeParameter] = schema }),
            })!.Error);
        }

        internal McpToolResult Call(object args)
            => Assert.IsType<McpToolResult>(_dispatcher.Dispatch(new JsonRpcRequest
            {
                Id = JsonSerializer.SerializeToElement(2), Method = McpProtocolSpec.Methods.ToolsCall,
                Params = JsonSerializer.SerializeToElement(new { name = "lifeblood_blast_radius", arguments = args }),
            })!.Result);

        internal (McpToolResult Result, JsonElement Payload) Tool(object args)
        {
            var result = Call(args);
            Assert.False(result.IsError ?? false, result.Content[0].Text);
            using var document = JsonDocument.Parse(result.Content[0].Text);
            return (result, document.RootElement.Clone());
        }

        public void Dispose()
        {
            _host.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }
}

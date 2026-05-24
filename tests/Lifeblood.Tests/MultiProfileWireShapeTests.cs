using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Analysis;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>INV-MULTI-DEFINE-WIRE-001 — Wave 6.D handler-side wire shape.</summary>
public class MultiProfileWireShapeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _graphPath;

    public MultiProfileWireShapeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-mp-wire-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "mod:Core", Name = "Core", Kind = SymbolKind.Module })
            .AddSymbol(new Symbol { Id = "type:Acme.A", Name = "A", Kind = SymbolKind.Type, ParentId = "mod:Core" })
            .AddSymbol(new Symbol { Id = "type:Acme.B", Name = "B", Kind = SymbolKind.Type, ParentId = "mod:Core" })
            .AddSymbol(new Symbol { Id = "type:Acme.C", Name = "C", Kind = SymbolKind.Type, ParentId = "mod:Core" })
            .AddEdge(new Edge
            {
                SourceId = "type:Acme.A", TargetId = "type:Acme.B", Kind = EdgeKind.Calls,
                Evidence = new Evidence { Kind = EvidenceKind.Semantic, AdapterName = "Test", Confidence = ConfidenceLevel.Proven },
                Profiles = new[] { "Editor", "Player" },
            })
            .AddEdge(new Edge
            {
                SourceId = "type:Acme.A", TargetId = "type:Acme.C", Kind = EdgeKind.References,
                Evidence = new Evidence { Kind = EvidenceKind.Semantic, AdapterName = "Test", Confidence = ConfidenceLevel.Proven },
                Profiles = new[] { "Player" },
            })
            .Build();

        var doc = new GraphDocument
        {
            Language = "test",
            Adapter = new AdapterCapability { CanDiscoverSymbols = true, TypeResolution = ConfidenceLevel.Proven },
            Graph = graph,
        };

        _graphPath = Path.Combine(_tempDir, "graph.json");
        using var stream = File.Create(_graphPath);
        new Lifeblood.Adapters.JsonGraph.JsonGraphExporter().Export(doc, stream);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static readonly PhysicalFileSystem Fs = new();

    private sealed class TestBlastRadiusProvider : IBlastRadiusProvider
    {
        public BlastRadiusResult Analyze(SemanticGraph graph, string targetSymbolId, int maxDepth = 10)
            => BlastRadiusAnalyzer.Analyze(graph, targetSymbolId, maxDepth);
    }

    private static ToolHandler CreateHandler()
    {
        IMcpGraphProvider provider = new LifebloodMcpProvider(new TestBlastRadiusProvider());
        ISymbolResolver resolver = new LifebloodSymbolResolver();
        ISemanticSearchProvider search = new LifebloodSemanticSearchProvider();
        IDeadCodeAnalyzer deadCode = new LifebloodDeadCodeAnalyzer();
        IPartialViewBuilder partialView = new LifebloodPartialViewBuilder(Fs);
        Lifeblood.Application.Ports.Right.Invariants.IInvariantProvider invariants
            = new LifebloodInvariantProvider(Fs);
        var classifications = ToolRegistry.GetDefinitions()
            .Where(d => d.EnvelopeClassification != null)
            .ToDictionary(d => d.Name, d => d.EnvelopeClassification!, System.StringComparer.Ordinal);
        IResponseDecorator decorator = new LifebloodResponseDecorator(classifications);
        return new ToolHandler(new GraphSession(Fs), provider, resolver, search, deadCode, partialView, invariants, decorator);
    }

    private static JsonElement? MakeArgs(object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    [Fact]
    public void Dependants_EdgeProfilesField_RoundTripsFromGraphToWire()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var result = handler.Handle("lifeblood_dependants", MakeArgs(new { symbolId = "type:Acme.B" }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        Assert.Contains("\"profiles\"", text);
        Assert.Contains("Editor", text);
        Assert.Contains("Player", text);
    }

    [Fact]
    public void Dependencies_EdgeProfilesField_RoundTripsFromGraphToWire()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var result = handler.Handle("lifeblood_dependencies", MakeArgs(new { symbolId = "type:Acme.A" }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        Assert.Contains("\"profiles\"", text);
    }

    [Fact]
    public void Dependencies_ProfileFilter_NarrowsToMatchingProfile()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var result = handler.Handle("lifeblood_dependencies", MakeArgs(new
        {
            symbolId = "type:Acme.A",
            profileFilter = new[] { "Editor" },
        }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        // Editor-filtered: A→B (Editor+Player) keeps, A→C (Player only) drops.
        Assert.Contains("type:Acme.B", text);
        Assert.DoesNotContain("type:Acme.C", text);
    }

    [Fact]
    public void Dependencies_ProfileFilterUnmatchedSet_FiltersOutAllProfileEdges()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var result = handler.Handle("lifeblood_dependencies", MakeArgs(new
        {
            symbolId = "type:Acme.A",
            profileFilter = new[] { "NonExistent" },
        }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        Assert.Contains("\"count\": 0", text);
    }

    [Fact]
    public void Dependants_NoProfileFilter_KeepsAllEdges_RegardlessOfProfiles()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var result = handler.Handle("lifeblood_dependants", MakeArgs(new { symbolId = "type:Acme.C" }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        Assert.Contains("\"count\": 1", text);
        Assert.Contains("type:Acme.A", text);
    }

    [Fact]
    public void Dependencies_NullProfilesEdge_PassesFilterByDefault_BackCompat()
    {
        // Single-profile back-compat: a graph with Edge.Profiles=null must
        // still appear in dependants/dependencies queries even when the
        // caller passes a profileFilter. The filter narrows multi-profile
        // results; it does not exclude pre-multi-define edges.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:X", Name = "X", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:Y", Name = "Y", Kind = SymbolKind.Type })
            .AddEdge(new Edge { SourceId = "type:X", TargetId = "type:Y", Kind = EdgeKind.Calls })
            .Build();
        var doc = new GraphDocument
        {
            Language = "test",
            Adapter = new AdapterCapability { CanDiscoverSymbols = true, TypeResolution = ConfidenceLevel.Proven },
            Graph = graph,
        };
        var path = Path.Combine(_tempDir, "single-profile.json");
        using (var stream = File.Create(path))
            new Lifeblood.Adapters.JsonGraph.JsonGraphExporter().Export(doc, stream);

        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = path }));

        var result = handler.Handle("lifeblood_dependencies", MakeArgs(new
        {
            symbolId = "type:X",
            profileFilter = new[] { "AnyProfile" },
        }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        Assert.Contains("type:Y", text);
        Assert.Contains("\"count\": 1", text);
    }
}

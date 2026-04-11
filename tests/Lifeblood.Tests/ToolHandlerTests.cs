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

/// <summary>
/// Tests for MCP ToolHandler — all 6 tools, error paths, and tool registry.
/// </summary>
public class ToolHandlerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _graphPath;

    public ToolHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // Build a minimal valid graph.json for testing
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "mod:Core", Name = "Core", Kind = SymbolKind.Module })
            .AddSymbol(new Symbol { Id = "type:Core.Foo", Name = "Foo", Kind = SymbolKind.Type, ParentId = "mod:Core", FilePath = "Foo.cs", Line = 1 })
            .AddSymbol(new Symbol { Id = "type:Core.Bar", Name = "Bar", Kind = SymbolKind.Type, ParentId = "mod:Core", FilePath = "Bar.cs", Line = 1 })
            .AddSymbol(new Symbol { Id = "method:Core.Foo.Do", Name = "Do", Kind = SymbolKind.Method, ParentId = "type:Core.Foo" })
            .AddEdge(new Edge
            {
                SourceId = "type:Core.Foo", TargetId = "type:Core.Bar", Kind = EdgeKind.DependsOn,
                Evidence = new Evidence { Kind = EvidenceKind.Semantic, AdapterName = "Test", Confidence = ConfidenceLevel.Proven },
            })
            .AddEdge(new Edge
            {
                SourceId = "method:Core.Foo.Do", TargetId = "type:Core.Bar", Kind = EdgeKind.Calls,
                Evidence = new Evidence { Kind = EvidenceKind.Semantic, AdapterName = "Test", Confidence = ConfidenceLevel.Proven },
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
        return new ToolHandler(new GraphSession(Fs), provider, resolver, search, deadCode, partialView, invariants);
    }

    private static JsonElement? MakeArgs(object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    [Fact]
    public void Handle_UnknownTool_ReturnsError()
    {
        var handler = CreateHandler();
        var result = handler.Handle("nonexistent_tool", null);

        Assert.True(result.IsError);
        Assert.Contains("Unknown tool", result.Content[0].Text);
    }

    [Fact]
    public void Handle_Analyze_LoadsGraph()
    {
        var handler = CreateHandler();
        var result = handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        Assert.Null(result.IsError);
        Assert.Contains("symbols", result.Content[0].Text);
        Assert.Contains("edges", result.Content[0].Text);
    }

    [Fact]
    public void Handle_Analyze_MissingPath_ReturnsMessage()
    {
        var handler = CreateHandler();
        var result = handler.Handle("lifeblood_analyze", null);

        Assert.Contains("Specify", result.Content[0].Text);
    }

    [Fact]
    public void Handle_Analyze_InvalidPath_ReturnsError()
    {
        var handler = CreateHandler();
        var result = handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = "/nonexistent/path.json" }));

        Assert.Contains("not found", result.Content[0].Text);
    }

    [Fact]
    public void Handle_Context_WithoutLoad_ReturnsError()
    {
        var handler = CreateHandler();
        var result = handler.Handle("lifeblood_context", null);

        Assert.True(result.IsError);
        Assert.Contains("No graph loaded", result.Content[0].Text);
    }

    [Fact]
    public void Handle_Context_AfterLoad_ReturnsJson()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var result = handler.Handle("lifeblood_context", null);

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        Assert.Contains("highValueFiles", text);
    }

    [Fact]
    public void Handle_Lookup_WithoutLoad_ReturnsError()
    {
        var handler = CreateHandler();
        var result = handler.Handle("lifeblood_lookup", MakeArgs(new { symbolId = "type:Core.Foo" }));

        Assert.True(result.IsError);
        Assert.Contains("No graph loaded", result.Content[0].Text);
    }

    [Fact]
    public void Handle_Lookup_Found()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var result = handler.Handle("lifeblood_lookup", MakeArgs(new { symbolId = "type:Core.Foo" }));

        Assert.Null(result.IsError);
        Assert.Contains("Foo", result.Content[0].Text);
        Assert.Contains("Type", result.Content[0].Text);
    }

    [Fact]
    public void Handle_Lookup_NotFound()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var result = handler.Handle("lifeblood_lookup", MakeArgs(new { symbolId = "type:DoesNotExist" }));

        Assert.True(result.IsError);
        Assert.Contains("not found", result.Content[0].Text);
    }

    [Fact]
    public void Handle_Lookup_MissingSymbolId_ReturnsError()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var result = handler.Handle("lifeblood_lookup", null);

        Assert.True(result.IsError);
        Assert.Contains("symbolId is required", result.Content[0].Text);
    }

    [Fact]
    public void Handle_Dependencies_ReturnsDeps()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var result = handler.Handle("lifeblood_dependencies", MakeArgs(new { symbolId = "type:Core.Foo" }));

        Assert.Null(result.IsError);
        Assert.Contains("Core.Bar", result.Content[0].Text);
    }

    [Fact]
    public void Handle_Dependants_ReturnsDependants()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var result = handler.Handle("lifeblood_dependants", MakeArgs(new { symbolId = "type:Core.Bar" }));

        Assert.Null(result.IsError);
        Assert.Contains("Core.Foo", result.Content[0].Text);
    }

    [Fact]
    public void Handle_BlastRadius_ReturnsAffected()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var result = handler.Handle("lifeblood_blast_radius", MakeArgs(new { symbolId = "type:Core.Bar" }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        Assert.Contains("affectedCount", text);
        Assert.Contains("Core.Foo", text);
    }

    [Fact]
    public void Handle_BlastRadius_WithMaxDepth()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var result = handler.Handle("lifeblood_blast_radius", MakeArgs(new { symbolId = "type:Core.Bar", maxDepth = 1 }));

        Assert.Null(result.IsError);
        Assert.Contains("\"maxDepth\": 1", result.Content[0].Text);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Search dispatch (lifeblood_search). Exercises the ToolHandler plumbing
    // for the search tool — args parsing, kinds filter coercion, limit
    // coercion, empty-query error, not-loaded error. SemanticSearchTests
    // pins the scoring; these tests pin that the dispatch layer routes
    // JsonElement args through to the provider correctly so a future
    // refactor of SearchQuery field names can't silently break the wire
    // surface without a test failure.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Handle_Search_WithoutLoad_ReturnsError()
    {
        var handler = CreateHandler();
        var result = handler.Handle("lifeblood_search", MakeArgs(new { query = "Foo" }));

        Assert.True(result.IsError);
        Assert.Contains("No graph loaded", result.Content[0].Text);
    }

    [Fact]
    public void Handle_Search_MissingQuery_ReturnsError()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var result = handler.Handle("lifeblood_search", MakeArgs(new { limit = 5 }));

        Assert.True(result.IsError);
        Assert.Contains("query is required", result.Content[0].Text);
    }

    [Fact]
    public void Handle_Search_AfterLoad_ReturnsHits()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var result = handler.Handle("lifeblood_search", MakeArgs(new { query = "Foo" }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        Assert.Contains("\"query\": \"Foo\"", text);
        Assert.Contains("type:Core.Foo", text);
    }

    [Fact]
    public void Handle_Search_MultiTokenQuery_RoutesToProvider()
    {
        // Proves the ToolHandler layer passes the raw query string through
        // to the provider unmolested. If a future edit started pre-trimming,
        // uppercasing, or re-tokenizing the query at the handler layer, it
        // would desync from what SemanticSearchTests pins at the provider
        // layer — this test would catch that.
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var result = handler.Handle("lifeblood_search", MakeArgs(new { query = "Foo Bar" }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        // Both Foo and Bar types exist in the fixture graph; the multi-token
        // ranked-OR query must surface both.
        Assert.Contains("type:Core.Foo", text);
        Assert.Contains("type:Core.Bar", text);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Dead code dispatch. Pins the INV-DEADCODE-001 contract: every
    // response from the dead_code tool MUST carry the experimental
    // status marker and the warning text listing known false-positive
    // classes. Removing either field is how this invariant regresses;
    // this test catches that before it ships.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Handle_DeadCode_Response_IncludesExperimentalWarning()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var result = handler.Handle("lifeblood_dead_code", null);

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        Assert.Contains("\"status\": \"experimental\"", text);
        Assert.Contains("method-group conversion", text);
        Assert.Contains("canonical-id drift", text);
        Assert.Contains("lifeblood_find_references", text);
    }

    [Fact]
    public void Handle_Search_KindsFilter_Applied()
    {
        // Kinds filter is a JSON string array on the wire. Pins that the
        // ParseKindsArray coercion at the handler layer converts the wire
        // format into SymbolKind[] and the provider honours it. The fixture
        // method is named "Do" (no QualifiedName set on any fixture symbol),
        // so the query "Do" exercises the literal-fallback tokenization
        // path: the 2-char token is below the min-length floor, so the
        // tokenizer falls back to treating the whole trimmed query as one
        // literal — which then hits the method's bare Name. With
        // kinds=["Method"] the Type symbols (Foo, Bar) are excluded by the
        // kind filter, leaving only the method.
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var unfiltered = handler.Handle("lifeblood_search", MakeArgs(new { query = "Do" }));
        Assert.Null(unfiltered.IsError);
        Assert.Contains("method:Core.Foo.Do", unfiltered.Content[0].Text);

        var filtered = handler.Handle("lifeblood_search",
            MakeArgs(new { query = "Do", kinds = new[] { "Method" } }));

        Assert.Null(filtered.IsError);
        var text = filtered.Content[0].Text;
        Assert.Contains("method:Core.Foo.Do", text);
        Assert.DoesNotContain("\"canonicalId\": \"type:", text);
    }

    [Fact]
    public void ToolRegistry_Returns22Tools()
    {
        var tools = ToolRegistry.GetTools();

        Assert.Equal(22, tools.Length);
        Assert.Contains(tools, t => t.Name == "lifeblood_analyze");
        Assert.Contains(tools, t => t.Name == "lifeblood_context");
        Assert.Contains(tools, t => t.Name == "lifeblood_lookup");
        Assert.Contains(tools, t => t.Name == "lifeblood_dependencies");
        Assert.Contains(tools, t => t.Name == "lifeblood_dependants");
        Assert.Contains(tools, t => t.Name == "lifeblood_blast_radius");
        Assert.Contains(tools, t => t.Name == "lifeblood_file_impact");
        Assert.Contains(tools, t => t.Name == "lifeblood_resolve_short_name");
        Assert.Contains(tools, t => t.Name == "lifeblood_invariant_check");
        Assert.Contains(tools, t => t.Name == "lifeblood_execute");
        Assert.Contains(tools, t => t.Name == "lifeblood_diagnose");
        Assert.Contains(tools, t => t.Name == "lifeblood_compile_check");
        Assert.Contains(tools, t => t.Name == "lifeblood_find_references");
        Assert.Contains(tools, t => t.Name == "lifeblood_rename");
        Assert.Contains(tools, t => t.Name == "lifeblood_format");
    }

    [Fact]
    public void ToolRegistry_AllToolsHaveDescriptions()
    {
        var tools = ToolRegistry.GetTools();

        foreach (var tool in tools)
        {
            Assert.False(string.IsNullOrEmpty(tool.Name));
            Assert.False(string.IsNullOrEmpty(tool.Description));
        }
    }

    [Fact]
    public void ToolRegistry_WithoutCompilationState_WriteSideToolsMarkedUnavailable()
    {
        var tools = ToolRegistry.GetTools(hasCompilationState: false);
        var writeSideNames = new[] { "lifeblood_execute", "lifeblood_diagnose", "lifeblood_compile_check",
            "lifeblood_find_references", "lifeblood_rename", "lifeblood_format" };

        foreach (var name in writeSideNames)
        {
            var tool = Assert.Single(tools, t => t.Name == name);
            Assert.StartsWith("[Unavailable", tool.Description);
        }

        // Read-side tools should NOT be marked unavailable
        var analyze = Assert.Single(tools, t => t.Name == "lifeblood_analyze");
        Assert.DoesNotContain("Unavailable", analyze.Description);
    }

    [Fact]
    public void ToolRegistry_WithCompilationState_NoUnavailableMarkers()
    {
        var tools = ToolRegistry.GetTools(hasCompilationState: true);
        Assert.All(tools, t => Assert.DoesNotContain("[Unavailable", t.Description));
    }
}

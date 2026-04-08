using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
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

    private static ToolHandler CreateHandler() =>
        new(new GraphSession(Fs));

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

    [Fact]
    public void ToolRegistry_Returns6Tools()
    {
        var tools = ToolRegistry.GetTools();

        Assert.Equal(12, tools.Length);
        Assert.Contains(tools, t => t.Name == "lifeblood_analyze");
        Assert.Contains(tools, t => t.Name == "lifeblood_context");
        Assert.Contains(tools, t => t.Name == "lifeblood_lookup");
        Assert.Contains(tools, t => t.Name == "lifeblood_dependencies");
        Assert.Contains(tools, t => t.Name == "lifeblood_dependants");
        Assert.Contains(tools, t => t.Name == "lifeblood_blast_radius");
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
}

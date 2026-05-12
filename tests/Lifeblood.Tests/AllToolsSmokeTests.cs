using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Analysis;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Results;
using Lifeblood.Server.Mcp;
using Xunit;
using Xunit.Abstractions;

namespace Lifeblood.Tests;

/// <summary>
/// Wave-end smoke: invoke every one of the 25 MCP tools through
/// <see cref="ToolHandler.Handle"/> against a real Roslyn-analyzed
/// workspace and assert each returns a structured JSON result without
/// throwing. Catches the regression class "C2 / C4 broke a tool's
/// dispatch via the resolver / search seam I didn't have eyes on."
///
/// This is a SMOKE test — minimal fixture, happy-path inputs, asserts
/// only that each tool responds with a JSON body and (where applicable)
/// resolves the test target symbol. Per-tool semantics are covered by
/// the dedicated test files.
/// </summary>
public class AllToolsSmokeTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _temp;
    private readonly PhysicalFileSystem _fs = new();
    private readonly ToolHandler _handler;

    public AllToolsSmokeTests(ITestOutputHelper output)
    {
        _output = output;
        _temp = Path.Combine(Path.GetTempPath(), $"lb-tools-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temp);

        File.WriteAllText(Path.Combine(_temp, "TestProject.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(_temp, "Code.cs"), @"
namespace Smoke;

/// <summary>A demo service that does work.</summary>
public interface IService { void Run(); }

public class ServiceImpl : IService
{
    /// <summary>Run the work loop.</summary>
    public void Run() { var x = new Helper(); x.Help(); }
}

public class Helper
{
    public void Help() { }
}

public enum Mode { Idle, Active, Done }
");
        _handler = CreateHandler();

        // Load the workspace so every read/write tool has graph + compilations.
        var loadResult = _handler.Handle("lifeblood_analyze",
            JsonArgs(new { projectPath = _temp, readOnly = false }));
        Assert.NotEqual(true, loadResult.IsError);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("lifeblood_lookup",                 "type:Smoke.IService", "symbolId")]
    [InlineData("lifeblood_dependencies",           "method:Smoke.ServiceImpl.Run()", "symbolId")]
    [InlineData("lifeblood_dependants",             "type:Smoke.Helper", "symbolId")]
    [InlineData("lifeblood_blast_radius",           "type:Smoke.Helper", "symbolId")]
    [InlineData("lifeblood_file_impact",            "Code.cs", "filePath")]
    [InlineData("lifeblood_resolve_short_name",     "ServiceImpl", "name")]
    [InlineData("lifeblood_find_references",        "type:Smoke.Helper", "symbolId")]
    [InlineData("lifeblood_find_definition",        "type:Smoke.Helper", "symbolId")]
    [InlineData("lifeblood_find_implementations",   "type:Smoke.IService", "symbolId")]
    [InlineData("lifeblood_documentation",          "type:Smoke.IService", "symbolId")]
    [InlineData("lifeblood_partial_view",           "type:Smoke.ServiceImpl", "symbolId")]
    [InlineData("lifeblood_authority_report",       "type:Smoke.ServiceImpl", "symbolId")]
    [InlineData("lifeblood_port_health",            "type:Smoke.IService", "symbolId")]
    public void Tool_HappyPath_ResolvesSymbolWithoutError(
        string toolName, string symbolValue, string symbolKey)
    {
        var args = JsonArgs(new Dictionary<string, object> { [symbolKey] = symbolValue });
        var result = _handler.Handle(toolName, args);
        AssertNotError(toolName, result);
    }

    [Fact]
    public void Tool_ResolveMember_ReturnsUniqueForKnownMember()
    {
        // 26th MCP tool — type-scoped member resolution. Verifies the
        // happy-path (Unique outcome) on a known member of the fixture.
        var result = _handler.Handle("lifeblood_resolve_member",
            JsonArgs(new { typeName = "ServiceImpl", memberName = "Run" }));
        AssertNotError("lifeblood_resolve_member", result);
        var doc = JsonDocument.Parse(ExtractText(result));
        Assert.Equal("Unique", doc.RootElement.GetProperty("outcome").GetString());
        Assert.True(doc.RootElement.GetProperty("count").GetInt32() >= 1);
        Assert.True(doc.RootElement.TryGetProperty("resolvedTypeId", out _));
    }

    [Fact]
    public void Tool_Search_ReturnsResultsWithMatchKind()
    {
        // C4 wave-end check: the new MatchKind field must thread through
        // ToolHandler → MCP serialization without losing the enum value.
        var result = _handler.Handle("lifeblood_search",
            JsonArgs(new { query = "service" }));
        AssertNotError("lifeblood_search", result);
        var doc = JsonDocument.Parse(ExtractText(result));
        var results = doc.RootElement.GetProperty("results");
        Assert.True(results.GetArrayLength() > 0);
        Assert.True(results[0].TryGetProperty("matchKind", out _),
            "MatchKind field missing from search wire shape.");
    }

    [Fact]
    public void Tool_Cycles_RespondsWithStructuredShape()
    {
        var result = _handler.Handle("lifeblood_cycles", JsonArgs(new { }));
        AssertNotError("lifeblood_cycles", result);
        var doc = JsonDocument.Parse(ExtractText(result));
        Assert.True(doc.RootElement.TryGetProperty("count", out _));
    }

    [Fact]
    public void Tool_Context_RespondsWithStructuredShape()
    {
        var result = _handler.Handle("lifeblood_context", JsonArgs(new { summarize = true }));
        AssertNotError("lifeblood_context", result);
    }

    [Fact]
    public void Tool_DeadCode_RespondsWithStructuredShape()
    {
        var result = _handler.Handle("lifeblood_dead_code", JsonArgs(new { summarize = true }));
        AssertNotError("lifeblood_dead_code", result);
    }

    [Fact]
    public void Tool_InvariantCheck_RespondsWithoutError()
    {
        var result = _handler.Handle("lifeblood_invariant_check", JsonArgs(new { mode = "audit" }));
        AssertNotError("lifeblood_invariant_check", result);
    }

    [Fact]
    public void Tool_SymbolAtPosition_RespondsWithoutError()
    {
        var codePath = Path.Combine(_temp, "Code.cs").Replace('\\', '/');
        var result = _handler.Handle("lifeblood_symbol_at_position",
            JsonArgs(new { filePath = codePath, line = 7, column = 14 }));
        AssertNotError("lifeblood_symbol_at_position", result);
    }

    [Fact]
    public void Tool_Diagnose_RespondsWithoutError()
    {
        var codePath = Path.Combine(_temp, "Code.cs").Replace('\\', '/');
        var result = _handler.Handle("lifeblood_diagnose",
            JsonArgs(new { filePath = codePath }));
        AssertNotError("lifeblood_diagnose", result);
    }

    [Fact]
    public void Tool_CompileCheck_FilePath_RespondsWithoutError()
    {
        var codePath = Path.Combine(_temp, "Code.cs").Replace('\\', '/');
        var result = _handler.Handle("lifeblood_compile_check",
            JsonArgs(new { filePath = codePath }));
        AssertNotError("lifeblood_compile_check", result);
    }

    [Fact]
    public void Tool_Format_RespondsWithoutError()
    {
        var result = _handler.Handle("lifeblood_format",
            JsonArgs(new { code = "class X{public void Y(){}}" }));
        AssertNotError("lifeblood_format", result);
    }

    [Fact]
    public void Tool_Rename_PreviewMode_RespondsWithoutError()
    {
        // Rename in preview-style mode (request the edit list, don't apply).
        var result = _handler.Handle("lifeblood_rename",
            JsonArgs(new { symbolId = "type:Smoke.Helper", newName = "Helper2" }));
        AssertNotError("lifeblood_rename", result);
    }

    [Fact]
    public void Tool_Execute_RunsScriptAgainstLoadedWorkspace()
    {
        // The heaviest tool — compiles + runs C# against the workspace's
        // Roslyn semantic model. If anything in the wave silently broke
        // the script-host wiring this would explode.
        var result = _handler.Handle("lifeblood_execute",
            JsonArgs(new { code = "return Graph.Symbols.Count;" }));
        AssertNotError("lifeblood_execute", result);
        var doc = JsonDocument.Parse(ExtractText(result));
        // Script host can serialize the int return as Number or String
        // depending on the JSON pipeline shape — accept either, just
        // confirm a non-empty returnValue is present.
        var rv = doc.RootElement.GetProperty("returnValue");
        var rvText = rv.ValueKind == JsonValueKind.Number
            ? rv.GetInt32().ToString()
            : rv.GetString();
        Assert.False(string.IsNullOrWhiteSpace(rvText));
        Assert.True(int.Parse(rvText!) > 0);
        _output.WriteLine($"execute returned graph symbol count = {rvText}");
    }

    private static void AssertNotError(string toolName, McpToolResult result)
    {
        // McpToolResult.IsError is bool? — null on success, true on error.
        // Assert.False(null) misfires because null isn't strictly false.
        Assert.NotEqual(true, result.IsError);
        Assert.NotNull(result.Content);
        Assert.NotEmpty(result.Content!);
    }

    private static string ExtractText(McpToolResult result)
    {
        // Result content is a list of MCP TextContent items; the first
        // carries the JSON body for every read-side tool.
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Content));
        var first = doc.RootElement[0];
        var text = first.GetProperty("text").GetString();
        Assert.NotNull(text);
        return text!;
    }

    private static JsonElement? JsonArgs(object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private sealed class TestBlastRadiusProvider : IBlastRadiusProvider
    {
        public BlastRadiusResult Analyze(Lifeblood.Domain.Graph.SemanticGraph graph, string targetSymbolId, int maxDepth = 10)
            => BlastRadiusAnalyzer.Analyze(graph, targetSymbolId, maxDepth);
    }

    private ToolHandler CreateHandler()
    {
        IMcpGraphProvider provider = new LifebloodMcpProvider(new TestBlastRadiusProvider());
        ISymbolResolver resolver = new LifebloodSymbolResolver();
        ISemanticSearchProvider search = new LifebloodSemanticSearchProvider();
        IDeadCodeAnalyzer deadCode = new LifebloodDeadCodeAnalyzer();
        IPartialViewBuilder partialView = new LifebloodPartialViewBuilder(_fs);
        Lifeblood.Application.Ports.Right.Invariants.IInvariantProvider invariants
            = new LifebloodInvariantProvider(_fs);
        var classifications = ToolRegistry.GetDefinitions()
            .Where(d => d.EnvelopeClassification != null)
            .ToDictionary(d => d.Name, d => d.EnvelopeClassification!, System.StringComparer.Ordinal);
        IResponseDecorator decorator = new LifebloodResponseDecorator(classifications);
        return new ToolHandler(new GraphSession(_fs), provider, resolver, search, deadCode, partialView, invariants, decorator);
    }
}

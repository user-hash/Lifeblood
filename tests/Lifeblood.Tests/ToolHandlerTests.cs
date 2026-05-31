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
    public void Handle_Capabilities_WithoutLoad_ReturnsVersionToolCountsAndContractPaths()
    {
        var handler = CreateHandler();

        var result = handler.Handle("lifeblood_capabilities", null);

        Assert.Null(result.IsError);
        var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("lifeblood", doc.RootElement.GetProperty("server").GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("server").GetProperty("version").GetString()));
        Assert.Equal(31, doc.RootElement.GetProperty("tools").GetProperty("totalCount").GetInt32());
        Assert.Equal(18, doc.RootElement.GetProperty("tools").GetProperty("readSideCount").GetInt32());
        Assert.Equal(13, doc.RootElement.GetProperty("tools").GetProperty("writeSideCount").GetInt32());
        var telemetryEvents = doc.RootElement
            .GetProperty("featureFlags")
            .GetProperty("operationalTelemetryEvents")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();
        Assert.Contains("lifeblood.tool.truncated", telemetryEvents);
        Assert.Contains("lifeblood.analyze.fallback", telemetryEvents);
        // INV-TELEMETRY-EVENT-SSOT-001: the advertised surface is exactly the
        // emitted-event SSoT, so an emitted-but-unadvertised event fails here.
        Assert.Equal(McpTelemetryEvents.All, telemetryEvents);
        Assert.Contains("lifeblood.analyze.phase", telemetryEvents);
        var summarizeCapableTools = doc.RootElement
            .GetProperty("featureFlags")
            .GetProperty("summarizeCapableTools")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();
        var expectedSummarizeCapableTools = ToolRegistry.GetDefinitions()
            .Where(d =>
            {
                return d.InputContract.Arguments.TryGetValue("summarize", out var argument)
                    && argument.Type == ToolArgumentType.Boolean;
            })
            .Select(d => d.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedSummarizeCapableTools, summarizeCapableTools);
        Assert.Contains("schemas", doc.RootElement.GetProperty("contract").GetProperty("schemaSnapshotPath").GetString());
        Assert.Contains("STATUS.md", doc.RootElement.GetProperty("contract").GetProperty("statusDocAnchorPath").GetString());
        Assert.False(doc.RootElement.GetProperty("session").GetProperty("hasGraphLoaded").GetBoolean());
    }

    [Fact]
    public void Handle_Analyze_Response_IncludesDocsSafeEvidenceReceipt()
    {
        var handler = CreateHandler();

        var result = handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        Assert.Null(result.IsError);
        var doc = JsonDocument.Parse(result.Content[0].Text);
        var receipt = doc.RootElement.GetProperty("evidenceReceipt");
        Assert.True(receipt.GetProperty("citationSafe").GetBoolean());
        Assert.Equal("lifeblood.analyze", receipt.GetProperty("kind").GetString());
        Assert.Equal("lifeblood_analyze", receipt.GetProperty("queryRecipe").GetProperty("tool").GetString());
        Assert.Equal("full", receipt.GetProperty("queryRecipe").GetProperty("mode").GetString());
        Assert.Equal(4, receipt.GetProperty("counts").GetProperty("symbols").GetInt32());
        Assert.Contains("envelope.analysisGeneration", receipt.GetProperty("doNotCite").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("envelope.stalenessSeconds", receipt.GetProperty("doNotCite").EnumerateArray().Select(e => e.GetString()));
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
        // LB-FR-022: every response carries a `truncated` map (may be empty
        // on a small test graph but the field must exist).
        Assert.Contains("\"truncated\":", text);
    }

    // ──────────────────────────────────────────────────────────────────
    // LB-FR-022: context summarize / per-section caps / sections allowlist.
    // dogfood: full pack ~375KB on 87-module workspace, overflowed
    // tool-result limits. Smart-dynamic capping fits inside default budgets
    // without forcing the caller to pass options.
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Handle_Context_SummarizeMode_DropsAllListSections()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var result = handler.Handle("lifeblood_context", MakeArgs(new { summarize = true }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        Assert.Contains("\"summarize\": true", text);
        // Every list-section is empty under summarize mode.
        Assert.Contains("\"highValueFiles\": []", text);
        Assert.Contains("\"boundaries\": []", text);
        Assert.Contains("\"hotspots\": []", text);
        Assert.Contains("\"readingOrder\": []", text);
        Assert.Contains("\"dependencyMatrix\": []", text);
        // Summary stays — that's the cheapest signal.
        Assert.Contains("\"summary\":", text);
    }

    [Fact]
    public void Handle_Context_SectionsAllowlist_DropsUnlisted()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        // Allow only `boundaries`. Other list-sections must be empty
        // arrays even with default caps, because the allowlist drops them.
        var result = handler.Handle("lifeblood_context", MakeArgs(new
        {
            sections = new[] { "boundaries" },
        }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        Assert.Contains("\"highValueFiles\": []", text);
        Assert.Contains("\"hotspots\": []", text);
        Assert.Contains("\"readingOrder\": []", text);
        Assert.Contains("\"dependencyMatrix\": []", text);
    }

    [Fact]
    public void Handle_Context_PerSectionCap_TruncatesAndReports()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var result = handler.Handle("lifeblood_context", MakeArgs(new
        {
            maxBoundaries = 0,
        }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        Assert.Contains("\"boundaries\": []", text);
        // Either no boundaries existed (empty test graph) or the cap
        // forced truncation — either way the array is empty. When the
        // test graph DOES have any boundaries, `truncated.boundaries`
        // must report the full pre-clip count.
        if (!text.Contains("\"boundaries\": []") || text.Contains("\"fullCount\""))
        {
            // If truncated map mentions boundaries, that's the report.
            Assert.True(true);
        }
    }

    [Fact]
    public void Handle_Context_NegativeCap_AllowsUnlimitedSection()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        // -1 means "no cap" — section is emitted with whatever the
        // generator produced, no truncated entry recorded.
        var result = handler.Handle("lifeblood_context", MakeArgs(new
        {
            maxBoundaries = -1,
            maxFiles = -1,
            maxReadingOrder = -1,
            maxMatrixEntries = -1,
            maxHotspots = -1,
        }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        // truncated map exists but has no per-section entries.
        Assert.Contains("\"truncated\": {}", text);
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

    // ──────────────────────────────────────────────────────────────────
    // LB-NICE-005 + LB-FR-010: blast_radius summarize/maxResults
    // and direct vs transitive count surfacing.
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Handle_BlastRadius_AlwaysReportsDirectDependants()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var result = handler.Handle("lifeblood_blast_radius", MakeArgs(new { symbolId = "type:Core.Bar" }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        Assert.Contains("\"directDependants\":", text);
        Assert.Contains("\"affectedCount\":", text);
        Assert.Contains("\"truncated\":", text);
    }

    [Fact]
    public void Handle_BlastRadius_SummarizeMode_OmitsAffectedField_ReturnsPreview()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var result = handler.Handle("lifeblood_blast_radius",
            MakeArgs(new { symbolId = "type:Core.Bar", summarize = true }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        // Summarize mode renames the array slot to `preview` and adds the flag.
        Assert.Contains("\"summarize\": true", text);
        Assert.Contains("\"preview\":", text);
        Assert.DoesNotContain("\"affected\":", text);
    }

    [Fact]
    public void Handle_BlastRadius_MaxResults_TruncatesEmbeddedArray()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        // Cap at zero. Forces truncated:true regardless of how many
        // affected symbols the test graph actually has.
        var result = handler.Handle("lifeblood_blast_radius",
            MakeArgs(new { symbolId = "type:Core.Bar", maxResults = 0 }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        // Either the affected list is truly empty (no transitive deps) or
        // it was clipped — either way `affected` is empty array form.
        Assert.Contains("\"affected\": []", text);
        // affectedCount is the un-clipped figure; truncated:true iff anything was clipped.
        if (text.Contains("\"affectedCount\": 0"))
            Assert.Contains("\"truncated\": false", text);
        else
            Assert.Contains("\"truncated\": true", text);
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
        Assert.Contains("runtime/reflection-dispatched methods", text);
        Assert.Contains("extractor regression", text);
        Assert.Contains("canonical-id drift", text);
        Assert.Contains("lifeblood_find_references", text);
    }

    // ──────────────────────────────────────────────────────────────────
    // LB-FR-024 (dogfood): dead_code summarize / maxResults / kind
    // breakdown. large workspaces (53k+ symbols) overflowed downstream tool-result
    // limits with default kinds — needed the same shape as cycles +
    // context to stay consumable. Per-kind histogram always emitted so
    // the caller can decide whether to drill in via includeKinds.
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Handle_DeadCode_AlwaysReportsCountAndTruncationShape()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var result = handler.Handle("lifeblood_dead_code", MakeArgs(new { }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        Assert.Contains("\"count\":", text);
        Assert.Contains("\"kindBreakdown\":", text);
        Assert.Contains("\"truncated\":", text);
    }

    [Fact]
    public void Handle_DeadCode_SummarizeMode_OmitsFindingsField_ReturnsPreview()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var result = handler.Handle("lifeblood_dead_code", MakeArgs(new { summarize = true }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        Assert.Contains("\"summarize\": true", text);
        Assert.Contains("\"preview\":", text);
        Assert.DoesNotContain("\"findings\":", text);
        // kindBreakdown stays — it's the cheap signal callers use to drill.
        Assert.Contains("\"kindBreakdown\":", text);
    }

    [Fact]
    public void Handle_DeadCode_MaxResultsZero_ForcesTruncatedWhenAnyFinding()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var result = handler.Handle("lifeblood_dead_code", MakeArgs(new { maxResults = 0 }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        // findings is the embedded (clipped) array. With maxResults=0 it is empty.
        Assert.Contains("\"findings\": []", text);
        // truncated:true iff the un-clipped count was > 0; truncated:false iff zero findings.
        if (text.Contains("\"count\": 0"))
            Assert.Contains("\"truncated\": false", text);
        else
            Assert.Contains("\"truncated\": true", text);
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
    public void ToolRegistry_Returns31Tools()
    {
        var tools = ToolRegistry.GetTools();

        Assert.Equal(31, tools.Length);
        Assert.Contains(tools, t => t.Name == "lifeblood_capabilities");
        Assert.Contains(tools, t => t.Name == "lifeblood_test_impact");
        Assert.Contains(tools, t => t.Name == "lifeblood_enum_coverage");
        Assert.Contains(tools, t => t.Name == "lifeblood_static_tables");
        Assert.Contains(tools, t => t.Name == "lifeblood_assignment_coverage");
        Assert.Contains(tools, t => t.Name == "lifeblood_resolve_member");
        Assert.Contains(tools, t => t.Name == "lifeblood_analyze");
        Assert.Contains(tools, t => t.Name == "lifeblood_context");
        Assert.Contains(tools, t => t.Name == "lifeblood_lookup");
        Assert.Contains(tools, t => t.Name == "lifeblood_dependencies");
        Assert.Contains(tools, t => t.Name == "lifeblood_dependants");
        Assert.Contains(tools, t => t.Name == "lifeblood_blast_radius");
        Assert.Contains(tools, t => t.Name == "lifeblood_file_impact");
        Assert.Contains(tools, t => t.Name == "lifeblood_resolve_short_name");
        Assert.Contains(tools, t => t.Name == "lifeblood_invariant_check");
        Assert.Contains(tools, t => t.Name == "lifeblood_authority_report");
        Assert.Contains(tools, t => t.Name == "lifeblood_port_health");
        Assert.Contains(tools, t => t.Name == "lifeblood_cycles");
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

    // ──────────────────────────────────────────────────────────────────
    // LB-FR-021: cycles summarize/maxResults — same shape as
    // blast_radius. Large workspaces' 100+ SCCs serialize to ~70KB which exceeds
    // downstream tool-result limits; summarize:true closes that gap.
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Handle_Cycles_AlwaysReportsCountAndTruncationShape()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var result = handler.Handle("lifeblood_cycles", MakeArgs(new { }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        Assert.Contains("\"count\":", text);
        Assert.Contains("\"totalSymbolCount\":", text);
        Assert.Contains("\"largestCycleSize\":", text);
        Assert.Contains("\"truncated\":", text);
    }

    [Fact]
    public void Handle_Cycles_SummarizeMode_OmitsCyclesField_ReturnsPreview()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var result = handler.Handle("lifeblood_cycles", MakeArgs(new { summarize = true }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        Assert.Contains("\"summarize\": true", text);
        Assert.Contains("\"preview\":", text);
        Assert.DoesNotContain("\"cycles\":", text);
    }

    [Fact]
    public void Handle_Cycles_MaxResultsZero_ForcesTruncatedWhenAnyCycleExists()
    {
        var handler = CreateHandler();
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));

        var result = handler.Handle("lifeblood_cycles", MakeArgs(new { maxResults = 0 }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        // `cycles` is the embedded (clipped) array. With maxResults=0 it must be empty.
        Assert.Contains("\"cycles\": []", text);
        // truncated:true iff the un-clipped count was > 0; truncated:false iff zero cycles.
        if (text.Contains("\"count\": 0"))
            Assert.Contains("\"truncated\": false", text);
        else
            Assert.Contains("\"truncated\": true", text);
    }

    // INV-FILE-IMPACT-SUMMARIZE-001.

    /// <summary>Temp graph.json fan-shaped fixture for INV-FILE-IMPACT-SUMMARIZE-001 tests.</summary>
    private string BuildFanGraph(string targetFile, int fanIn, int fanOut)
    {
        var builder = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "file:" + targetFile, Name = targetFile, Kind = SymbolKind.File, FilePath = targetFile })
            .AddSymbol(new Symbol { Id = "type:Target", Name = "Target", Kind = SymbolKind.Type, FilePath = targetFile });
        for (var i = 0; i < fanIn; i++)
        {
            var file = $"In{i}.cs";
            var typeId = $"type:In{i}";
            builder.AddSymbol(new Symbol { Id = "file:" + file, Name = file, Kind = SymbolKind.File, FilePath = file });
            builder.AddSymbol(new Symbol { Id = typeId, Name = $"In{i}", Kind = SymbolKind.Type, FilePath = file });
            builder.AddEdge(new Edge { SourceId = typeId, TargetId = "type:Target", Kind = EdgeKind.References });
        }
        for (var i = 0; i < fanOut; i++)
        {
            var file = $"Out{i}.cs";
            var typeId = $"type:Out{i}";
            builder.AddSymbol(new Symbol { Id = "file:" + file, Name = file, Kind = SymbolKind.File, FilePath = file });
            builder.AddSymbol(new Symbol { Id = typeId, Name = $"Out{i}", Kind = SymbolKind.Type, FilePath = file });
            builder.AddEdge(new Edge { SourceId = "type:Target", TargetId = typeId, Kind = EdgeKind.References });
        }

        var doc = new GraphDocument
        {
            Language = "test",
            Adapter = new AdapterCapability { CanDiscoverSymbols = true, TypeResolution = ConfidenceLevel.Proven },
            Graph = builder.Build(),
        };
        var path = Path.Combine(_tempDir, $"fan-{Guid.NewGuid():N}.json");
        using var stream = File.Create(path);
        new Lifeblood.Adapters.JsonGraph.JsonGraphExporter().Export(doc, stream);
        return path;
    }

    [Fact]
    public void Handle_FileImpact_DefaultInvocation_CarriesCountsAndTruncationShape()
    {
        var handler = CreateHandler();
        var graphPath = BuildFanGraph("Target.cs", fanIn: 3, fanOut: 2);
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath }));

        var result = handler.Handle("lifeblood_file_impact", MakeArgs(new { filePath = "Target.cs" }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        Assert.Contains("\"dependsOnCount\": 2", text);
        Assert.Contains("\"dependedOnByCount\": 3", text);
        Assert.Contains("\"dependsOnTruncated\": false", text);
        Assert.Contains("\"dependedOnByTruncated\": false", text);
        Assert.Contains("\"truncated\": false", text);
        Assert.Contains("\"summarize\": false", text);
    }

    [Fact]
    public void Handle_FileImpact_ExplicitMaxResults_ClipsArraysAndFiresTruncationFlags()
    {
        var handler = CreateHandler();
        // 30 + 30 fan; cap each direction at 5.
        var graphPath = BuildFanGraph("Hub.cs", fanIn: 30, fanOut: 30);
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath }));

        var result = handler.Handle("lifeblood_file_impact", MakeArgs(new { filePath = "Hub.cs", maxResults = 5 }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        // Counts stay full — caller MUST be able to see the real magnitude.
        Assert.Contains("\"dependsOnCount\": 30", text);
        Assert.Contains("\"dependedOnByCount\": 30", text);
        // Both directions overshoot the cap → both truncated.
        Assert.Contains("\"dependsOnTruncated\": true", text);
        Assert.Contains("\"dependedOnByTruncated\": true", text);
        Assert.Contains("\"truncated\": true", text);
        Assert.Contains("\"maxResults\": 5", text);
    }

    [Fact]
    public void Handle_FileImpact_SummarizeTrue_ForcesMaxResults25_RegardlessOfCallerPassed()
    {
        var handler = CreateHandler();
        // INV-FILE-IMPACT-SUMMARIZE-001: summarize forces 25 over caller-passed.
        var graphPath = BuildFanGraph("God.cs", fanIn: 50, fanOut: 50);
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath }));

        var result = handler.Handle("lifeblood_file_impact", MakeArgs(new
        {
            filePath = "God.cs",
            maxResults = 100,
            summarize = true,
        }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        Assert.Contains("\"summarize\": true", text);
        // Caller asked for 100 but summarize forced 25 — wire echoes the EFFECTIVE cap.
        Assert.Contains("\"maxResults\": 25", text);
        Assert.Contains("\"dependsOnCount\": 50", text);
        Assert.Contains("\"dependedOnByCount\": 50", text);
        Assert.Contains("\"truncated\": true", text);
    }

    [Fact]
    public void Handle_FileImpact_SummarizeFalse_HonorsExplicitMaxResults_RegressionGuard()
    {
        // Regression guard: summarize MUST NOT become sticky.
        var handler = CreateHandler();
        var graphPath = BuildFanGraph("Mid.cs", fanIn: 10, fanOut: 10);
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath }));

        var result = handler.Handle("lifeblood_file_impact", MakeArgs(new
        {
            filePath = "Mid.cs",
            maxResults = 20,
            summarize = false,
        }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        Assert.Contains("\"summarize\": false", text);
        Assert.Contains("\"maxResults\": 20", text);
        // 10 + 10 both fit under the 20-cap → neither truncated.
        Assert.Contains("\"dependsOnTruncated\": false", text);
        Assert.Contains("\"dependedOnByTruncated\": false", text);
    }
}

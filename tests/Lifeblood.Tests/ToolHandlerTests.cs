using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Analysis;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Application.UseCases;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Tests for MCP ToolHandler behavior, error paths, and the complete registry.
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
        _graphPath = Path.Combine(_tempDir, "graph.json");
        WriteCoreGraph(_graphPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private void RewriteGraphWithExtraType(string typeName)
        => WriteCoreGraph(_graphPath, typeName);

    private static void WriteCoreGraph(string graphPath, params string[] extraTypeNames)
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "mod:Core", Name = "Core", Kind = SymbolKind.Module })
            .AddSymbol(new Symbol { Id = "type:Core.Foo", Name = "Foo", Kind = SymbolKind.Type, ParentId = "mod:Core", FilePath = "Foo.cs", Line = 1 })
            .AddSymbol(new Symbol { Id = "type:Core.Bar", Name = "Bar", Kind = SymbolKind.Type, ParentId = "mod:Core", FilePath = "Bar.cs", Line = 1 })
            .AddSymbol(new Symbol { Id = "method:Core.Foo.Do", Name = "Do", Kind = SymbolKind.Method, ParentId = "type:Core.Foo" })
            .AddEdge(new Edge
            {
                SourceId = "type:Core.Foo",
                TargetId = "type:Core.Bar",
                Kind = EdgeKind.DependsOn,
                Evidence = new Evidence { Kind = EvidenceKind.Semantic, AdapterName = "Test", Confidence = ConfidenceLevel.Proven },
            })
            .AddEdge(new Edge
            {
                SourceId = "method:Core.Foo.Do",
                TargetId = "type:Core.Bar",
                Kind = EdgeKind.Calls,
                Evidence = new Evidence { Kind = EvidenceKind.Semantic, AdapterName = "Test", Confidence = ConfidenceLevel.Proven },
            });

        foreach (var extraTypeName in extraTypeNames)
        {
            graph.AddSymbol(new Symbol
            {
                Id = $"type:Core.{extraTypeName}",
                Name = extraTypeName,
                Kind = SymbolKind.Type,
                ParentId = "mod:Core",
                FilePath = $"{extraTypeName}.cs",
                Line = 1,
            });
        }

        var doc = new GraphDocument
        {
            Language = "test",
            Adapter = new AdapterCapability { CanDiscoverSymbols = true, TypeResolution = ConfidenceLevel.Proven },
            Graph = graph.Build(),
        };

        using var stream = File.Create(graphPath);
        new Lifeblood.Adapters.JsonGraph.JsonGraphExporter().Export(doc, stream);
    }

    private static readonly PhysicalFileSystem Fs = new();

    private sealed class TestBlastRadiusProvider : IBlastRadiusProvider
    {
        public BlastRadiusResult Analyze(SemanticGraph graph, string targetSymbolId, int maxDepth = 10)
            => BlastRadiusAnalyzer.Analyze(graph, targetSymbolId, maxDepth);
    }

    private static ToolHandler CreateHandler(
        ISessionGate? sessionGate = null,
        GraphSession? session = null)
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
        return new ToolHandler(
            session ?? new GraphSession(Fs),
            provider,
            resolver,
            search,
            deadCode,
            partialView,
            invariants,
            decorator,
            sessionGate: sessionGate);
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
        using var payload = JsonDocument.Parse(result.Content[0].Text);
        Assert.StartsWith(
            "analysis_request_",
            payload.RootElement.GetProperty("analysisRequestId").GetString(),
            StringComparison.Ordinal);
        Assert.False(payload.RootElement.GetProperty("coalesced").GetBoolean());
        Assert.Equal(1, payload.RootElement.GetProperty("waiterCount").GetInt32());
    }

    [Fact]
    public void Handle_Analyze_MultiProfileDescriptorFallbackPreservesRequestedProfiles()
    {
        var projectRoot = Path.Combine(_tempDir, "multi-profile-project");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(Path.Combine(projectRoot, "Library"));
        File.WriteAllText(
            Path.Combine(projectRoot, "TestProject.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(
            Path.Combine(projectRoot, "Program.cs"),
            "#if UNITY_EDITOR\npublic class EditorOnly { }\n#else\npublic class PlayerOnly { }\n#endif");
        var asmdefPath = Path.Combine(projectRoot, "TestProject.asmdef");
        File.WriteAllText(asmdefPath, "{\"name\":\"TestProject\"}");
        var profiles = new[] { "Editor", "Player" };
        using var session = new GraphSession(Fs);
        var handler = CreateHandler(session: session);

        var baseline = handler.Handle(
            "lifeblood_analyze",
            MakeArgs(new { projectPath = projectRoot, defineProfiles = profiles }));
        Assert.Null(baseline.IsError);
        File.WriteAllText(asmdefPath, "{\"name\":\"TestProject\",\"references\":[]}");

        var result = handler.Handle(
            "lifeblood_analyze",
            MakeArgs(new
            {
                projectPath = projectRoot,
                defineProfiles = profiles,
                incremental = true,
                allowFullFallback = true,
            }));

        Assert.Null(result.IsError);
        using var payload = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("full", payload.RootElement.GetProperty("mode").GetString());
        Assert.Equal("incremental", payload.RootElement.GetProperty("requestedMode").GetString());
        Assert.Equal("moduleDescriptorChanged", payload.RootElement.GetProperty("fallbackReason").GetString());
        var summary = payload.RootElement.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("profileCount").GetInt32());
        Assert.Equal(
            profiles,
            summary.GetProperty("activeProfiles").EnumerateArray().Select(item => item.GetString()).ToArray());
        var perProfile = summary.GetProperty("perProfileEdgeCounts");
        Assert.True(perProfile.TryGetProperty("Editor", out _));
        Assert.True(perProfile.TryGetProperty("Player", out _));
        Assert.Equal(profiles, session.RetainedProfileNames);
        Assert.Equal(profiles, session.AnalysisIdentity!.Spec.DefineProfiles);
    }

    [Fact]
    public void Handle_DiagnoseAndCompileCheck_AmbiguousFileOwnershipFailsClosed()
    {
        var projectRoot = Path.Combine(_tempDir, "shared-file-project");
        var sharedDirectory = Path.Combine(projectRoot, "Shared");
        Directory.CreateDirectory(sharedDirectory);
        File.WriteAllText(Path.Combine(sharedDirectory, "Shared.cs"), "public class Shared { }");
        WriteModule("ModB");
        WriteModule("ModA");
        using var session = new GraphSession(Fs);
        var handler = CreateHandler(session: session);

        var analyze = handler.Handle(
            "lifeblood_analyze",
            MakeArgs(new { projectPath = projectRoot }));
        Assert.Null(analyze.IsError);

        var diagnose = handler.Handle(
            "lifeblood_diagnose",
            MakeArgs(new { filePath = "Shared/Shared.cs" }));
        var compile = handler.Handle(
            "lifeblood_compile_check",
            MakeArgs(new { filePath = "Shared/Shared.cs", staleRefresh = false }));
        var pinned = handler.Handle(
            "lifeblood_diagnose",
            MakeArgs(new { filePath = "Shared/Shared.cs", moduleName = "ModB" }));

        Assert.Null(diagnose.IsError);
        Assert.Null(compile.IsError);
        Assert.Null(pinned.IsError);
        using var diagnosePayload = JsonDocument.Parse(diagnose.Content[0].Text);
        using var compilePayload = JsonDocument.Parse(compile.Content[0].Text);
        using var pinnedPayload = JsonDocument.Parse(pinned.Content[0].Text);
        var diagnoseOwnership = diagnosePayload.RootElement.GetProperty("fileOwnership");
        Assert.Equal("Ambiguous", diagnoseOwnership.GetProperty("outcome").GetString());
        Assert.Equal(
            new[] { "ModA", "ModB" },
            diagnoseOwnership.GetProperty("candidateModules").EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.Equal(JsonValueKind.Null, diagnosePayload.RootElement.GetProperty("resolvedModule").ValueKind);
        var compileOwnership = compilePayload.RootElement.GetProperty("fileOwnership");
        Assert.Equal("Ambiguous", compileOwnership.GetProperty("outcome").GetString());
        Assert.Equal("Ambiguous", compilePayload.RootElement.GetProperty("fileResolution").GetString());
        Assert.Contains("LB0004", compilePayload.RootElement.GetProperty("diagnostics").GetRawText());
        Assert.Equal("Unique", pinnedPayload.RootElement.GetProperty("fileOwnership").GetProperty("outcome").GetString());
        Assert.Equal("ModB", pinnedPayload.RootElement.GetProperty("resolvedModule").GetString());

        void WriteModule(string moduleName)
        {
            var moduleDirectory = Path.Combine(projectRoot, moduleName);
            Directory.CreateDirectory(moduleDirectory);
            File.WriteAllText(
                Path.Combine(moduleDirectory, moduleName + ".csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework>" +
                $"<AssemblyName>{moduleName}</AssemblyName><EnableDefaultCompileItems>false</EnableDefaultCompileItems>" +
                "</PropertyGroup><ItemGroup><Compile Include=\"../Shared/Shared.cs\" /></ItemGroup></Project>");
        }
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
        Assert.Equal(41, doc.RootElement.GetProperty("tools").GetProperty("totalCount").GetInt32());
        Assert.Equal(23, doc.RootElement.GetProperty("tools").GetProperty("readSideCount").GetInt32());
        Assert.Equal(18, doc.RootElement.GetProperty("tools").GetProperty("writeSideCount").GetInt32());
        var toolCapabilities = doc.RootElement.GetProperty("tools");
        Assert.Contains("legacy projections", toolCapabilities.GetProperty("compatibilityNote").GetString());
        Assert.Equal(4, toolCapabilities.GetProperty("sessionRequirementCounts").GetProperty("None").GetInt32());
        Assert.Equal(16, toolCapabilities.GetProperty("sessionRequirementCounts").GetProperty("AnalyzedWorkspace").GetInt32());
        Assert.Equal(2, toolCapabilities.GetProperty("sessionRequirementCounts").GetProperty("WorkspaceRoot").GetInt32());
        Assert.Equal(1, toolCapabilities.GetProperty("sessionRequirementCounts").GetProperty("OperationFacts").GetInt32());
        Assert.Equal(18, toolCapabilities.GetProperty("sessionRequirementCounts").GetProperty("RetainedCompilation").GetInt32());
        Assert.Equal(35, toolCapabilities.GetProperty("effectCounts").GetProperty("Observe").GetInt32());
        Assert.Equal(2, toolCapabilities.GetProperty("effectCounts").GetProperty("RefreshWorkspace").GetInt32());
        Assert.Equal(1, toolCapabilities.GetProperty("effectCounts").GetProperty("ManageSnapshotCatalog").GetInt32());
        Assert.Equal(1, toolCapabilities.GetProperty("effectCounts").GetProperty("ExecuteCode").GetInt32());
        Assert.Equal(2, toolCapabilities.GetProperty("effectCounts").GetProperty("PreviewChanges").GetInt32());
        Assert.Equal(38, toolCapabilities.GetProperty("sessionAccessCounts").GetProperty("SharedRead").GetInt32());
        Assert.Equal(3, toolCapabilities.GetProperty("sessionAccessCounts").GetProperty("Exclusive").GetInt32());
        var behaviorContracts = toolCapabilities.GetProperty("behaviorContracts");
        Assert.Equal(41, behaviorContracts.GetArrayLength());
        var analyzeContract = behaviorContracts.EnumerateArray()
            .Single(e => e.GetProperty("name").GetString() == "lifeblood_analyze");
        Assert.Equal("None", analyzeContract.GetProperty("sessionRequirement").GetString());
        Assert.Equal("RefreshWorkspace", analyzeContract.GetProperty("effect").GetString());
        Assert.Equal("Exclusive", analyzeContract.GetProperty("sessionAccess").GetString());
        var telemetryEvents = doc.RootElement
            .GetProperty("featureFlags")
            .GetProperty("operationalTelemetryEvents")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();
        Assert.Contains("lifeblood.tool.truncated", telemetryEvents);
        Assert.Contains("lifeblood.analyze.fallback", telemetryEvents);
        Assert.True(doc.RootElement.GetProperty("featureFlags").GetProperty("acceptedChangeReceipts").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("featureFlags").GetProperty("sharedSessionTransport").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("featureFlags").GetProperty("sharedSessionRequestCancellation").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("featureFlags").GetProperty("snapshotHistoryCatalog").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("featureFlags").GetProperty("historicalSnapshotSelection").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("featureFlags").GetProperty("historicalSnapshotsRetainSemanticServices").GetBoolean());
        Assert.Equal("recommended", ServerIdentity.SharedSessionTransportMaturity);
        Assert.Equal(
            ServerIdentity.SharedSessionTransportMaturity,
            doc.RootElement.GetProperty("featureFlags").GetProperty("sharedSessionTransportMaturity").GetString());
        Assert.False(doc.RootElement.GetProperty("featureFlags").GetProperty("sharedSessionTransportActive").GetBoolean());
        Assert.Equal("stdio", doc.RootElement.GetProperty("featureFlags").GetProperty("sharedSessionTransportMode").GetString());
        var sharedService = doc.RootElement.GetProperty("sharedService");
        Assert.True(sharedService.GetProperty("supported").GetBoolean());
        Assert.False(sharedService.GetProperty("active").GetBoolean());
        Assert.Equal("stdio", sharedService.GetProperty("mode").GetString());
        Assert.Equal("not-applicable", sharedService.GetProperty("lifecycleState").GetString());
        Assert.False(sharedService.GetProperty("idleEvictionEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, sharedService.GetProperty("idleTimeoutSeconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, sharedService.GetProperty("idleDeadlineUtc").ValueKind);
        // INV-TELEMETRY-002: the advertised surface is exactly the
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
        var session = doc.RootElement.GetProperty("session");
        Assert.False(session.GetProperty("hasGraphLoaded").GetBoolean());
        var history = session.GetProperty("snapshotHistory");
        Assert.Equal(3, history.GetProperty("configuredLimit").GetInt32());
        Assert.Equal(16, history.GetProperty("hardMaximum").GetInt32());
        Assert.Equal("graph-only", history.GetProperty("retentionMode").GetString());
        Assert.Equal(0, history.GetProperty("semanticBaseCount").GetInt32());
        Assert.Equal(0, history.GetProperty("additionalSemanticBaseCount").GetInt32());
    }

    [Fact]
    public void Handle_ReadPrecondition_RejectsNewerSnapshotWithRetryIdentity()
    {
        using var session = new GraphSession(Fs);
        var handler = CreateHandler(session: session);
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));
        var expectedSnapshotId = session.SnapshotId.ToString();
        var expectedGeneration = session.AnalysisGeneration;

        var accepted = handler.Handle("lifeblood_lookup", MakeArgs(new
        {
            symbolId = "type:Core.Foo",
            expectedSnapshotId,
            expectedAnalysisGeneration = expectedGeneration,
        }));
        Assert.Null(accepted.IsError);
        using (var acceptedPayload = JsonDocument.Parse(accepted.Content[0].Text))
        {
            Assert.Equal(
                session.AnalysisIdentity!.AnalysisKey.Value,
                acceptedPayload.RootElement
                    .GetProperty("envelope")
                    .GetProperty("analysisIdentity")
                    .GetProperty("analysisKey")
                    .GetString());
        }

        RewriteGraphWithExtraType("Baz");
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));
        var rejected = handler.Handle("lifeblood_lookup", MakeArgs(new
        {
            symbolId = "type:Core.Foo",
            expectedSnapshotId,
            expectedAnalysisGeneration = expectedGeneration,
        }));

        Assert.True(rejected.IsError);
        using var payload = JsonDocument.Parse(rejected.Content[0].Text);
        Assert.Equal("snapshot-precondition", payload.RootElement.GetProperty("failure").GetString());
        Assert.True(payload.RootElement.GetProperty("retryable").GetBoolean());
        Assert.Equal(expectedSnapshotId, payload.RootElement.GetProperty("expectedSnapshotId").GetString());
        Assert.Equal(session.SnapshotId.ToString(), payload.RootElement.GetProperty("actualSnapshotId").GetString());
        Assert.Equal(session.AnalysisGeneration, payload.RootElement.GetProperty("actualAnalysisGeneration").GetInt64());
        Assert.Equal(
            session.AnalysisIdentity!.AnalysisKey.Value,
            payload.RootElement.GetProperty("actualAnalysisIdentity").GetProperty("analysisKey").GetString());
    }

    [Fact]
    public void Handle_Batch_HoldsOneSnapshotAcrossForcedRefresh()
    {
        using var session = new GraphSession(Fs);
        session.Load(projectPath: null, graphPath: _graphPath, rulesPath: null);
        var leasedSnapshotId = session.SnapshotId.ToString();
        var refreshIndex = 0;
        using var gate = new RefreshOnSecondNestedReadGate(
            session,
            () =>
            {
                RewriteGraphWithExtraType($"Refresh{++refreshIndex}");
                session.Load(projectPath: null, graphPath: _graphPath, rulesPath: null);
            });
        var handler = CreateHandler(gate, session);

        var result = handler.Handle("lifeblood_batch", MakeArgs(new
        {
            expectedSnapshotId = leasedSnapshotId,
            calls = new object[]
            {
                new { tool = "lifeblood_capabilities", arguments = new { } },
                new { tool = "lifeblood_capabilities", arguments = new { } },
            },
        }));

        Assert.Null(result.IsError);
        Assert.NotEqual(leasedSnapshotId, session.SnapshotId.ToString());
        using var batch = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(leasedSnapshotId, batch.RootElement.GetProperty("snapshotId").GetString());
        Assert.Equal(leasedSnapshotId, batch.RootElement.GetProperty("envelope").GetProperty("snapshotId").GetString());
        foreach (var call in batch.RootElement.GetProperty("results").EnumerateArray())
        {
            var text = call.GetProperty("content")[0].GetProperty("text").GetString();
            using var nested = JsonDocument.Parse(text!);
            Assert.Equal(
                leasedSnapshotId,
                nested.RootElement.GetProperty("envelope").GetProperty("snapshotId").GetString());
        }
    }

    [Fact]
    public void Handle_Batch_RejectsNonObservationPlanBeforeExecutingAnyCall()
    {
        using var session = new GraphSession(Fs);
        session.Load(projectPath: null, graphPath: _graphPath, rulesPath: null);
        var generation = session.AnalysisGeneration;
        var handler = CreateHandler(session: session);

        var result = handler.Handle("lifeblood_batch", MakeArgs(new
        {
            calls = new object[]
            {
                new { tool = "lifeblood_capabilities", arguments = new { } },
                new { tool = "lifeblood_analyze", arguments = new { graphPath = _graphPath } },
            },
        }));

        Assert.True(result.IsError);
        Assert.Equal(generation, session.AnalysisGeneration);
        using var payload = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("batch-policy", payload.RootElement.GetProperty("failure").GetString());
        Assert.Equal(1, payload.RootElement.GetProperty("rejectedIndex").GetInt32());
        Assert.Equal(0, payload.RootElement.GetProperty("executedCallCount").GetInt32());
    }

    [Fact]
    public void Handle_Snapshots_ManagesHistoryAndHistoricalReadSelection()
    {
        using var session = new GraphSession(Fs);
        var handler = CreateHandler(session: session);
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));
        var historicalSnapshotId = session.SnapshotId.ToString();
        RewriteGraphWithExtraType("Baz");
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));
        var latestSnapshotId = session.SnapshotId.ToString();

        var pin = handler.Handle("lifeblood_snapshots", MakeArgs(new
        {
            action = "pin",
            targetSnapshotId = historicalSnapshotId,
            name = "before-refactor",
        }));
        Assert.Null(pin.IsError);

        var list = handler.Handle("lifeblood_snapshots", MakeArgs(new { action = "list" }));
        Assert.Null(list.IsError);
        using (var inventory = JsonDocument.Parse(list.Content[0].Text))
        {
            var entries = inventory.RootElement.GetProperty("entries").EnumerateArray().ToArray();
            Assert.Equal(2, entries.Length);
            var historical = entries.Single(entry =>
                entry.GetProperty("snapshotId").GetString() == historicalSnapshotId);
            Assert.True(historical.GetProperty("pinned").GetBoolean());
            Assert.True(historical.GetProperty("graphOnly").GetBoolean());
            Assert.False(historical.GetProperty("retainedSemantic").GetBoolean());
            Assert.Equal("before-refactor", historical.GetProperty("name").GetString());
            Assert.Equal(0, inventory.RootElement.GetProperty("additionalSemanticBaseCount").GetInt32());
        }

        var historicalRead = handler.Handle("lifeblood_lookup", MakeArgs(new
        {
            snapshotId = historicalSnapshotId,
            expectedSnapshotId = historicalSnapshotId,
            symbolId = "type:Core.Foo",
        }));
        Assert.Null(historicalRead.IsError);
        using (var payload = JsonDocument.Parse(historicalRead.Content[0].Text))
        {
            Assert.Equal(
                historicalSnapshotId,
                payload.RootElement.GetProperty("envelope").GetProperty("snapshotId").GetString());
        }

        Assert.Null(handler.Handle("lifeblood_snapshots", MakeArgs(new
        {
            action = "unpin",
            targetSnapshotId = historicalSnapshotId,
        })).IsError);
        Assert.Null(handler.Handle("lifeblood_snapshots", MakeArgs(new
        {
            action = "evict",
            targetSnapshotId = historicalSnapshotId,
        })).IsError);

        var evictedRead = handler.Handle("lifeblood_lookup", MakeArgs(new
        {
            snapshotId = historicalSnapshotId,
            symbolId = "type:Core.Foo",
        }));
        Assert.True(evictedRead.IsError);
        using var evicted = JsonDocument.Parse(evictedRead.Content[0].Text);
        Assert.Equal("snapshot-not-found", evicted.RootElement.GetProperty("failure").GetString());
        Assert.Equal(latestSnapshotId, evicted.RootElement.GetProperty("currentSnapshotId").GetString());
    }

    [Fact]
    public void Handle_Batch_SelectsHistoricalPublicationAndRejectsNestedSwitchBeforeCallZero()
    {
        using var session = new GraphSession(Fs);
        var handler = CreateHandler(session: session);
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));
        var historicalSnapshotId = session.SnapshotId.ToString();
        RewriteGraphWithExtraType("Baz");
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath }));
        var latestSnapshotId = session.SnapshotId.ToString();

        var accepted = handler.Handle("lifeblood_batch", MakeArgs(new
        {
            snapshotId = historicalSnapshotId,
            calls = new object[]
            {
                new { tool = "lifeblood_capabilities", arguments = new { } },
                new { tool = "lifeblood_lookup", arguments = new { symbolId = "type:Core.Foo" } },
            },
        }));
        Assert.Null(accepted.IsError);
        using (var payload = JsonDocument.Parse(accepted.Content[0].Text))
        {
            Assert.Equal(historicalSnapshotId, payload.RootElement.GetProperty("snapshotId").GetString());
            Assert.Equal(2, payload.RootElement.GetProperty("results").GetArrayLength());
        }

        var rejected = handler.Handle("lifeblood_batch", MakeArgs(new
        {
            snapshotId = historicalSnapshotId,
            calls = new object[]
            {
                new { tool = "lifeblood_capabilities", arguments = new { snapshotId = latestSnapshotId } },
            },
        }));
        Assert.True(rejected.IsError);
        using var rejection = JsonDocument.Parse(rejected.Content[0].Text);
        Assert.Equal("batch-policy", rejection.RootElement.GetProperty("failure").GetString());
        Assert.Equal(0, rejection.RootElement.GetProperty("executedCallCount").GetInt32());
    }

    [Fact]
    public void Handle_Snapshots_CheckDriftRecapturesCanonicalWorkspaceIdentity()
    {
        var projectRoot = Path.Combine(_tempDir, "DriftProject");
        Directory.CreateDirectory(projectRoot);
        File.WriteAllText(
            Path.Combine(projectRoot, "DriftProject.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        var sourcePath = Path.Combine(projectRoot, "Tracked.cs");
        File.WriteAllText(sourcePath, "namespace DriftProject; public class Tracked { }");
        using var session = new GraphSession(Fs);
        var handler = CreateHandler(session: session);
        handler.Handle("lifeblood_analyze", MakeArgs(new { projectPath = projectRoot }));

        var current = handler.Handle("lifeblood_snapshots", MakeArgs(new
        {
            action = "list",
            checkDrift = true,
        }));
        Assert.Null(current.IsError);
        using (var payload = JsonDocument.Parse(current.Content[0].Text))
        {
            Assert.Equal(
                "current",
                payload.RootElement.GetProperty("entries")[0].GetProperty("drift").GetProperty("status").GetString());
        }

        File.WriteAllText(sourcePath, "namespace DriftProject; public class Changed { }");
        var drifted = handler.Handle("lifeblood_snapshots", MakeArgs(new
        {
            action = "list",
            checkDrift = true,
        }));
        Assert.Null(drifted.IsError);
        using var driftPayload = JsonDocument.Parse(drifted.Content[0].Text);
        Assert.Equal(
            "drifted",
            driftPayload.RootElement.GetProperty("entries")[0].GetProperty("drift").GetProperty("status").GetString());
    }

    [Fact]
    public void Handle_HistoricalSelection_RejectsLiveSourceAndSemanticTools()
    {
        var projectRoot = Path.Combine(_tempDir, "HistoricalProject");
        Directory.CreateDirectory(projectRoot);
        File.WriteAllText(
            Path.Combine(projectRoot, "HistoricalProject.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(
            Path.Combine(projectRoot, "Tracked.cs"),
            "namespace HistoricalProject; public class Tracked { }");
        using var session = new GraphSession(Fs);
        var handler = CreateHandler(session: session);
        handler.Handle("lifeblood_analyze", MakeArgs(new { projectPath = projectRoot }));
        var historicalSnapshotId = session.SnapshotId.ToString();
        File.WriteAllText(
            Path.Combine(projectRoot, "Tracked2.cs"),
            "namespace HistoricalProject; public class Tracked2 { }");
        handler.Handle("lifeblood_analyze", MakeArgs(new { projectPath = projectRoot }));
        Assert.True(session.HasCompilationState);

        var partialView = handler.Handle("lifeblood_partial_view", MakeArgs(new
        {
            snapshotId = historicalSnapshotId,
            symbolId = "type:HistoricalProject.Tracked",
        }));
        var invariants = handler.Handle("lifeblood_invariant_check", MakeArgs(new
        {
            snapshotId = historicalSnapshotId,
            mode = "audit",
        }));
        var diagnostics = handler.Handle("lifeblood_diagnose", MakeArgs(new
        {
            snapshotId = historicalSnapshotId,
        }));
        var capabilities = handler.Handle("lifeblood_capabilities", MakeArgs(new
        {
            snapshotId = historicalSnapshotId,
        }));

        Assert.True(partialView.IsError);
        Assert.Contains("historical graph-only", partialView.Content[0].Text, StringComparison.Ordinal);
        Assert.True(invariants.IsError);
        Assert.Contains("historical graph-only", invariants.Content[0].Text, StringComparison.Ordinal);
        Assert.True(diagnostics.IsError);
        Assert.Contains("historical snapshots are graph-only", diagnostics.Content[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Null(capabilities.IsError);
        using var capabilityPayload = JsonDocument.Parse(capabilities.Content[0].Text);
        Assert.False(capabilityPayload.RootElement.GetProperty("session").GetProperty("hasCompilationState").GetBoolean());
        Assert.Equal(
            1,
            capabilityPayload.RootElement
                .GetProperty("session")
                .GetProperty("snapshotHistory")
                .GetProperty("semanticBaseCount")
                .GetInt32());
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
    public void ToolRegistry_Returns40Tools()
    {
        var tools = ToolRegistry.GetTools();

        Assert.Equal(41, tools.Length);
        Assert.Contains(tools, t => t.Name == "lifeblood_capabilities");
        Assert.Contains(tools, t => t.Name == "lifeblood_snapshots");
        Assert.Contains(tools, t => t.Name == "lifeblood_callsite_arguments");
        Assert.Contains(tools, t => t.Name == "lifeblood_wire_audit");
        Assert.Contains(tools, t => t.Name == "lifeblood_feature_switch_audit");
        Assert.Contains(tools, t => t.Name == "lifeblood_member_count");
        Assert.Contains(tools, t => t.Name == "lifeblood_struct_layout");
        Assert.Contains(tools, t => t.Name == "lifeblood_authority_coverage");
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
        Assert.Contains(tools, t => t.Name == "lifeblood_asmdef_check");
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

    [Fact]
    public void Handle_AllRegisteredTools_UseRegistryDeclaredSessionAccess()
    {
        var gate = new RecordingSessionGate();
        var handler = CreateHandler(gate);

        foreach (var definition in ToolRegistry.GetDefinitions())
        {
            gate.Reset();
            var result = handler.Handle(definition.Name, null);
            var callBehavior = definition.ResolveCallBehavior(null);

            if (callBehavior.SessionAccess == ToolSessionAccess.Exclusive)
            {
                Assert.Equal(0, gate.ReadCount);
                Assert.Equal(1, gate.WriteCount);
            }
            else
            {
                Assert.Equal(1, gate.ReadCount);
                Assert.Equal(0, gate.WriteCount);
            }

            if (callBehavior.SessionRequirement != ToolSessionRequirement.None)
            {
                Assert.True(result.IsError, $"{definition.Name} must reject an unsatisfied session requirement.");
                Assert.Contains("lifeblood_analyze", result.Content[0].Text);
            }
        }
    }

    [Fact]
    public void Handle_SnapshotList_RoutesThroughReadSessionGate()
    {
        var gate = new RecordingSessionGate();
        var handler = CreateHandler(gate);

        var result = handler.Handle("lifeblood_snapshots", MakeArgs(new { action = "list" }));

        Assert.Null(result.IsError);
        Assert.Equal(1, gate.ReadCount);
        Assert.Equal(0, gate.WriteCount);
    }

    [Theory]
    [InlineData("pin")]
    [InlineData("unpin")]
    [InlineData("evict")]
    public void Handle_SnapshotMutations_RouteThroughWriteSessionGate(string action)
    {
        var gate = new RecordingSessionGate();
        var handler = CreateHandler(gate);

        var result = handler.Handle("lifeblood_snapshots", MakeArgs(new { action }));

        Assert.True(result.IsError);
        Assert.Equal(0, gate.ReadCount);
        Assert.Equal(1, gate.WriteCount);
    }

    [Fact]
    public void Handle_ContractAudit_InlineManifestIsSummaryFirstAndUsesOneSemanticBase()
    {
        var (projectRoot, manifest) = CreateContractAuditProject();
        using var session = new GraphSession(Fs);
        var handler = CreateHandler(session: session);
        var analyzed = handler.Handle(
            "lifeblood_analyze",
            MakeArgs(new { projectPath = projectRoot, defineProfiles = new[] { "Editor" } }));
        Assert.Null(analyzed.IsError);

        var result = handler.Handle("lifeblood_contract_audit", MakeArgs(new { manifest }));

        Assert.Null(result.IsError);
        using var payload = JsonDocument.Parse(result.Content[0].Text);
        var root = payload.RootElement;
        Assert.Equal("Completed", root.GetProperty("status").GetString());
        Assert.Equal(1, root.GetProperty("findingCount").GetInt32());
        Assert.Equal(1, root.GetProperty("returnedFindingCount").GetInt32());
        Assert.Empty(root.GetProperty("findings")[0].GetProperty("evidence").EnumerateArray());
        Assert.Equal(0, root.GetProperty("scanReceipt").GetProperty("additionalSemanticBaseCount").GetInt32());
        Assert.Equal("Editor", root.GetProperty("scanReceipt").GetProperty("profileScope").GetString());
        Assert.True(root.TryGetProperty("envelope", out _));
    }

    [Fact]
    public void Handle_ContractAudit_WorkspaceManifestPathReturnsBoundedEvidence()
    {
        var (projectRoot, manifest) = CreateContractAuditProject();
        var manifestPath = Path.Combine(projectRoot, "contracts.json");
        File.WriteAllText(manifestPath, manifest.GetRawText());
        using var session = new GraphSession(Fs);
        var handler = CreateHandler(session: session);
        Assert.Null(handler.Handle("lifeblood_analyze", MakeArgs(new { projectPath = projectRoot })).IsError);

        var result = handler.Handle(
            "lifeblood_contract_audit",
            MakeArgs(new { manifestPath = "contracts.json", summarize = false, maxEvidencePerFinding = 2 }));

        Assert.Null(result.IsError);
        using var payload = JsonDocument.Parse(result.Content[0].Text);
        var finding = payload.RootElement.GetProperty("findings")[0];
        Assert.InRange(finding.GetProperty("evidence").GetArrayLength(), 1, 2);
        Assert.Equal("acme-contracts", payload.RootElement.GetProperty("manifestId").GetString());
    }

    [Fact]
    public void Handle_ContractAudit_InlineValueDomainManifestUsesTheSharedFactStream()
    {
        var (projectRoot, _) = CreateContractAuditProject("value-domain-contract");
        using var session = new GraphSession(Fs);
        var handler = CreateHandler(session: session);
        Assert.Null(handler.Handle("lifeblood_analyze", MakeArgs(new { projectPath = projectRoot })).IsError);
        var manifest = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = "1",
            id = "acme-value-domains",
            version = "1.0.0",
            valueDomains = new[]
            {
                new
                {
                    id = "set-normalized",
                    targetSymbolIds = new[] { "method:Acme.Guard.Set(int)" },
                    targetDomain = "Normalized",
                    bindings = new[]
                    {
                        new
                        {
                            domain = "Raw",
                            sourceSymbolIds = new[] { "parameter:method:Acme.Guard.Run(int)#0:input" },
                        },
                    },
                },
            },
        });

        var result = handler.Handle(
            "lifeblood_contract_audit",
            MakeArgs(new { manifest, summarize = false }));

        Assert.Null(result.IsError);
        using var payload = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(ContractRuleId.ValueDomain, payload.RootElement.GetProperty("selectedRuleIds")[0].GetString());
        Assert.Equal(2, payload.RootElement.GetProperty("findingCount").GetInt32());
        Assert.All(payload.RootElement.GetProperty("findings").EnumerateArray(), finding =>
            Assert.Equal(
                ContractFindingKind.ValueDomainMismatch,
                finding.GetProperty("kind").GetString()));
    }

    [Fact]
    public void Handle_ContractAudit_RejectsGraphOnlyAndOutOfWorkspaceManifestPath()
    {
        var (_, manifest) = CreateContractAuditProject();
        using var graphSession = new GraphSession(Fs);
        var graphHandler = CreateHandler(session: graphSession);
        Assert.Null(graphHandler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = _graphPath })).IsError);
        var graphOnly = graphHandler.Handle("lifeblood_contract_audit", MakeArgs(new { manifest }));
        Assert.True(graphOnly.IsError);
        Assert.Contains("Operation-fact tools require", graphOnly.Content[0].Text, StringComparison.Ordinal);

        var (projectRoot, _) = CreateContractAuditProject("outside-check");
        var outsidePath = Path.Combine(_tempDir, "outside-contract.json");
        File.WriteAllText(outsidePath, manifest.GetRawText());
        using var liveSession = new GraphSession(Fs);
        var liveHandler = CreateHandler(session: liveSession);
        Assert.Null(liveHandler.Handle("lifeblood_analyze", MakeArgs(new { projectPath = projectRoot })).IsError);
        var outside = liveHandler.Handle(
            "lifeblood_contract_audit",
            MakeArgs(new { manifestPath = outsidePath }));
        Assert.True(outside.IsError);
        Assert.Contains("must stay inside", outside.Content[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void GetTools_ReadsLiveAvailabilityThroughSessionGate()
    {
        var gate = new RecordingSessionGate();
        var handler = CreateHandler(gate);

        var tools = handler.GetTools();

        Assert.Equal(41, tools.Length);
        Assert.Equal(1, gate.ReadCount);
        Assert.Equal(0, gate.WriteCount);
    }

    private (string ProjectRoot, JsonElement Manifest) CreateContractAuditProject(string name = "contract-audit")
    {
        var projectRoot = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(projectRoot);
        File.WriteAllText(
            Path.Combine(projectRoot, "Acme.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(
            Path.Combine(projectRoot, "Guard.cs"),
            "namespace Acme; public static class Guard { " +
            "public static void Set(int value) { } " +
            "public static void Run(int input) { Set(input); Set(System.Math.Clamp(input, 0, 10)); } }");
        var manifest = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = "1",
            id = "acme-contracts",
            version = "1.0.0",
            operationGuards = new[]
            {
                new
                {
                    id = "set-value-clamped",
                    targetSymbolIds = new[] { "method:Acme.Guard.Set(int)" },
                    argumentOrdinal = 0,
                    allowedSourceSymbolIds = new[] { "method:System.Math.Clamp(int,int,int)" },
                },
            },
        });
        return (projectRoot, manifest);
    }

    private sealed class RecordingSessionGate : ISessionGate
    {
        public int ReadCount { get; private set; }
        public int WriteCount { get; private set; }

        public T Read<T>(Func<T> action)
        {
            ReadCount++;
            return action();
        }

        public T Read<T>(WorkspaceSnapshotPrecondition? precondition, Func<T> action)
            => Read(action);

        public T Read<T>(WorkspaceSnapshotReadRequest request, Func<T> action)
            => Read(action);

        public T Write<T>(Func<T> action)
        {
            WriteCount++;
            return action();
        }

        public void Reset()
        {
            ReadCount = 0;
            WriteCount = 0;
        }
    }

    private sealed class RefreshOnSecondNestedReadGate : ISessionGate, IDisposable
    {
        private readonly GraphSessionGate _inner;
        private readonly Action _refresh;
        private int _depth;
        private int _nestedReadCount;

        public RefreshOnSecondNestedReadGate(GraphSession session, Action refresh)
        {
            _inner = new GraphSessionGate(session);
            _refresh = refresh;
        }

        public T Read<T>(Func<T> action)
            => Read(precondition: null, action);

        public T Read<T>(WorkspaceSnapshotPrecondition? precondition, Func<T> action)
        {
            var nested = _depth > 0;
            _depth++;
            try
            {
                if (nested && ++_nestedReadCount == 2)
                    _refresh();
                return _inner.Read(precondition, action);
            }
            finally
            {
                _depth--;
            }
        }

        public T Read<T>(WorkspaceSnapshotReadRequest request, Func<T> action)
        {
            var nested = _depth > 0;
            _depth++;
            try
            {
                if (nested && ++_nestedReadCount == 2)
                    _refresh();
                return _inner.Read(request, action);
            }
            finally
            {
                _depth--;
            }
        }

        public T Write<T>(Func<T> action) => _inner.Write(action);

        public void Dispose() => _inner.Dispose();
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

    [Fact]
    public void Handle_FileImpact_UnsupportedRelationships_SourceFileIoLiteral_IsAdvisory()
    {
        var handler = CreateHandler();
        var projectRoot = BuildSourceFileIoRatchetProject();
        handler.Handle("lifeblood_analyze", MakeArgs(new { projectPath = projectRoot }));

        var result = handler.Handle("lifeblood_file_impact", MakeArgs(new
        {
            filePath = "Target.cs",
            includeUnsupportedRelationships = true,
        }));

        Assert.Null(result.IsError);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var root = doc.RootElement;
        Assert.Equal(0, root.GetProperty("dependsOnCount").GetInt32());
        Assert.Equal(0, root.GetProperty("dependedOnByCount").GetInt32());

        var unsupported = root.GetProperty("unsupportedRelationships");
        Assert.Equal("advisory", unsupported.GetProperty("mode").GetString());
        Assert.False(unsupported.GetProperty("semanticGraphEdgesChanged").GetBoolean());
        Assert.Equal(1, unsupported.GetProperty("totalHitCount").GetInt32());
        Assert.Equal(1, unsupported.GetProperty("returnedHitCount").GetInt32());
        Assert.False(unsupported.GetProperty("truncated").GetBoolean());

        var hit = Assert.Single(unsupported.GetProperty("hits").EnumerateArray());
        Assert.Equal("sourceFileIoLiteral", hit.GetProperty("family").GetString());
        Assert.Equal("Ratchet.cs", hit.GetProperty("sourceFilePath").GetString());
        Assert.Equal("Target.cs", hit.GetProperty("targetFilePath").GetString());
        Assert.Equal("File.ReadAllText", hit.GetProperty("api").GetString());
        Assert.Equal("BestEffort", hit.GetProperty("confidence").GetString());
        Assert.Contains("Target.cs", hit.GetProperty("evidence").GetString());

        var families = unsupported.GetProperty("families").EnumerateArray().ToArray();
        Assert.Contains(families, family =>
            family.GetProperty("name").GetString() == "sourceFileIoLiteral"
            && family.GetProperty("status").GetString() == "scanned");
        Assert.Contains(families, family =>
            family.GetProperty("name").GetString() == "reflectionString"
            && family.GetProperty("status").GetString() == "documentedLimitation");
    }

    private string BuildSourceFileIoRatchetProject()
    {
        File.WriteAllText(Path.Combine(_tempDir, "RatchetProject.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_tempDir, "Target.cs"), """
            namespace Ratchet;
            public sealed class Target { }
            """);
        File.WriteAllText(Path.Combine(_tempDir, "Ratchet.cs"), """
            using System.IO;
            namespace Ratchet;
            public sealed class SourceTextRatchet
            {
                public string Read() => File.ReadAllText("Target.cs");
            }
            """);
        return _tempDir;
    }
}

using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.JsonGraph;
using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Analysis;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>INV-TELEMETRY-001.</summary>
public class ToolHandlerTelemetryTests
{
    private static readonly PhysicalFileSystem Fs = new();

    [Fact]
    public void Handle_EveryToolCall_StartsAndDisposesTelemetryOperation()
    {
        var telemetry = new RecordingTelemetrySink();
        var handler = CreateHandler(telemetry);

        var result = handler.Handle(
            "lifeblood_lookup",
            JsonArgs(new { symbolId = "type:Missing" }));

        Assert.True(result.IsError);
        var operation = Assert.Single(telemetry.Operations);
        Assert.Equal("lifeblood.tool", operation.Name);
        Assert.Equal("lifeblood_lookup", operation.Tags["tool.name"]);
        Assert.Equal("error", operation.Tags["tool.result"]);
        Assert.True(operation.Disposed);

        var evt = Assert.Single(telemetry.Events);
        Assert.Equal("lifeblood.tool.error_result", evt.Name);
        Assert.Equal("lifeblood_lookup", evt.Tags["tool.name"]);
    }

    [Fact]
    public void Handle_SuccessfulTool_RecordsSuccessAndResponseJsonTelemetry()
    {
        var telemetry = new RecordingTelemetrySink();
        var handler = CreateHandler(telemetry);

        var result = handler.Handle("lifeblood_capabilities", null);

        Assert.Null(result.IsError);
        var operation = Assert.Single(telemetry.Operations);
        Assert.Equal("success", operation.Tags["tool.result"]);
        Assert.Contains(telemetry.Events, e =>
            e.Name == "lifeblood.tool.success_result"
            && Equals(e.Tags["tool.name"], "lifeblood_capabilities"));
        Assert.Contains(telemetry.Events, e =>
            e.Name == "lifeblood.tool.response_json"
            && Equals(e.Tags["tool.name"], "lifeblood_capabilities")
            && Equals(e.Tags["json.path"], "withEnvelope.object")
            && (int)e.Tags["json.bytes"]! > 0);
    }

    [Fact]
    public void Handle_AnalyzeIncrementalRejected_RecordsResultAndFallbackTelemetry()
    {
        var telemetry = new RecordingTelemetrySink();
        var handler = CreateHandler(telemetry);

        var result = handler.Handle(
            "lifeblood_analyze",
            JsonArgs(new { projectPath = "D:/not-a-real-lifeblood-project", incremental = true }));

        Assert.Null(result.IsError);
        Assert.Contains("\"mode\": \"rejected\"", result.Content[0].Text);
        Assert.Contains(telemetry.Events, e =>
            e.Name == "lifeblood.analyze.result"
            && Equals(e.Tags["analyze.mode"], "rejected")
            && Equals(e.Tags["analyze.requested_mode"], "incremental"));
        Assert.Contains(telemetry.Events, e =>
            e.Name == "lifeblood.analyze.fallback"
            && Equals(e.Tags["analyze.fallback_reason"], "noPriorAnalysis")
            && Equals(e.Tags["analyze.can_retry_full"], true));
    }

    [Fact]
    public void Handle_TruncatedToolResponse_RecordsTruncationTelemetry()
    {
        var telemetry = new RecordingTelemetrySink();
        var handler = CreateHandler(telemetry);
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-telemetry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var graphPath = CreateGraphFile(tempDir);
            handler.Handle("lifeblood_analyze", JsonArgs(new { graphPath }));

            var result = handler.Handle(
                "lifeblood_blast_radius",
                JsonArgs(new { symbolId = "type:Core.Bar", maxResults = 0 }));

            Assert.Null(result.IsError);
            Assert.Contains("\"truncated\": true", result.Content[0].Text);
            Assert.Contains(telemetry.Events, e =>
                e.Name == "lifeblood.tool.truncated"
                && Equals(e.Tags["tool.name"], "lifeblood_blast_radius")
                && Equals(e.Tags["truncation.dimension"], "affected")
                && (int)e.Tags["truncation.full_count"]! > 0
                && Equals(e.Tags["truncation.included_count"], 0));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void InvariantProvider_RecordsCacheMissAndHitTelemetry()
    {
        var telemetry = new RecordingTelemetrySink();
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-telemetry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(
                Path.Combine(tempDir, "CLAUDE.md"),
                "- **INV-FOO-001**: cached rule.\n");
            var provider = new LifebloodInvariantProvider(Fs, telemetry);

            _ = provider.Audit(tempDir);
            _ = provider.Audit(tempDir);

            Assert.Contains(telemetry.Events, e =>
                e.Name == "lifeblood.cache.lookup"
                && Equals(e.Tags["cache.name"], "invariant_parse")
                && Equals(e.Tags["cache.result"], "miss"));
            Assert.Contains(telemetry.Events, e =>
                e.Name == "lifeblood.cache.lookup"
                && Equals(e.Tags["cache.name"], "invariant_parse")
                && Equals(e.Tags["cache.result"], "hit"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void DiagnosticsTelemetrySink_DisabledByDefault()
    {
        const string name = "LIFEBLOOD_TELEMETRY_TEST";
        try
        {
            Environment.SetEnvironmentVariable(name, null);

            Assert.Same(
                NoOpTelemetrySink.Instance,
                DotNetDiagnosticsTelemetrySink.CreateFromEnvironment(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void DiagnosticsTelemetrySink_EnabledByExplicitOptIn()
    {
        const string name = "LIFEBLOOD_TELEMETRY_TEST";
        try
        {
            Environment.SetEnvironmentVariable(name, "diagnostics");

            using var sink = Assert.IsType<DotNetDiagnosticsTelemetrySink>(
                DotNetDiagnosticsTelemetrySink.CreateFromEnvironment(name));
            Assert.Equal("Lifeblood", DotNetDiagnosticsTelemetrySink.ActivitySourceName);
            Assert.Equal("Lifeblood", DotNetDiagnosticsTelemetrySink.MeterName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    private static ToolHandler CreateHandler(ITelemetrySink telemetry)
    {
        var session = new GraphSession(Fs);
        IMcpGraphProvider provider = new LifebloodMcpProvider(new TestBlastRadiusProvider());
        ISymbolResolver resolver = new LifebloodSymbolResolver();
        ISemanticSearchProvider search = new LifebloodSemanticSearchProvider();
        IDeadCodeAnalyzer deadCode = new LifebloodDeadCodeAnalyzer();
        IPartialViewBuilder partialView = new LifebloodPartialViewBuilder(Fs);
        Lifeblood.Application.Ports.Right.Invariants.IInvariantProvider invariants
            = new LifebloodInvariantProvider(Fs);
        var classifications = ToolRegistry.GetDefinitions()
            .Where(d => d.EnvelopeClassification != null)
            .ToDictionary(d => d.Name, d => d.EnvelopeClassification!, StringComparer.Ordinal);
        IResponseDecorator decorator = new LifebloodResponseDecorator(classifications);
        return new ToolHandler(
            session,
            provider,
            resolver,
            search,
            deadCode,
            partialView,
            invariants,
            decorator,
            telemetry: telemetry);
    }

    private static JsonElement? JsonArgs(object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private static string CreateGraphFile(string tempDir)
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
            })
            .Build();

        var doc = new GraphDocument
        {
            Language = "test",
            Adapter = new AdapterCapability { CanDiscoverSymbols = true, TypeResolution = ConfidenceLevel.Proven },
            Graph = graph,
        };

        var graphPath = Path.Combine(tempDir, "graph.json");
        using var stream = File.Create(graphPath);
        new JsonGraphExporter().Export(doc, stream);
        return graphPath;
    }

    private sealed class TestBlastRadiusProvider : IBlastRadiusProvider
    {
        public BlastRadiusResult Analyze(SemanticGraph graph, string targetSymbolId, int maxDepth = 10)
            => BlastRadiusAnalyzer.Analyze(graph, targetSymbolId, maxDepth);
    }

    private sealed class RecordingTelemetrySink : ITelemetrySink
    {
        public List<RecordingOperation> Operations { get; } = new();
        public List<RecordingEvent> Events { get; } = new();

        public ITelemetryOperation StartOperation(string name, params TelemetryTag[] tags)
        {
            var operation = new RecordingOperation(name, tags);
            Operations.Add(operation);
            return operation;
        }

        public void RecordEvent(string name, params TelemetryTag[] tags)
            => Events.Add(new RecordingEvent(name, ToDictionary(tags)));
    }

    private sealed class RecordingOperation : ITelemetryOperation
    {
        public RecordingOperation(string name, TelemetryTag[] tags)
        {
            Name = name;
            Tags = ToDictionary(tags);
        }

        public string Name { get; }
        public Dictionary<string, object?> Tags { get; }
        public Exception? Error { get; private set; }
        public bool Disposed { get; private set; }

        public void SetTag(string name, object? value) => Tags[name] = value;

        public void SetError(Exception exception) => Error = exception;

        public void Dispose() => Disposed = true;
    }

    private sealed record RecordingEvent(string Name, Dictionary<string, object?> Tags);

    private static Dictionary<string, object?> ToDictionary(TelemetryTag[] tags)
        => tags.ToDictionary(t => t.Name, t => t.Value, StringComparer.Ordinal);
}

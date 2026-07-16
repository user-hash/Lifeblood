using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Analysis;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Results;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

public sealed class EvidenceDriftToolTests : IDisposable
{
    private readonly string _root;
    private readonly string _baselinePath;
    private readonly PhysicalFileSystem _fileSystem = new();
    private readonly GraphSession _session;
    private readonly ToolHandler _handler;

    public EvidenceDriftToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"lifeblood-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "EvidenceFixture.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(_root, "Code.cs"), "namespace EvidenceFixture; public sealed class Host { public int Value => 1; }");

        _session = new GraphSession(_fileSystem);
        _handler = CreateHandler(_session);
        var analyzed = _handler.Handle(
            "lifeblood_analyze",
            JsonArgs(new { projectPath = _root, readOnly = false }));
        Assert.NotEqual(true, analyzed.IsError);

        var docs = Path.Combine(_root, "docs", "code-maps");
        Directory.CreateDirectory(docs);
        _baselinePath = Path.Combine(docs, "EVIDENCE.generated.md");
        WriteBaseline(symbolOverride: null);
    }

    [Fact]
    public void Handle_CurrentThenStaleBaseline_ReturnsExplicitRefreshGuidance()
    {
        var current = Parse(_handler.Handle("lifeblood_evidence_drift", JsonArgs(new { })));

        Assert.Equal("current", current.GetProperty("verdict").GetString());
        Assert.False(current.GetProperty("refreshAnalysisRecommended").GetBoolean());
        Assert.False(current.GetProperty("refreshEvidenceRecommended").GetBoolean());
        Assert.Equal(0, current.GetProperty("additionalSemanticBaseCount").GetInt32());
        Assert.Equal(_session.SnapshotId.ToString(), current.GetProperty("snapshot").GetProperty("snapshotId").GetString());
        Assert.Equal("Evidence current.", current.GetProperty("recommendation").GetString());

        WriteBaseline(checked(_session.Graph!.Symbols.Count * 2L));
        var stale = Parse(_handler.Handle(
            "lifeblood_evidence_drift",
            JsonArgs(new { relativeTolerancePercent = 0.5 })));

        Assert.Equal("stale", stale.GetProperty("verdict").GetString());
        Assert.False(stale.GetProperty("refreshAnalysisRecommended").GetBoolean());
        Assert.True(stale.GetProperty("refreshEvidenceRecommended").GetBoolean());
        Assert.Contains(stale.GetProperty("metrics").EnumerateArray(), row =>
            row.GetProperty("metric").GetString() == "symbols"
            && row.GetProperty("status").GetString() == "stale");
    }

    [Fact]
    public void Handle_SourceDrift_FailsClosedBeforeClaimingBaselineVerdict()
    {
        File.AppendAllText(Path.Combine(_root, "Code.cs"), Environment.NewLine + "// source drift");

        var result = Parse(_handler.Handle("lifeblood_evidence_drift", JsonArgs(new { })));

        Assert.Equal("unavailable", result.GetProperty("verdict").GetString());
        Assert.Equal("current", result.GetProperty("metricVerdict").GetString());
        Assert.True(result.GetProperty("refreshAnalysisRecommended").GetBoolean());
        Assert.False(result.GetProperty("refreshEvidenceRecommended").GetBoolean());
        Assert.Equal(
            "drifted",
            result.GetProperty("snapshot").GetProperty("freshness").GetProperty("status").GetString());
    }

    [Fact]
    public void Handle_BaselinePathOutsideWorkspace_IsRejected()
    {
        var result = _handler.Handle(
            "lifeblood_evidence_drift",
            JsonArgs(new { baselinePath = Path.Combine(_root, "..", "outside.md") }));

        Assert.True(result.IsError);
    }

    public void Dispose()
    {
        _session.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void WriteBaseline(long? symbolOverride)
    {
        var graph = _session.Graph!;
        var analysis = _session.Analysis!;
        File.WriteAllText(_baselinePath, $$"""
            # Evidence

            | Metric | Value |
            |---|---:|
            | Symbols | {{symbolOverride ?? graph.Symbols.Count}} |
            | Edges | {{graph.Edges.Count}} |
            | Modules | {{analysis.Metrics.TotalModules}} |
            | Types | {{analysis.Metrics.TotalTypes}} |
            | File symbols | {{analysis.Metrics.TotalFiles}} |
            | Architecture violations | {{analysis.Violations.Length}} |
            | Cycles | {{analysis.Cycles.Length}} |
            | Unique invariants | 0 |
            | Declared occurrences | 0 |
            | Duplicate declarations | 0 |
            | Duplicate IDs | 0 |
            | Parse warnings | 0 |
            """);
    }

    private ToolHandler CreateHandler(GraphSession session)
    {
        IMcpGraphProvider provider = new LifebloodMcpProvider(new TestBlastRadiusProvider());
        ISymbolResolver resolver = new LifebloodSymbolResolver();
        ISemanticSearchProvider search = new LifebloodSemanticSearchProvider();
        IDeadCodeAnalyzer deadCode = new LifebloodDeadCodeAnalyzer();
        IPartialViewBuilder partialView = new LifebloodPartialViewBuilder(_fileSystem);
        Lifeblood.Application.Ports.Right.Invariants.IInvariantProvider invariants =
            new LifebloodInvariantProvider(_fileSystem);
        var classifications = ToolRegistry.GetDefinitions()
            .Where(definition => definition.EnvelopeClassification != null)
            .ToDictionary(
                definition => definition.Name,
                definition => definition.EnvelopeClassification!,
                StringComparer.Ordinal);
        IResponseDecorator decorator = new LifebloodResponseDecorator(classifications);
        return new ToolHandler(
            session,
            provider,
            resolver,
            search,
            deadCode,
            partialView,
            invariants,
            decorator);
    }

    private static JsonElement Parse(McpToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        var content = JsonDocument.Parse(JsonSerializer.Serialize(result.Content)).RootElement;
        var text = content[0].GetProperty("text").GetString();
        Assert.NotNull(text);
        return JsonDocument.Parse(text!).RootElement.Clone();
    }

    private static JsonElement? JsonArgs(object value)
        => JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(value));

    private sealed class TestBlastRadiusProvider : IBlastRadiusProvider
    {
        public BlastRadiusResult Analyze(
            Lifeblood.Domain.Graph.SemanticGraph graph,
            string targetSymbolId,
            int maxDepth = 10)
            => BlastRadiusAnalyzer.Analyze(graph, targetSymbolId, maxDepth);
    }
}

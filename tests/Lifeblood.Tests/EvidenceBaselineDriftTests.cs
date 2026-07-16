using Lifeblood.Analysis;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Xunit;

namespace Lifeblood.Tests;

public sealed class EvidenceBaselineDriftTests
{
    [Fact]
    public void ParseBaseline_RecognizesGeneratedEvidenceTablesAndCommitStamp()
    {
        var baseline = EvidenceBaselineDriftEvaluator.ParseBaseline("""
            | Field | Value |
            |---|---|
            | ExampleApp HEAD | `abc1234` |
            | Symbols | **12,345** |
            | Edges (Editor ∪ Player union) | **23,456** |
            | — Editor profile edges | 15,678 |
            | — Player profile edges | 14,567 |
            | Modules | **100** |
            | Types | **123** |
            | Lifeblood `File` symbols | **45** |
            | Architecture violations | **0** |
            | Cycles (SCC ≥ 2) | **138** |
            | Unique invariants | **2** |
            | Declared occurrences | **2** |
            | Duplicate declarations | **0** |
            | Duplicate IDs | **0** |
            | Parse warnings | **0** |
            """);

        Assert.Empty(baseline.ParseErrors);
        Assert.Equal("abc1234", baseline.CommitStamp);
        Assert.Equal(12_345, baseline.Values[EvidenceMetricKeys.Symbols]);
        Assert.Equal(23_456, baseline.Values[EvidenceMetricKeys.Edges]);
        Assert.Equal(15_678, baseline.Values[EvidenceMetricKeys.ProfileEdges("Editor")]);
        Assert.Equal(14_567, baseline.Values[EvidenceMetricKeys.ProfileEdges("Player")]);
        Assert.Equal(45, baseline.Values[EvidenceMetricKeys.Files]);
        Assert.Equal(2, baseline.Values[EvidenceMetricKeys.InvariantUnique]);
    }

    [Fact]
    public void CaptureLiveMetrics_ProfileEdgeCountsShareOneUnionProjection()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:B", Name = "B", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:C", Name = "C", Kind = SymbolKind.Type })
            .AddEdge(new Edge
            {
                SourceId = "type:A",
                TargetId = "type:B",
                Kind = EdgeKind.References,
                Profiles = new[] { "Editor", "Player" },
            })
            .AddEdge(new Edge
            {
                SourceId = "type:B",
                TargetId = "type:C",
                Kind = EdgeKind.Calls,
                Profiles = new[] { "Player" },
            })
            .Build();
        var analysis = new AnalysisResult
        {
            Metrics = new GraphMetrics { TotalModules = 1, TotalTypes = 3 },
        };

        var live = EvidenceBaselineDriftEvaluator.CaptureLiveMetrics(
            graph,
            analysis,
            new[] { "Editor", "Player" },
            new EvidenceInvariantMetrics(0, 0, 0, 0, 0));

        Assert.Equal(2, live.Values[EvidenceMetricKeys.Edges]);
        Assert.Equal(1, live.Values[EvidenceMetricKeys.ProfileEdges("Editor")]);
        Assert.Equal(2, live.Values[EvidenceMetricKeys.ProfileEdges("Player")]);
    }

    [Fact]
    public void Evaluate_UsesRelativeToleranceForVolumeCounts()
    {
        var live = Live(new Dictionary<string, long>
        {
            [EvidenceMetricKeys.Symbols] = 1_004,
        });
        var within = EvidenceBaselineDriftEvaluator.Evaluate(
            Baseline((EvidenceMetricKeys.Symbols, 1_000)),
            live,
            relativeTolerancePercent: 0.5);
        var stale = EvidenceBaselineDriftEvaluator.Evaluate(
            Baseline((EvidenceMetricKeys.Symbols, 1_000)),
            Live(new Dictionary<string, long> { [EvidenceMetricKeys.Symbols] = 1_006 }),
            relativeTolerancePercent: 0.5);

        Assert.Equal("current", within.Verdict);
        Assert.Equal("withinTolerance", Assert.Single(within.Metrics).Status);
        Assert.Equal(0.4, Assert.Single(within.Metrics).AbsolutePercentDrift);
        Assert.Equal("stale", stale.Verdict);
        Assert.Equal("stale", Assert.Single(stale.Metrics).Status);
    }

    [Fact]
    public void Evaluate_FlagsSafetyRegressionsAndAnyLiveParseWarning()
    {
        var live = Live(new Dictionary<string, long>
        {
            [EvidenceMetricKeys.Violations] = 1,
            [EvidenceMetricKeys.Cycles] = 4,
            [EvidenceMetricKeys.InvariantDuplicateDeclarations] = 2,
            [EvidenceMetricKeys.InvariantDuplicateIds] = 1,
            [EvidenceMetricKeys.InvariantParseWarnings] = 1,
        });
        var baseline = Baseline(
            (EvidenceMetricKeys.Violations, 0),
            (EvidenceMetricKeys.Cycles, 3),
            (EvidenceMetricKeys.InvariantDuplicateDeclarations, 1),
            (EvidenceMetricKeys.InvariantDuplicateIds, 0),
            (EvidenceMetricKeys.InvariantParseWarnings, 1));

        var result = EvidenceBaselineDriftEvaluator.Evaluate(baseline, live);

        Assert.Equal("flag", result.Verdict);
        Assert.All(result.Metrics, row => Assert.Equal("flag", row.Status));
    }

    [Fact]
    public void Evaluate_MissingRequiredMetricFailsClosed()
    {
        var result = EvidenceBaselineDriftEvaluator.Evaluate(
            Baseline(),
            Live(new Dictionary<string, long> { [EvidenceMetricKeys.Symbols] = 10 }));

        Assert.Equal("unavailable", result.Verdict);
        Assert.False(result.BaselineComplete);
        Assert.Equal(EvidenceMetricKeys.Symbols, Assert.Single(result.MissingBaselineMetrics));
        Assert.Equal("missingBaseline", Assert.Single(result.Metrics).Status);
    }

    private static EvidenceBaseline Baseline(params (string Key, long Value)[] values)
        => new(
            values.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal),
            CommitStamp: "",
            ParseErrors: Array.Empty<string>());

    private static EvidenceLiveMetrics Live(IReadOnlyDictionary<string, long> values)
        => new(values, values.Keys.ToArray(), Array.Empty<string>());
}

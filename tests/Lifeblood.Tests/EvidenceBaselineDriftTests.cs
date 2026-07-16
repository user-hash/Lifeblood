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
            | DAWG HEAD | `06077f3ac` |
            | Symbols | **88,745** |
            | Edges (Editor ∪ Player union) | **346,286** |
            | — Editor profile edges | 233,388 |
            | — Player profile edges | 162,629 |
            | Modules | **100** |
            | Types | **5,544** |
            | Lifeblood `File` symbols | **4,379** |
            | Architecture violations | **0** |
            | Cycles (SCC ≥ 2) | **138** |
            | Unique invariants | **2** |
            | Declared occurrences | **2** |
            | Duplicate declarations | **0** |
            | Duplicate IDs | **0** |
            | Parse warnings | **0** |
            """);

        Assert.Empty(baseline.ParseErrors);
        Assert.Equal("06077f3ac", baseline.CommitStamp);
        Assert.Equal(88_745, baseline.Values[EvidenceMetricKeys.Symbols]);
        Assert.Equal(346_286, baseline.Values[EvidenceMetricKeys.Edges]);
        Assert.Equal(233_388, baseline.Values[EvidenceMetricKeys.ProfileEdges("Editor")]);
        Assert.Equal(162_629, baseline.Values[EvidenceMetricKeys.ProfileEdges("Player")]);
        Assert.Equal(4_379, baseline.Values[EvidenceMetricKeys.Files]);
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

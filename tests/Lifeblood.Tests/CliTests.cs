using Lifeblood.CLI;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Rules;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Tests for CLI internals: AnalysisPipeline, RulesLoader, and argument parsing.
/// </summary>
public class CliTests : IDisposable
{
    private readonly string _tempDir;

    public CliTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-cli-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static SemanticGraph BuildTestGraph()
    {
        return new GraphBuilder()
            .AddSymbol(new Symbol { Id = "mod:App", Name = "App", Kind = SymbolKind.Module, QualifiedName = "App" })
            .AddSymbol(new Symbol { Id = "type:App.Service", Name = "Service", Kind = SymbolKind.Type, ParentId = "mod:App", QualifiedName = "App.Service" })
            .AddSymbol(new Symbol { Id = "type:App.Repo", Name = "Repo", Kind = SymbolKind.Type, ParentId = "mod:App", QualifiedName = "App.Repo" })
            .AddSymbol(new Symbol { Id = "method:App.Service.Do", Name = "Do", Kind = SymbolKind.Method, ParentId = "type:App.Service", QualifiedName = "App.Service.Do" })
            .AddEdge(new Edge
            {
                SourceId = "type:App.Service", TargetId = "type:App.Repo", Kind = EdgeKind.DependsOn,
                Evidence = new Evidence { Kind = EvidenceKind.Semantic, AdapterName = "Test", Confidence = ConfidenceLevel.Proven },
            })
            .Build();
    }

    [Fact]
    public void AnalysisPipeline_Run_ProducesMetrics()
    {
        var graph = BuildTestGraph();

        var result = AnalysisPipeline.Run(graph);

        Assert.Equal(4, result.Metrics.TotalSymbols);
        Assert.Equal(1, result.Metrics.TotalModules);
        Assert.Equal(2, result.Metrics.TotalTypes);
        Assert.Equal(0, result.Metrics.ViolationCount);
        Assert.Equal(0, result.Metrics.CycleCount);
    }

    [Fact]
    public void AnalysisPipeline_Run_WithNullRules_NoViolations()
    {
        var graph = BuildTestGraph();

        var result = AnalysisPipeline.Run(graph, null);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void AnalysisPipeline_Run_WithRules_DetectsViolations()
    {
        var graph = BuildTestGraph();
        var rules = new[]
        {
            new ArchitectureRule
            {
                Id = "test-rule",
                Source = "*.Service",
                MustNotReference = "*.Repo",
            },
        };

        var result = AnalysisPipeline.Run(graph, rules);

        Assert.NotEmpty(result.Violations);
    }

    [Fact]
    public void AnalysisPipeline_Run_ProducesCoupling()
    {
        var graph = BuildTestGraph();

        var result = AnalysisPipeline.Run(graph);

        Assert.NotEmpty(result.Coupling);
    }

    [Fact]
    public void AnalysisPipeline_Run_ProducesTiers()
    {
        var graph = BuildTestGraph();

        var result = AnalysisPipeline.Run(graph);

        Assert.NotEmpty(result.Tiers);
    }

    [Fact]
    public void AnalysisPipeline_Run_EmptyGraph_NoErrors()
    {
        var graph = new GraphBuilder().Build();

        var result = AnalysisPipeline.Run(graph);

        Assert.Equal(0, result.Metrics.TotalSymbols);
        Assert.Empty(result.Coupling);
        Assert.Empty(result.Cycles);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void AnalysisPipeline_Run_DetectsCycles()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:B", Name = "B", Kind = SymbolKind.Type })
            .AddEdge(new Edge
            {
                SourceId = "type:A", TargetId = "type:B", Kind = EdgeKind.DependsOn,
                Evidence = new Evidence { Kind = EvidenceKind.Semantic, AdapterName = "Test" },
            })
            .AddEdge(new Edge
            {
                SourceId = "type:B", TargetId = "type:A", Kind = EdgeKind.DependsOn,
                Evidence = new Evidence { Kind = EvidenceKind.Semantic, AdapterName = "Test" },
            })
            .Build();

        var result = AnalysisPipeline.Run(graph);

        Assert.NotEmpty(result.Cycles);
        Assert.True(result.Metrics.CycleCount > 0);
    }

    [Fact]
    public void RulesLoader_Load_ValidFile_ReturnsRules()
    {
        var rulesPath = Path.Combine(_tempDir, "rules.json");
        File.WriteAllText(rulesPath, """
        {
            "rules": [
                { "id": "R1", "source": "*.Domain", "mustNotReference": "*.Infrastructure" }
            ]
        }
        """);

        var rules = RulesLoader.Load(rulesPath);

        Assert.Single(rules);
        Assert.Equal("R1", rules[0].Id);
        Assert.Equal("*.Domain", rules[0].Source);
        Assert.Equal("*.Infrastructure", rules[0].MustNotReference);
    }

    [Fact]
    public void RulesLoader_Load_EmptyRules_ReturnsEmpty()
    {
        var rulesPath = Path.Combine(_tempDir, "empty-rules.json");
        File.WriteAllText(rulesPath, """{ "rules": [] }""");

        var rules = RulesLoader.Load(rulesPath);

        Assert.Empty(rules);
    }

    [Fact]
    public void RulesLoader_Load_MultipleRules()
    {
        var rulesPath = Path.Combine(_tempDir, "multi-rules.json");
        File.WriteAllText(rulesPath, """
        {
            "rules": [
                { "id": "R1", "source": "*.Domain", "mustNotReference": "*.Infra" },
                { "id": "R2", "source": "*.App", "mayOnlyReference": "*.Domain" }
            ]
        }
        """);

        var rules = RulesLoader.Load(rulesPath);

        Assert.Equal(2, rules.Length);
        Assert.Equal("R2", rules[1].Id);
        Assert.Equal("*.Domain", rules[1].MayOnlyReference);
    }

    [Fact]
    public void RulesLoader_Load_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() =>
            RulesLoader.Load(Path.Combine(_tempDir, "does-not-exist.json")));
    }
}

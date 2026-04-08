using Lifeblood.Adapters.CSharp;
using Lifeblood.Analysis;
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

    private readonly RulesLoader _rulesLoader = new(new PhysicalFileSystem());

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
                Evidence = new Evidence { Kind = EvidenceKind.Semantic, AdapterName = "Test", Confidence = ConfidenceLevel.Proven },
            })
            .AddEdge(new Edge
            {
                SourceId = "type:B", TargetId = "type:A", Kind = EdgeKind.DependsOn,
                Evidence = new Evidence { Kind = EvidenceKind.Semantic, AdapterName = "Test", Confidence = ConfidenceLevel.Proven },
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

        var rules = _rulesLoader.LoadRules(rulesPath);

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

        var rules = _rulesLoader.LoadRules(rulesPath);

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

        var rules = _rulesLoader.LoadRules(rulesPath);

        Assert.Equal(2, rules.Length);
        Assert.Equal("R2", rules[1].Id);
        Assert.Equal("*.Domain", rules[1].MayOnlyReference);
    }

    [Fact]
    public void RulesLoader_Load_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() =>
            _rulesLoader.LoadRules(Path.Combine(_tempDir, "does-not-exist.json")));
    }

    // ── Built-in rule pack tests ──

    [Theory]
    [InlineData("hexagonal")]
    [InlineData("clean-architecture")]
    [InlineData("lifeblood")]
    public void RulePacks_ResolveBuiltIn_KnownPack_ReturnsRules(string packName)
    {
        var rules = RulePacks.ResolveBuiltIn(packName);

        Assert.NotNull(rules);
        Assert.NotEmpty(rules);
        Assert.All(rules, r => Assert.False(string.IsNullOrEmpty(r.Id)));
    }

    [Fact]
    public void RulePacks_ResolveBuiltIn_UnknownPack_ReturnsNull()
    {
        var rules = RulePacks.ResolveBuiltIn("not-a-real-pack");

        Assert.Null(rules);
    }

    [Fact]
    public void RulePacks_BuiltIn_ContainsAllThreePacks()
    {
        Assert.Contains("hexagonal", RulePacks.BuiltIn);
        Assert.Contains("clean-architecture", RulePacks.BuiltIn);
        Assert.Contains("lifeblood", RulePacks.BuiltIn);
    }

    [Theory]
    [InlineData("hexagonal")]
    [InlineData("clean-architecture")]
    [InlineData("lifeblood")]
    public void RulesLoader_LoadRules_BuiltInName_ReturnsRules(string packName)
    {
        var rules = _rulesLoader.LoadRules(packName);

        Assert.NotEmpty(rules);
    }

    [Fact]
    public void RulePacks_ParseJson_ValidJson_ReturnsRules()
    {
        var json = """{ "rules": [{ "id": "T1", "source": "*.A", "mustNotReference": "*.B" }] }""";

        var rules = RulePacks.ParseJson(json);

        Assert.NotNull(rules);
        Assert.Single(rules);
        Assert.Equal("T1", rules[0].Id);
    }

    [Fact]
    public void RulePacks_Hexagonal_HasExpectedRuleIds()
    {
        var rules = RulePacks.ResolveBuiltIn("hexagonal")!;

        Assert.Contains(rules, r => r.Id == "HEX-001");
        Assert.All(rules, r => Assert.NotNull(r.Source));
        Assert.All(rules, r => Assert.True(
            r.MustNotReference != null || r.MayOnlyReference != null,
            $"Rule {r.Id} has no constraint"));
    }

    [Fact]
    public void RulePacks_Lifeblood_HasExpectedRuleIds()
    {
        var rules = RulePacks.ResolveBuiltIn("lifeblood")!;

        Assert.Contains(rules, r => r.Id == "LB-001");
        Assert.True(rules.Length >= 16, $"Expected >=16 lifeblood rules, got {rules.Length}");
    }
}

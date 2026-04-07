using Lifeblood.Analysis;
using Lifeblood.Connectors.ContextPack;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Xunit;
using DomainSymbolKind = Lifeblood.Domain.Graph.SymbolKind;

namespace Lifeblood.Tests;

public class ContextPackTests
{
    [Fact]
    public void Generate_ProducesHighValueFiles()
    {
        var (graph, analysis) = BuildTestScenario();
        var generator = new AgentContextGenerator();
        var pack = generator.Generate(graph, analysis);

        Assert.NotEmpty(pack.HighValueFiles);
        Assert.Contains(pack.HighValueFiles, f => f.FilePath == "Domain.cs");
    }

    [Fact]
    public void Generate_ProducesBoundaries()
    {
        var (graph, analysis) = BuildTestScenario();
        var generator = new AgentContextGenerator();
        var pack = generator.Generate(graph, analysis);

        Assert.NotEmpty(pack.Boundaries);
        Assert.Contains(pack.Boundaries, b => b.ModuleName == "Domain" && b.IsPure);
    }

    [Fact]
    public void Generate_ProducesReadingOrder()
    {
        var (graph, analysis) = BuildTestScenario();
        var generator = new AgentContextGenerator();
        var pack = generator.Generate(graph, analysis);

        Assert.NotEmpty(pack.ReadingOrder);
    }

    [Fact]
    public void Generate_NoViolations_ReportsClean()
    {
        var (graph, analysis) = BuildTestScenario();
        var generator = new AgentContextGenerator();
        var pack = generator.Generate(graph, analysis);

        Assert.Empty(pack.ActiveViolations);
        Assert.Contains(pack.Invariants, i => i.Contains("No architecture violations"));
    }

    [Fact]
    public void InstructionFile_ContainsModules()
    {
        var (graph, analysis) = BuildTestScenario();
        var generator = new InstructionFileGenerator();
        var output = generator.Generate(graph, analysis);

        Assert.Contains("Domain", output);
        Assert.Contains("App", output);
        Assert.Contains("## Architecture", output);
    }

    private static (SemanticGraph graph, AnalysisResult analysis) BuildTestScenario()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "mod:Domain", Name = "Domain", QualifiedName = "Domain", Kind = DomainSymbolKind.Module })
            .AddSymbol(new Symbol { Id = "mod:App", Name = "App", QualifiedName = "App", Kind = DomainSymbolKind.Module })
            .AddSymbol(new Symbol { Id = "file:Domain.cs", Name = "Domain.cs", Kind = DomainSymbolKind.File, FilePath = "Domain.cs", ParentId = "mod:Domain" })
            .AddSymbol(new Symbol { Id = "file:App.cs", Name = "App.cs", Kind = DomainSymbolKind.File, FilePath = "App.cs", ParentId = "mod:App" })
            .AddSymbol(new Symbol { Id = "type:Entity", Name = "Entity", QualifiedName = "Domain.Entity", Kind = DomainSymbolKind.Type, ParentId = "file:Domain.cs" })
            .AddSymbol(new Symbol { Id = "type:UseCase", Name = "UseCase", QualifiedName = "App.UseCase", Kind = DomainSymbolKind.Type, ParentId = "file:App.cs" })
            .AddEdge(new Edge { SourceId = "mod:App", TargetId = "mod:Domain", Kind = EdgeKind.DependsOn })
            .AddEdge(new Edge { SourceId = "type:UseCase", TargetId = "type:Entity", Kind = EdgeKind.References })
            .Build();

        var coupling = CouplingAnalyzer.Analyze(graph, new[] { DomainSymbolKind.Type, DomainSymbolKind.Module });
        var tiers = TierClassifier.Classify(graph);
        var cycles = CircularDependencyDetector.Detect(graph);

        var analysis = new AnalysisResult
        {
            Coupling = coupling,
            Tiers = tiers,
            Cycles = cycles,
            Violations = Array.Empty<Violation>(),
        };

        return (graph, analysis);
    }
}

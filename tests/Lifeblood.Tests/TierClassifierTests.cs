using Lifeblood.Analysis;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Xunit;

namespace Lifeblood.Tests;

public class TierClassifierTests
{
    [Fact]
    public void Classify_PureLeaf_IsPure()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "mod:Domain", Name = "Domain", Kind = SymbolKind.Module })
            .Build();

        var tiers = TierClassifier.Classify(graph);
        var domain = tiers.First(t => t.SymbolId == "mod:Domain");
        Assert.Equal(ArchitectureTier.Pure, domain.Tier);
    }

    [Fact]
    public void Classify_BoundaryModule_IsBoundary()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "mod:Domain", Name = "Domain", Kind = SymbolKind.Module })
            .AddSymbol(new Symbol { Id = "mod:App", Name = "App", Kind = SymbolKind.Module })
            .AddSymbol(new Symbol { Id = "mod:Infra", Name = "Infra", Kind = SymbolKind.Module })
            .AddEdge(new Edge { SourceId = "mod:App", TargetId = "mod:Domain", Kind = EdgeKind.DependsOn })
            .AddEdge(new Edge { SourceId = "mod:Infra", TargetId = "mod:App", Kind = EdgeKind.DependsOn })
            .Build();

        var tiers = TierClassifier.Classify(graph);
        var app = tiers.First(t => t.SymbolId == "mod:App");
        Assert.Equal(ArchitectureTier.Boundary, app.Tier);
    }

    [Fact]
    public void Classify_RuntimeModule_IsRuntime()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "mod:Domain", Name = "Domain", Kind = SymbolKind.Module })
            .AddSymbol(new Symbol { Id = "mod:Infra", Name = "Infra", Kind = SymbolKind.Module })
            .AddEdge(new Edge { SourceId = "mod:Infra", TargetId = "mod:Domain", Kind = EdgeKind.DependsOn })
            .Build();

        var tiers = TierClassifier.Classify(graph);
        var infra = tiers.First(t => t.SymbolId == "mod:Infra");
        Assert.Equal(ArchitectureTier.Runtime, infra.Tier);
    }

    [Fact]
    public void Classify_TestModule_IsTooling()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "mod:Domain.Tests", Name = "Domain.Tests", Kind = SymbolKind.Module })
            .AddSymbol(new Symbol { Id = "mod:Domain", Name = "Domain", Kind = SymbolKind.Module })
            .AddEdge(new Edge { SourceId = "mod:Domain.Tests", TargetId = "mod:Domain", Kind = EdgeKind.DependsOn })
            .Build();

        var tiers = TierClassifier.Classify(graph);
        var tests = tiers.First(t => t.SymbolId == "mod:Domain.Tests");
        Assert.Equal(ArchitectureTier.Tooling, tests.Tier);
    }
}

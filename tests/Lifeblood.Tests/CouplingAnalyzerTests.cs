using Lifeblood.Analysis;
using Lifeblood.Domain.Graph;
using Xunit;

namespace Lifeblood.Tests;

public class CouplingAnalyzerTests
{
    private static readonly SymbolKind[] TypesOnly = { SymbolKind.Type };

    [Fact]
    public void Analyze_EmptyGraph_ReturnsEmpty()
    {
        var graph = new SemanticGraph();
        var results = CouplingAnalyzer.Analyze(graph, TypesOnly);
        Assert.Empty(results);
    }

    [Fact]
    public void Analyze_NoEdges_ZeroCoupling()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type })
            .Build();

        var results = CouplingAnalyzer.Analyze(graph, TypesOnly);
        Assert.Single(results);
        Assert.Equal(0, results[0].FanIn);
        Assert.Equal(0, results[0].FanOut);
        Assert.Equal(0f, results[0].Instability);
    }

    [Fact]
    public void Analyze_CountsDistinctDependants_NotEdges()
    {
        // A references B twice via different edge kinds — should still be 1 fan-out for A
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:B", Name = "B", Kind = SymbolKind.Type })
            .AddEdge(new Edge { SourceId = "type:A", TargetId = "type:B", Kind = EdgeKind.References })
            .AddEdge(new Edge { SourceId = "type:A", TargetId = "type:B", Kind = EdgeKind.Calls })
            .Build();

        var results = CouplingAnalyzer.Analyze(graph, TypesOnly);
        var metricsA = results.First(m => m.SymbolId == "type:A");
        var metricsB = results.First(m => m.SymbolId == "type:B");

        Assert.Equal(1, metricsA.FanOut); // distinct: B only
        Assert.Equal(0, metricsA.FanIn);
        Assert.Equal(1, metricsB.FanIn);  // distinct: A only
        Assert.Equal(0, metricsB.FanOut);
    }

    [Fact]
    public void Analyze_ExcludesContainsEdges()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "mod:App", Name = "App", Kind = SymbolKind.Module })
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type, ParentId = "mod:App" })
            .Build();

        // GraphBuilder synthesizes Contains edge. CouplingAnalyzer should exclude it.
        var results = CouplingAnalyzer.Analyze(graph, TypesOnly);
        var metricsA = results.First(m => m.SymbolId == "type:A");

        Assert.Equal(0, metricsA.FanIn);
        Assert.Equal(0, metricsA.FanOut);
    }

    [Fact]
    public void Analyze_Instability_Correct()
    {
        // B depends on A and C. FanIn=0, FanOut=2. Instability = 2/(0+2) = 1.0
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:B", Name = "B", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:C", Name = "C", Kind = SymbolKind.Type })
            .AddEdge(new Edge { SourceId = "type:B", TargetId = "type:A", Kind = EdgeKind.DependsOn })
            .AddEdge(new Edge { SourceId = "type:B", TargetId = "type:C", Kind = EdgeKind.DependsOn })
            .Build();

        var results = CouplingAnalyzer.Analyze(graph, TypesOnly);
        var metricsB = results.First(m => m.SymbolId == "type:B");

        Assert.Equal(0, metricsB.FanIn);
        Assert.Equal(2, metricsB.FanOut);
        Assert.Equal(1.0f, metricsB.Instability);
    }

    [Fact]
    public void Analyze_Instability_Stable()
    {
        // A has 2 incoming, 0 outgoing. Instability = 0/(2+0) = 0.0
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:B", Name = "B", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:C", Name = "C", Kind = SymbolKind.Type })
            .AddEdge(new Edge { SourceId = "type:B", TargetId = "type:A", Kind = EdgeKind.DependsOn })
            .AddEdge(new Edge { SourceId = "type:C", TargetId = "type:A", Kind = EdgeKind.References })
            .Build();

        var results = CouplingAnalyzer.Analyze(graph, TypesOnly);
        var metricsA = results.First(m => m.SymbolId == "type:A");

        Assert.Equal(2, metricsA.FanIn);
        Assert.Equal(0, metricsA.FanOut);
        Assert.Equal(0f, metricsA.Instability);
    }

    [Fact]
    public void Analyze_FiltersTargetKinds()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "method:A.Do", Name = "Do", Kind = SymbolKind.Method, ParentId = "type:A" })
            .Build();

        var typesOnly = CouplingAnalyzer.Analyze(graph, new[] { SymbolKind.Type });
        var methodsOnly = CouplingAnalyzer.Analyze(graph, new[] { SymbolKind.Method });

        Assert.Single(typesOnly);
        Assert.Equal("type:A", typesOnly[0].SymbolId);
        Assert.Single(methodsOnly);
        Assert.Equal("method:A.Do", methodsOnly[0].SymbolId);
    }
}

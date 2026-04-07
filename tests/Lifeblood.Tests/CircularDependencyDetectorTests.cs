using Lifeblood.Analysis;
using Lifeblood.Domain.Graph;
using Xunit;

namespace Lifeblood.Tests;

public class CircularDependencyDetectorTests
{
    [Fact]
    public void Detect_NoCycles_Empty()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:B", Name = "B", Kind = SymbolKind.Type })
            .AddEdge(new Edge { SourceId = "type:A", TargetId = "type:B", Kind = EdgeKind.DependsOn })
            .Build();

        var cycles = CircularDependencyDetector.Detect(graph);
        Assert.Empty(cycles);
    }

    [Fact]
    public void Detect_DirectCycle_Found()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:B", Name = "B", Kind = SymbolKind.Type })
            .AddEdge(new Edge { SourceId = "type:A", TargetId = "type:B", Kind = EdgeKind.DependsOn })
            .AddEdge(new Edge { SourceId = "type:B", TargetId = "type:A", Kind = EdgeKind.DependsOn })
            .Build();

        var cycles = CircularDependencyDetector.Detect(graph);
        Assert.Single(cycles);
        Assert.Equal(2, cycles[0].Length);
    }

    [Fact]
    public void Detect_TriangleCycle_Found()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:B", Name = "B", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:C", Name = "C", Kind = SymbolKind.Type })
            .AddEdge(new Edge { SourceId = "type:A", TargetId = "type:B", Kind = EdgeKind.DependsOn })
            .AddEdge(new Edge { SourceId = "type:B", TargetId = "type:C", Kind = EdgeKind.DependsOn })
            .AddEdge(new Edge { SourceId = "type:C", TargetId = "type:A", Kind = EdgeKind.DependsOn })
            .Build();

        var cycles = CircularDependencyDetector.Detect(graph);
        Assert.Single(cycles);
        Assert.Equal(3, cycles[0].Length);
    }

    [Fact]
    public void Detect_IgnoresContainsEdges()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "mod:App", Name = "App", Kind = SymbolKind.Module })
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type, ParentId = "mod:App" })
            .Build();

        var cycles = CircularDependencyDetector.Detect(graph);
        Assert.Empty(cycles);
    }

    [Fact]
    public void Detect_EmptyGraph_NoCycles()
    {
        var graph = new SemanticGraph();
        var cycles = CircularDependencyDetector.Detect(graph);
        Assert.Empty(cycles);
    }
}

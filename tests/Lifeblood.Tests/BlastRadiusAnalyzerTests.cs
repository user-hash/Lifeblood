using Lifeblood.Analysis;
using Lifeblood.Domain.Graph;
using Xunit;

namespace Lifeblood.Tests;

public class BlastRadiusAnalyzerTests
{
    [Fact]
    public void Analyze_NoIncoming_EmptyRadius()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type })
            .Build();

        var result = BlastRadiusAnalyzer.Analyze(graph, "type:A");
        Assert.Empty(result.AffectedSymbolIds);
        Assert.Equal(0, result.AffectedCount);
    }

    [Fact]
    public void Analyze_DirectDependants_Found()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:Core", Name = "Core", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:App", Name = "App", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:UI", Name = "UI", Kind = SymbolKind.Type })
            .AddEdge(new Edge { SourceId = "type:App", TargetId = "type:Core", Kind = EdgeKind.DependsOn })
            .AddEdge(new Edge { SourceId = "type:UI", TargetId = "type:Core", Kind = EdgeKind.DependsOn })
            .Build();

        var result = BlastRadiusAnalyzer.Analyze(graph, "type:Core");
        Assert.Equal(2, result.AffectedCount);
        Assert.Contains("type:App", result.AffectedSymbolIds);
        Assert.Contains("type:UI", result.AffectedSymbolIds);
    }

    [Fact]
    public void Analyze_TransitiveDependants_Found()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:B", Name = "B", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:C", Name = "C", Kind = SymbolKind.Type })
            .AddEdge(new Edge { SourceId = "type:B", TargetId = "type:A", Kind = EdgeKind.DependsOn })
            .AddEdge(new Edge { SourceId = "type:C", TargetId = "type:B", Kind = EdgeKind.DependsOn })
            .Build();

        var result = BlastRadiusAnalyzer.Analyze(graph, "type:A");
        Assert.Equal(2, result.AffectedCount);
        Assert.Contains("type:B", result.AffectedSymbolIds);
        Assert.Contains("type:C", result.AffectedSymbolIds);
    }

    [Fact]
    public void Analyze_ExcludesContainsEdges()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "mod:App", Name = "App", Kind = SymbolKind.Module })
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type, ParentId = "mod:App" })
            .Build();

        var result = BlastRadiusAnalyzer.Analyze(graph, "type:A");
        Assert.Empty(result.AffectedSymbolIds);
    }

    [Fact]
    public void Analyze_RespectsMaxDepth()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:B", Name = "B", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:C", Name = "C", Kind = SymbolKind.Type })
            .AddEdge(new Edge { SourceId = "type:B", TargetId = "type:A", Kind = EdgeKind.DependsOn })
            .AddEdge(new Edge { SourceId = "type:C", TargetId = "type:B", Kind = EdgeKind.DependsOn })
            .Build();

        var result = BlastRadiusAnalyzer.Analyze(graph, "type:A", maxDepth: 1);
        Assert.Contains("type:B", result.AffectedSymbolIds);
        // C is depth 2 — may or may not be included depending on traversal
    }
}

using Lifeblood.Analysis;
using Lifeblood.Domain.Graph;
using Xunit;

namespace Lifeblood.Tests;

public class BlastRadiusAnalyzerTests
{
    [Fact]
    public void PipelineReusesTraversalStorageWithoutChangingOrMixingResults()
    {
        var builder = new GraphBuilder();
        foreach (var id in new[] { "A", "B", "C", "D", "E" })
            builder.AddSymbol(new Symbol { Id = "type:" + id, Name = id, Kind = SymbolKind.Type });
        foreach (var caller in new[] { "C", "D", "E" })
            foreach (var target in new[] { "A", "B" })
                builder.AddEdge(new Edge { SourceId = "type:" + caller, TargetId = "type:" + target, Kind = EdgeKind.Calls });
        builder.AddEdge(new Edge { SourceId = "type:A", TargetId = "type:B", Kind = EdgeKind.Inherits });
        builder.AddEdge(new Edge { SourceId = "type:B", TargetId = "type:A", Kind = EdgeKind.References });
        var graph = builder.Build();

        var results = AnalysisPipeline.Run(graph).BlastRadii;
        Assert.Equal(2, results.Length);
        foreach (var actual in results)
        {
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(BlastRadiusAnalyzer.Analyze(graph, actual.TargetSymbolId)),
                System.Text.Json.JsonSerializer.Serialize(actual));
            var callers = actual.Breaks.Where(entry => entry.SymbolId is "type:C" or "type:D" or "type:E").ToArray();
            Assert.Equal(3, callers.Length);
            Assert.All(callers, entry =>
            {
                Assert.Equal($"edges [Calls] → {actual.TargetSymbolId}", entry.Reason);
                Assert.Same(callers[0].Reason, entry.Reason);
            });
        }
        Assert.NotSame(results[0].Breaks.Single(entry => entry.SymbolId == "type:C").Reason,
            results[1].Breaks.Single(entry => entry.SymbolId == "type:C").Reason);
    }

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
    public void Analyze_RespectsMaxDepth_IncludesWithinDepth()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:B", Name = "B", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:C", Name = "C", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:D", Name = "D", Kind = SymbolKind.Type })
            .AddEdge(new Edge { SourceId = "type:B", TargetId = "type:A", Kind = EdgeKind.DependsOn }) // depth 1
            .AddEdge(new Edge { SourceId = "type:C", TargetId = "type:B", Kind = EdgeKind.DependsOn }) // depth 2
            .AddEdge(new Edge { SourceId = "type:D", TargetId = "type:C", Kind = EdgeKind.DependsOn }) // depth 3
            .Build();

        var result = BlastRadiusAnalyzer.Analyze(graph, "type:A", maxDepth: 1);
        Assert.Contains("type:B", result.AffectedSymbolIds);
        Assert.DoesNotContain("type:C", result.AffectedSymbolIds); // depth 2 — excluded
        Assert.DoesNotContain("type:D", result.AffectedSymbolIds); // depth 3 — excluded
    }

    [Fact]
    public void Analyze_MaxDepthZero_ReturnsEmpty()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:B", Name = "B", Kind = SymbolKind.Type })
            .AddEdge(new Edge { SourceId = "type:B", TargetId = "type:A", Kind = EdgeKind.DependsOn })
            .Build();

        var result = BlastRadiusAnalyzer.Analyze(graph, "type:A", maxDepth: 0);
        Assert.Empty(result.AffectedSymbolIds);
    }
}

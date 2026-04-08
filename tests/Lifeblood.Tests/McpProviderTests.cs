using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Analysis;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Xunit;

namespace Lifeblood.Tests;

public class McpProviderTests
{
    private readonly LifebloodMcpProvider _provider = new(new TestBlastRadiusProvider());

    private sealed class TestBlastRadiusProvider : IBlastRadiusProvider
    {
        public BlastRadiusResult Analyze(SemanticGraph graph, string targetSymbolId, int maxDepth = 10)
            => BlastRadiusAnalyzer.Analyze(graph, targetSymbolId, maxDepth);
    }

    [Fact]
    public void LookupSymbol_Found()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type })
            .Build();

        var result = _provider.LookupSymbol(graph, "type:A");
        Assert.NotNull(result);
        Assert.Equal("A", result!.Name);
    }

    [Fact]
    public void LookupSymbol_NotFound()
    {
        var graph = new SemanticGraph();
        var result = _provider.LookupSymbol(graph, "type:Missing");
        Assert.Null(result);
    }

    [Fact]
    public void GetDependencies_ReturnsDeps()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:B", Name = "B", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:C", Name = "C", Kind = SymbolKind.Type })
            .AddEdge(new Edge { SourceId = "type:A", TargetId = "type:B", Kind = EdgeKind.DependsOn })
            .AddEdge(new Edge { SourceId = "type:A", TargetId = "type:C", Kind = EdgeKind.References })
            .Build();

        var deps = _provider.GetDependencies(graph, "type:A");
        Assert.Equal(2, deps.Length);
    }

    [Fact]
    public void GetDependencies_ExcludesContains()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "mod:App", Name = "App", Kind = SymbolKind.Module })
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type, ParentId = "mod:App" })
            .Build();

        var deps = _provider.GetDependencies(graph, "mod:App");
        Assert.Empty(deps);
    }

    [Fact]
    public void GetBlastRadius_TransitiveReach()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:Core", Name = "Core", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:App", Name = "App", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:UI", Name = "UI", Kind = SymbolKind.Type })
            .AddEdge(new Edge { SourceId = "type:App", TargetId = "type:Core", Kind = EdgeKind.DependsOn })
            .AddEdge(new Edge { SourceId = "type:UI", TargetId = "type:App", Kind = EdgeKind.DependsOn })
            .Build();

        var affected = _provider.GetBlastRadius(graph, "type:Core");
        Assert.Contains("type:App", affected);
        Assert.Contains("type:UI", affected);
        Assert.DoesNotContain("type:Core", affected);
    }
}

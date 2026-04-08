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

    // ── File Impact ──

    [Fact]
    public void GetFileImpact_ReturnsDependsOnAndDependedOnBy()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "file:A.cs", Name = "A.cs", Kind = SymbolKind.File, FilePath = "A.cs" })
            .AddSymbol(new Symbol { Id = "file:B.cs", Name = "B.cs", Kind = SymbolKind.File, FilePath = "B.cs" })
            .AddSymbol(new Symbol { Id = "file:C.cs", Name = "C.cs", Kind = SymbolKind.File, FilePath = "C.cs" })
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type, FilePath = "A.cs" })
            .AddSymbol(new Symbol { Id = "type:B", Name = "B", Kind = SymbolKind.Type, FilePath = "B.cs" })
            .AddSymbol(new Symbol { Id = "type:C", Name = "C", Kind = SymbolKind.Type, FilePath = "C.cs" })
            .AddEdge(new Edge { SourceId = "type:A", TargetId = "type:B", Kind = EdgeKind.References })
            .AddEdge(new Edge { SourceId = "type:C", TargetId = "type:A", Kind = EdgeKind.References })
            .Build();

        var result = _provider.GetFileImpact(graph, "file:A.cs");

        Assert.Equal("file:A.cs", result.FileId);
        Assert.Equal("A.cs", result.FilePath);
        Assert.Single(result.DependsOn);
        Assert.Equal("file:B.cs", result.DependsOn[0].FileId);
        Assert.Single(result.DependedOnBy);
        Assert.Equal("file:C.cs", result.DependedOnBy[0].FileId);
    }

    [Fact]
    public void GetFileImpact_OrdersByEdgeCountDescending()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "file:Core.cs", Name = "Core.cs", Kind = SymbolKind.File, FilePath = "Core.cs" })
            .AddSymbol(new Symbol { Id = "file:A.cs", Name = "A.cs", Kind = SymbolKind.File, FilePath = "A.cs" })
            .AddSymbol(new Symbol { Id = "file:B.cs", Name = "B.cs", Kind = SymbolKind.File, FilePath = "B.cs" })
            .AddSymbol(new Symbol { Id = "type:Core", Name = "Core", Kind = SymbolKind.Type, FilePath = "Core.cs" })
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type, FilePath = "A.cs" })
            .AddSymbol(new Symbol { Id = "type:B", Name = "B", Kind = SymbolKind.Type, FilePath = "B.cs" })
            .AddSymbol(new Symbol { Id = "method:B.Do()", Name = "Do", Kind = SymbolKind.Method, FilePath = "B.cs" })
            // A → Core: 1 edge
            .AddEdge(new Edge { SourceId = "type:A", TargetId = "type:Core", Kind = EdgeKind.References })
            // B → Core: 2 edges
            .AddEdge(new Edge { SourceId = "type:B", TargetId = "type:Core", Kind = EdgeKind.Implements })
            .AddEdge(new Edge { SourceId = "method:B.Do()", TargetId = "type:Core", Kind = EdgeKind.Calls })
            .Build();

        var result = _provider.GetFileImpact(graph, "file:Core.cs");

        Assert.Equal(2, result.DependedOnBy.Length);
        // B.cs first (2 edges), A.cs second (1 edge)
        Assert.Equal("file:B.cs", result.DependedOnBy[0].FileId);
        Assert.Equal(2, result.DependedOnBy[0].EdgeCount);
        Assert.Equal("file:A.cs", result.DependedOnBy[1].FileId);
        Assert.Equal(1, result.DependedOnBy[1].EdgeCount);
    }

    [Fact]
    public void GetFileImpact_EmptyForIsolatedFile()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "file:Lonely.cs", Name = "Lonely.cs", Kind = SymbolKind.File, FilePath = "Lonely.cs" })
            .AddSymbol(new Symbol { Id = "type:Lonely", Name = "Lonely", Kind = SymbolKind.Type, FilePath = "Lonely.cs" })
            .Build();

        var result = _provider.GetFileImpact(graph, "file:Lonely.cs");

        Assert.Empty(result.DependsOn);
        Assert.Empty(result.DependedOnBy);
    }
}

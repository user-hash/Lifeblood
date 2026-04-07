using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Xunit;

namespace Lifeblood.Tests;

public class GraphBuilderTests
{
    [Fact]
    public void Build_Empty_ReturnsEmptyGraph()
    {
        var graph = new GraphBuilder().Build();

        Assert.Empty(graph.Symbols);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public void Build_SynthesizesContainsEdges_FromParentId()
    {
        var module = new Symbol { Id = "mod:App", Name = "App", Kind = SymbolKind.Module };
        var file = new Symbol { Id = "file:Auth.cs", Name = "Auth.cs", Kind = SymbolKind.File, ParentId = "mod:App" };
        var type = new Symbol { Id = "type:AuthService", Name = "AuthService", Kind = SymbolKind.Type, ParentId = "file:Auth.cs" };
        var method = new Symbol { Id = "method:AuthService.Login", Name = "Login", Kind = SymbolKind.Method, ParentId = "type:AuthService" };

        var graph = new GraphBuilder()
            .AddSymbols(new[] { module, file, type, method })
            .Build();

        Assert.Equal(4, graph.Symbols.Count);

        var containsEdges = graph.Edges.Where(e => e.Kind == EdgeKind.Contains).ToArray();
        Assert.Equal(3, containsEdges.Length);
        Assert.Contains(containsEdges, e => e.SourceId == "mod:App" && e.TargetId == "file:Auth.cs");
        Assert.Contains(containsEdges, e => e.SourceId == "file:Auth.cs" && e.TargetId == "type:AuthService");
        Assert.Contains(containsEdges, e => e.SourceId == "type:AuthService" && e.TargetId == "method:AuthService.Login");
    }

    [Fact]
    public void Build_PreservesExplicitEdges()
    {
        var typeA = new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type };
        var typeB = new Symbol { Id = "type:B", Name = "B", Kind = SymbolKind.Type };
        var callEdge = new Edge
        {
            SourceId = "type:A", TargetId = "type:B", Kind = EdgeKind.Calls,
            Evidence = new Evidence { Kind = EvidenceKind.Semantic, AdapterName = "Roslyn" },
        };

        var graph = new GraphBuilder()
            .AddSymbol(typeA).AddSymbol(typeB)
            .AddEdge(callEdge)
            .Build();

        Assert.Single(graph.Edges);
        Assert.Equal(EdgeKind.Calls, graph.Edges[0].Kind);
        Assert.Equal("Roslyn", graph.Edges[0].Evidence.AdapterName);
    }

    [Fact]
    public void Build_NoDuplicateContains_WhenExplicitContainsExists()
    {
        var parent = new Symbol { Id = "type:Parent", Name = "Parent", Kind = SymbolKind.Type };
        var child = new Symbol { Id = "method:Parent.Do", Name = "Do", Kind = SymbolKind.Method, ParentId = "type:Parent" };
        var explicitContains = new Edge
        {
            SourceId = "type:Parent", TargetId = "method:Parent.Do", Kind = EdgeKind.Contains,
            Evidence = new Evidence { Kind = EvidenceKind.Semantic, AdapterName = "Roslyn" },
        };

        var graph = new GraphBuilder()
            .AddSymbols(new[] { parent, child })
            .AddEdge(explicitContains)
            .Build();

        var containsEdge = Assert.Single(graph.Edges, e => e.Kind == EdgeKind.Contains);
        Assert.Equal("Roslyn", containsEdge.Evidence.AdapterName);
    }

    [Fact]
    public void Build_DeduplicatesSymbols_ById()
    {
        var v1 = new Symbol { Id = "type:Foo", Name = "Foo_v1", Kind = SymbolKind.Type };
        var v2 = new Symbol { Id = "type:Foo", Name = "Foo_v2", Kind = SymbolKind.Type };

        var graph = new GraphBuilder()
            .AddSymbol(v1).AddSymbol(v2)
            .Build();

        Assert.Single(graph.Symbols);
        Assert.Equal("Foo_v2", graph.Symbols[0].Name); // last-write wins
    }

    [Fact]
    public void Build_SkipsContains_WhenParentNotInGraph()
    {
        var orphan = new Symbol { Id = "type:Orphan", Name = "Orphan", Kind = SymbolKind.Type, ParentId = "mod:Missing" };

        var graph = new GraphBuilder()
            .AddSymbol(orphan)
            .Build();

        Assert.Single(graph.Symbols);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public void Build_SynthesizedContains_HasInferredEvidence()
    {
        var parent = new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type };
        var child = new Symbol { Id = "method:A.Do", Name = "Do", Kind = SymbolKind.Method, ParentId = "type:A" };

        var graph = new GraphBuilder()
            .AddSymbols(new[] { parent, child })
            .Build();

        var edge = Assert.Single(graph.Edges);
        Assert.Equal(EvidenceKind.Inferred, edge.Evidence.Kind);
        Assert.Equal("GraphBuilder", edge.Evidence.AdapterName);
        Assert.Equal(ConfidenceLevel.Proven, edge.Evidence.Confidence);
    }

    [Fact]
    public void Build_MixedExplicitAndSynthesized()
    {
        var mod = new Symbol { Id = "mod:Core", Name = "Core", Kind = SymbolKind.Module };
        var file = new Symbol { Id = "file:Foo.cs", Name = "Foo.cs", Kind = SymbolKind.File, ParentId = "mod:Core" };
        var typeA = new Symbol { Id = "type:Foo", Name = "Foo", Kind = SymbolKind.Type, ParentId = "file:Foo.cs" };
        var typeB = new Symbol { Id = "type:Bar", Name = "Bar", Kind = SymbolKind.Type };
        var dep = new Edge { SourceId = "type:Foo", TargetId = "type:Bar", Kind = EdgeKind.DependsOn };

        var graph = new GraphBuilder()
            .AddSymbols(new[] { mod, file, typeA, typeB })
            .AddEdge(dep)
            .Build();

        Assert.Equal(4, graph.Symbols.Count);
        // 2 synthesized Contains + 1 explicit DependsOn
        Assert.Equal(3, graph.Edges.Count);
        Assert.Equal(2, graph.Edges.Count(e => e.Kind == EdgeKind.Contains));
        Assert.Single(graph.Edges, e => e.Kind == EdgeKind.DependsOn);
    }
}

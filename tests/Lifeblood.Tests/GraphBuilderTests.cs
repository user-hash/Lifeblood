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
            Evidence = new Evidence { Kind = EvidenceKind.Semantic, AdapterName = "Roslyn", Confidence = ConfidenceLevel.Proven },
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
            Evidence = new Evidence { Kind = EvidenceKind.Semantic, AdapterName = "Roslyn", Confidence = ConfidenceLevel.Proven },
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
    public void Build_DropsDanglingEdges()
    {
        var typeA = new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type };
        var danglingEdge = new Edge
        {
            SourceId = "type:A", TargetId = "type:NonExistent", Kind = EdgeKind.References,
        };

        var graph = new GraphBuilder()
            .AddSymbol(typeA)
            .AddEdge(danglingEdge)
            .Build();

        Assert.Single(graph.Symbols);
        Assert.Empty(graph.Edges); // dangling edge dropped
    }

    [Fact]
    public void Build_DropsDanglingEdges_BothDirections()
    {
        var typeA = new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type };
        var typeB = new Symbol { Id = "type:B", Name = "B", Kind = SymbolKind.Type };
        var validEdge = new Edge { SourceId = "type:A", TargetId = "type:B", Kind = EdgeKind.References };
        var danglingSource = new Edge { SourceId = "type:Missing", TargetId = "type:A", Kind = EdgeKind.References };
        var danglingTarget = new Edge { SourceId = "type:A", TargetId = "type:Missing", Kind = EdgeKind.References };

        var graph = new GraphBuilder()
            .AddSymbols(new[] { typeA, typeB })
            .AddEdge(validEdge)
            .AddEdge(danglingSource)
            .AddEdge(danglingTarget)
            .Build();

        Assert.Single(graph.Edges); // only the valid edge survives
        Assert.Equal("type:A", graph.Edges[0].SourceId);
        Assert.Equal("type:B", graph.Edges[0].TargetId);
    }

    [Fact]
    public void Build_SelfReferenceParentId_NoSelfContains()
    {
        // A symbol with ParentId == Id should NOT produce a self-referencing Contains edge
        var selfRef = new Symbol { Id = "type:Self", Name = "Self", Kind = SymbolKind.Type, ParentId = "type:Self" };

        var graph = new GraphBuilder()
            .AddSymbol(selfRef)
            .Build();

        Assert.Single(graph.Symbols);
        Assert.Empty(graph.Edges); // no self-referencing Contains edge
    }

    [Fact]
    public void Build_MixedExplicitAndSynthesized()
    {
        var mod = new Symbol { Id = "mod:Core", Name = "Core", Kind = SymbolKind.Module };
        var file = new Symbol { Id = "file:Foo.cs", Name = "Foo.cs", Kind = SymbolKind.File, ParentId = "mod:Core", FilePath = "Foo.cs" };
        var typeA = new Symbol { Id = "type:Foo", Name = "Foo", Kind = SymbolKind.Type, ParentId = "file:Foo.cs", FilePath = "Foo.cs" };
        var typeB = new Symbol { Id = "type:Bar", Name = "Bar", Kind = SymbolKind.Type };
        var dep = new Edge { SourceId = "type:Foo", TargetId = "type:Bar", Kind = EdgeKind.DependsOn };

        var graph = new GraphBuilder()
            .AddSymbols(new[] { mod, file, typeA, typeB })
            .AddEdge(dep)
            .Build();

        Assert.Equal(4, graph.Symbols.Count);
        // 2 synthesized Contains + 1 explicit DependsOn (no file edge — typeB has no file)
        Assert.Equal(3, graph.Edges.Count);
        Assert.Equal(2, graph.Edges.Count(e => e.Kind == EdgeKind.Contains));
        Assert.Single(graph.Edges, e => e.Kind == EdgeKind.DependsOn);
    }

    // ── File-Level Edge Derivation ──

    [Fact]
    public void Build_DerivesFileEdges_BetweenDifferentFiles()
    {
        var fileA = new Symbol { Id = "file:A.cs", Name = "A.cs", Kind = SymbolKind.File, FilePath = "A.cs" };
        var fileB = new Symbol { Id = "file:B.cs", Name = "B.cs", Kind = SymbolKind.File, FilePath = "B.cs" };
        var typeA = new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type, ParentId = "file:A.cs", FilePath = "A.cs" };
        var typeB = new Symbol { Id = "type:B", Name = "B", Kind = SymbolKind.Type, ParentId = "file:B.cs", FilePath = "B.cs" };
        var refEdge = new Edge
        {
            SourceId = "type:A", TargetId = "type:B", Kind = EdgeKind.References,
            Evidence = new Evidence { Kind = EvidenceKind.Semantic, AdapterName = "Roslyn", Confidence = ConfidenceLevel.Proven },
        };

        var graph = new GraphBuilder()
            .AddSymbols(new[] { fileA, fileB, typeA, typeB })
            .AddEdge(refEdge)
            .Build();

        var fileEdge = graph.Edges.FirstOrDefault(e =>
            e.SourceId == "file:A.cs" && e.TargetId == "file:B.cs" && e.Kind == EdgeKind.References);
        Assert.NotNull(fileEdge);
        Assert.Equal(EvidenceKind.Inferred, fileEdge!.Evidence.Kind);
        Assert.Equal("GraphBuilder", fileEdge.Evidence.AdapterName);
        Assert.True(fileEdge.Properties.ContainsKey("edgeCount"));
        Assert.Equal("1", fileEdge.Properties["edgeCount"]);
    }

    [Fact]
    public void Build_FileEdges_AggregateEdgeCount()
    {
        var fileA = new Symbol { Id = "file:A.cs", Name = "A.cs", Kind = SymbolKind.File, FilePath = "A.cs" };
        var fileB = new Symbol { Id = "file:B.cs", Name = "B.cs", Kind = SymbolKind.File, FilePath = "B.cs" };
        var typeA = new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type, ParentId = "file:A.cs", FilePath = "A.cs" };
        var typeB = new Symbol { Id = "type:B", Name = "B", Kind = SymbolKind.Type, ParentId = "file:B.cs", FilePath = "B.cs" };
        var methodA = new Symbol { Id = "method:A.Do()", Name = "Do", Kind = SymbolKind.Method, ParentId = "type:A", FilePath = "A.cs" };

        var graph = new GraphBuilder()
            .AddSymbols(new[] { fileA, fileB, typeA, typeB, methodA })
            .AddEdge(new Edge { SourceId = "type:A", TargetId = "type:B", Kind = EdgeKind.References })
            .AddEdge(new Edge { SourceId = "type:A", TargetId = "type:B", Kind = EdgeKind.Implements })
            .AddEdge(new Edge { SourceId = "method:A.Do()", TargetId = "type:B", Kind = EdgeKind.Calls })
            .Build();

        var fileEdge = graph.Edges.FirstOrDefault(e =>
            e.SourceId == "file:A.cs" && e.TargetId == "file:B.cs" && e.Kind == EdgeKind.References);
        Assert.NotNull(fileEdge);
        Assert.Equal("3", fileEdge!.Properties["edgeCount"]);
    }

    [Fact]
    public void Build_FileEdges_NoSelfReferences()
    {
        var file = new Symbol { Id = "file:Same.cs", Name = "Same.cs", Kind = SymbolKind.File, FilePath = "Same.cs" };
        var typeA = new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type, ParentId = "file:Same.cs", FilePath = "Same.cs" };
        var typeB = new Symbol { Id = "type:B", Name = "B", Kind = SymbolKind.Type, ParentId = "file:Same.cs", FilePath = "Same.cs" };

        var graph = new GraphBuilder()
            .AddSymbols(new[] { file, typeA, typeB })
            .AddEdge(new Edge { SourceId = "type:A", TargetId = "type:B", Kind = EdgeKind.References })
            .Build();

        // No file-level self-reference edge
        Assert.DoesNotContain(graph.Edges, e =>
            e.SourceId == "file:Same.cs" && e.TargetId == "file:Same.cs" && e.Kind == EdgeKind.References);
    }

    [Fact]
    public void Build_FileEdges_SkipsSymbolsWithoutFilePath()
    {
        var fileA = new Symbol { Id = "file:A.cs", Name = "A.cs", Kind = SymbolKind.File, FilePath = "A.cs" };
        var typeA = new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type, ParentId = "file:A.cs", FilePath = "A.cs" };
        var typeB = new Symbol { Id = "type:B", Name = "B", Kind = SymbolKind.Type }; // no FilePath

        var graph = new GraphBuilder()
            .AddSymbols(new[] { fileA, typeA, typeB })
            .AddEdge(new Edge { SourceId = "type:A", TargetId = "type:B", Kind = EdgeKind.References })
            .Build();

        // No file-level edge derived — typeB has no file
        var fileEdges = graph.Edges.Where(e =>
            e.SourceId.StartsWith("file:") && e.TargetId.StartsWith("file:")).ToArray();
        Assert.Empty(fileEdges);
    }

    [Fact]
    public void Build_FileEdges_BidirectionalCycle()
    {
        var fileA = new Symbol { Id = "file:A.cs", Name = "A.cs", Kind = SymbolKind.File, FilePath = "A.cs" };
        var fileB = new Symbol { Id = "file:B.cs", Name = "B.cs", Kind = SymbolKind.File, FilePath = "B.cs" };
        var typeA = new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type, FilePath = "A.cs" };
        var typeB = new Symbol { Id = "type:B", Name = "B", Kind = SymbolKind.Type, FilePath = "B.cs" };

        var graph = new GraphBuilder()
            .AddSymbols(new[] { fileA, fileB, typeA, typeB })
            .AddEdge(new Edge { SourceId = "type:A", TargetId = "type:B", Kind = EdgeKind.References })
            .AddEdge(new Edge { SourceId = "type:B", TargetId = "type:A", Kind = EdgeKind.References })
            .Build();

        Assert.Contains(graph.Edges, e =>
            e.SourceId == "file:A.cs" && e.TargetId == "file:B.cs" && e.Kind == EdgeKind.References);
        Assert.Contains(graph.Edges, e =>
            e.SourceId == "file:B.cs" && e.TargetId == "file:A.cs" && e.Kind == EdgeKind.References);
    }
}

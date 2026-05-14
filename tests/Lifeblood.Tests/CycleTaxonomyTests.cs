using Lifeblood.Analysis;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Regression tests for INV-CYCLE-TAXONOMY-001 —
/// <see cref="CircularDependencyDetector.DetectClassified"/> labels each
/// SCC with a triage bucket (Generated / PartialClassCluster /
/// LikelyRealLoop) so a caller can fold noise without re-walking
/// members. Precedence Generated &gt; Partial &gt; LikelyReal. Closes
/// LB-TRACK-20260514-008.
/// </summary>
public class CycleTaxonomyTests
{
    [Fact]
    public void DetectClassified_CrossTypeCycle_LikelyRealLoop()
    {
        // Two Type symbols in unrelated containers, no generated path
        // markers — the canonical "real architectural loop" shape.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:Acme.A", Name = "A", Kind = SymbolKind.Type, FilePath = "src/A.cs" })
            .AddSymbol(new Symbol { Id = "type:Acme.B", Name = "B", Kind = SymbolKind.Type, FilePath = "src/B.cs" })
            .AddEdge(new Edge { SourceId = "type:Acme.A", TargetId = "type:Acme.B", Kind = EdgeKind.DependsOn })
            .AddEdge(new Edge { SourceId = "type:Acme.B", TargetId = "type:Acme.A", Kind = EdgeKind.DependsOn })
            .Build();

        var descriptors = CircularDependencyDetector.DetectClassified(graph);

        var desc = Assert.Single(descriptors);
        Assert.Equal(CycleBucket.LikelyRealLoop, desc.Bucket);
        Assert.Equal(2, desc.Symbols.Length);
    }

    [Fact]
    public void DetectClassified_GeneratedObjPath_GeneratedBucket()
    {
        // One participating symbol's path has an `obj` segment — that
        // single signal demotes the whole cycle to the Generated bucket.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:Acme.A", Name = "A", Kind = SymbolKind.Type, FilePath = "src/A.cs" })
            .AddSymbol(new Symbol { Id = "type:Acme.B", Name = "B", Kind = SymbolKind.Type, FilePath = "obj/Debug/B.cs" })
            .AddEdge(new Edge { SourceId = "type:Acme.A", TargetId = "type:Acme.B", Kind = EdgeKind.DependsOn })
            .AddEdge(new Edge { SourceId = "type:Acme.B", TargetId = "type:Acme.A", Kind = EdgeKind.DependsOn })
            .Build();

        var descriptors = CircularDependencyDetector.DetectClassified(graph);

        Assert.Equal(CycleBucket.GeneratedOrStaticAnalysisArtifact, Assert.Single(descriptors).Bucket);
    }

    [Fact]
    public void DetectClassified_DotGeneratedDotFilename_GeneratedBucket()
    {
        // Source-generator output convention: `*.Generated.*` anywhere
        // in the filename matches regardless of folder layout.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:Acme.A", Name = "A", Kind = SymbolKind.Type, FilePath = "src/A.Generated.cs" })
            .AddSymbol(new Symbol { Id = "type:Acme.B", Name = "B", Kind = SymbolKind.Type, FilePath = "src/B.cs" })
            .AddEdge(new Edge { SourceId = "type:Acme.A", TargetId = "type:Acme.B", Kind = EdgeKind.DependsOn })
            .AddEdge(new Edge { SourceId = "type:Acme.B", TargetId = "type:Acme.A", Kind = EdgeKind.DependsOn })
            .Build();

        Assert.Equal(CycleBucket.GeneratedOrStaticAnalysisArtifact,
            Assert.Single(CircularDependencyDetector.DetectClassified(graph)).Bucket);
    }

    [Fact]
    public void DetectClassified_DotGCsFilename_GeneratedBucket()
    {
        // Roslyn source-generator output convention: `*.g.cs`.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:Acme.A", Name = "A", Kind = SymbolKind.Type, FilePath = "src/A.cs" })
            .AddSymbol(new Symbol { Id = "type:Acme.B", Name = "B", Kind = SymbolKind.Type, FilePath = "src/B.g.cs" })
            .AddEdge(new Edge { SourceId = "type:Acme.A", TargetId = "type:Acme.B", Kind = EdgeKind.DependsOn })
            .AddEdge(new Edge { SourceId = "type:Acme.B", TargetId = "type:Acme.A", Kind = EdgeKind.DependsOn })
            .Build();

        Assert.Equal(CycleBucket.GeneratedOrStaticAnalysisArtifact,
            Assert.Single(CircularDependencyDetector.DetectClassified(graph)).Bucket);
    }

    [Fact]
    public void DetectClassified_TwoMethodsSameType_PartialClassCluster()
    {
        // Mutual recursion between two methods of one type. The SCC
        // surfaces them as a 2-element cycle but both walk up the
        // Contains chain to the same enclosing Type — bucket as
        // PartialClassCluster, not a real architectural loop.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:Acme.Host", Name = "Host", Kind = SymbolKind.Type, FilePath = "src/Host.cs" })
            .AddSymbol(new Symbol { Id = "method:Acme.Host.Foo()", Name = "Foo", Kind = SymbolKind.Method, ParentId = "type:Acme.Host", FilePath = "src/Host.cs" })
            .AddSymbol(new Symbol { Id = "method:Acme.Host.Bar()", Name = "Bar", Kind = SymbolKind.Method, ParentId = "type:Acme.Host", FilePath = "src/Host.cs" })
            .AddEdge(new Edge { SourceId = "method:Acme.Host.Foo()", TargetId = "method:Acme.Host.Bar()", Kind = EdgeKind.Calls })
            .AddEdge(new Edge { SourceId = "method:Acme.Host.Bar()", TargetId = "method:Acme.Host.Foo()", Kind = EdgeKind.Calls })
            .Build();

        var descriptors = CircularDependencyDetector.DetectClassified(graph);

        Assert.Equal(CycleBucket.PartialClassCluster, Assert.Single(descriptors).Bucket);
    }

    [Fact]
    public void DetectClassified_GeneratedBeatsPartial_PrecedenceHolds()
    {
        // Same enclosing-type setup as the PartialClassCluster test,
        // BUT one method's file path lives under `obj/` — Generated
        // wins per precedence rule (Generated > Partial > LikelyReal).
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:Acme.Host", Name = "Host", Kind = SymbolKind.Type, FilePath = "src/Host.cs" })
            .AddSymbol(new Symbol { Id = "method:Acme.Host.Foo()", Name = "Foo", Kind = SymbolKind.Method, ParentId = "type:Acme.Host", FilePath = "src/Host.cs" })
            .AddSymbol(new Symbol { Id = "method:Acme.Host.Bar()", Name = "Bar", Kind = SymbolKind.Method, ParentId = "type:Acme.Host", FilePath = "obj/Host.Generated.cs" })
            .AddEdge(new Edge { SourceId = "method:Acme.Host.Foo()", TargetId = "method:Acme.Host.Bar()", Kind = EdgeKind.Calls })
            .AddEdge(new Edge { SourceId = "method:Acme.Host.Bar()", TargetId = "method:Acme.Host.Foo()", Kind = EdgeKind.Calls })
            .Build();

        Assert.Equal(CycleBucket.GeneratedOrStaticAnalysisArtifact,
            Assert.Single(CircularDependencyDetector.DetectClassified(graph)).Bucket);
    }

    [Fact]
    public void DetectClassified_MembershipMatchesLegacyDetect()
    {
        // Back-compat: DetectClassified must surface the same SCC
        // members the legacy `Detect()` already does. The bucket is
        // additive metadata, never a member-set change.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:Acme.A", Name = "A", Kind = SymbolKind.Type, FilePath = "src/A.cs" })
            .AddSymbol(new Symbol { Id = "type:Acme.B", Name = "B", Kind = SymbolKind.Type, FilePath = "src/B.cs" })
            .AddSymbol(new Symbol { Id = "type:Acme.C", Name = "C", Kind = SymbolKind.Type, FilePath = "src/C.cs" })
            .AddEdge(new Edge { SourceId = "type:Acme.A", TargetId = "type:Acme.B", Kind = EdgeKind.DependsOn })
            .AddEdge(new Edge { SourceId = "type:Acme.B", TargetId = "type:Acme.C", Kind = EdgeKind.DependsOn })
            .AddEdge(new Edge { SourceId = "type:Acme.C", TargetId = "type:Acme.A", Kind = EdgeKind.DependsOn })
            .Build();

        var legacy = CircularDependencyDetector.Detect(graph);
        var classified = CircularDependencyDetector.DetectClassified(graph);

        Assert.Equal(legacy.Length, classified.Length);
        for (int i = 0; i < legacy.Length; i++)
            Assert.Equal(legacy[i], classified[i].Symbols);
    }

    [Fact]
    public void DetectClassified_EmptyGraph_EmptyResult()
    {
        Assert.Empty(CircularDependencyDetector.DetectClassified(new SemanticGraph()));
    }
}

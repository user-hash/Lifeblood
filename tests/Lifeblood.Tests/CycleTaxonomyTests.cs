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

    [Fact]
    public void DetectClassified_FileSccDeclaringSamePartialType_PartialClassCluster()
    {
        // File-level SCC where every file is a partial declaration of
        // the same Type. The graph encodes the relationship through
        // outgoing Contains edges (File → Type), exactly the way the
        // CSharp extractor emits partials on a real workspace (every
        // partial declaration of Voice surfaces as
        // file:Voice.X.cs --Contains--> type:Voice).
        //
        // Pre-fix bucket: LikelyRealLoop (the walk-up returned null on
        // File roots because Files have no Contains-parent). The fix:
        // for File-kind cycle members, use outgoing Contains to Type
        // children as the "candidate enclosing type" set and bucket as
        // PartialClassCluster iff the intersection across every member
        // is non-empty. INV-CYCLE-TAXONOMY-001.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:Acme.Voice", Name = "Voice", Kind = SymbolKind.Type, FilePath = "src/Voice.cs" })
            .AddSymbol(new Symbol { Id = "file:src/Voice.cs", Name = "Voice.cs", Kind = SymbolKind.File, FilePath = "src/Voice.cs" })
            .AddSymbol(new Symbol { Id = "file:src/Voice.Filter.cs", Name = "Voice.Filter.cs", Kind = SymbolKind.File, FilePath = "src/Voice.Filter.cs" })
            .AddSymbol(new Symbol { Id = "file:src/Voice.Modulation.cs", Name = "Voice.Modulation.cs", Kind = SymbolKind.File, FilePath = "src/Voice.Modulation.cs" })
            // Every file declares the same partial type via outgoing Contains.
            .AddEdge(new Edge { SourceId = "file:src/Voice.cs", TargetId = "type:Acme.Voice", Kind = EdgeKind.Contains })
            .AddEdge(new Edge { SourceId = "file:src/Voice.Filter.cs", TargetId = "type:Acme.Voice", Kind = EdgeKind.Contains })
            .AddEdge(new Edge { SourceId = "file:src/Voice.Modulation.cs", TargetId = "type:Acme.Voice", Kind = EdgeKind.Contains })
            // 3-cycle on References between the file nodes (the empirical
            // shape: method-in-Voice.cs calls method-in-Voice.Filter.cs
            // produces a file→file References edge).
            .AddEdge(new Edge { SourceId = "file:src/Voice.cs", TargetId = "file:src/Voice.Filter.cs", Kind = EdgeKind.References })
            .AddEdge(new Edge { SourceId = "file:src/Voice.Filter.cs", TargetId = "file:src/Voice.Modulation.cs", Kind = EdgeKind.References })
            .AddEdge(new Edge { SourceId = "file:src/Voice.Modulation.cs", TargetId = "file:src/Voice.cs", Kind = EdgeKind.References })
            .Build();

        var descriptors = CircularDependencyDetector.DetectClassified(graph);

        // Only the file-SCC participates in a cycle; the type itself
        // has no incoming non-Contains edges. Find that SCC and assert
        // it bucketed as a partial cluster.
        var fileScc = descriptors.Single(d =>
            d.Symbols.All(s => s.StartsWith("file:")));
        Assert.Equal(CycleBucket.PartialClassCluster, fileScc.Bucket);
        Assert.Equal(3, fileScc.Symbols.Length);
    }

    [Fact]
    public void DetectClassified_FileSccDeclaringDifferentTypes_LikelyRealLoop()
    {
        // Inverse of the previous test: file-level SCC where each file
        // declares a DIFFERENT type. Intersection of candidate
        // enclosing types across cycle members is empty — this is a
        // real architectural file-coupling loop, not a partial-class
        // cluster, and must bucket as LikelyRealLoop. Pins down that
        // the new file-SCC handling doesn't over-claim "partial"
        // status for unrelated files that happen to depend on each
        // other.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:Acme.A", Name = "A", Kind = SymbolKind.Type, FilePath = "src/A.cs" })
            .AddSymbol(new Symbol { Id = "type:Acme.B", Name = "B", Kind = SymbolKind.Type, FilePath = "src/B.cs" })
            .AddSymbol(new Symbol { Id = "file:src/A.cs", Name = "A.cs", Kind = SymbolKind.File, FilePath = "src/A.cs" })
            .AddSymbol(new Symbol { Id = "file:src/B.cs", Name = "B.cs", Kind = SymbolKind.File, FilePath = "src/B.cs" })
            .AddEdge(new Edge { SourceId = "file:src/A.cs", TargetId = "type:Acme.A", Kind = EdgeKind.Contains })
            .AddEdge(new Edge { SourceId = "file:src/B.cs", TargetId = "type:Acme.B", Kind = EdgeKind.Contains })
            .AddEdge(new Edge { SourceId = "file:src/A.cs", TargetId = "file:src/B.cs", Kind = EdgeKind.References })
            .AddEdge(new Edge { SourceId = "file:src/B.cs", TargetId = "file:src/A.cs", Kind = EdgeKind.References })
            .Build();

        var descriptors = CircularDependencyDetector.DetectClassified(graph);

        var fileScc = descriptors.Single(d =>
            d.Symbols.All(s => s.StartsWith("file:")));
        Assert.Equal(CycleBucket.LikelyRealLoop, fileScc.Bucket);
    }

    [Fact]
    public void DetectClassified_MixedKindSccSamePartialType_PartialClassCluster()
    {
        // SCC whose members span File + Method kinds — the file
        // declares partial type T, the methods are members of T. Every
        // member's "candidate enclosing types" set contains T, so the
        // intersection is { T } and the cycle is a partial cluster.
        // Pins down that the generalized set-intersection logic
        // doesn't regress on the mixed-kind case.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:Acme.Voice", Name = "Voice", Kind = SymbolKind.Type, FilePath = "src/Voice.cs" })
            .AddSymbol(new Symbol { Id = "method:Acme.Voice.Foo()", Name = "Foo", Kind = SymbolKind.Method, ParentId = "type:Acme.Voice", FilePath = "src/Voice.cs" })
            .AddSymbol(new Symbol { Id = "file:src/Voice.Helpers.cs", Name = "Voice.Helpers.cs", Kind = SymbolKind.File, FilePath = "src/Voice.Helpers.cs" })
            .AddEdge(new Edge { SourceId = "type:Acme.Voice", TargetId = "method:Acme.Voice.Foo()", Kind = EdgeKind.Contains })
            .AddEdge(new Edge { SourceId = "file:src/Voice.Helpers.cs", TargetId = "type:Acme.Voice", Kind = EdgeKind.Contains })
            .AddEdge(new Edge { SourceId = "method:Acme.Voice.Foo()", TargetId = "file:src/Voice.Helpers.cs", Kind = EdgeKind.References })
            .AddEdge(new Edge { SourceId = "file:src/Voice.Helpers.cs", TargetId = "method:Acme.Voice.Foo()", Kind = EdgeKind.References })
            .Build();

        var descriptors = CircularDependencyDetector.DetectClassified(graph);

        Assert.Equal(CycleBucket.PartialClassCluster, Assert.Single(descriptors).Bucket);
    }
}

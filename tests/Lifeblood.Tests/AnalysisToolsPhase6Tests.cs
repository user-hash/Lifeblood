using Lifeblood.Adapters.CSharp;
using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Phase 6 (2026-04-11) regression tests for the new analysis tools:
///   - LifebloodDeadCodeAnalyzer (IDeadCodeAnalyzer)
///   - LifebloodPartialViewBuilder (IPartialViewBuilder)
///   - BlastRadiusAnalyzer.Analyze break-kind classification
/// </summary>
public class AnalysisToolsPhase6Tests
{
    private static readonly Evidence Evidence = new()
    {
        Kind = EvidenceKind.Semantic,
        AdapterName = "test",
        Confidence = ConfidenceLevel.Proven,
    };

    // ─────────────────────────────────────────────────────────────────────
    // Dead-code analyzer
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void DeadCode_InternalMethodWithNoCallers_IsFlagged()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Foo", Name = "Foo", Kind = SymbolKind.Type, FilePath = "Foo.cs" })
            .AddSymbol(new Symbol
            {
                Id = "method:N.Foo.Dead()", Name = "Dead", Kind = SymbolKind.Method,
                FilePath = "Foo.cs", ParentId = "type:N.Foo",
                Visibility = Visibility.Internal,
            })
            .AddEdge(new Edge
            {
                SourceId = "type:N.Foo", TargetId = "method:N.Foo.Dead()",
                Kind = EdgeKind.Contains, Evidence = Evidence,
            })
            .Build();

        var findings = new LifebloodDeadCodeAnalyzer().FindDeadCode(
            graph, new DeadCodeOptions());

        Assert.Contains(findings, f => f.CanonicalId == "method:N.Foo.Dead()");
    }

    [Fact]
    public void DeadCode_PublicMethodWithNoCallers_IsExcludedByDefault()
    {
        // Public surface is assumed reachable from outside the graph
        // (external consumers, reflection). Default options exclude it.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Foo", Name = "Foo", Kind = SymbolKind.Type, FilePath = "Foo.cs" })
            .AddSymbol(new Symbol
            {
                Id = "method:N.Foo.PublicNoCallers()", Name = "PublicNoCallers", Kind = SymbolKind.Method,
                FilePath = "Foo.cs", ParentId = "type:N.Foo",
                Visibility = Visibility.Public,
            })
            .AddEdge(new Edge { SourceId = "type:N.Foo", TargetId = "method:N.Foo.PublicNoCallers()", Kind = EdgeKind.Contains, Evidence = Evidence })
            .Build();

        var findings = new LifebloodDeadCodeAnalyzer().FindDeadCode(graph, new DeadCodeOptions());

        Assert.DoesNotContain(findings, f => f.CanonicalId == "method:N.Foo.PublicNoCallers()");
    }

    [Fact]
    public void DeadCode_MethodWithIncomingCallsEdge_IsNotFlagged()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Foo", Name = "Foo", Kind = SymbolKind.Type, FilePath = "Foo.cs" })
            .AddSymbol(new Symbol
            {
                Id = "method:N.Foo.Alive()", Name = "Alive", Kind = SymbolKind.Method,
                FilePath = "Foo.cs", ParentId = "type:N.Foo",
                Visibility = Visibility.Internal,
            })
            .AddSymbol(new Symbol
            {
                Id = "method:N.Foo.Caller()", Name = "Caller", Kind = SymbolKind.Method,
                FilePath = "Foo.cs", ParentId = "type:N.Foo",
                Visibility = Visibility.Public,
            })
            .AddEdge(new Edge { SourceId = "type:N.Foo", TargetId = "method:N.Foo.Alive()", Kind = EdgeKind.Contains, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "type:N.Foo", TargetId = "method:N.Foo.Caller()", Kind = EdgeKind.Contains, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "method:N.Foo.Caller()", TargetId = "method:N.Foo.Alive()", Kind = EdgeKind.Calls, Evidence = Evidence })
            .Build();

        var findings = new LifebloodDeadCodeAnalyzer().FindDeadCode(graph, new DeadCodeOptions());

        Assert.DoesNotContain(findings, f => f.CanonicalId == "method:N.Foo.Alive()");
    }

    [Fact]
    public void DeadCode_ExcludeTests_SkipsTestsPathSegment()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.InTests", Name = "InTests", Kind = SymbolKind.Type, FilePath = "tests/InTests.cs", Visibility = Visibility.Internal })
            .AddSymbol(new Symbol { Id = "type:N.InSrc", Name = "InSrc", Kind = SymbolKind.Type, FilePath = "src/InSrc.cs", Visibility = Visibility.Internal })
            .Build();

        var findings = new LifebloodDeadCodeAnalyzer().FindDeadCode(
            graph, new DeadCodeOptions(ExcludePublic: false, ExcludeTests: true));

        Assert.DoesNotContain(findings, f => f.CanonicalId == "type:N.InTests");
        Assert.Contains(findings, f => f.CanonicalId == "type:N.InSrc");
    }

    [Fact]
    public void DeadCode_IncludeKindsNarrowsScope()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Dead", Name = "Dead", Kind = SymbolKind.Type, FilePath = "Dead.cs", Visibility = Visibility.Internal })
            .AddSymbol(new Symbol
            {
                Id = "method:N.Host.Dead()", Name = "Dead", Kind = SymbolKind.Method,
                FilePath = "Host.cs", ParentId = "type:N.Host", Visibility = Visibility.Internal,
            })
            .Build();

        var findings = new LifebloodDeadCodeAnalyzer().FindDeadCode(
            graph, new DeadCodeOptions(IncludeKinds: new[] { SymbolKind.Method }));

        Assert.All(findings, f => Assert.Equal(SymbolKind.Method, f.Kind));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Partial view builder
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void PartialView_NonexistentType_ReturnsDiagnostic()
    {
        var graph = new GraphBuilder().Build();
        var fs = new PhysicalFileSystem();
        var view = new LifebloodPartialViewBuilder(fs)
            .Build(graph, "type:Nope.Nothing", projectRoot: "");

        Assert.Empty(view.Segments);
        Assert.Contains("not found", view.Diagnostic, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PartialView_NonTypeSymbol_ReturnsDiagnostic()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol
            {
                Id = "method:N.Foo.Bar()", Name = "Bar", Kind = SymbolKind.Method,
                FilePath = "Foo.cs",
            })
            .Build();
        var fs = new PhysicalFileSystem();
        var view = new LifebloodPartialViewBuilder(fs)
            .Build(graph, "method:N.Foo.Bar()", projectRoot: "");

        Assert.Empty(view.Segments);
        Assert.Contains("Type", view.Diagnostic);
    }

    [Fact]
    public void PartialView_SingleFileType_ReturnsOneSegmentWithSource()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-pv-{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Foo.cs"), "public class Foo { }");

            var graph = new GraphBuilder()
                .AddSymbol(new Symbol
                {
                    Id = "file:Foo.cs", Name = "Foo.cs", Kind = SymbolKind.File, FilePath = "Foo.cs",
                })
                .AddSymbol(new Symbol
                {
                    Id = "type:N.Foo", Name = "Foo", Kind = SymbolKind.Type, FilePath = "Foo.cs", ParentId = "file:Foo.cs",
                })
                .AddEdge(new Edge
                {
                    SourceId = "file:Foo.cs", TargetId = "type:N.Foo",
                    Kind = EdgeKind.Contains, Evidence = Evidence,
                })
                .Build();

            var view = new LifebloodPartialViewBuilder(new PhysicalFileSystem())
                .Build(graph, "type:N.Foo", projectRoot: tempDir);

            Assert.Single(view.Segments);
            Assert.Contains("public class Foo", view.Segments[0].Source);
            Assert.Contains("public class Foo", view.CombinedSource);
            Assert.Contains("Foo.cs", view.CombinedSource);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void PartialView_MultipleFiles_StitchesInDeterministicOrder()
    {
        // Two partial files for one type: Foo.A.cs and Foo.B.cs.
        // Builder must emit both segments in lexicographic order.
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-pvm-{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Foo.A.cs"), "// half A");
            File.WriteAllText(Path.Combine(tempDir, "Foo.B.cs"), "// half B");

            var graph = new GraphBuilder()
                .AddSymbol(new Symbol { Id = "file:Foo.A.cs", Name = "Foo.A.cs", Kind = SymbolKind.File, FilePath = "Foo.A.cs" })
                .AddSymbol(new Symbol { Id = "file:Foo.B.cs", Name = "Foo.B.cs", Kind = SymbolKind.File, FilePath = "Foo.B.cs" })
                .AddSymbol(new Symbol { Id = "type:N.Foo", Name = "Foo", Kind = SymbolKind.Type, FilePath = "Foo.A.cs" })
                .AddEdge(new Edge { SourceId = "file:Foo.A.cs", TargetId = "type:N.Foo", Kind = EdgeKind.Contains, Evidence = Evidence })
                .AddEdge(new Edge { SourceId = "file:Foo.B.cs", TargetId = "type:N.Foo", Kind = EdgeKind.Contains, Evidence = Evidence })
                .Build();

            var view = new LifebloodPartialViewBuilder(new PhysicalFileSystem())
                .Build(graph, "type:N.Foo", projectRoot: tempDir);

            Assert.Equal(2, view.Segments.Length);
            Assert.Equal("Foo.A.cs", view.Segments[0].FilePath);
            Assert.Equal("Foo.B.cs", view.Segments[1].FilePath);
            Assert.Contains("half A", view.CombinedSource);
            Assert.Contains("half B", view.CombinedSource);
            // A must appear before B in the combined output.
            var aIdx = view.CombinedSource.IndexOf("half A", System.StringComparison.Ordinal);
            var bIdx = view.CombinedSource.IndexOf("half B", System.StringComparison.Ordinal);
            Assert.True(aIdx >= 0 && bIdx > aIdx);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // BlastRadiusAnalyzer break-kind classification (B7)
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void BlastRadius_DirectCaller_ClassifiedAsBindingRemoval()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "method:N.Target()", Name = "Target", Kind = SymbolKind.Method })
            .AddSymbol(new Symbol { Id = "method:N.Caller()", Name = "Caller", Kind = SymbolKind.Method })
            .AddEdge(new Edge { SourceId = "method:N.Caller()", TargetId = "method:N.Target()", Kind = EdgeKind.Calls, Evidence = Evidence })
            .Build();

        var result = BlastRadiusAnalyzer.Analyze(graph, "method:N.Target()");

        Assert.Contains(result.Breaks, b =>
            b.SymbolId == "method:N.Caller()" && b.Kind == BreakKind.BindingRemoval);
    }

    [Fact]
    public void BlastRadius_Implementer_ClassifiedAsSignatureChange()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.IFoo", Name = "IFoo", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:N.Foo", Name = "Foo", Kind = SymbolKind.Type })
            .AddEdge(new Edge { SourceId = "type:N.Foo", TargetId = "type:N.IFoo", Kind = EdgeKind.Implements, Evidence = Evidence })
            .Build();

        var result = BlastRadiusAnalyzer.Analyze(graph, "type:N.IFoo");

        Assert.Contains(result.Breaks, b =>
            b.SymbolId == "type:N.Foo" && b.Kind == BreakKind.SignatureChange);
    }

    [Fact]
    public void BlastRadius_NoDependants_ProducesEmptyBreaks()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "method:N.Orphan()", Name = "Orphan", Kind = SymbolKind.Method })
            .Build();

        var result = BlastRadiusAnalyzer.Analyze(graph, "method:N.Orphan()");

        Assert.Empty(result.Breaks);
        Assert.Equal(0, result.AffectedCount);
    }
}

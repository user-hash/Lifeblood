using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Graph;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Pins the CallSite provenance on extractor-emitted edges
/// (`INV-EDGE-CALLSITE-001`). Every dependency / dependant query must
/// surface the (file, line, column) of the authoring expression plus the
/// canonical id of the enclosing declaration so callers stop falling back
/// to manual file reading.
///
/// Covers: Calls (invocation), References (member access, field reference),
/// and the round-trip through JsonGraph export/import.
/// </summary>
public class EdgeCallSiteTests : IDisposable
{
    private readonly string _tempDir;
    private readonly PhysicalFileSystem _fs = new();
    private readonly AnalysisConfig _config = new() { RetainCompilations = true };

    public EdgeCallSiteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lifeblood-callsite-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Extract_CallEdge_AttachesCallSiteWithFileLineColumn()
    {
        WriteProject(@"
namespace NS;
public class Svc {
    public void Do() {}
    public void Caller() {
        Do();
    }
}");

        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        var graph = analyzer.AnalyzeWorkspace(_tempDir, _config);

        var callEdge = graph.Edges.FirstOrDefault(e =>
            e.Kind == EdgeKind.Calls &&
            e.SourceId.Contains("Caller") &&
            e.TargetId.Contains(".Do("));

        Assert.NotNull(callEdge);
        Assert.NotNull(callEdge.CallSite);
        Assert.Equal("Program.cs", callEdge.CallSite.FilePath);
        Assert.True(callEdge.CallSite.Line > 0);
        Assert.True(callEdge.CallSite.Column > 0);
        Assert.NotEmpty(callEdge.CallSite.ContainingSymbolId);
        Assert.Contains("Caller", callEdge.CallSite.ContainingSymbolId);
    }

    [Fact]
    public void Extract_FieldReference_AttachesCallSite()
    {
        WriteProject(@"
namespace NS;
public class Svc {
    private int _count;
    public int Read() { return _count; }
}");

        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        var graph = analyzer.AnalyzeWorkspace(_tempDir, _config);

        var fieldEdge = graph.Edges.FirstOrDefault(e =>
            e.Kind == EdgeKind.References &&
            e.SourceId.Contains("Read") &&
            e.TargetId.Contains("_count"));

        Assert.NotNull(fieldEdge);
        Assert.NotNull(fieldEdge.CallSite);
        Assert.Equal("Program.cs", fieldEdge.CallSite.FilePath);
        Assert.True(fieldEdge.CallSite.Line > 0);
    }

    [Fact]
    public void JsonGraph_RoundTripsCallSite()
    {
        WriteProject(@"
namespace NS;
public class Svc {
    public void A() {}
    public void B() { A(); }
}");

        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        var graph1 = analyzer.AnalyzeWorkspace(_tempDir, _config);

        var edge1 = graph1.Edges.First(e =>
            e.Kind == EdgeKind.Calls && e.SourceId.Contains("B") && e.TargetId.Contains(".A("));
        Assert.NotNull(edge1.CallSite);
        var line1 = edge1.CallSite.Line;

        // Round-trip via JsonGraph exporter + importer
        var doc1 = new Lifeblood.Domain.Graph.GraphDocument
        {
            Version = Lifeblood.Domain.Graph.GraphDocument.CurrentVersion,
            Language = "csharp",
            Graph = graph1,
        };
        using var ms = new MemoryStream();
        new Lifeblood.Adapters.JsonGraph.JsonGraphExporter().Export(doc1, ms);
        ms.Position = 0;

        var doc2 = new Lifeblood.Adapters.JsonGraph.JsonGraphImporter().ImportDocument(ms);
        var edge2 = doc2.Graph.Edges.First(e =>
            e.Kind == EdgeKind.Calls && e.SourceId.Contains("B") && e.TargetId.Contains(".A("));

        Assert.NotNull(edge2.CallSite);
        Assert.Equal(line1, edge2.CallSite.Line);
        Assert.Equal(edge1.CallSite.FilePath, edge2.CallSite.FilePath);
        Assert.Equal(edge1.CallSite.ContainingSymbolId, edge2.CallSite.ContainingSymbolId);
    }

    [Fact]
    public void Extract_ModuleDependsOn_HasNoCallSite()
    {
        // module→module DependsOn edges have no single authoring location.
        // Confirm the contract: graph-derived edges leave CallSite null.
        WriteProject(@"namespace NS; public class A {}");
        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        var graph = analyzer.AnalyzeWorkspace(_tempDir, _config);

        foreach (var e in graph.Edges.Where(e => e.Kind == EdgeKind.DependsOn))
        {
            Assert.Null(e.CallSite);
        }
    }

    private void WriteProject(string code)
    {
        var csproj = @"<Project Sdk=""Microsoft.NET.Sdk""><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>";
        File.WriteAllText(Path.Combine(_tempDir, "TestProject.csproj"), csproj);
        File.WriteAllText(Path.Combine(_tempDir, "Program.cs"), code);
    }
}

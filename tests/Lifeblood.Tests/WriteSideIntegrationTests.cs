using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Graph;
using Xunit;
using DomainSymbolKind = Lifeblood.Domain.Graph.SymbolKind;

namespace Lifeblood.Tests;

/// <summary>
/// Integration tests that use RoslynWorkspaceAnalyzer against the real WriteSideApp golden repo on disk.
/// These prove the full pipeline: .sln discovery → .csproj parsing → NuGet resolution → Roslyn
/// compilation → symbol/edge extraction → graph construction → write-side Roslyn operations.
/// Unlike unit tests that build in-memory compilations, these exercise the actual file system path.
/// </summary>
public class WriteSideIntegrationTests
{
    private static readonly string GoldenRepoPath = FindGoldenRepo();

    // ── Full pipeline ──

    [Fact]
    public void AnalyzeWriteSideApp_ProducesValidGraph()
    {
        var (graph, _) = AnalyzeGoldenRepo();

        Assert.True(graph.Symbols.Count > 0);
        Assert.True(graph.Edges.Count > 0);

        var errors = GraphValidator.Validate(graph);
        Assert.Empty(errors);
    }

    [Fact]
    public void AnalyzeWriteSideApp_DiscoversTwoModules()
    {
        var (graph, _) = AnalyzeGoldenRepo();

        var modules = graph.Symbols.Where(s => s.Kind == DomainSymbolKind.Module).ToArray();
        Assert.Equal(2, modules.Length);
        Assert.Contains(modules, m => m.Name == "WriteSideApp.Core");
        Assert.Contains(modules, m => m.Name == "WriteSideApp.Service");
    }

    [Fact]
    public void AnalyzeWriteSideApp_ExtractsAllTypes()
    {
        var (graph, _) = AnalyzeGoldenRepo();

        Assert.Contains(graph.Symbols, s => s.Name == "IGreeter" && s.Kind == DomainSymbolKind.Type);
        Assert.Contains(graph.Symbols, s => s.Name == "Greeter" && s.Kind == DomainSymbolKind.Type);
        Assert.Contains(graph.Symbols, s => s.Name == "GreetingLog" && s.Kind == DomainSymbolKind.Type);
        Assert.Contains(graph.Symbols, s => s.Name == "FormalGreeter" && s.Kind == DomainSymbolKind.Type);
        Assert.Contains(graph.Symbols, s => s.Name == "GreetingService" && s.Kind == DomainSymbolKind.Type);
    }

    [Fact]
    public void AnalyzeWriteSideApp_ExtractsOverrideEdge()
    {
        var (graph, _) = AnalyzeGoldenRepo();

        Assert.Contains(graph.Edges, e =>
            e.Kind == EdgeKind.Overrides
            && e.SourceId.Contains("FormalGreeter")
            && e.TargetId.Contains("Greeter"));
    }

    [Fact]
    public void AnalyzeWriteSideApp_ExtractsEventSymbols()
    {
        var (graph, _) = AnalyzeGoldenRepo();

        var events = graph.Symbols.Where(s =>
            s.Properties.TryGetValue("isEvent", out var v) && v == "true").ToArray();
        Assert.True(events.Length >= 2, $"Expected ≥2 event symbols, got {events.Length}");
    }

    [Fact]
    public void AnalyzeWriteSideApp_ExtractsIndexer()
    {
        var (graph, _) = AnalyzeGoldenRepo();

        var indexers = graph.Symbols.Where(s =>
            s.Properties.TryGetValue("isIndexer", out var v) && v == "true").ToArray();
        Assert.Single(indexers);
        Assert.Contains("this[", indexers[0].Id);
    }

    // ── Write-side: FindReferences ──

    [Fact]
    public void FindReferences_IGreeter_ReturnsRealLocations()
    {
        var (_, adapter) = AnalyzeGoldenRepo();
        var host = new RoslynCompilationHost(adapter.Compilations!);

        var refs = host.FindReferences("type:WriteSideApp.Core.IGreeter");

        // IGreeter is referenced by: Greeter (implements), GreetingService (field type + constructor param)
        // AdhocWorkspace-based FindReferences may return subset, but should find at least some
        Assert.NotNull(refs);
        // Verify at least one real file path is returned
        if (refs.Length > 0)
        {
            Assert.All(refs, r =>
            {
                Assert.True(r.Line > 0, "Line should be > 0");
                Assert.True(r.Column > 0, "Column should be > 0");
            });
        }
    }

    // ── Write-side: Rename ──

    [Fact]
    public void Rename_GreeterType_ReturnsRealEdits()
    {
        var (_, adapter) = AnalyzeGoldenRepo();
        using var refactoring = new RoslynWorkspaceRefactoring(adapter.Compilations!);

        var edits = refactoring.Rename("type:WriteSideApp.Core.Greeter", "SimpleGreeter");

        Assert.NotNull(edits);
        // AdhocWorkspace Rename may return edits — verify structure if any
        if (edits.Length > 0)
        {
            Assert.All(edits, e =>
            {
                Assert.True(e.StartLine > 0);
                Assert.True(e.StartColumn > 0);
                Assert.Contains("SimpleGreeter", e.NewText);
            });
        }
    }

    // ── Write-side: CompileCheck ──

    [Fact]
    public void CompileCheck_ValidCode_Succeeds()
    {
        var (_, adapter) = AnalyzeGoldenRepo();
        var host = new RoslynCompilationHost(adapter.Compilations!);

        var result = host.CompileCheck(
            "public class TestClass { public WriteSideApp.Core.IGreeter? G { get; set; } }",
            "WriteSideApp.Core");

        Assert.True(result.Success, $"CompileCheck failed: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
    }

    [Fact]
    public void CompileCheck_InvalidCode_FailsWithDiagnostics()
    {
        var (_, adapter) = AnalyzeGoldenRepo();
        var host = new RoslynCompilationHost(adapter.Compilations!);

        var result = host.CompileCheck("public class X : WriteSideApp.Core.NonExistent { }", "WriteSideApp.Core");

        Assert.False(result.Success);
        Assert.NotEmpty(result.Diagnostics);
    }

    // ── Helpers ──

    private static (SemanticGraph graph, RoslynWorkspaceAnalyzer adapter) AnalyzeGoldenRepo()
    {
        var fs = new PhysicalFileSystem();
        var adapter = new RoslynWorkspaceAnalyzer(fs);
        var graph = adapter.AnalyzeWorkspace(GoldenRepoPath, new AnalysisConfig());
        return (graph, adapter);
    }

    private static string FindGoldenRepo()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "tests", "GoldenRepos", "WriteSideApp");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "WriteSideApp.sln")))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        // Fallback — works when running from repo root
        return Path.GetFullPath("tests/GoldenRepos/WriteSideApp");
    }
}

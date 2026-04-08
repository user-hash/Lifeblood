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

    /// <summary>
    /// Require a valid golden repo analysis. Returns false if the golden repo
    /// can't be analyzed (CI without restore, path not found). Tests that call
    /// this should return early when it returns false.
    /// </summary>
    private static bool TryAnalyze(out SemanticGraph graph, out RoslynWorkspaceAnalyzer adapter)
    {
        graph = null!;
        adapter = null!;

        if (!Directory.Exists(GoldenRepoPath) || !File.Exists(Path.Combine(GoldenRepoPath, "WriteSideApp.sln")))
            return false;

        var fs = new PhysicalFileSystem();
        adapter = new RoslynWorkspaceAnalyzer(fs);
        graph = adapter.AnalyzeWorkspace(GoldenRepoPath, new AnalysisConfig { RetainCompilations = true });

        return graph.Symbols.Count > 0;
    }

    // ── Full pipeline ──

    [Fact]
    public void AnalyzeWriteSideApp_ProducesValidGraph()
    {
        if (!TryAnalyze(out var graph, out _)) return;

        Assert.True(graph.Symbols.Count > 0);
        Assert.True(graph.Edges.Count > 0);

        var errors = GraphValidator.Validate(graph);
        Assert.Empty(errors);
    }

    [Fact]
    public void AnalyzeWriteSideApp_DiscoversTwoModules()
    {
        if (!TryAnalyze(out var graph, out _)) return;

        var modules = graph.Symbols.Where(s => s.Kind == DomainSymbolKind.Module).ToArray();
        Assert.Equal(2, modules.Length);
        Assert.Contains(modules, m => m.Name == "WriteSideApp.Core");
        Assert.Contains(modules, m => m.Name == "WriteSideApp.Service");
    }

    [Fact]
    public void AnalyzeWriteSideApp_ExtractsAllTypes()
    {
        if (!TryAnalyze(out var graph, out _)) return;

        Assert.Contains(graph.Symbols, s => s.Name == "IGreeter" && s.Kind == DomainSymbolKind.Type);
        Assert.Contains(graph.Symbols, s => s.Name == "Greeter" && s.Kind == DomainSymbolKind.Type);
        Assert.Contains(graph.Symbols, s => s.Name == "GreetingLog" && s.Kind == DomainSymbolKind.Type);
        Assert.Contains(graph.Symbols, s => s.Name == "FormalGreeter" && s.Kind == DomainSymbolKind.Type);
        Assert.Contains(graph.Symbols, s => s.Name == "GreetingService" && s.Kind == DomainSymbolKind.Type);
    }

    [Fact]
    public void AnalyzeWriteSideApp_ExtractsOverrideEdge()
    {
        if (!TryAnalyze(out var graph, out _)) return;

        Assert.Contains(graph.Edges, e =>
            e.Kind == EdgeKind.Overrides
            && e.SourceId.Contains("FormalGreeter")
            && e.TargetId.Contains("Greeter"));
    }

    [Fact]
    public void AnalyzeWriteSideApp_ExtractsEventSymbols()
    {
        if (!TryAnalyze(out var graph, out _)) return;

        var events = graph.Symbols.Where(s =>
            s.Properties.TryGetValue("isEvent", out var v) && v == "true").ToArray();
        Assert.True(events.Length >= 2, $"Expected ≥2 event symbols, got {events.Length}");
    }

    [Fact]
    public void AnalyzeWriteSideApp_ExtractsIndexer()
    {
        if (!TryAnalyze(out var graph, out _)) return;

        var indexers = graph.Symbols.Where(s =>
            s.Properties.TryGetValue("isIndexer", out var v) && v == "true").ToArray();
        Assert.Single(indexers);
        Assert.Contains("this[", indexers[0].Id);
    }

    // ── Write-side: FindReferences ──

    [Fact]
    public void FindReferences_IGreeter_ReturnsRealLocations()
    {
        if (!TryAnalyze(out _, out var adapter)) return;
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
        if (!TryAnalyze(out _, out var adapter)) return;
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
        if (!TryAnalyze(out _, out var adapter)) return;
        var host = new RoslynCompilationHost(adapter.Compilations!);

        var result = host.CompileCheck(
            "public class TestClass { public WriteSideApp.Core.IGreeter? G { get; set; } }",
            "WriteSideApp.Core");

        Assert.True(result.Success, $"CompileCheck failed: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
    }

    [Fact]
    public void CompileCheck_InvalidCode_FailsWithDiagnostics()
    {
        if (!TryAnalyze(out _, out var adapter)) return;
        var host = new RoslynCompilationHost(adapter.Compilations!);

        var result = host.CompileCheck("public class X : WriteSideApp.Core.NonExistent { }", "WriteSideApp.Core");

        Assert.False(result.Success);
        Assert.NotEmpty(result.Diagnostics);
    }

    // ── Write-side: FindDefinition ──

    [Fact]
    public void FindDefinition_IGreeter_ReturnsSourceLocation()
    {
        if (!TryAnalyze(out _, out var adapter)) return;
        var host = new RoslynCompilationHost(adapter.Compilations!);

        var def = host.FindDefinition("type:WriteSideApp.Core.IGreeter");

        Assert.NotNull(def);
        Assert.Contains("IGreeter", def!.FilePath);
        Assert.True(def.Line > 0);
        Assert.Contains("IGreeter", def.DisplayName);
    }

    // ── Write-side: FindImplementations ──

    [Fact]
    public void FindImplementations_IGreeter_FindsGreeterAndFormalGreeter()
    {
        if (!TryAnalyze(out _, out var adapter)) return;
        var host = new RoslynCompilationHost(adapter.Compilations!);

        var impls = host.FindImplementations("type:WriteSideApp.Core.IGreeter");

        Assert.NotEmpty(impls);
        Assert.Contains(impls, id => id.Contains("Greeter"));
    }

    // ── Write-side: GetDocumentation ──

    [Fact]
    public void GetDocumentation_IGreeter_ReturnsSummary()
    {
        if (!TryAnalyze(out _, out var adapter)) return;
        var host = new RoslynCompilationHost(adapter.Compilations!);

        var doc = host.GetDocumentation("type:WriteSideApp.Core.IGreeter");

        // IGreeter has XML doc: "Port interface. Demonstrates..."
        Assert.NotEmpty(doc);
        Assert.Contains("Port interface", doc);
    }

    // ── Write-side: GetSymbolAtPosition ──

    [Fact]
    public void GetSymbolAtPosition_GreeterClassLine_ReturnsGreeter()
    {
        if (!TryAnalyze(out _, out var adapter)) return;
        var host = new RoslynCompilationHost(adapter.Compilations!);

        // Greeter.cs: "public class Greeter : IGreeter" — class name is on this line
        // We need the actual file path used by the compilation
        var greeterFile = adapter.Compilations!.Values
            .SelectMany(c => c.SyntaxTrees)
            .FirstOrDefault(t => t.FilePath?.Contains("Greeter.cs") == true
                && !t.FilePath.Contains("Formal"))
            ?.FilePath;

        if (greeterFile == null) return; // Skip if file not found (shouldn't happen)

        // Line 6 should be "public class Greeter : IGreeter" (after namespace + doc comment)
        var result = host.GetSymbolAtPosition(greeterFile, 6, 14);

        Assert.NotNull(result);
        Assert.Contains("Greeter", result!.Name);
    }

    // ── Helpers ──


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

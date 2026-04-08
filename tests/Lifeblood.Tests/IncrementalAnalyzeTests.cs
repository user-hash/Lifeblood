using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Graph;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Tests for incremental re-analyze. Uses real temp files to verify
/// timestamp-based change detection and selective recompilation.
/// </summary>
public class IncrementalAnalyzeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly PhysicalFileSystem _fs = new();
    private readonly AnalysisConfig _config = new() { RetainCompilations = true };

    public IncrementalAnalyzeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lifeblood-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void IncrementalAnalyze_NoChanges_ReturnsZeroChanged()
    {
        WriteSingleFileProject("public class Foo { }");
        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        analyzer.AnalyzeWorkspace(_tempDir, _config);

        Assert.True(analyzer.HasSnapshot);

        var (graph, changedCount) = analyzer.IncrementalAnalyze(_config);

        Assert.Equal(0, changedCount);
        Assert.True(graph.Symbols.Count > 0);
    }

    [Fact]
    public void IncrementalAnalyze_FileModified_DetectsChange()
    {
        var filePath = WriteSingleFileProject("public class Foo { }");
        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        var graph1 = analyzer.AnalyzeWorkspace(_tempDir, _config);

        // Verify initial state
        Assert.Contains(graph1.Symbols, s => s.Name == "Foo" && s.Kind == SymbolKind.Type);

        // Modify the file — add a method
        Thread.Sleep(50); // ensure timestamp changes
        File.WriteAllText(filePath, "public class Foo { public void Bar() { } }");

        var (graph2, changedCount) = analyzer.IncrementalAnalyze(_config);

        Assert.True(changedCount > 0, "Should detect changed file");
        Assert.Contains(graph2.Symbols, s => s.Name == "Bar" && s.Kind == SymbolKind.Method);
    }

    [Fact]
    public void IncrementalAnalyze_AddType_Detected()
    {
        var filePath = WriteSingleFileProject("public class Foo { }");
        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        var graph1 = analyzer.AnalyzeWorkspace(_tempDir, _config);

        Assert.DoesNotContain(graph1.Symbols, s => s.Name == "Extra");

        // Add a new type to the file
        Thread.Sleep(50);
        File.WriteAllText(filePath, "public class Foo { }\npublic class Extra { }");

        var (graph2, _) = analyzer.IncrementalAnalyze(_config);

        Assert.Contains(graph2.Symbols, s => s.Name == "Extra" && s.Kind == SymbolKind.Type);
    }

    [Fact]
    public void IncrementalAnalyze_RemoveMethod_UpdatesGraph()
    {
        var filePath = WriteSingleFileProject("public class Foo { public void Remove() { } }");
        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        var graph1 = analyzer.AnalyzeWorkspace(_tempDir, _config);

        Assert.Contains(graph1.Symbols, s => s.Name == "Remove");

        // Remove the method
        Thread.Sleep(50);
        File.WriteAllText(filePath, "public class Foo { }");

        var (graph2, _) = analyzer.IncrementalAnalyze(_config);

        Assert.DoesNotContain(graph2.Symbols, s => s.Name == "Remove" && s.Kind == SymbolKind.Method);
    }

    [Fact]
    public void IncrementalAnalyze_UnchangedFileSymbolsPreserved()
    {
        // Two-file project: only change one
        WriteTwoFileProject(
            "public class Stable { public void Keep() { } }",
            "public class Dynamic { }");

        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        var graph1 = analyzer.AnalyzeWorkspace(_tempDir, _config);

        Assert.Contains(graph1.Symbols, s => s.Name == "Keep");
        Assert.Contains(graph1.Symbols, s => s.Name == "Dynamic");

        // Modify only Dynamic.cs
        var dynamicPath = Path.Combine(_tempDir, "Dynamic.cs");
        Thread.Sleep(50);
        File.WriteAllText(dynamicPath, "public class Dynamic { public int Value { get; set; } }");

        var (graph2, changedCount) = analyzer.IncrementalAnalyze(_config);

        Assert.Equal(1, changedCount); // only Dynamic.cs changed
        Assert.Contains(graph2.Symbols, s => s.Name == "Keep"); // Stable.cs preserved
        Assert.Contains(graph2.Symbols, s => s.Name == "Value" && s.Kind == SymbolKind.Property); // new member
    }

    [Fact]
    public void IncrementalAnalyze_FileEdgesUpdated()
    {
        // Initial: Foo references Bar
        WriteTwoFileProject(
            "public class Foo { private Bar _b; }",
            "public class Bar { }");

        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        var graph1 = analyzer.AnalyzeWorkspace(_tempDir, _config);

        // Should have file-level edge Stable.cs → Dynamic.cs
        var fileEdge1 = graph1.Edges.FirstOrDefault(e =>
            e.SourceId.Contains("Stable") && e.TargetId.Contains("Dynamic") && e.Kind == EdgeKind.References);
        Assert.NotNull(fileEdge1);

        // Modify Foo to NOT reference Bar anymore
        var stablePath = Path.Combine(_tempDir, "Stable.cs");
        Thread.Sleep(50);
        File.WriteAllText(stablePath, "public class Foo { }");

        var (graph2, _) = analyzer.IncrementalAnalyze(_config);

        // File-level edge should be gone
        var fileEdge2 = graph2.Edges.FirstOrDefault(e =>
            e.SourceId.Contains("Stable") && e.TargetId.Contains("Dynamic") && e.Kind == EdgeKind.References);
        Assert.Null(fileEdge2);
    }

    [Fact]
    public void IncrementalAnalyze_WithoutPriorAnalysis_Throws()
    {
        var analyzer = new RoslynWorkspaceAnalyzer(_fs);

        Assert.False(analyzer.HasSnapshot);
        Assert.Throws<InvalidOperationException>(() => analyzer.IncrementalAnalyze(_config));
    }

    // ── Helpers ──

    private string WriteSingleFileProject(string code)
    {
        var csproj = @"<Project Sdk=""Microsoft.NET.Sdk""><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>";
        File.WriteAllText(Path.Combine(_tempDir, "TestProject.csproj"), csproj);

        var csPath = Path.Combine(_tempDir, "Program.cs");
        File.WriteAllText(csPath, code);
        return csPath;
    }

    private void WriteTwoFileProject(string code1, string code2)
    {
        var csproj = @"<Project Sdk=""Microsoft.NET.Sdk""><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>";
        File.WriteAllText(Path.Combine(_tempDir, "TestProject.csproj"), csproj);
        File.WriteAllText(Path.Combine(_tempDir, "Stable.cs"), code1);
        File.WriteAllText(Path.Combine(_tempDir, "Dynamic.cs"), code2);
    }
}

using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// xUnit collection for tests that exercise <see cref="RoslynCodeExecutor.Execute"/>.
///
/// The executor redirects <c>Console.Out</c> / <c>Console.Error</c> globally
/// to capture script output. xUnit runs test classes in parallel by default
/// — two concurrent <c>Execute()</c> calls would clobber each other's
/// redirected console streams. Putting every test class that calls
/// <c>Execute()</c> in this single collection forces them to run sequentially.
///
/// The comment in <see cref="RoslynCodeExecutor"/> already flags the
/// thread-unsafety: "Safe only because MCP server is single-threaded.
/// ProcessIsolatedCodeExecutor avoids this entirely."
/// </summary>
[CollectionDefinition("ScriptExecutorSerial", DisableParallelization = true)]
public sealed class ScriptExecutorSerialCollection { }

/// <summary>
/// Tests for the script-host globals path (Plan v4 Seam #3 / INV-VIEW-001..003).
/// These verify that lifeblood_execute scripts can reach the loaded semantic
/// state via top-level identifiers <c>Graph</c>, <c>Compilations</c>, and
/// <c>ModuleDependencies</c> on the <see cref="RoslynSemanticView"/> globals
/// object — and that pre-existing pure-C# scripts (no globals reference)
/// continue to work unchanged.
/// </summary>
[Collection("ScriptExecutorSerial")]
public class RoslynSemanticViewTests
{
    private static (RoslynWorkspaceAnalyzer analyzer, string tempDir) BuildSingleModuleWorkspace()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-view-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        File.WriteAllText(Path.Combine(tempDir, "MyClass.cs"), @"
namespace ViewTest
{
    public class MyClass { public int Value => 42; }
    public class OtherClass { }
}");
        File.WriteAllText(Path.Combine(tempDir, "TestProject.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>");

        var fs = new PhysicalFileSystem();
        var analyzer = new RoslynWorkspaceAnalyzer(fs);
        analyzer.AnalyzeWorkspace(tempDir, new AnalysisConfig { RetainCompilations = true });
        return (analyzer, tempDir);
    }

    [Fact]
    public void RoslynSemanticView_Construction_ExposesAllThreeFields()
    {
        // Sanity test: the view is a passive POCO. Constructor takes three
        // fields and exposes them as read-only properties. No mutation, no
        // copy semantics.
        var compilations = new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal);
        var graph = new Lifeblood.Domain.Graph.SemanticGraph();
        var deps = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var view = new RoslynSemanticView(compilations, graph, deps);

        Assert.Same(compilations, view.Compilations);
        Assert.Same(graph, view.Graph);
        Assert.Same(deps, view.ModuleDependencies);
    }

    [Fact]
    public void Execute_GraphGlobal_ReturnsSymbolCount()
    {
        // Plan v4 INV-VIEW-002: scripts reach the loaded SemanticGraph via
        // the top-level identifier `Graph`. The script counts symbols and
        // returns a positive number, proving the globals object is wired.
        var (analyzer, tempDir) = BuildSingleModuleWorkspace();
        try
        {
            var view = new RoslynSemanticView(
                analyzer.Compilations!,
                analyzer.Compilations is { Count: > 0 }
                    ? GetGraphFromAnalyzer(analyzer, tempDir)
                    : new Lifeblood.Domain.Graph.SemanticGraph(),
                analyzer.ModuleDependencies ?? new Dictionary<string, string[]>(StringComparer.Ordinal));

            var executor = new RoslynCodeExecutor(view);
            var result = executor.Execute("return Graph.Symbols.Count;");

            Assert.True(result.Success,
                "Script that reads `Graph.Symbols.Count` must succeed. Error: " + result.Error);
            Assert.NotNull(result.ReturnValue);
            Assert.True(int.Parse(result.ReturnValue!) > 0,
                $"Graph.Symbols.Count returned {result.ReturnValue}; expected > 0 " +
                "for a workspace with a non-empty source file.");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Execute_CompilationsGlobal_ListsModules()
    {
        // INV-VIEW-002: scripts reach the loaded compilations via `Compilations`.
        // Verify the module name appears in the script's view of the dictionary.
        var (analyzer, tempDir) = BuildSingleModuleWorkspace();
        try
        {
            var view = new RoslynSemanticView(
                analyzer.Compilations!,
                GetGraphFromAnalyzer(analyzer, tempDir),
                analyzer.ModuleDependencies ?? new Dictionary<string, string[]>(StringComparer.Ordinal));

            var executor = new RoslynCodeExecutor(view);
            var result = executor.Execute("return Compilations.Keys.Count();");

            Assert.True(result.Success,
                "Script that reads `Compilations.Keys.Count()` must succeed. Error: " + result.Error);
            Assert.Equal("1", result.ReturnValue);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Execute_PureScriptWithoutGlobals_StillWorks()
    {
        // Backward compat: scripts that don't reference any globals must
        // continue to compile and execute identically to before. The view
        // is passive — its members are exposed at top-level scope, but a
        // script that doesn't use them is unaffected.
        var (analyzer, tempDir) = BuildSingleModuleWorkspace();
        try
        {
            var view = new RoslynSemanticView(
                analyzer.Compilations!,
                GetGraphFromAnalyzer(analyzer, tempDir),
                analyzer.ModuleDependencies ?? new Dictionary<string, string[]>(StringComparer.Ordinal));

            var executor = new RoslynCodeExecutor(view);
            var result = executor.Execute("return Enumerable.Range(0, 5).Sum();");

            Assert.True(result.Success);
            Assert.Equal("10", result.ReturnValue);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Re-analyze the workspace to recover the SemanticGraph (the analyzer
    /// doesn't expose its post-load graph as a public property, only via the
    /// AnalyzeWorkspace return value). This is a test-only convenience.
    /// </summary>
    private static Lifeblood.Domain.Graph.SemanticGraph GetGraphFromAnalyzer(
        RoslynWorkspaceAnalyzer analyzer, string tempDir)
    {
        return analyzer.AnalyzeWorkspace(tempDir, new AnalysisConfig { RetainCompilations = true });
    }
}

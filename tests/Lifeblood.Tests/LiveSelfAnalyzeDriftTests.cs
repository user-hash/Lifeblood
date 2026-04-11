using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Decisive experiment for the Phase 2 live-drift investigation. The
/// synthetic three-module integration test
/// (<see cref="CanonicalSymbolFormatTests.AnalyzeWorkspace_ThreeModuleTransitiveChain_ProducesCanonicalMethodIds"/>)
/// passes, but live self-analysis of the Lifeblood repo via the MCP
/// server still shows `method:Lifeblood.Connectors.Mcp.LifebloodSymbolResolver.Resolve(SemanticGraph,string)`
/// (unqualified) instead of the fully-qualified form. This test runs
/// the REAL analyzer against the REAL Lifeblood.sln on disk and asserts
/// the fully-qualified form exists. If it passes, the fix is correct
/// and the drift is environmental (stale installed tool). If it fails,
/// the fix has a bug the synthetic test doesn't exercise.
/// </summary>
public class LiveSelfAnalyzeDriftTests
{
    [Fact]
    public void LifebloodSelfAnalyze_LifebloodSymbolResolverResolve_HasFullyQualifiedParameter()
    {
        // Locate the Lifeblood repo root by walking up from the test
        // assembly location until we find Lifeblood.sln. This keeps the
        // test portable across CI and local-dev environments.
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "Lifeblood.sln")))
            current = current.Parent;
        Assert.NotNull(current);
        var projectRoot = current!.FullName;

        var fs = new PhysicalFileSystem();
        var analyzer = new RoslynWorkspaceAnalyzer(fs);
        var graph = analyzer.AnalyzeWorkspace(projectRoot, new AnalysisConfig());

        // The FQ canonical ID we expect after Phase 2's transitive-deps fix.
        var canonical = "method:Lifeblood.Connectors.Mcp.LifebloodSymbolResolver.Resolve(Lifeblood.Domain.Graph.SemanticGraph,string)";
        var fqMatch = graph.GetSymbol(canonical);

        // The buggy unqualified form Phase 2 was meant to eliminate.
        var unqualified = "method:Lifeblood.Connectors.Mcp.LifebloodSymbolResolver.Resolve(SemanticGraph,string)";
        var uqMatch = graph.GetSymbol(unqualified);

        Assert.True(fqMatch != null,
            $"FQ canonical ID not found. uqMatch={(uqMatch != null ? "EXISTS" : "missing")}. " +
            "This is the drift: the transitive-deps fix is either broken on the real .sln path or the test harness is exercising a different code path than the MCP server.");
        Assert.Null(uqMatch);
    }
}

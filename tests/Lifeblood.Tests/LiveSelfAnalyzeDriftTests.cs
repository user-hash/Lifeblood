using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Live-drift regression for INV-CANONICAL-001. The synthetic
/// three-module integration test
/// (<see cref="CanonicalSymbolFormatTests.AnalyzeWorkspace_ThreeModuleTransitiveChain_ProducesCanonicalMethodIds"/>)
/// covers the transitive-deps closure on a minimal in-memory project;
/// this test runs the REAL analyzer against the REAL Lifeblood.sln on
/// disk and asserts the fully-qualified canonical form exists. Catches
/// the drift class where the synthetic path passes but a non-test
/// callsite (size, layout, csproj-resolved-deps) regresses.
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

        // The FQ canonical ID the transitive-deps closure produces.
        var canonical = "method:Lifeblood.Connectors.Mcp.LifebloodSymbolResolver.Resolve(Lifeblood.Domain.Graph.SemanticGraph,string)";
        var fqMatch = graph.GetSymbol(canonical);

        // The buggy unqualified form INV-CANONICAL-001 forbids.
        var unqualified = "method:Lifeblood.Connectors.Mcp.LifebloodSymbolResolver.Resolve(SemanticGraph,string)";
        var uqMatch = graph.GetSymbol(unqualified);

        Assert.True(fqMatch != null,
            $"FQ canonical ID not found. uqMatch={(uqMatch != null ? "EXISTS" : "missing")}. " +
            "This is the drift: the transitive-deps fix is either broken on the real .sln path or the test harness is exercising a different code path than the MCP server.");
        Assert.Null(uqMatch);
    }
}

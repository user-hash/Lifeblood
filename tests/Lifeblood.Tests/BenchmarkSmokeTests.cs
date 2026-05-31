using System.Diagnostics;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-BENCH-SMOKE-001: in-suite smoke for the self-analyze workload that
/// the runtime-benchmark harness (<c>tools/runtime-benchmarks/</c>) measures
/// out-of-process. The harness spawns the CLI and is not gated by the unit
/// suite; this guards the same code path the harness depends on so the
/// benchmark can never silently regress to an empty/crashing analyze before
/// the out-of-process run is invoked.
/// </summary>
public class BenchmarkSmokeTests
{
    private const int MinSymbolFloor = 1_000;
    private const int MinEdgeFloor = 1_000;

    [Fact]
    public void SelfAnalyze_Completes_WithPositiveTimingAndNonTrivialGraph()
    {
        var projectRoot = FindRepoRoot();
        var analyzer = new RoslynWorkspaceAnalyzer(new PhysicalFileSystem());

        var sw = Stopwatch.StartNew();
        var graph = analyzer.AnalyzeWorkspace(projectRoot, new AnalysisConfig());
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds > 0,
            "Self-analyze produced no measurable wall-time; benchmark timing path is dead.");
        Assert.True(graph.Symbols.Count >= MinSymbolFloor,
            $"Self-analyze produced {graph.Symbols.Count} symbols (< {MinSymbolFloor} floor); analyze regressed to near-empty.");
        Assert.True(graph.Edges.Count >= MinEdgeFloor,
            $"Self-analyze produced {graph.Edges.Count} edges (< {MinEdgeFloor} floor); edge-emission regressed.");
    }

    [Fact]
    public void RuntimeBenchmarkScript_DeclaresExpandedWorkloadsAndMachineReadableMeasurements()
    {
        var repoRoot = FindRepoRoot();
        var script = File.ReadAllText(Path.Combine(
            repoRoot,
            "tools",
            "runtime-benchmarks",
            "run-lifeblood-runtime-benchmark.ps1"));

        Assert.Contains("\"incremental-noop\"", script);
        Assert.Contains("\"cli-help\"", script);
        Assert.Contains("\"context\"", script);
        Assert.Contains("parseDurationMs", script);
        Assert.Contains("category =", script);
        Assert.Contains("schemaVersion = 1", script);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "Lifeblood.sln")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return current!.FullName;
    }
}

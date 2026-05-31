using System.Diagnostics;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
        Assert.Contains("BenchmarkRunId", script);
        Assert.Contains("benchmarkRunId", script);
        Assert.Contains("DotnetExe", script);
        Assert.Contains("AllowExperimentalTargets", script);
        Assert.Contains("SkipExperimentalTests", script);
        Assert.Contains("RestoreIgnoreFailedSources", script);
        Assert.Contains("PackageSources", script);
        Assert.Contains("DotnetCliHome", script);
        Assert.Contains("WorkDirRoot", script);
        Assert.Contains("experimentalTargets", script);
        Assert.Contains("executionMode", script);
        Assert.Contains("run-lifeblood-experimental-target.ps1", script);
        Assert.Contains("experimentalTargetReport", script);
        Assert.Contains("experimentalTargetStatus", script);
        Assert.Contains("experimentalTargetError", script);
        Assert.Contains("missing-report", script);
        Assert.Contains("Pass -AllowExperimentalTargets", script);
        Assert.Contains("$resolvedProject = (Resolve-Path $Project).Path", script);
        Assert.Contains("$workloadProject = if ($workload -in @(\"self-analyze\", \"self-context\", \"cli-help\")) { $runnerSelfRoot } else { $resolvedProject }", script);
        Assert.DoesNotContain("$Workloads -contains \"self-analyze\"", script);

        var mcpScript = File.ReadAllText(Path.Combine(
            repoRoot,
            "tools",
            "runtime-benchmarks",
            "run-lifeblood-mcp-gc-benchmark.ps1"));

        Assert.Contains("readSideToolCalls", mcpScript);
        Assert.Contains("lifeblood_context", mcpScript);
        Assert.Contains("lifeblood_dead_code", mcpScript);
        Assert.Contains("dispatchLatencyMs", mcpScript);
        Assert.Contains("allReadSideCompleted", mcpScript);
        Assert.Contains("EnvironmentOverrides", mcpScript);
        Assert.Contains("SetEnvironmentVariable", mcpScript);
        Assert.Contains("previousEnvironment", mcpScript);
        Assert.Contains("LIFEBLOOD_BENCHMARK_RUN_ID", mcpScript);
        Assert.Contains("benchmarkRunId", mcpScript);
        Assert.DoesNotContain("[hashtable]$Env", mcpScript);

        var jsonParserBenchmark = File.ReadAllText(Path.Combine(
            repoRoot,
            "tools",
            "runtime-benchmarks",
            "Lifeblood.JsonParserBenchmark",
            "Program.cs"));
        var jsonParserProject = File.ReadAllText(Path.Combine(
            repoRoot,
            "tools",
            "runtime-benchmarks",
            "Lifeblood.JsonParserBenchmark",
            "Lifeblood.JsonParserBenchmark.csproj"));

        Assert.Contains("PipeReader", jsonParserBenchmark);
        Assert.Contains("pipe-reader-buffered", jsonParserBenchmark);
        Assert.Contains("utf8-span", jsonParserBenchmark);
        Assert.Contains("adoptionPosture", jsonParserBenchmark);
        Assert.Contains("source-generated JSON contexts remain gated", jsonParserBenchmark);
        Assert.Contains("EquivalentToCurrent", jsonParserBenchmark);
        Assert.Contains("<TargetFramework>net8.0</TargetFramework>", jsonParserProject);
        Assert.Contains("<FrameworkReference Include=\"Microsoft.AspNetCore.App\" />", jsonParserProject);

        var diagnostics = CompileBenchmarkSource(jsonParserBenchmark)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToArray();
        Assert.Empty(diagnostics);
    }

    private static IEnumerable<Diagnostic> CompileBenchmarkSource(string source)
    {
        var parseOptions = CSharpParseOptions.Default
            .WithLanguageVersion(LanguageVersion.CSharp12);
        var implicitUsings = """
            global using System;
            global using System.Collections.Generic;
            global using System.IO;
            global using System.Linq;
            global using System.Threading;
            global using System.Threading.Tasks;
            """;
        var compilation = CSharpCompilation.Create(
            "Lifeblood.JsonParserBenchmark.CompileProbe",
            new[]
            {
                CSharpSyntaxTree.ParseText(implicitUsings, parseOptions),
                CSharpSyntaxTree.ParseText(source, parseOptions),
            },
            BenchmarkReferences(),
            new CSharpCompilationOptions(OutputKind.ConsoleApplication)
                .WithNullableContextOptions(NullableContextOptions.Enable)
                .WithOptimizationLevel(OptimizationLevel.Release));

        return compilation.GetDiagnostics();
    }

    private static IReadOnlyList<MetadataReference> BenchmarkReferences()
    {
        var references = new List<MetadataReference>();
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var dotnetRoot = runtimeDir == null
            ? null
            : Directory.GetParent(runtimeDir)?.Parent?.Parent?.FullName;
        if (dotnetRoot != null)
        {
            AddReferencePack(references, dotnetRoot, "Microsoft.NETCore.App.Ref");
            AddReferencePack(references, dotnetRoot, "Microsoft.AspNetCore.App.Ref");
        }

        Assert.NotEmpty(references);
        return references;
    }

    private static void AddReferencePack(
        List<MetadataReference> references,
        string dotnetRoot,
        string packName)
    {
        var packRoot = Path.Combine(dotnetRoot, "packs", packName);
        if (!Directory.Exists(packRoot))
        {
            return;
        }

        var refDir = new DirectoryInfo(packRoot)
            .EnumerateDirectories()
            .OrderByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .SelectMany(d =>
            {
                var refRoot = Path.Combine(d.FullName, "ref");
                return Directory.Exists(refRoot)
                    ? new DirectoryInfo(refRoot)
                        .EnumerateDirectories("net*")
                        .OrderByDescending(refDir => refDir.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(refDir => refDir.FullName)
                    : Enumerable.Empty<string>();
            })
            .FirstOrDefault(Directory.Exists);
        if (refDir == null)
        {
            return;
        }

        AddReferencesFromDirectory(references, refDir);
    }

    private static void AddReferencesFromDirectory(
        List<MetadataReference> references,
        string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*.dll"))
        {
            references.Add(MetadataReference.CreateFromFile(path));
        }
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

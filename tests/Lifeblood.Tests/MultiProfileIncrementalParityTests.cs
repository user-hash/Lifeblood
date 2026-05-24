using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Graph;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-MULTI-DEFINE-INCREMENTAL-001. Multi-profile snapshots MUST preserve
/// per-edge <c>Profiles[]</c> provenance across <see cref="RoslynWorkspaceAnalyzer.IncrementalAnalyze"/>.
///
/// Pinned contracts:
///   1. Editor+Player full analyze emits Caller.Hit→Target.Run with Profiles=["Player"].
///   2. Touching the file under the same profile set returns Mode=Incremental
///      (not FullFallback); the edge is STILL present with Profiles=["Player"].
///   3. profileFilter ["Player"] keeps the edge; ["Editor"] excludes it.
///   4. <c>RetainedProfileNames</c> stays Count=2 across the incremental transition.
///   5. Single-profile snapshot (Count == 1) keeps <c>Edge.Profiles=null</c>
///      byte-stable post-incremental.
/// </summary>
public sealed class MultiProfileIncrementalParityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly PhysicalFileSystem _fs = new();

    public MultiProfileIncrementalParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lifeblood-mpi-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void IncrementalAnalyze_AfterMultiProfileFull_PreservesPlayerOnlyEdgeProvenance()
    {
        WriteMultiProfileWorkspace();

        var resolver = new EditorPlayerProfileResolver();
        var analyzer = new RoslynWorkspaceAnalyzer(_fs, resolver);
        var config = new AnalysisConfig
        {
            RetainCompilations = true,
            DefineProfiles = new[] { "Editor", "Player" },
        };

        var graph1 = analyzer.AnalyzeWorkspace(_tempDir, config);

        // (1) Edge present with Player-only provenance.
        var edge1 = FindCallEdge(graph1);
        Assert.NotNull(edge1);
        Assert.NotNull(edge1!.Profiles);
        Assert.Equal(new[] { "Player" }, edge1.Profiles);

        // (4-pre) Snapshot retained both profile names.
        Assert.Equal(new[] { "Editor", "Player" }, analyzer.RetainedProfileNames);

        var callerPath = Path.Combine(_tempDir, "Caller.cs");
        Thread.Sleep(50);
        var callerCode = File.ReadAllText(callerPath);
        File.WriteAllText(callerPath, callerCode + "\n// touch\n");

        var incremental = analyzer.IncrementalAnalyze(config);

        // (2) Incremental mode, edge still tagged Player-only.
        Assert.Equal(IncrementalMode.Incremental, incremental.Mode);
        Assert.True(incremental.ChangedFileCount > 0);
        Assert.NotNull(incremental.Graph);

        var edge2 = FindCallEdge(incremental.Graph!);
        Assert.NotNull(edge2);
        Assert.NotNull(edge2!.Profiles);
        Assert.Equal(new[] { "Player" }, edge2.Profiles);

        // (3) profileFilter parity — asserted on Edge.Profiles directly,
        // independent of read-side handler implementations.
        var keptUnderPlayer = ApplyProfileFilter(incremental.Graph!, new[] { "Player" })
            .FirstOrDefault(e => IsCallerHitToTargetRun(e));
        Assert.NotNull(keptUnderPlayer);

        var keptUnderEditor = ApplyProfileFilter(incremental.Graph!, new[] { "Editor" })
            .FirstOrDefault(e => IsCallerHitToTargetRun(e));
        Assert.Null(keptUnderEditor);

        // (4-post) Snapshot still retains both profiles.
        Assert.Equal(new[] { "Editor", "Player" }, analyzer.RetainedProfileNames);
    }

    [Fact]
    public void IncrementalAnalyze_SingleProfileSnapshot_BackCompatByteStable()
    {
        // INV-MULTI-DEFINE-INCREMENTAL-001 contract #5. Single-profile snapshot
        // keeps Edge.Profiles=null through incremental re-analyze.
        WriteMultiProfileWorkspace();

        var resolver = new EditorPlayerProfileResolver();
        var analyzer = new RoslynWorkspaceAnalyzer(_fs, resolver);
        var singleProfileConfig = new AnalysisConfig
        {
            RetainCompilations = true,
            DefineProfiles = new[] { "Editor" },
        };

        analyzer.AnalyzeWorkspace(_tempDir, singleProfileConfig);
        Assert.Equal(new[] { "Editor" }, analyzer.RetainedProfileNames);

        var callerPath = Path.Combine(_tempDir, "Caller.cs");
        Thread.Sleep(50);
        File.AppendAllText(callerPath, "\n// touch\n");

        var incremental = analyzer.IncrementalAnalyze(singleProfileConfig);

        Assert.Equal(IncrementalMode.Incremental, incremental.Mode);
        Assert.NotNull(incremental.Graph);

        // Every emitted edge in the changed file must keep Profiles == null
        // (single-profile back-compat). Sample by file id.
        var changedFileEdges = incremental.Graph!.Edges
            .Where(e => e.SourceId.Contains("Caller.cs") || e.TargetId.Contains("Caller.cs"))
            .ToArray();
        Assert.All(changedFileEdges, e => Assert.Null(e.Profiles));
    }

    private static Edge? FindCallEdge(SemanticGraph graph) =>
        graph.Edges.FirstOrDefault(IsCallerHitToTargetRun);

    private static bool IsCallerHitToTargetRun(Edge e) =>
        e.Kind == EdgeKind.Calls
        && e.SourceId.Contains("Caller.Hit")
        && e.TargetId.Contains("Target.Run");

    private static IEnumerable<Edge> ApplyProfileFilter(SemanticGraph graph, string[] filter)
    {
        var allowed = new HashSet<string>(filter, StringComparer.Ordinal);
        foreach (var e in graph.Edges)
        {
            // Pre-Wave-6 single-profile back-compat: null Profiles always passes.
            if (e.Profiles == null) yield return e;
            else if (e.Profiles.Any(allowed.Contains)) yield return e;
        }
    }

    private void WriteMultiProfileWorkspace()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Caller.cs"), """
            namespace Mpi {
              public class Caller {
                public void Hit() {
            #if PLAYER_ONLY
                  Target.Run();
            #endif
                }
              }
            }
            """);
        File.WriteAllText(Path.Combine(_tempDir, "Target.cs"), """
            namespace Mpi {
              public class Target {
                public static void Run() { }
              }
            }
            """);
        File.WriteAllText(Path.Combine(_tempDir, "Mpi.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <AssemblyName>Mpi</AssemblyName>
              </PropertyGroup>
            </Project>
            """);
    }

    /// <summary>
    /// Editor identity + Player with PLAYER_ONLY added. Mirrors
    /// <see cref="UnityDefineProfileResolver"/> shape without requiring a
    /// Library/ directory on disk.
    /// </summary>
    private sealed class EditorPlayerProfileResolver : IDefineProfileResolver
    {
        public IReadOnlyList<DefineProfile> ResolveProfiles(string projectRoot) => new[]
        {
            new DefineProfile { Name = "Editor", AddDefines = Array.Empty<string>(), RemoveDefines = Array.Empty<string>() },
            new DefineProfile { Name = "Player", AddDefines = new[] { "PLAYER_ONLY" }, RemoveDefines = Array.Empty<string>() },
        };
    }
}

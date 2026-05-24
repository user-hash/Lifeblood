using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Graph;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-MULTI-DEFINE-INCREMENTAL-001. Pins the Wave 6 multi-profile contract
/// across the incremental seam. Pre-fix incremental rebuilt edges from a
/// single un-tagged extraction pass — every Player-only callsite emitted by
/// the previous full multi-profile analyze vanished on the next file-touch
/// (or worse, leaked through profileFilter:["Editor"] as an untagged edge).
/// Post-fix incremental replays the snapshot's <c>ActiveProfiles</c> per the
/// same loop the full-analyze path uses, so per-edge <c>Profiles[]</c>
/// provenance survives a file-touch byte-stable.
///
/// Asserted invariants:
///   1. Full Editor+Player analyze produces a Caller.Hit→Target.Run edge
///      tagged Profiles=["Player"] (Editor pass sees empty body; Player pass
///      sees the guarded call).
///   2. After touching the file under the SAME profile set, incremental
///      re-analyze returns Mode=Incremental (NOT FullFallback) and the edge
///      is STILL present with Profiles=["Player"].
///   3. profileFilter parity post-incremental: filter ["Player"] keeps the
///      edge; filter ["Editor"] excludes it (asserted directly on
///      <c>Edge.Profiles</c> since the filter is a pure list-shape narrow).
///   4. <c>RetainedProfileNames</c> stays Count=2 across the incremental
///      transition so <c>GraphSession</c> can echo it on the wire response.
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

        // (1) Full analyze: edge present with Player-only provenance.
        var edge1 = FindCallEdge(graph1);
        Assert.NotNull(edge1);
        Assert.NotNull(edge1!.Profiles);
        Assert.Equal(new[] { "Player" }, edge1.Profiles);

        // (4-pre) Snapshot retained both profile names.
        Assert.Equal(new[] { "Editor", "Player" }, analyzer.RetainedProfileNames);

        // Touch the guarded file — change a whitespace inside the guarded
        // block so the syntax is identical post-Editor preprocessor + the
        // call edge is structurally unchanged post-Player preprocessor.
        var callerPath = Path.Combine(_tempDir, "Caller.cs");
        Thread.Sleep(50);
        var callerCode = File.ReadAllText(callerPath);
        File.WriteAllText(callerPath, callerCode + "\n// touch\n");

        var incremental = analyzer.IncrementalAnalyze(config);

        // (2) Incremental, not full fallback.
        Assert.Equal(IncrementalMode.Incremental, incremental.Mode);
        Assert.True(incremental.ChangedFileCount > 0);
        Assert.NotNull(incremental.Graph);

        var edge2 = FindCallEdge(incremental.Graph!);
        Assert.NotNull(edge2);
        Assert.NotNull(edge2!.Profiles);
        Assert.Equal(new[] { "Player" }, edge2.Profiles);

        // (3) profileFilter parity — the contract the wire `profileFilter`
        // input applies on dependants/dependencies handlers. We assert it
        // directly on the edge to stay independent of the read-side tool
        // surface (which has its own ratchets).
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
        // Regression guard: a Wave-6-era snapshot under single-profile
        // (Count == 1) MUST keep Edge.Profiles == null through incremental
        // re-analyze. The multi-profile loop must collapse to the same
        // wire shape pre-Wave-6 emitted.
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
    /// Test resolver returning two profiles: Editor identity + Player with
    /// PLAYER_ONLY added. Mirrors UnityDefineProfileResolver shape without
    /// requiring a Library/ directory on disk.
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

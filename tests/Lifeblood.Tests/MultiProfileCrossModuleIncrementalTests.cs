using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Graph;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-MULTI-DEFINE-INCREMENTAL-001 + INV-INCREMENTAL-XREF-001 cross-product.
/// Pins that multi-profile snapshots preserve CROSS-PROJECT edges across
/// incremental analyze when only the caller file changes.
///
/// Pre-fix: <c>AnalysisSnapshot.DowngradedRefs</c> was a single dict (not
/// keyed by profile); the analyzer only seeded it from the first-profile
/// pass and passed <c>carryDowngraded:null</c> on non-first passes. On an
/// incremental file-touch in module B (caller), the Player-profile pass
/// recompiled ONLY B with no metadata reference for unchanged module A
/// (callee). The cross-project Player-only edge bound to a Roslyn error
/// symbol and was silently dropped by GraphBuilder.
///
/// Fix: <c>DowngradedRefsByProfile</c> per-profile carry dicts.
/// </summary>
public sealed class MultiProfileCrossModuleIncrementalTests : IDisposable
{
    private readonly string _tempDir;
    private readonly PhysicalFileSystem _fs = new();

    public MultiProfileCrossModuleIncrementalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lifeblood-mpxm-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void IncrementalAnalyze_CrossProjectPlayerOnlyEdge_SurvivesCallerTouch()
    {
        WriteTwoModuleProject();

        var resolver = new EditorPlayerProfileResolver();
        var analyzer = new RoslynWorkspaceAnalyzer(_fs, resolver);
        var config = new AnalysisConfig
        {
            RetainCompilations = true,
            DefineProfiles = new[] { "Editor", "Player" },
        };

        var graph1 = analyzer.AnalyzeWorkspace(_tempDir, config);

        // Precondition: full multi-profile analyze emits the cross-project
        // Player-only call edge ModuleB.Caller.Hit → ModuleA.Target.Run with
        // Profiles=["Player"].
        var edge1 = FindCrossProjectCallEdge(graph1);
        Assert.NotNull(edge1);
        Assert.Equal(new[] { "Player" }, edge1!.Profiles);

        int xprefPlayer1 = CountXProjectCallEdgesUnderProfile(graph1, "Player");
        Assert.True(xprefPlayer1 > 0, "Setup: full analyze must emit >=1 cross-project Player edge.");

        // Touch caller only. Module B is the changed module; Module A is
        // unchanged. Pre-fix: non-first profile (Player) carry was null,
        // local downgraded dict in ProcessInOrder started empty over B,
        // A had no PE image, cross-project edge dropped.
        var callerPath = Path.Combine(_tempDir, "ModuleB", "Caller.cs");
        Thread.Sleep(50);
        File.AppendAllText(callerPath, "\n// touch\n");

        var incremental = analyzer.IncrementalAnalyze(config);

        Assert.Equal(IncrementalMode.Incremental, incremental.Mode);
        Assert.True(incremental.ChangedFileCount > 0);
        Assert.NotNull(incremental.Graph);

        // The cross-project Player edge MUST survive with provenance intact.
        var edge2 = FindCrossProjectCallEdge(incremental.Graph!);
        Assert.NotNull(edge2);
        Assert.Equal(new[] { "Player" }, edge2!.Profiles);

        int xprefPlayer2 = CountXProjectCallEdgesUnderProfile(incremental.Graph!, "Player");
        Assert.Equal(xprefPlayer1, xprefPlayer2);
    }

    [Fact]
    public void IncrementalAnalyze_CrossProjectPlayerEdge_StableAcrossRepeatedTouches()
    {
        // Hammer the carry: touch caller N times. Each round must produce
        // the same per-profile edge counts. Pre-fix this would degrade
        // monotonically because the snapshot never accumulated Player-side
        // PE images for unchanged modules.
        WriteTwoModuleProject();

        var resolver = new EditorPlayerProfileResolver();
        var analyzer = new RoslynWorkspaceAnalyzer(_fs, resolver);
        var config = new AnalysisConfig
        {
            RetainCompilations = true,
            DefineProfiles = new[] { "Editor", "Player" },
        };

        var graph1 = analyzer.AnalyzeWorkspace(_tempDir, config);
        int xprefPlayer1 = CountXProjectCallEdgesUnderProfile(graph1, "Player");
        int xprefEditor1 = CountXProjectCallEdgesUnderProfile(graph1, "Editor");

        var callerPath = Path.Combine(_tempDir, "ModuleB", "Caller.cs");
        for (int i = 0; i < 3; i++)
        {
            Thread.Sleep(50);
            File.AppendAllText(callerPath, $"\n// touch {i}\n");
            var r = analyzer.IncrementalAnalyze(config);
            Assert.Equal(IncrementalMode.Incremental, r.Mode);
            Assert.NotNull(r.Graph);
            Assert.Equal(xprefPlayer1, CountXProjectCallEdgesUnderProfile(r.Graph!, "Player"));
            Assert.Equal(xprefEditor1, CountXProjectCallEdgesUnderProfile(r.Graph!, "Editor"));
        }
    }

    private static Edge? FindCrossProjectCallEdge(SemanticGraph graph)
    {
        return graph.Edges.FirstOrDefault(e =>
            e.Kind == EdgeKind.Calls
            && e.SourceId.Contains("Caller.Hit")
            && e.TargetId.Contains("Target.Run"));
    }

    private static int CountXProjectCallEdgesUnderProfile(SemanticGraph graph, string profile)
    {
        int n = 0;
        foreach (var e in graph.Edges)
        {
            if (e.Kind != EdgeKind.Calls) continue;
            if (!e.SourceId.Contains("Caller.Hit")) continue;
            if (!e.TargetId.Contains("Target.Run")) continue;
            if (e.Profiles == null) { n++; continue; }
            if (e.Profiles.Contains(profile)) n++;
        }
        return n;
    }

    private void WriteTwoModuleProject()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "ModuleA"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "ModuleB"));

        File.WriteAllText(Path.Combine(_tempDir, "ModuleA", "ModuleA.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <AssemblyName>ModuleA</AssemblyName>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_tempDir, "ModuleA", "Target.cs"), """
            namespace ModuleA {
              public class Target {
                public static void Run() { }
              }
            }
            """);

        File.WriteAllText(Path.Combine(_tempDir, "ModuleB", "ModuleB.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <AssemblyName>ModuleB</AssemblyName>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\ModuleA\ModuleA.csproj" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_tempDir, "ModuleB", "Caller.cs"), """
            namespace ModuleB {
              public class Caller {
                public void Hit() {
            #if PLAYER_ONLY
                  ModuleA.Target.Run();
            #endif
                }
              }
            }
            """);
    }

    /// <summary>
    /// Editor identity + Player adds PLAYER_ONLY. Mirrors
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

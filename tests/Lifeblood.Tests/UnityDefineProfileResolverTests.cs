using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Left;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>INV-MULTI-DEFINE-UNITY-RESOLVER-001 — Wave 6.C.</summary>
public class UnityDefineProfileResolverTests : IDisposable
{
    private readonly string _unityRoot;
    private readonly string _nonUnityRoot;
    private readonly PhysicalFileSystem _fs = new();

    public UnityDefineProfileResolverTests()
    {
        _unityRoot = Path.Combine(Path.GetTempPath(), $"lifeblood-unity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_unityRoot, "Library"));

        _nonUnityRoot = Path.Combine(Path.GetTempPath(), $"lifeblood-nonunity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_nonUnityRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_unityRoot)) Directory.Delete(_unityRoot, recursive: true);
        if (Directory.Exists(_nonUnityRoot)) Directory.Delete(_nonUnityRoot, recursive: true);
    }

    [Fact]
    public void UnityWorkspace_ReturnsExactly_Editor_Player_AndStandalone()
    {
        var resolver = new UnityDefineProfileResolver(_fs);

        var profiles = resolver.ResolveProfiles(_unityRoot);

        Assert.Equal(3, profiles.Count);
        Assert.Equal(UnityDefineProfileResolver.EditorProfileName, profiles[0].Name);
        Assert.Equal(UnityDefineProfileResolver.PlayerProfileName, profiles[1].Name);
        Assert.Equal(UnityDefineProfileResolver.StandaloneProfileName, profiles[2].Name);
    }

    [Fact]
    public void UnityWorkspace_EditorProfile_IsIdentity()
    {
        var resolver = new UnityDefineProfileResolver(_fs);

        var editor = resolver.ResolveProfiles(_unityRoot).Single(p => p.Name == "Editor");

        Assert.Empty(editor.AddDefines);
        Assert.Empty(editor.RemoveDefines);
    }

    [Fact]
    public void UnityWorkspace_PlayerProfile_RemovesUnityEditorFamily_AndAddsNothing()
    {
        var resolver = new UnityDefineProfileResolver(_fs);

        var player = resolver.ResolveProfiles(_unityRoot).Single(p => p.Name == "Player");

        Assert.Empty(player.AddDefines);
        Assert.Contains("UNITY_EDITOR", player.RemoveDefines);
        Assert.Contains("UNITY_EDITOR_WIN", player.RemoveDefines);
        Assert.Contains("UNITY_EDITOR_64", player.RemoveDefines);
        Assert.Contains("UNITY_EDITOR_OSX", player.RemoveDefines);
        Assert.Contains("UNITY_EDITOR_LINUX", player.RemoveDefines);
        Assert.Equal(5, player.RemoveDefines.Length);
    }

    [Fact]
    public void UnityWorkspace_StandaloneProfile_RemovesUnityEditorFamily_AndAddsUnityStandalone()
    {
        var resolver = new UnityDefineProfileResolver(_fs);

        var standalone = resolver.ResolveProfiles(_unityRoot).Single(p => p.Name == "Standalone");

        Assert.Equal(new[] { "UNITY_STANDALONE" }, standalone.AddDefines);
        Assert.Contains("UNITY_EDITOR", standalone.RemoveDefines);
        Assert.Contains("UNITY_EDITOR_WIN", standalone.RemoveDefines);
        Assert.Contains("UNITY_EDITOR_64", standalone.RemoveDefines);
        Assert.Contains("UNITY_EDITOR_OSX", standalone.RemoveDefines);
        Assert.Contains("UNITY_EDITOR_LINUX", standalone.RemoveDefines);
        Assert.Equal(5, standalone.RemoveDefines.Length);
    }

    [Fact]
    public void NonUnityWorkspace_NoLibraryDir_FallsBackToSingleEditorProfile()
    {
        var resolver = new UnityDefineProfileResolver(_fs);

        var profiles = resolver.ResolveProfiles(_nonUnityRoot);

        var profile = Assert.Single(profiles);
        Assert.Equal(UnityDefineProfileResolver.EditorProfileName, profile.Name);
        Assert.Empty(profile.AddDefines);
        Assert.Empty(profile.RemoveDefines);
    }

    [Fact]
    public void PlayerProfile_AppliedToBaselineWithUnityEditor_StripsEditorDiscriminators()
    {
        var resolver = new UnityDefineProfileResolver(_fs);
        var player = resolver.ResolveProfiles(_unityRoot).Single(p => p.Name == "Player");
        var baseline = new[]
        {
            "DEBUG",
            "UNITY_EDITOR",
            "UNITY_EDITOR_WIN",
            "UNITY_EDITOR_64",
            "UNITY_2023",
            "PLATFORM_ANDROID",
            "UNITY_ANDROID",
        };

        var active = DefineProfileApplier.Apply(baseline, player);

        Assert.DoesNotContain("UNITY_EDITOR", active);
        Assert.DoesNotContain("UNITY_EDITOR_WIN", active);
        Assert.DoesNotContain("UNITY_EDITOR_64", active);
        Assert.Contains("DEBUG", active);
        Assert.Contains("UNITY_2023", active);
        Assert.Contains("PLATFORM_ANDROID", active);
        Assert.Contains("UNITY_ANDROID", active);
    }

    [Fact]
    public void PlayerProfile_AppliedToBaselineWithoutEditorDefines_IsNoOp()
    {
        var resolver = new UnityDefineProfileResolver(_fs);
        var player = resolver.ResolveProfiles(_unityRoot).Single(p => p.Name == "Player");
        var baseline = new[] { "DEBUG", "UNITY_2023", "PLATFORM_ANDROID" };

        var active = DefineProfileApplier.Apply(baseline, player);

        Assert.Equal(baseline.OrderBy(s => s, StringComparer.Ordinal), active);
    }

    [Fact]
    public void StandaloneProfile_AppliedToBaseline_AddsStandaloneAndStripsEditorDiscriminators()
    {
        var resolver = new UnityDefineProfileResolver(_fs);
        var standalone = resolver.ResolveProfiles(_unityRoot).Single(p => p.Name == "Standalone");
        var baseline = new[]
        {
            "DEBUG",
            "UNITY_EDITOR",
            "UNITY_EDITOR_WIN",
            "UNITY_2023",
        };

        var active = DefineProfileApplier.Apply(baseline, standalone);

        Assert.DoesNotContain("UNITY_EDITOR", active);
        Assert.DoesNotContain("UNITY_EDITOR_WIN", active);
        Assert.Contains("DEBUG", active);
        Assert.Contains("UNITY_2023", active);
        Assert.Contains("UNITY_STANDALONE", active);
    }

    [Fact]
    public void Resolver_IsIdempotent_RepeatedCallsReturnSameShape()
    {
        var resolver = new UnityDefineProfileResolver(_fs);

        var first = resolver.ResolveProfiles(_unityRoot);
        var second = resolver.ResolveProfiles(_unityRoot);

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Name, second[i].Name);
            Assert.Equal(first[i].AddDefines, second[i].AddDefines);
            Assert.Equal(first[i].RemoveDefines, second[i].RemoveDefines);
        }
    }

    [Fact]
    public void Resolver_AsmdefVsSdkStyleCsproj_TreatedIdenticallyForProfileVocabulary()
    {
        // Profile vocabulary is workspace-shape-aware (Library/ existence),
        // NOT csproj-flavor-aware. Asmdef-generated workspaces (old-format
        // schema) and SDK-style workspaces under the same Library/ get the
        // same Unity profile vocabulary.
        var resolver = new UnityDefineProfileResolver(_fs);

        var profiles = resolver.ResolveProfiles(_unityRoot);

        Assert.Equal(3, profiles.Count);
    }
}

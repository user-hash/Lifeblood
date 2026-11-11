using Lifeblood.Adapters.CSharp;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

public sealed class AutoRefreshProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lifeblood-auto-profile-{Guid.NewGuid():N}");

    public AutoRefreshProfileTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Library"));
        File.WriteAllText(Path.Combine(_root, "Runtime.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
            <TargetFramework>net8.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems>
            <UnityProjectType>Game:1</UnityProjectType><DefineConstants>UNITY_EDITOR</DefineConstants>
            </PropertyGroup><ItemGroup><Compile Include="Runtime.cs" /><Compile Include="Other.cs" /></ItemGroup></Project>
            """);
        File.WriteAllText(Path.Combine(_root, "Runtime.cs"), """
            namespace App;
            public static class Routes {
                public static void EditorOnly() { }
                public static void PlayerOnly() { }
                public static void Run() {
                #if UNITY_EDITOR
                    EditorOnly();
                #else
                    PlayerOnly();
                #endif
                }
            }
            """);
        File.WriteAllText(Path.Combine(_root, "Other.cs"), "namespace App; public class Other { }");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("noop")]
    [InlineData("source")]
    [InlineData("descriptor")]
    [InlineData("moduleSet")]
    public void AutomaticRefresh_PreservesUnionAndPhysicalInputInventory(string change)
    {
        var fs = new PhysicalFileSystem();
        using var session = new GraphSession(fs);
        _ = session.Load(_root, null, null, defineProfiles: new[] { "Editor", "Player" });
        var before = session.SnapshotId;
        Assert.Contains(session.Graph!.Edges, edge => edge.Profiles?.Contains("Player") == true);
        Assert.Equal(2, session.CurrentSnapshot.TrackedFilePaths.Count);

        if (change == "source")
        {
            var path = Path.Combine(_root, "Runtime.cs");
            File.AppendAllText(path, "\npublic class Added { }");
            File.SetLastWriteTimeUtc(path, session.AnalyzedAtUtc!.Value.AddSeconds(1));
        }
        else if (change == "descriptor")
        {
            var path = Path.Combine(_root, "Runtime.csproj");
            File.WriteAllText(path, File.ReadAllText(path).Replace("</PropertyGroup>", "<AllowUnsafeBlocks>true</AllowUnsafeBlocks></PropertyGroup>"));
        }
        else if (change == "moduleSet")
        {
            File.WriteAllText(Path.Combine(_root, "Added.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems><UnityProjectType>Game:1</UnityProjectType>
                </PropertyGroup><ItemGroup><Compile Include="Added.cs" /></ItemGroup></Project>
                """);
            File.WriteAllText(Path.Combine(_root, "Added.cs"), "namespace Added; public class NewModule { }");
        }

        var changed = session.MaybeRefreshIfStale();
        Assert.Equal(new[] { "Editor", "Player" }, session.RetainedProfileNames);
        Assert.Contains(session.Graph!.Edges, edge => edge.Profiles?.Contains("Player") == true);
        Assert.Contains(session.Graph.Edges, edge => edge.Profiles?.Contains("Editor") == true);
        var expectedFiles = change == "moduleSet" ? 3 : 2;
        Assert.Equal(expectedFiles, session.CurrentSnapshot.TrackedFilePaths.Count);
        var observation = new LifebloodResponseDecorator().Decorate("lifeblood_lookup", new EnvelopeContext
        {
            AnalyzedAtUtc = session.AnalyzedAtUtc,
            TrackedFilePaths = session.CurrentSnapshot.TrackedFilePaths,
            FileSystem = fs,
        });
        Assert.Equal(expectedFiles, observation.Freshness!.InventoryFileCount);
        Assert.Equal(expectedFiles, observation.Freshness.CheckedFileCount);
        if (change == "noop")
        {
            Assert.Null(changed);
            Assert.Equal(before, session.SnapshotId);
        }
        else
        {
            Assert.Equal(change == "source" ? 1 : expectedFiles, changed);
            Assert.NotEqual(before, session.SnapshotId);
        }
    }
}

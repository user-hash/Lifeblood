using Lifeblood.Adapters.CSharp;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Domain.Graph;
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

        var receipt = session.RefreshIfStale();
        var changed = receipt.Status == "refreshed" ? receipt.ChangedFileCount : null;
        Assert.Equal(change == "noop" ? "unchanged" : "refreshed", receipt.Status);
        Assert.Equal(before.Value, receipt.SnapshotBefore);
        Assert.Equal(session.SnapshotId.Value, receipt.SnapshotAfter);
        Assert.Null(receipt.FailureDetail);
        if (change == "moduleSet")
        {
            Assert.Equal("full", receipt.Mode);
            Assert.Equal("moduleSetChanged", receipt.FallbackReason);
            Assert.Equal("module", receipt.Trigger!.Kind);
            Assert.Equal("Added", receipt.Trigger.Input);
            Assert.Equal("added", receipt.Trigger.Change);
        }
        else if (change == "descriptor") Assert.NotNull(receipt.Trigger);
        else Assert.Null(receipt.Trigger);
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

    [Fact]
    public void AutomaticRefresh_ZeroSourceFullFallbackPublishesTheNewModuleGraph()
    {
        var original = Path.Combine(_root, "Runtime.csproj");
        File.WriteAllText(original, File.ReadAllText(original).Replace("<Compile Include=\"Runtime.cs\" /><Compile Include=\"Other.cs\" />", ""));
        File.Delete(Path.Combine(_root, "Runtime.cs"));
        File.Delete(Path.Combine(_root, "Other.cs"));
        using var session = new GraphSession(new PhysicalFileSystem());
        _ = session.Load(_root, null, null, defineProfiles: new[] { "Editor", "Player" });
        Assert.Empty(session.CurrentSnapshot.TrackedFilePaths);
        var before = session.SnapshotId;
        File.WriteAllText(Path.Combine(_root, "Empty.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework>
            <EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup></Project>
            """);
        var receipt = session.RefreshIfStale();
        Assert.Equal("refreshed", receipt.Status);
        Assert.Equal("full", receipt.Mode);
        Assert.Equal(0, receipt.ChangedFileCount);
        Assert.NotEqual(before, session.SnapshotId);
        Assert.Contains(session.Graph!.Symbols, symbol => symbol.Kind == SymbolKind.Module && symbol.Name == "Empty");
    }

    [Fact]
    public void AutomaticRefresh_FailureReportsBoundedEvidenceAndPreservesPublication()
    {
        var fs = new FailingFs();
        using var session = new GraphSession(fs);
        _ = session.Load(_root, null, null);
        var before = session.SnapshotId;
        fs.Fail = true;
        var receipt = session.RefreshIfStale();
        Assert.Equal("failed", receipt.Status);
        Assert.StartsWith("IOException:", receipt.FailureDetail);
        Assert.True(receipt.FailureDetail!.Length <= 2048);
        Assert.Equal(before, session.SnapshotId);
        Assert.Equal(receipt.SnapshotBefore, receipt.SnapshotAfter);
        Assert.Null(session.MaybeRefreshIfStale());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => session.RefreshIfStale(canceled.Token));
    }

    private sealed class FailingFs : IFileSystem
    {
        private readonly PhysicalFileSystem physical = new();
        public bool Fail { get; set; }
        public bool FileExists(string path) => physical.FileExists(path);
        public bool DirectoryExists(string path) => physical.DirectoryExists(path);
        public string ReadAllText(string path) => physical.ReadAllText(path);
        public IEnumerable<string> ReadLines(string path) => physical.ReadLines(path);
        public Stream OpenRead(string path) => physical.OpenRead(path);
        public Stream OpenWrite(string path) => physical.OpenWrite(path);
        public DateTime GetLastWriteTimeUtc(string path) => physical.GetLastWriteTimeUtc(path);
        public string[] FindFiles(string directory, string pattern, bool recursive = true)
            => Fail ? throw new IOException(new string('x', 3000)) : physical.FindFiles(directory, pattern, recursive);
    }
}

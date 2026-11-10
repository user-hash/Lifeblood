using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Domain.Graph;
using Xunit;

namespace Lifeblood.Tests;

public sealed class AnalysisSnapshotMemoryTests
{
    [Fact]
    public void RetiredSemanticHostsAreCollectibleWhileGraphHistoryRemainsAvailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "lifeblood-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Fixture.csproj"),
                "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><PropertyGroup><AssemblyName>Fixture</AssemblyName><LangVersion>12</LangVersion></PropertyGroup><ItemGroup><Compile Include=\"Fixture.cs\" /></ItemGroup></Project>");
            var source = Path.Combine(root, "Fixture.cs");
            File.WriteAllText(source, "public class Fixture { public int Value => 0; }");
            using var session = new Lifeblood.Server.Mcp.GraphSession(
                new Lifeblood.Adapters.CSharp.PhysicalFileSystem(), preserveSharedSemanticPublication: true);
            session.Load(root, null, null);
            var retired = Enumerable.Range(1, 6).Select(index => ReplaceHost(session, source, root, index)).ToArray();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.All(retired, reference => Assert.False(reference.IsAlive));
            Assert.Equal(3, session.SnapshotHistory.Count);
            Assert.All(session.SnapshotHistory, entry => Assert.False(entry.Snapshot.HasCompilationState));
            Assert.True(session.HasCompilationState);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference ReplaceHost(Lifeblood.Server.Mcp.GraphSession session, string source, string root, int index)
    {
        var retired = new WeakReference(session.CompilationHost!);
        File.WriteAllText(source, $"public class Fixture {{ public int Value => {index}; }}");
        session.Load(root, null, null, incremental: true, authoritativeChangedFiles: new[] { source });
        return retired;
    }

    [Fact]
    public void ProfileAppendOnCandidateDoesNotChangeCommittedFileFacts()
    {
        var snapshot = new AnalysisSnapshot { ProjectRoot = "fixture" };
        var file = new Symbol { Id = "file:a.cs" };
        var first = new Edge { SourceId = file.Id, TargetId = "type:A" };
        snapshot.ReplaceFile(file.Id, file, new(), new() { first });
        var original = snapshot.RebuildGraph();
        var candidate = snapshot.ForkForCandidate();

        Assert.Same(original, candidate.UnchangedGraph);
        Assert.Same(snapshot.EdgesByFile[file.Id], candidate.EdgesByFile[file.Id]);
        candidate.AppendProfileEdges(file.Id, new() { new Edge { SourceId = file.Id, TargetId = "type:B" } });

        Assert.Single(snapshot.EdgesByFile[file.Id]);
        Assert.Equal(2, candidate.EdgesByFile[file.Id].Count);
        Assert.Same(original, snapshot.UnchangedGraph);
    }

    [Fact]
    public void RepeatedEdgeAddressesShareStringsWithoutChangingOccurrenceEvidence()
    {
        var snapshot = new AnalysisSnapshot { ProjectRoot = "fixture" };
        var firstId = new string("method:A.M()".ToCharArray());
        var secondId = new string("method:A.M()".ToCharArray());
        Assert.NotSame(firstId, secondId);
        var site = new CallSite { FilePath = "a.cs", Line = 3, Column = 4,
            EndLine = 3, EndColumn = 9, ContainingSymbolId = secondId };
        var edge = new Edge { SourceId = secondId, TargetId = "type:B", CallSite = site,
            Profiles = new[] { "Player" }, Properties = new Dictionary<string, string> { ["role"] = "read" } };
        snapshot.ReplaceFile("file:a.cs", new Symbol { Id = "file:a.cs" },
            new() { new Symbol { Id = firstId } }, new() { edge });

        var pooled = Assert.Single(snapshot.EdgesByFile["file:a.cs"]);
        Assert.Same(firstId, pooled.SourceId);
        Assert.Same(firstId, pooled.CallSite!.ContainingSymbolId);
        Assert.Same(edge.Profiles, pooled.Profiles);
        Assert.Same(edge.Properties, pooled.Properties);
        Assert.Equal(site.FilePath, pooled.CallSite.FilePath);
        Assert.Equal(site.Line, pooled.CallSite.Line);
        Assert.Equal(site.Column, pooled.CallSite.Column);
        Assert.Equal(site.EndLine, pooled.CallSite.EndLine);
        Assert.Equal(site.EndColumn, pooled.CallSite.EndColumn);
        Assert.Same(secondId, edge.SourceId);
    }

    [Fact]
    public void DefaultGraphPropertiesShareAnImmutableEmptyDictionary()
    {
        Assert.Same(new Edge().Properties, new Edge().Properties);
        Assert.Same(new Symbol().Properties, new Edge().Properties);
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, string>)new Edge().Properties).Add("role", "read"));
    }
}

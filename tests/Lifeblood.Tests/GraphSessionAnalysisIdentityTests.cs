using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

public sealed class GraphSessionAnalysisIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"lifeblood-analysis-identity-{Guid.NewGuid():N}");

    public GraphSessionAnalysisIdentityTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "Identity.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(
            Path.Combine(_root, "Types.cs"),
            "namespace Identity; public class Target { } public class Caller { private readonly Target _target = new(); }");
    }

    [Fact]
    public void Incremental_RuleContentChangePublishesIdentityWithoutRebuildingSemanticBase()
    {
        var rulesPath = Path.Combine(_root, "rules.json");
        File.WriteAllText(rulesPath, "{\"rules\":[]}");
        using var session = new GraphSession(new PhysicalFileSystem());

        var fullJson = session.Load(_root, graphPath: null, rulesPath: rulesPath);
        using (var full = JsonDocument.Parse(fullJson))
        {
            var wireIdentity = full.RootElement.GetProperty("analysisIdentity");
            Assert.Equal(session.AnalysisIdentity!.AnalysisKey.Value, wireIdentity.GetProperty("analysisKey").GetString());
            Assert.Equal(session.AnalysisIdentity.BaseKey.Value, wireIdentity.GetProperty("baseKey").GetString());
        }

        var committedGraph = session.Graph;
        var committedHost = session.CompilationHost;
        var committedSnapshot = session.CurrentSnapshot;
        var committedSnapshotId = session.SnapshotId;
        var committedGeneration = session.AnalysisGeneration;
        var committedIdentity = session.AnalysisIdentity!;
        Assert.Empty(session.Analysis!.Violations);

        File.WriteAllText(
            rulesPath,
            "{\"rules\":[{\"id\":\"FORBID\",\"source\":\"*\",\"mustNotReference\":\"*\"}]}");

        var refreshJson = session.Load(_root, graphPath: null, rulesPath: null, incremental: true);
        using (var refresh = JsonDocument.Parse(refreshJson))
        {
            Assert.Equal("incremental", refresh.RootElement.GetProperty("mode").GetString());
            Assert.Equal(0, refresh.RootElement.GetProperty("changedSourceFiles").GetInt32());
            Assert.Equal(
                session.AnalysisIdentity!.AnalysisKey.Value,
                refresh.RootElement.GetProperty("analysisIdentity").GetProperty("analysisKey").GetString());
        }

        Assert.Same(committedGraph, session.Graph);
        Assert.Same(committedHost, session.CompilationHost);
        Assert.NotSame(committedSnapshot, session.CurrentSnapshot);
        Assert.NotEqual(committedSnapshotId, session.SnapshotId);
        Assert.Equal(committedGeneration + 1, session.AnalysisGeneration);
        Assert.Equal(committedIdentity.Source, session.AnalysisIdentity!.Source);
        Assert.NotEqual(committedIdentity.BaseKey, session.AnalysisIdentity.BaseKey);
        Assert.NotEqual(committedIdentity.AnalysisKey, session.AnalysisIdentity.AnalysisKey);
        Assert.NotEmpty(session.Analysis!.Violations);

        var refreshedSnapshot = session.CurrentSnapshot;
        var refreshedSnapshotId = session.SnapshotId;
        var noOpJson = session.Load(_root, graphPath: null, rulesPath: null, incremental: true);
        using (var noOp = JsonDocument.Parse(noOpJson))
            Assert.Equal("incremental-noop", noOp.RootElement.GetProperty("mode").GetString());

        Assert.Same(refreshedSnapshot, session.CurrentSnapshot);
        Assert.Equal(refreshedSnapshotId, session.SnapshotId);

        File.WriteAllText(rulesPath, "{");
        Assert.Throws<JsonException>(() =>
            session.Load(_root, graphPath: null, rulesPath: null, incremental: true));
        Assert.Same(refreshedSnapshot, session.CurrentSnapshot);
        Assert.Equal(refreshedSnapshotId, session.SnapshotId);
    }

    [Fact]
    public void MaybeRefreshIfStale_ReloadsCommittedRuleSourceAndReportsZeroSourceChanges()
    {
        var rulesPath = Path.Combine(_root, "rules.json");
        File.WriteAllText(rulesPath, "{\"rules\":[]}");
        using var session = new GraphSession(new PhysicalFileSystem());
        _ = session.Load(_root, graphPath: null, rulesPath: rulesPath);
        var graph = session.Graph;
        var host = session.CompilationHost;
        var generation = session.AnalysisGeneration;

        File.WriteAllText(
            rulesPath,
            "{\"rules\":[{\"id\":\"FORBID\",\"source\":\"*\",\"mustNotReference\":\"*\"}]}");

        var changedFiles = session.MaybeRefreshIfStale();

        Assert.Equal(0, changedFiles);
        Assert.Equal(generation + 1, session.AnalysisGeneration);
        Assert.Same(graph, session.Graph);
        Assert.Same(host, session.CompilationHost);
        Assert.NotEmpty(session.Analysis!.Violations);
    }

    [Fact]
    public void Incremental_DescriptorTimestampOnlyChurnKeepsSameCommittedIdentity()
    {
        var projectPath = Path.Combine(_root, "Identity.csproj");
        using var session = new GraphSession(new PhysicalFileSystem());
        _ = session.Load(_root, graphPath: null, rulesPath: null);
        var snapshot = session.CurrentSnapshot;
        var snapshotId = session.SnapshotId;
        var identity = session.AnalysisIdentity;

        File.SetLastWriteTimeUtc(projectPath, File.GetLastWriteTimeUtc(projectPath).AddMinutes(1));
        var json = session.Load(_root, graphPath: null, rulesPath: null, incremental: true);
        using var result = JsonDocument.Parse(json);

        Assert.Equal("incremental-noop", result.RootElement.GetProperty("mode").GetString());
        Assert.Same(snapshot, session.CurrentSnapshot);
        Assert.Equal(snapshotId, session.SnapshotId);
        Assert.Equal(identity, session.AnalysisIdentity);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

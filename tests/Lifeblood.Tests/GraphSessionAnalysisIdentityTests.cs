using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Domain.Workspaces;
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

    [Fact]
    public void PrepareAnalysis_FingerprintMatchesCommittedFullAnalysis()
    {
        using var session = new GraphSession(new PhysicalFileSystem());
        var request = new AnalyzeToolRequest
        {
            ProjectPath = _root,
            ReadOnly = false,
            DefineProfiles = new[] { "Editor" },
            ExcludePaths = new[] { "obj/**", "bin/**" },
        };

        var prepared = session.PrepareAnalysis(request);
        _ = session.Load(
            request.ProjectPath,
            graphPath: null,
            rulesPath: null,
            readOnly: request.ReadOnly,
            defineProfiles: request.DefineProfiles,
            excludePaths: request.ExcludePaths,
            expectedAnalysisKey: prepared.Identity.AnalysisKey);

        Assert.Equal(prepared.Identity, session.AnalysisIdentity);
    }

    [Fact]
    public void PrepareAnalysis_CoalescingPolicyDistinguishesChangeScanAndReceiptProjection()
    {
        using var session = new GraphSession(new PhysicalFileSystem());
        var baseline = session.PrepareAnalysis(new AnalyzeToolRequest
        {
            ProjectPath = _root,
            Incremental = true,
        });
        var authoritativeEmpty = session.PrepareAnalysis(new AnalyzeToolRequest
        {
            ProjectPath = _root,
            Incremental = true,
            AuthoritativeChangedFiles = Array.Empty<string>(),
        });
        var detailed = session.PrepareAnalysis(new AnalyzeToolRequest
        {
            ProjectPath = _root,
            Incremental = true,
            ChangeReceiptMode = "detail",
            ChangeReceiptLimit = 1,
        });

        Assert.Equal(baseline.Identity.AnalysisKey, authoritativeEmpty.Identity.AnalysisKey);
        Assert.Equal(baseline.Identity.AnalysisKey, detailed.Identity.AnalysisKey);
        Assert.NotEqual(baseline.CoalescingKey, authoritativeEmpty.CoalescingKey);
        Assert.NotEqual(baseline.CoalescingKey, detailed.CoalescingKey);
        Assert.NotEqual(authoritativeEmpty.CoalescingKey, detailed.CoalescingKey);
    }

    [Fact]
    public void PrepareAnalysis_CommittedIncrementalBaseAvoidsDuplicateContentPreflight()
    {
        var fileSystem = new CountingReadFileSystem(new PhysicalFileSystem());
        using var session = new GraphSession(fileSystem);
        _ = session.Load(_root, graphPath: null, rulesPath: null);
        fileSystem.ResetReadCount();

        var prepared = session.PrepareAnalysis(
            new AnalyzeToolRequest { ProjectPath = _root, Incremental = true },
            AnalysisPreparationMode.CommittedIncrementalBase);

        Assert.Equal(0, fileSystem.ReadAllTextCount);
        Assert.Equal(session.AnalysisIdentity, prepared.Identity);
        Assert.Null(prepared.ExpectedAnalysisKey);
    }

    [Fact]
    public void PrepareAnalysis_CommittedIncrementalPolicySeparatesScopeAndRetention()
    {
        using var session = new GraphSession(new PhysicalFileSystem());
        _ = session.Load(_root, graphPath: null, rulesPath: null, defineProfiles: new[] { "Editor" });

        PreparedAnalyzeRequest Prepare(AnalyzeToolRequest request)
            => session.PrepareAnalysis(request, AnalysisPreparationMode.CommittedIncrementalBase);

        var baseline = Prepare(new AnalyzeToolRequest
        {
            ProjectPath = _root,
            Incremental = true,
            DefineProfiles = new[] { "Editor" },
        });
        var player = Prepare(new AnalyzeToolRequest
        {
            ProjectPath = _root,
            Incremental = true,
            DefineProfiles = new[] { "Player" },
        });
        var excluded = Prepare(new AnalyzeToolRequest
        {
            ProjectPath = _root,
            Incremental = true,
            DefineProfiles = new[] { "Editor" },
            ExcludePaths = new[] { "generated/**" },
        });
        var readOnly = Prepare(new AnalyzeToolRequest
        {
            ProjectPath = _root,
            Incremental = true,
            ReadOnly = true,
            DefineProfiles = new[] { "Editor" },
        });

        Assert.Equal(baseline.Identity, player.Identity);
        Assert.Equal(baseline.Identity, excluded.Identity);
        Assert.Equal(baseline.Identity, readOnly.Identity);
        Assert.NotEqual(baseline.CoalescingKey, player.CoalescingKey);
        Assert.NotEqual(baseline.CoalescingKey, excluded.CoalescingKey);
        Assert.NotEqual(baseline.CoalescingKey, readOnly.CoalescingKey);
    }

    [Fact]
    public void Load_ChangedInputAfterPreparationRejectsCandidateAndPreservesPublication()
    {
        using var session = new GraphSession(new PhysicalFileSystem());
        _ = session.Load(_root, graphPath: null, rulesPath: null);
        var request = new AnalyzeToolRequest { ProjectPath = _root, Incremental = true };
        var prepared = session.PrepareAnalysis(request);
        var committedSnapshot = session.CurrentSnapshot;
        Assert.Equal(committedSnapshot.Identity, prepared.Identity);

        File.WriteAllText(
            Path.Combine(_root, "Types.cs"),
            "namespace Identity; public class Changed { public int Value => 1; }");

        var failure = Assert.Throws<AnalysisInputChangedException>(() =>
            session.Load(
                request.ProjectPath,
                graphPath: null,
                rulesPath: null,
                incremental: true,
                expectedAnalysisKey: prepared.Identity.AnalysisKey));

        Assert.Equal(prepared.Identity.AnalysisKey, failure.Expected);
        Assert.NotEqual(prepared.Identity.AnalysisKey, failure.Actual);
        Assert.Same(committedSnapshot, session.CurrentSnapshot);
        Assert.True(session.IsLoaded);

        _ = session.Load(request.ProjectPath, graphPath: null, rulesPath: null, incremental: true);
        Assert.NotSame(committedSnapshot, session.CurrentSnapshot);
        Assert.NotEqual(prepared.Identity.AnalysisKey, session.AnalysisIdentity!.AnalysisKey);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class CountingReadFileSystem : Lifeblood.Application.Ports.Infrastructure.IFileSystem
    {
        private readonly Lifeblood.Application.Ports.Infrastructure.IFileSystem _inner;

        public CountingReadFileSystem(Lifeblood.Application.Ports.Infrastructure.IFileSystem inner)
            => _inner = inner;

        public int ReadAllTextCount { get; private set; }

        public void ResetReadCount() => ReadAllTextCount = 0;

        public string ReadAllText(string path)
        {
            ReadAllTextCount++;
            return _inner.ReadAllText(path);
        }

        public IEnumerable<string> ReadLines(string path) => _inner.ReadLines(path);
        public Stream OpenRead(string path) => _inner.OpenRead(path);
        public Stream OpenWrite(string path) => _inner.OpenWrite(path);
        public bool FileExists(string path) => _inner.FileExists(path);
        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public string[] FindFiles(string directory, string pattern, bool recursive = true)
            => _inner.FindFiles(directory, pattern, recursive);
        public DateTime GetLastWriteTimeUtc(string path) => _inner.GetLastWriteTimeUtc(path);
    }
}

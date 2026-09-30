using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Domain.Workspaces;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

public sealed class SharedSemanticRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lifeblood-hot-" + Guid.NewGuid().ToString("N"));
    public SharedSemanticRetentionTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Hot.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");
        File.WriteAllText(Path.Combine(_root, "Hot.cs"), "namespace Hot; public class C { public void M() {} }");
    }

    [Fact]
    public void SharedCompatibleReadOnlyRequestKeepsTheExactHotPublication()
    {
        using var session = new GraphSession(new PhysicalFileSystem(), preserveSharedSemanticPublication: true);
        session.Load(_root, null, null);
        var original = session.CurrentSnapshot;
        var host = session.CompilationHost;
        using var readOnly = JsonDocument.Parse(session.Load(_root, null, null, readOnly: true));
        Assert.Same(original, session.CurrentSnapshot);
        Assert.Same(host, session.CompilationHost);
        Assert.Equal(original.SnapshotId, session.HotSemanticSnapshotId);
        Assert.Equal("reusedCurrent", readOnly.RootElement.GetProperty("publication").GetProperty("action").GetString());
        Assert.Equal(AnalysisRetentionMode.RetainedSemantic, session.AnalysisIdentity!.Spec.RetentionMode);
    }

    [Fact]
    public void PrivateReadOnlyRequestStillReleasesSemanticState()
    {
        using var session = new GraphSession(new PhysicalFileSystem());
        session.Load(_root, null, null);
        session.Load(_root, null, null, readOnly: true);
        Assert.False(session.HasCompilationState);
        Assert.Equal(SnapshotId.None, session.HotSemanticSnapshotId);
        Assert.Equal(AnalysisRetentionMode.GraphOnly, session.AnalysisIdentity!.Spec.RetentionMode);
    }

    [Fact]
    public void GraphOnlyCurrentCanRestoreTheOriginalHotServicesAfterExactRecapture()
    {
        using var session = new GraphSession(new PhysicalFileSystem(), preserveSharedSemanticPublication: true);
        session.Load(_root, null, null);
        var original = session.CurrentSnapshot;
        var host = session.CompilationHost;
        var graphPath = ExportCurrentGraph(session);
        session.Load(null, graphPath, null);
        Assert.False(session.HasCompilationState);
        Assert.Equal(original.SnapshotId, session.HotSemanticSnapshotId);
        Assert.All(session.SnapshotHistory, entry => Assert.False(entry.Snapshot.RetainsSemanticServices));
        var graphOnlyGeneration = session.AnalysisGeneration;
        using var recovery = JsonDocument.Parse(session.Load(_root, null, null, incremental: true));
        Assert.Same(host, session.CompilationHost);
        Assert.Same(original.Graph, session.Graph);
        Assert.Equal(graphOnlyGeneration + 1, session.AnalysisGeneration);
        Assert.Equal("restoredHot", recovery.RootElement.GetProperty("publication").GetProperty("action").GetString());
        Assert.Equal(0, original.ActiveLeaseCount);
    }

    [Fact]
    public void ChangedInputsCannotRestoreAnOldHotBundleAndReplacementIsExplicit()
    {
        using var session = new GraphSession(new PhysicalFileSystem(), preserveSharedSemanticPublication: true);
        session.Load(_root, null, null);
        var original = session.CurrentSnapshot;
        var originalHost = session.CompilationHost;
        File.AppendAllText(Path.Combine(_root, "Hot.cs"), " public class Added {} ");
        session.Load(_root, null, null, readOnly: true);
        Assert.False(session.HasCompilationState);
        Assert.Equal("preserved", session.SemanticRetention!.Action);
        Assert.Equal(original.SnapshotId, session.HotSemanticSnapshotId);
        Assert.Equal(1, session.ResidentSemanticBaseCount);
        using var rejected = JsonDocument.Parse(session.Load(_root, null, null, incremental: true));
        Assert.Equal("rejected", rejected.RootElement.GetProperty("mode").GetString());
        Assert.Equal("compilationStateUnavailable", rejected.RootElement.GetProperty("fallbackReason").GetString());
        session.Load(_root, null, null);
        Assert.NotSame(originalHost, session.CompilationHost);
        Assert.Equal("replaced", session.SemanticRetention!.Action);
        Assert.Equal(original.SnapshotId.Value, session.SemanticRetention.DisplacedHotSnapshotId);
        Assert.Equal("usage", session.SemanticRetention.RecoveryCostReceipt);
        Assert.Equal(1, session.SemanticRetention.HotSemanticOwnerCount);
        Assert.Equal(0, original.ActiveLeaseCount);
    }

    [Fact]
    public void ExplicitResetReleasesTheHotLeaseAndReportsEviction()
    {
        using var session = new GraphSession(new PhysicalFileSystem(), preserveSharedSemanticPublication: true);
        session.Load(_root, null, null);
        var original = session.CurrentSnapshot;
        Assert.Equal(1, original.ActiveLeaseCount);
        session.Dispose();
        Assert.Equal(0, original.ActiveLeaseCount);
        Assert.Equal(SnapshotId.None, session.HotSemanticSnapshotId);
        Assert.Equal(0, session.ResidentSemanticBaseCount);
        Assert.Equal("evicted", session.SemanticRetention!.Action);
        Assert.Equal("sessionReset", session.SemanticRetention.Reason);
        Assert.Equal(original.SnapshotId.Value, session.SemanticRetention.DisplacedHotSnapshotId);
        Assert.Throws<ObjectDisposedException>(() => session.SnapshotHistory);
    }

    [Fact]
    public void RecoveryInheritsHotScopeAndRejectsInputsChangedAfterPreparation()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Library"));
        var rulesPath = Path.Combine(_root, "rules.json");
        File.WriteAllText(rulesPath, "{\"rules\":[]}");
        using var session = new GraphSession(new PhysicalFileSystem(), preserveSharedSemanticPublication: true);
        session.Load(_root, null, rulesPath, defineProfiles: new[] { "Player", "Editor" }, excludePaths: new[] { "Ignored/**" });
        var hot = session.CurrentSnapshot;
        var graphPath = ExportCurrentGraph(session);
        session.Load(null, graphPath, null);
        var graphOnly = session.CurrentSnapshot;
        var prepared = session.PrepareAnalysis(new AnalyzeToolRequest { ProjectPath = _root, Incremental = true },
            AnalysisPreparationMode.CommittedIncrementalBase);
        Assert.Equal(hot.Identity, prepared.Identity);
        Assert.NotNull(prepared.ExpectedAnalysisKey);
        File.AppendAllText(Path.Combine(_root, "Hot.cs"), " public class ChangedAfterPreparation {} ");
        Assert.Throws<AnalysisInputChangedException>(() => session.Load(_root, null, rulesPath, incremental: true,
            defineProfiles: prepared.Request.DefineProfiles, excludePaths: prepared.Request.ExcludePaths,
            expectedAnalysisKey: prepared.ExpectedAnalysisKey));
        Assert.Same(graphOnly, session.CurrentSnapshot);
        Assert.Equal(hot.SnapshotId, session.HotSemanticSnapshotId);
    }

    [Theory]
    [InlineData("descriptor")]
    [InlineData("rules")]
    [InlineData("exclusions")]
    [InlineData("profileOrder")]
    public void RecoveryRefusesChangedSemanticScope(string change)
    {
        Directory.CreateDirectory(Path.Combine(_root, "Library"));
        var rulesPath = Path.Combine(_root, "rules.json");
        File.WriteAllText(rulesPath, "{\"rules\":[]}");
        using var session = new GraphSession(new PhysicalFileSystem(), preserveSharedSemanticPublication: true);
        session.Load(_root, null, rulesPath, defineProfiles: new[] { "Editor", "Player" });
        var hot = session.CurrentSnapshot;
        session.Load(null, ExportCurrentGraph(session), null);
        var graphOnly = session.CurrentSnapshot;
        if (change == "descriptor") File.AppendAllText(Path.Combine(_root, "Hot.csproj"), "<!-- changed -->");
        if (change == "rules") File.WriteAllText(rulesPath, "{\"rules\":[{\"id\":\"Changed\",\"source\":\"*\",\"mustNotReference\":\"*\"}]}");
        using var refused = JsonDocument.Parse(session.Load(_root, null, rulesPath, incremental: true,
            defineProfiles: change == "profileOrder" ? new[] { "Player", "Editor" } : new[] { "Editor", "Player" },
            excludePaths: change == "exclusions" ? new[] { "Hot.cs" } : null));
        Assert.Equal("rejected", refused.RootElement.GetProperty("mode").GetString());
        Assert.Same(graphOnly, session.CurrentSnapshot);
        Assert.Equal(hot.SnapshotId, session.HotSemanticSnapshotId);
        Assert.False(session.HasCompilationState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RuleOnlyPublicationDoesNotClaimASemanticRebuild(bool shared)
    {
        var rulesPath = Path.Combine(_root, "rules.json");
        File.WriteAllText(rulesPath, "{\"rules\":[]}");
        using var session = new GraphSession(new PhysicalFileSystem(), preserveSharedSemanticPublication: shared);
        session.Load(_root, null, rulesPath);
        var host = session.CompilationHost;
        File.WriteAllText(rulesPath, "{\"rules\":[{\"id\":\"Changed\",\"source\":\"*\",\"mustNotReference\":\"*\"}]}");
        session.Load(_root, null, null, incremental: true);
        Assert.Same(host, session.CompilationHost);
        Assert.Null(session.SemanticRetention!.RecoveryCostReceipt);
    }

    [Fact]
    public void RetentionReceiptPinsTheAdditiveWireContract()
    {
        using var session = new GraphSession(new PhysicalFileSystem(), preserveSharedSemanticPublication: true);
        session.Load(_root, null, null);
        var payload = JsonSerializer.SerializeToElement(session.SemanticRetention,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var expected = new[] {
            "policy", "action", "reason", "publicationSnapshotId", "hotSnapshotId",
            "displacedHotSnapshotId", "hotSemanticOwnerCount", "compilationServicesAvailable",
            "recoveryCostReceipt", "publishingClientLeaseId", "displacedClientLeaseId" };
        Assert.Equal(expected.OrderBy(name => name), payload.EnumerateObject().Select(property => property.Name).OrderBy(name => name));
        Assert.Equal("sharedHotPublication", payload.GetProperty("policy").GetString());
        Assert.Equal(1, payload.GetProperty("hotSemanticOwnerCount").GetInt32());
        Assert.True(payload.GetProperty("compilationServicesAvailable").GetBoolean());
        Assert.Equal(session.SnapshotId.Value, payload.GetProperty("publicationSnapshotId").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("publishingClientLeaseId").ValueKind);
    }

    [Fact]
    public void SemanticCompatibilityIgnoresOnlyRetentionPreference()
    {
        var rules = new RuleSetIdentity(RuleSetSourceKind.None, null, ContentFingerprint.ComputeUtf8("test", "rules"));
        WorkspaceAnalysisIdentity Identity(AnalysisRetentionMode retention, string text = "source", string descriptor = "descriptor",
            string[]? profiles = null, string[]? exclusions = null, string? policy = "canonical-v2")
            => new(WorkspaceKey.FromCanonicalIdentity(_root),
                new AnalysisSpec(profiles ?? new[] { "Editor", "Player" }, exclusions, retention, AnalysisDescriptorPolicy.WorkspaceDiscovery, rules, policy),
                new SourceFingerprint(new[] { new FingerprintEntry("Hot.cs", ContentFingerprint.ComputeUtf8("test", text)) },
                    new[] { new FingerprintEntry("Hot.csproj", ContentFingerprint.ComputeUtf8("test", descriptor)) }));
        var hot = Identity(AnalysisRetentionMode.RetainedSemantic);
        var graphOnly = Identity(AnalysisRetentionMode.GraphOnly);
        Assert.True(hot.HasSameSemanticInputs(graphOnly));
        Assert.NotEqual(hot.AnalysisKey, graphOnly.AnalysisKey);
        Assert.False(hot.HasSameSemanticInputs(Identity(AnalysisRetentionMode.GraphOnly, text: "changed")));
        Assert.False(hot.HasSameSemanticInputs(Identity(AnalysisRetentionMode.GraphOnly, descriptor: "changed")));
        Assert.False(hot.HasSameSemanticInputs(Identity(AnalysisRetentionMode.GraphOnly, profiles: new[] { "Player", "Editor" })));
        Assert.False(hot.HasSameSemanticInputs(Identity(AnalysisRetentionMode.GraphOnly, exclusions: new[] { "Tests/**" })));
        Assert.False(hot.HasSameSemanticInputs(Identity(AnalysisRetentionMode.GraphOnly, policy: "canonical-v1")));
        var otherRules = new RuleSetIdentity(RuleSetSourceKind.File, "rules.json", ContentFingerprint.ComputeUtf8("test", "different rules"));
        Assert.False(hot.HasSameSemanticInputs(new WorkspaceAnalysisIdentity(hot.Workspace,
            new AnalysisSpec(hot.Spec.DefineProfiles, null, AnalysisRetentionMode.GraphOnly,
                AnalysisDescriptorPolicy.WorkspaceDiscovery, otherRules, "canonical-v2"), hot.Source)));
        Assert.False(hot.HasSameSemanticInputs(new WorkspaceAnalysisIdentity(hot.Workspace,
            new AnalysisSpec(hot.Spec.DefineProfiles, null, AnalysisRetentionMode.GraphOnly,
                AnalysisDescriptorPolicy.ImportedGraph, rules, "canonical-v2"), hot.Source)));
        Assert.False(hot.HasSameSemanticInputs(new WorkspaceAnalysisIdentity(WorkspaceKey.FromCanonicalIdentity("another workspace"), graphOnly.Spec, graphOnly.Source)));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string ExportCurrentGraph(GraphSession session)
    {
        var graphPath = Path.Combine(_root, "graph.json");
        using var stream = File.Create(graphPath);
        new Lifeblood.Adapters.JsonGraph.JsonGraphExporter().Export(new Lifeblood.Domain.Graph.GraphDocument {
            Graph = session.Graph!, Language = "csharp", Adapter = session.AdapterCapability! }, stream);
        return graphPath;
    }
}

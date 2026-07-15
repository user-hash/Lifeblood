using Lifeblood.Domain.Workspaces;
using Xunit;

namespace Lifeblood.Tests;

public sealed class WorkspaceAnalysisIdentityTests
{
    [Fact]
    public void AnalysisSpec_NormalizesNonSemanticGlobOrderButPreservesProfileOrder()
    {
        var rules = RuleIdentity("same-rules", RuleSetSourceKind.BuiltIn, "hexagonal");
        var first = new AnalysisSpec(
            new[] { "Editor", "Player" },
            new[] { " B\\** ", "a/**", "A/**" },
            AnalysisRetentionMode.RetainedSemantic,
            AnalysisDescriptorPolicy.WorkspaceDiscovery,
            rules);
        var equivalent = new AnalysisSpec(
            new[] { "Editor", "Player" },
            new[] { "A/**", "b/**" },
            AnalysisRetentionMode.RetainedSemantic,
            AnalysisDescriptorPolicy.WorkspaceDiscovery,
            RuleIdentity("same-rules", RuleSetSourceKind.File, "D:/repo/rules.json"));
        var reversedProfiles = new AnalysisSpec(
            new[] { "Player", "Editor" },
            new[] { "a/**", "b/**" },
            AnalysisRetentionMode.RetainedSemantic,
            AnalysisDescriptorPolicy.WorkspaceDiscovery,
            rules);

        Assert.Equal(first, equivalent);
        Assert.Equal(first.Fingerprint, equivalent.Fingerprint);
        Assert.Equal(new[] { "a/**", "B/**" }, first.ExcludePathGlobs);
        Assert.NotEqual(first, reversedProfiles);
    }

    [Fact]
    public void SourceFingerprint_IsContentAuthoritativeAndOrderIndependent()
    {
        var a = ContentFingerprint.ComputeUtf8("test.content", "a");
        var b = ContentFingerprint.ComputeUtf8("test.content", "b");
        var descriptor = ContentFingerprint.ComputeUtf8("test.content", "descriptor-v1");
        var first = new SourceFingerprint(
            new[] { new FingerprintEntry("b.cs", b), new FingerprintEntry("a.cs", a) },
            new[] { new FingerprintEntry("project.csproj", descriptor) });
        var reordered = new SourceFingerprint(
            new[] { new FingerprintEntry("a.cs", a), new FingerprintEntry("b.cs", b) },
            new[] { new FingerprintEntry("project.csproj", descriptor) });
        var changedDescriptor = new SourceFingerprint(
            new[] { new FingerprintEntry("a.cs", a), new FingerprintEntry("b.cs", b) },
            new[]
            {
                new FingerprintEntry(
                    "project.csproj",
                    ContentFingerprint.ComputeUtf8("test.content", "descriptor-v2")),
            });

        Assert.Equal(first, reordered);
        Assert.Equal(2, first.SourceFileCount);
        Assert.Equal(1, first.DescriptorFileCount);
        Assert.NotEqual(first, changedDescriptor);
        Assert.Equal(first.SourceContent, changedDescriptor.SourceContent);
        Assert.NotEqual(first.Descriptors, changedDescriptor.Descriptors);
    }

    [Fact]
    public void AnalysisKey_ChangesOnlyWhenWorkspaceSpecOrSourceChanges()
    {
        var workspace = WorkspaceKey.FromCanonicalIdentity("D:/REPO");
        var rules = RuleIdentity("rules-v1", RuleSetSourceKind.BuiltIn, "lifeblood");
        var spec = new AnalysisSpec(
            new[] { "Editor" },
            Array.Empty<string>(),
            AnalysisRetentionMode.RetainedSemantic,
            AnalysisDescriptorPolicy.WorkspaceDiscovery,
            rules);
        var source = new SourceFingerprint(
            new[]
            {
                new FingerprintEntry(
                    "Program.cs",
                    ContentFingerprint.ComputeUtf8("test.content", "class Program {}")),
            },
            Array.Empty<FingerprintEntry>());

        var first = new WorkspaceAnalysisIdentity(workspace, spec, source);
        var same = new WorkspaceAnalysisIdentity(workspace, spec, source);
        var graphOnly = new WorkspaceAnalysisIdentity(
            workspace,
            new AnalysisSpec(
                new[] { "Editor" },
                Array.Empty<string>(),
                AnalysisRetentionMode.GraphOnly,
                AnalysisDescriptorPolicy.WorkspaceDiscovery,
                rules),
            source);

        Assert.Equal(first.BaseKey, same.BaseKey);
        Assert.Equal(first.AnalysisKey, same.AnalysisKey);
        Assert.NotEqual(first.BaseKey, graphOnly.BaseKey);
        Assert.NotEqual(first.AnalysisKey, graphOnly.AnalysisKey);
        Assert.StartsWith("base_", first.BaseKey.Value, StringComparison.Ordinal);
        Assert.StartsWith("analysis_", first.AnalysisKey.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void ContentFingerprint_WireFormRoundTripsAndRejectsNonCanonicalHex()
    {
        var fingerprint = ContentFingerprint.ComputeUtf8("test.content", "hello");

        Assert.True(ContentFingerprint.TryParse(fingerprint.Value, out var parsed));
        Assert.Equal(fingerprint, parsed);
        Assert.False(ContentFingerprint.TryParse(fingerprint.Value.ToUpperInvariant(), out _));
    }

    private static RuleSetIdentity RuleIdentity(
        string content,
        RuleSetSourceKind sourceKind,
        string source)
        => new(
            sourceKind,
            source,
            ContentFingerprint.ComputeUtf8("lifeblood.rules-content.v1", content));
}

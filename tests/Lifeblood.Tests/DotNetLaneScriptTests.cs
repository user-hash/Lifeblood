using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Tracks the .NET runtime/packaging lane scripts as machine-readable,
/// report-first tooling. These tests are source-shape ratchets; the scripts
/// themselves remain opt-in because they build/package out of process.
/// </summary>
public class DotNetLaneScriptTests
{
    [Fact]
    public void ExperimentalTargetLane_SkipsHonestly_WhenTargetSdkIsUnavailable()
    {
        var script = ReadRepoFile("tools", "dotnet-lanes", "run-lifeblood-experimental-target.ps1");

        Assert.Contains("No installed .NET SDK can build", script);
        Assert.Contains("FailWhenUnavailable", script);
        Assert.Contains("Production project files remain pinned to net8.0", script);
        Assert.Contains("Copy-SourceTree", script);
        Assert.Contains("Set-CopiedProjectTargetFrameworks", script);
        Assert.Contains("retargets only the copied solution projects", script);
        Assert.Contains("omits root global.json", script);
        Assert.Contains("workDirFallbackReason", script);
        Assert.Contains("Could not clean the previous experimental work directory", script);
        Assert.Contains("--disable-parallel", script);
        Assert.Contains("RestoreIgnoreFailedSources", script);
        Assert.Contains("--ignore-failed-sources", script);
        Assert.DoesNotContain("-p:TargetFramework=$TargetFramework", script);
    }

    [Fact]
    public void ExperimentalTargetLane_EmitsSemanticTestAndSchemaReceipts()
    {
        var script = ReadRepoFile("tools", "dotnet-lanes", "run-lifeblood-experimental-target.ps1");

        Assert.Contains("Read-StatusAnchors", script);
        Assert.Contains("Get-SchemaSnapshotInventory", script);
        Assert.Contains("testSummary", script);
        Assert.Contains("testComparison", script);
        Assert.Contains("semantic-self-analyze", script);
        Assert.Contains("semanticComparison", script);
        Assert.Contains("matchesStatusAnchors", script);
        Assert.Contains("docs/STATUS.md selfAnalyze* anchors from the production net8.0 lane", script);
    }

    [Fact]
    public void ToolPackagingLane_CoversDotnetToolExecAndDnxAsOptionalNet10Smokes()
    {
        var script = ReadRepoFile("tools", "dotnet-lanes", "run-lifeblood-tool-packaging.ps1");

        Assert.Contains("smoke-dotnet-tool-exec-help", script);
        Assert.Contains("smoke-dnx-help", script);
        Assert.Contains("smoke-cli-help-contract", script);
        Assert.Contains("requires .NET 10.0.100 SDK or later", script);
        Assert.Contains("this script never publishes packages", script);
        Assert.Contains("artifactRootFallbackReason", script);
        Assert.Contains("Could not clean the previous artifact root", script);
        Assert.Contains("Add-SkippedStep", script);
    }

    [Fact]
    public void ToolPackagingLane_CarriesReportOnlyPublishExperiments()
    {
        var script = ReadRepoFile("tools", "dotnet-lanes", "run-lifeblood-tool-packaging.ps1");

        Assert.Contains("RunPublishExperiments", script);
        Assert.Contains("PublishRuntimeIdentifier", script);
        Assert.Contains("publishExperiments", script);
        Assert.Contains("report-only", script);
        Assert.Contains("experiment-publish-cli-framework-dependent", script);
        Assert.Contains("experiment-publish-mcp-framework-dependent", script);
        Assert.Contains("experiment-publish-cli-self-contained", script);
        Assert.Contains("experiment-publish-cli-trimmed", script);
        Assert.Contains("experiment-publish-cli-aot", script);
        Assert.Contains("experiment-publish-mcp-self-contained", script);
        Assert.Contains("MCP trimming is intentionally skipped", script);
        Assert.Contains("PublishTrimmed=true", script);
        Assert.Contains("PublishAot=true", script);
    }

    private static string ReadRepoFile(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()));

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "Lifeblood.sln")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return current!.FullName;
    }
}

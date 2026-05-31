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
        Assert.DoesNotContain("-p:TargetFramework=$TargetFramework", script);
    }

    [Fact]
    public void ToolPackagingLane_CoversDotnetToolExecAndDnxAsOptionalNet10Smokes()
    {
        var script = ReadRepoFile("tools", "dotnet-lanes", "run-lifeblood-tool-packaging.ps1");

        Assert.Contains("smoke-dotnet-tool-exec-help", script);
        Assert.Contains("smoke-dnx-help", script);
        Assert.Contains("requires .NET 10.0.100 SDK or later", script);
        Assert.Contains("this script never publishes packages", script);
        Assert.Contains("Add-SkippedStep", script);
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

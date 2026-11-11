using System.Text.Json;
using Lifeblood.Application.Ports.Output;
using Lifeblood.CLI;
using Xunit;

namespace Lifeblood.Tests;

public sealed class CliAnalyzeJsonTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lifeblood-cli-json-" + Guid.NewGuid().ToString("N"));
    public CliAnalyzeJsonTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "Library"));
        File.WriteAllText(Path.Combine(root, "App.csproj"), """
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><PropertyGroup>
            <AssemblyName>App</AssemblyName><LangVersion>12</LangVersion><DefineConstants>UNITY_EDITOR</DefineConstants>
            <UnityProjectType>Game:1</UnityProjectType></PropertyGroup>
            <ItemGroup><Compile Include="App.cs" /></ItemGroup></Project>
            """);
        File.WriteAllText(Path.Combine(root, "App.cs"), "namespace App; public class A { public B Value = new B(); } public class B { }");
    }
    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void JsonReceipt_BindsActualExecutionInputsProfilesAndUsageWithoutPublication()
    {
        using var output = new StringWriter();
        Assert.Equal(0, AnalyzeJsonCommand.Run(["--project", root, "--json", "--profiles", "Editor,Player"], output));
        using var json = JsonDocument.Parse(output.ToString());
        var receipt = json.RootElement;
        Assert.True(receipt.GetProperty("success").GetBoolean());
        Assert.Equal("standaloneBatch", receipt.GetProperty("executionMode").GetString());
        Assert.False(receipt.GetProperty("daemonContacted").GetBoolean());
        Assert.False(receipt.GetProperty("semanticServicesRetained").GetBoolean());
        Assert.Equal("notPublished", receipt.GetProperty("publication").GetString());
        Assert.Equal(CliIdentity.Capture().Version, receipt.GetProperty("execution").GetProperty("version").GetString());
        var input = receipt.GetProperty("inputVerification");
        Assert.Equal("matchedAtObservations", input.GetProperty("status").GetString());
        var analyzed = input.GetProperty("analyzed");
        Assert.Equal(input.GetProperty("before").GetProperty("analysisKey").GetString(), analyzed.GetProperty("analysisKey").GetString());
        Assert.Equal(input.GetProperty("after").GetProperty("analysisKey").GetString(), analyzed.GetProperty("analysisKey").GetString());
        Assert.Equal(new[] { "Editor", "Player" }, analyzed.GetProperty("defineProfiles").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("GraphOnly", analyzed.GetProperty("retentionMode").GetString());
        Assert.Equal("notCollected", receipt.GetProperty("diagnostics").GetProperty("compilerDiagnostics").GetString());
        Assert.True(receipt.GetProperty("usage").GetProperty("peakWorkingSetBytes").GetInt64() > 0);
        Assert.True(receipt.GetProperty("callerWallTimeMs").GetInt64() >= receipt.GetProperty("usage").GetProperty("wallTimeMs").GetInt64());
    }

    [Theory]
    [InlineData("source")]
    [InlineData("rules")]
    public void InputChangeDuringRun_IsReportedInsteadOfQualifiedSuccess(string kind)
    {
        var rules = Path.Combine(root, "rules.json");
        File.WriteAllText(rules, """{"rules":[]}""");
        var progress = new CompletionMutation(() =>
        {
            if (kind == "source") File.AppendAllText(Path.Combine(root, "App.cs"), " public class Changed { }");
            else File.WriteAllText(rules, """{"rules":[{"id":"new-rule","source":"None","mustNotReference":"Missing"}]}""");
        });
        using var output = new StringWriter();
        Assert.Equal(3, AnalyzeJsonCommand.Run(["--project", root, "--json", "--rules", "rules.json"], output, progress: progress));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("analysis-input-changed", json.RootElement.GetProperty("failure").GetString());
        Assert.Equal("driftDetected", json.RootElement.GetProperty("inputVerification").GetProperty("status").GetString());
    }

    [Fact]
    public void RuleViolation_RemainsStructuredAndUsesSharedCanonicalRuleIdentity()
    {
        var path = Path.Combine(root, "rules.json");
        File.WriteAllText(path, """{"rules":[{"id":"deny-b","source":"App.A","mustNotReference":"App.B"}]}""");
        using var output = new StringWriter();
        Assert.Equal(2, AnalyzeJsonCommand.Run(["--project", root, "--json", "--rules", "rules.json"], output));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("rule-violations", json.RootElement.GetProperty("failure").GetString());
        Assert.NotEmpty(json.RootElement.GetProperty("rules").GetProperty("violations").EnumerateArray());
        var rules = new Lifeblood.Server.Mcp.AnalysisRuleSetResolver(new Lifeblood.Adapters.CSharp.PhysicalFileSystem())
            .Resolve("rules.json", root);
        Assert.Equal(rules.Identity.Fingerprint.Value, json.RootElement.GetProperty("rules").GetProperty("fingerprint").GetString());
    }

    [Theory]
    [InlineData("--incremental")]
    [InlineData("--json")]
    [InlineData("--unknown")]
    [InlineData("--graph")]
    public void UnsupportedOrDuplicateOptions_ProduceJsonFailure(string option)
    {
        using var output = new StringWriter();
        Assert.Equal(1, AnalyzeJsonCommand.Run(["--project", root, "--json", option], output));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.False(json.RootElement.GetProperty("daemonContacted").GetBoolean());
    }

    private sealed class CompletionMutation(Action change) : IProgressSink
    {
        public void Report(string phase, int current, int total) { if (phase == "Complete") change(); }
    }
}

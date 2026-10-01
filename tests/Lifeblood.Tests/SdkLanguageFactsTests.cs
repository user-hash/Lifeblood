using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

public sealed class SdkLanguageFactsTests
{
    [Theory]
    [InlineData("net8.0", "12.0")]
    [InlineData("net9.0", "13.0")]
    [InlineData("net10.0", "14.0")]
    public void OmittedLanguage_UsesSdkTargetDefault(string framework, string expected)
    {
        using var fixture = new Fixture();
        fixture.WriteProject(framework);
        var discovery = new RoslynModuleDiscovery(new PhysicalFileSystem());
        var module = Assert.Single(discovery.DiscoverModules(fixture.Root));
        Assert.Equal(expected, module.LanguageVersion);
        Assert.Equal("msbuild", module.Properties["languageVersionAuthority"]);
        Assert.False(string.IsNullOrEmpty(module.Properties["sdkVersion"]));
        Assert.Contains(discovery.LastDescriptorContentHashes.Keys,
            path => path.EndsWith(".lifeblood-sdk-facts", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("default", "14.0")]
    [InlineData("latest", "14.0")]
    [InlineData("latestMajor", "14.0")]
    [InlineData("preview", "preview")]
    [InlineData("12", "12")]
    public void ExplicitLanguage_UsesSdkCompilerForSpecialValues(string declared, string expected)
    {
        using var fixture = new Fixture();
        fixture.WriteProject("net8.0", $"<LangVersion>{declared}</LangVersion>");
        var module = Assert.Single(new RoslynModuleDiscovery(new PhysicalFileSystem()).DiscoverModules(fixture.Root));
        Assert.Equal(expected, module.LanguageVersion);
        Assert.Equal(declared, module.Properties["declaredLanguageVersion"]);
    }

    [Fact]
    public void ImportsAndConditions_DetermineTheFinalLanguageFact()
    {
        using var fixture = new Fixture();
        fixture.WriteProject("net8.0", "", """
            <PropertyGroup Condition="'$(TargetFramework)' == 'net10.0'"><LangVersion>14</LangVersion></PropertyGroup>
            <Import Project="custom.targets" />
            """);
        File.WriteAllText(Path.Combine(fixture.Root, "Directory.Build.props"),
            "<Project><PropertyGroup><LangVersion>10</LangVersion></PropertyGroup></Project>");
        var targets = Path.Combine(fixture.Root, "custom.targets");
        File.WriteAllText(targets, "<Project><PropertyGroup><LangVersion>11</LangVersion></PropertyGroup></Project>");
        var discovery = new RoslynModuleDiscovery(new PhysicalFileSystem());
        var module = Assert.Single(discovery.DiscoverModules(fixture.Root));
        Assert.Equal("11", module.LanguageVersion);
        Assert.Contains(targets, discovery.LastSdkInputContentHashes.Keys);
        Assert.Contains(Path.Combine(fixture.Root, "Directory.Build.props"), discovery.LastSdkInputContentHashes.Keys);
    }

    [Fact]
    public void MultiTarget_UsesFirstEffectiveFramework()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.ProjectPath, """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFrameworks>net8.0;net10.0</TargetFrameworks></PropertyGroup></Project>
            """);
        var module = Assert.Single(new RoslynModuleDiscovery(new PhysicalFileSystem()).DiscoverModules(fixture.Root));
        Assert.Equal("net8.0", module.TargetFramework);
        Assert.Equal("12.0", module.LanguageVersion);
    }

    [Fact]
    public void Net8_DefaultRejectsCSharp14InRetainedCompilation()
    {
        using var fixture = new Fixture();
        fixture.WriteProject("net8.0");
        File.WriteAllText(Path.Combine(fixture.Root, "Empty.cs"), """
            public static class Extensions { extension(string value) { public int Count() => value.Length; } }
            """);
        var analyzer = new RoslynWorkspaceAnalyzer(new PhysicalFileSystem());
        analyzer.AnalyzeWorkspace(fixture.Root, new AnalysisConfig { RetainCompilations = true });
        var compilation = analyzer.Compilations!["Test"];
        Assert.All(compilation.SyntaxTrees, tree =>
            Assert.Equal(LanguageVersion.CSharp12, ((CSharpParseOptions)tree.Options).LanguageVersion));
        Assert.Contains(compilation.GetDiagnostics(), diagnostic => diagnostic.Id == "CS9202"
            && diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public void ImportedEditWithPreservedTimestamp_RequiresFallbackAndRefreshesLanguage()
    {
        using var fixture = new Fixture();
        fixture.WriteProject("net8.0");
        var props = Path.Combine(fixture.Root, "Directory.Build.props");
        File.WriteAllText(props, "<Project><PropertyGroup><LangVersion>12</LangVersion></PropertyGroup></Project>");
        var analyzer = new RoslynWorkspaceAnalyzer(new PhysicalFileSystem());
        analyzer.AnalyzeWorkspace(fixture.Root, new AnalysisConfig { RetainCompilations = true });
        var original = analyzer.CurrentSourceFingerprint;
        var stamp = File.GetLastWriteTimeUtc(props);
        File.WriteAllText(props, "<Project><PropertyGroup><LangVersion>13</LangVersion></PropertyGroup></Project>");
        File.SetLastWriteTimeUtc(props, stamp);
        var result = analyzer.IncrementalAnalyze(new AnalysisConfig { RetainCompilations = true, AllowFullFallback = true });
        Assert.Equal(IncrementalMode.FullFallback, result.Mode);
        Assert.Equal(FallbackReason.ModuleDescriptorChanged, result.Reason);
        Assert.Contains("descriptorKind=sdk", result.Detail);
        Assert.NotEqual(original, analyzer.CurrentSourceFingerprint);
        Assert.All(analyzer.Compilations!["Test"].SyntaxTrees, tree =>
            Assert.Equal(LanguageVersion.CSharp13, ((CSharpParseOptions)tree.Options).LanguageVersion));
    }

    [Fact]
    public void GlobalJsonAddition_ChangesIdentityAndRejectsUnapprovedFallback()
    {
        using var fixture = new Fixture();
        fixture.WriteProject("net8.0");
        var discovery = new RoslynModuleDiscovery(new PhysicalFileSystem());
        var sdk = Assert.Single(discovery.DiscoverModules(fixture.Root)).Properties["sdkVersion"];
        var analyzer = new RoslynWorkspaceAnalyzer(new PhysicalFileSystem());
        var config = new AnalysisConfig { RetainCompilations = true };
        analyzer.AnalyzeWorkspace(fixture.Root, config);
        var committed = analyzer.CurrentSourceFingerprint;
        File.WriteAllText(Path.Combine(fixture.Root, "global.json"),
            System.Text.Json.JsonSerializer.Serialize(new { sdk = new { version = sdk, rollForward = "disable" } }));
        var fresh = analyzer.CaptureAnalysisInputs(fixture.Root, config);
        Assert.NotEqual(committed, fresh.SourceFingerprint);
        var result = analyzer.IncrementalAnalyze(config);
        Assert.Equal(IncrementalMode.Rejected, result.Mode);
        Assert.Equal(FallbackReason.ModuleDescriptorChanged, result.Reason);
        Assert.Equal(committed, analyzer.CurrentSourceFingerprint);
    }

    [Fact]
    public void GeneratedUnityDescriptor_DoesNotInvokeSdkEvaluation()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.ProjectPath, """
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
            <PropertyGroup><AssemblyName>Test</AssemblyName><LangVersion>9.0</LangVersion><UnityProjectType>Game:1</UnityProjectType></PropertyGroup>
            <ItemGroup><Compile Include="Empty.cs"/></ItemGroup></Project>
            """);
        var discovery = new RoslynModuleDiscovery(new PhysicalFileSystem());
        var module = Assert.Single(discovery.DiscoverModules(fixture.Root));
        Assert.Equal("9.0", module.LanguageVersion);
        Assert.Equal("descriptor", module.Properties["languageVersionAuthority"]);
        Assert.Empty(discovery.LastSdkInputContentHashes);
    }

    [Fact]
    public void BatchEvaluation_PreservesEachProjectsEffectiveFactsAndEscapedPaths()
    {
        using var fixture = new Fixture();
        fixture.WriteProject("net8.0");
        var nested = Path.Combine(fixture.Root, "module with spaces & unicode č");
        Directory.CreateDirectory(nested);
        var second = Path.Combine(nested, "Second.csproj");
        File.WriteAllText(second, """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
            """);

        var facts = SdkProjectFactsReader.Read(fixture.Root, new[] { fixture.ProjectPath, second });
        Assert.Equal(2, facts.Count);
        Assert.Equal("12.0", facts[fixture.ProjectPath].LanguageVersion);
        Assert.Equal("14.0", facts[second].LanguageVersion);
        Assert.NotEmpty(facts[fixture.ProjectPath].Imports);
        Assert.NotEmpty(facts[second].Imports);
    }

    [Fact]
    public void UnsupportedLanguage_RejectsDiscoveryInsteadOfGuessing()
    {
        using var fixture = new Fixture();
        fixture.WriteProject("net8.0", "<LangVersion>unsupported-language</LangVersion>");
        var discovery = new RoslynModuleDiscovery(new PhysicalFileSystem());
        var failure = Assert.Throws<InvalidOperationException>(() => discovery.DiscoverModules(fixture.Root));
        Assert.Contains("SDK project evaluation failed", failure.Message);
        fixture.WriteProject("net8.0");
        Assert.Equal("12.0", Assert.Single(discovery.DiscoverModules(fixture.Root)).LanguageVersion);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"lifeblood-sdk-language-{Guid.NewGuid():N}");
        public string ProjectPath => Path.Combine(Root, "Test.csproj");
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "Empty.cs"), "public class Empty { }");
        }
        public void WriteProject(string framework, string properties = "", string items = "")
            => File.WriteAllText(ProjectPath, $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{framework}</TargetFramework>{properties}</PropertyGroup>{items}</Project>");
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}

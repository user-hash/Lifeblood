using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.PathClassification;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Pins source-generated graph paths to workspace and module identity. Roslyn
/// generator hint paths are relative virtual paths; resolving them through the
/// process working directory leaks whichever repository launched the server.
/// </summary>
public sealed class SourceGeneratedPathProvenanceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"lifeblood-sourcegen-path-{Guid.NewGuid():N}");

    public SourceGeneratedPathProvenanceTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Resolve_PhysicalAndGeneratedPaths_UseDisjointStableNamespaces()
    {
        var ordinaryPhysical = SyntaxTreePathIdentity.Resolve(
            _tempDir,
            "ModuleA",
            Path.Combine(_tempDir, "src", "Widget.cs"));
        var reservedPhysical = SyntaxTreePathIdentity.Resolve(
            _tempDir,
            "ModuleA",
            Path.Combine(_tempDir, "generated", "ModuleA", "Widget.g.cs"));
        var nestedReservedPhysical = SyntaxTreePathIdentity.Resolve(
            _tempDir,
            "ModuleA",
            Path.Combine(_tempDir, "source", "generated", "ModuleA", "Widget.g.cs"));
        var generated = SyntaxTreePathIdentity.Resolve(
            _tempDir,
            "Module/A",
            @"Generator\..\Widget.g.cs");

        Assert.Equal("src/Widget.cs", ordinaryPhysical.GraphPath);
        Assert.False(ordinaryPhysical.IsGenerated);
        Assert.Equal("source/generated/ModuleA/Widget.g.cs", reservedPhysical.GraphPath);
        Assert.Equal("source/source/generated/ModuleA/Widget.g.cs", nestedReservedPhysical.GraphPath);
        Assert.Equal("generated/Module%2FA/Generator/%2E%2E/Widget.g.cs", generated.GraphPath);
        Assert.True(generated.IsGenerated);
    }

    [Fact]
    public void AnalyzeWorkspace_SourceGeneratedFiles_AreModuleQualifiedAndWorkspaceNeutral()
    {
        WriteJsonContextProject(_tempDir, "JsonApp", "JsonApp");
        var analyzer = new RoslynWorkspaceAnalyzer(new PhysicalFileSystem());

        var graph = analyzer.AnalyzeWorkspace(
            _tempDir,
            new AnalysisConfig { RetainCompilations = true });

        var generatedFiles = GeneratedFileIds(graph, "JsonApp");
        Assert.NotEmpty(generatedFiles);
        Assert.All(generatedFiles, id => Assert.StartsWith("file:generated/JsonApp/", id));
        Assert.All(
            graph.Symbols.Where(symbol => generatedFiles.Contains(symbol.Id, StringComparer.Ordinal)),
            symbol => Assert.Equal(PathBucket.Generated, PathBucketClassifier.Classify(symbol.FilePath)));
        Assert.DoesNotContain(graph.Symbols, symbol =>
            symbol.FilePath.Contains("../", StringComparison.Ordinal)
            || symbol.FilePath.Contains("..\\", StringComparison.Ordinal));
    }

    [Fact]
    public void AnalyzeWorkspace_SameGeneratorHintsInDifferentModules_DoNotCollide()
    {
        WriteJsonContextProject(
            Path.Combine(_tempDir, "ModuleA"),
            "ModuleA",
            "One");
        WriteJsonContextProject(
            Path.Combine(_tempDir, "ModuleB"),
            "ModuleB",
            "Two");
        var analyzer = new RoslynWorkspaceAnalyzer(new PhysicalFileSystem());

        var graph = analyzer.AnalyzeWorkspace(
            _tempDir,
            new AnalysisConfig { RetainCompilations = true });

        Assert.NotEmpty(GeneratedFileIds(graph, "ModuleA"));
        Assert.NotEmpty(GeneratedFileIds(graph, "ModuleB"));
        Assert.DoesNotContain(
            graph.Symbols
                .Where(symbol => symbol.Kind == SymbolKind.File)
                .GroupBy(symbol => symbol.Id, StringComparer.Ordinal),
            group => group.Count() > 1);
    }

    [Fact]
    public void IncrementalAnalyze_PreservesTheGeneratedPathSet()
    {
        var sourcePath = WriteJsonContextProject(_tempDir, "JsonApp", "JsonApp");
        var analyzer = new RoslynWorkspaceAnalyzer(new PhysicalFileSystem());
        var config = new AnalysisConfig { RetainCompilations = true };
        var fullGraph = analyzer.AnalyzeWorkspace(_tempDir, config);
        var fullGeneratedFiles = GeneratedFileIds(fullGraph, "JsonApp");

        Thread.Sleep(50);
        File.AppendAllText(sourcePath, Environment.NewLine);

        var incremental = analyzer.IncrementalAnalyze(config);

        Assert.Equal(IncrementalMode.Incremental, incremental.Mode);
        Assert.NotNull(incremental.Graph);
        Assert.Equal(fullGeneratedFiles, GeneratedFileIds(incremental.Graph!, "JsonApp"));
    }

    private static string[] GeneratedFileIds(SemanticGraph graph, string moduleName)
    {
        return graph.Symbols
            .Where(symbol =>
                symbol.Kind == SymbolKind.File
                && symbol.FilePath.StartsWith(
                    $"generated/{moduleName}/",
                    StringComparison.Ordinal))
            .Select(symbol => symbol.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
    }

    private static string WriteJsonContextProject(
        string projectDirectory,
        string assemblyName,
        string sourceNamespace)
    {
        Directory.CreateDirectory(projectDirectory);
        var sourcePath = Path.Combine(projectDirectory, "Program.cs");
        File.WriteAllText(sourcePath, $$"""
            using System.Text.Json.Serialization;

            namespace {{sourceNamespace}};

            public sealed record Payload(int Id);

            [JsonSerializable(typeof(Payload))]
            public partial class SharedContext : JsonSerializerContext
            {
            }
            """);

        File.WriteAllText(
            Path.Combine(projectDirectory, $"{assemblyName}.csproj"),
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <AssemblyName>{{assemblyName}}</AssemblyName>
                <TargetFramework>{{CurrentTestTargetFramework()}}</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        return sourcePath;
    }

    private static string CurrentTestTargetFramework()
    {
        var targetFrameworkName = AppContext.TargetFrameworkName;
        Assert.False(string.IsNullOrWhiteSpace(targetFrameworkName));

        var framework = new System.Runtime.Versioning.FrameworkName(targetFrameworkName!);
        Assert.Equal(".NETCoreApp", framework.Identifier);
        return $"net{framework.Version.Major}.{framework.Version.Minor}";
    }
}

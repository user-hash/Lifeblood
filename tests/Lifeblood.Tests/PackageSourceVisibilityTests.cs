using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Analysis;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Results;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// LB-INTAKE-20260629-024. Unity embedded package source visibility must be
/// explicit: package files can be included by project descriptors, excluded
/// by analysis scope, or present under a package asmdef but absent from the
/// loaded Roslyn descriptor set.
/// </summary>
public sealed class PackageSourceVisibilityTests : IDisposable
{
    private readonly string _root;
    private readonly PhysicalFileSystem _fs = new();

    public PackageSourceVisibilityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"lifeblood-package-visibility-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        WriteUnityPackageFixture(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Analyze_UnityPackageVisibilityReportsIncludedExcludedAndUnboundSources()
    {
        using var session = new GraphSession(_fs);

        var json = session.Load(
            _root,
            graphPath: null,
            rulesPath: null,
            excludePaths: new[] { "Packages/com.acme.tools/Editor/*" });
        using var document = JsonDocument.Parse(json);

        var visibility = document.RootElement.GetProperty("packageSourceVisibility");
        Assert.True(visibility.GetProperty("isUnityWorkspace").GetBoolean());
        Assert.Equal(1, visibility.GetProperty("packageCount").GetInt32());
        Assert.Equal(1, visibility.GetProperty("includedSourceFileCount").GetInt32());
        Assert.Equal(1, visibility.GetProperty("excludedSourceFileCount").GetInt32());
        Assert.Equal(1, visibility.GetProperty("unboundSourceFileCount").GetInt32());

        var package = Assert.Single(visibility.GetProperty("packages").EnumerateArray());
        Assert.Equal("com.acme.tools", package.GetProperty("name").GetString());
        Assert.Equal("Packages/com.acme.tools", package.GetProperty("rootPath").GetString());
        Assert.Equal(3, package.GetProperty("sourceFileCount").GetInt32());
        Assert.False(package.GetProperty("truncated").GetBoolean());

        var files = package.GetProperty("files").EnumerateArray()
            .ToDictionary(
                file => file.GetProperty("path").GetString()!,
                file => file,
                StringComparer.Ordinal);
        Assert.Equal(
            PackageSourceVisibilityStatus.Included,
            files["Packages/com.acme.tools/Runtime/Included.cs"].GetProperty("status").GetString());
        Assert.Equal(
            "Acme.Tools",
            files["Packages/com.acme.tools/Runtime/Included.cs"].GetProperty("moduleName").GetString());
        Assert.Equal(
            PackageSourceVisibilityStatus.Excluded,
            files["Packages/com.acme.tools/Editor/Excluded.cs"].GetProperty("status").GetString());
        Assert.Equal(
            PackageSourceVisibilityStatus.Unbound,
            files["Packages/com.acme.tools/Runtime/Unbound.cs"].GetProperty("status").GetString());
        Assert.Equal(
            "Acme.Tools",
            files["Packages/com.acme.tools/Runtime/Unbound.cs"].GetProperty("expectedAssembly").GetString());
    }

    [Fact]
    public void Analyze_DefaultMcpPackageVisibilitySummaryOmitsPerFileInventory()
    {
        using var session = new GraphSession(_fs);
        var handler = CreateHandler(session);

        var result = handler.Handle(
            "lifeblood_analyze",
            JsonArgs(new { projectPath = _root }));

        Assert.NotEqual(true, result.IsError);
        using var payload = JsonDocument.Parse(result.Content[0].Text);
        var visibility = payload.RootElement.GetProperty("packageSourceVisibility");
        var package = Assert.Single(visibility.GetProperty("packages").EnumerateArray());

        Assert.Equal("summary", visibility.GetProperty("mode").GetString());
        Assert.Equal(0, package.GetProperty("returnedFileCount").GetInt32());
        Assert.Equal(3, package.GetProperty("omittedFileCount").GetInt32());
        Assert.True(package.GetProperty("truncated").GetBoolean());
        Assert.Empty(package.GetProperty("files").EnumerateArray());
    }

    [Fact]
    public void Analyze_DetailMcpPackageVisibilityReturnsBoundedPerFileInventory()
    {
        using var session = new GraphSession(_fs);
        var handler = CreateHandler(session);

        var result = handler.Handle(
            "lifeblood_analyze",
            JsonArgs(new
            {
                projectPath = _root,
                packageSourceVisibilityMode = "detail",
            }));

        Assert.NotEqual(true, result.IsError);
        using var payload = JsonDocument.Parse(result.Content[0].Text);
        var visibility = payload.RootElement.GetProperty("packageSourceVisibility");
        var package = Assert.Single(visibility.GetProperty("packages").EnumerateArray());

        Assert.Equal("detail", visibility.GetProperty("mode").GetString());
        Assert.Equal(3, package.GetProperty("returnedFileCount").GetInt32());
        Assert.Equal(0, package.GetProperty("omittedFileCount").GetInt32());
        Assert.False(package.GetProperty("truncated").GetBoolean());
        Assert.Equal(3, package.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public void Analyze_DefaultMcpPackageVisibilitySummaryStaysBoundedForManyPackageFiles()
    {
        WriteManyUnboundEmbeddedPackage(_root, "com.acme.large", fileCount: 80);
        using var session = new GraphSession(_fs);
        var handler = CreateHandler(session);

        var result = handler.Handle(
            "lifeblood_analyze",
            JsonArgs(new { projectPath = _root }));

        Assert.NotEqual(true, result.IsError);
        Assert.True(result.Content[0].Text.Length < 8_000);
        using var payload = JsonDocument.Parse(result.Content[0].Text);
        var visibility = payload.RootElement.GetProperty("packageSourceVisibility");
        var package = visibility.GetProperty("packages").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "com.acme.large");

        Assert.Equal("summary", visibility.GetProperty("mode").GetString());
        Assert.Equal(80, package.GetProperty("sourceFileCount").GetInt32());
        Assert.Equal(0, package.GetProperty("returnedFileCount").GetInt32());
        Assert.Equal(80, package.GetProperty("omittedFileCount").GetInt32());
        Assert.True(package.GetProperty("truncated").GetBoolean());
        Assert.Empty(package.GetProperty("files").EnumerateArray());
    }

    [Fact]
    public void Analyze_DetailMcpPackageVisibilityCapsManyPackageFiles()
    {
        WriteManyUnboundEmbeddedPackage(_root, "com.acme.large", fileCount: 80);
        using var session = new GraphSession(_fs);
        var handler = CreateHandler(session);

        var result = handler.Handle(
            "lifeblood_analyze",
            JsonArgs(new
            {
                projectPath = _root,
                packageSourceVisibilityMode = "detail",
            }));

        Assert.NotEqual(true, result.IsError);
        using var payload = JsonDocument.Parse(result.Content[0].Text);
        var visibility = payload.RootElement.GetProperty("packageSourceVisibility");
        var package = visibility.GetProperty("packages").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "com.acme.large");

        Assert.Equal("detail", visibility.GetProperty("mode").GetString());
        Assert.Equal(80, package.GetProperty("sourceFileCount").GetInt32());
        Assert.Equal(64, package.GetProperty("returnedFileCount").GetInt32());
        Assert.Equal(16, package.GetProperty("omittedFileCount").GetInt32());
        Assert.True(package.GetProperty("truncated").GetBoolean());
        Assert.Equal(64, package.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public void CompileCheck_UnboundPackageFileCarriesPackageResolution()
    {
        using var session = new GraphSession(_fs);
        var handler = CreateHandler(session);

        var analyze = handler.Handle(
            "lifeblood_analyze",
            JsonArgs(new
            {
                projectPath = _root,
                excludePaths = new[] { "Packages/com.acme.tools/Editor/*" },
            }));
        Assert.NotEqual(true, analyze.IsError);

        var result = handler.Handle(
            "lifeblood_compile_check",
            JsonArgs(new
            {
                filePath = "Packages/com.acme.tools/Runtime/Unbound.cs",
                staleRefresh = false,
            }));
        Assert.NotEqual(true, result.IsError);
        using var payload = JsonDocument.Parse(result.Content[0].Text);

        Assert.False(payload.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("NotInAnyCompilation", payload.RootElement.GetProperty("fileResolution").GetString());
        Assert.Contains(
            "package 'com.acme.tools'",
            payload.RootElement.GetProperty("staleDescriptorHint").GetString(),
            StringComparison.Ordinal);

        var resolution = payload.RootElement.GetProperty("packageSourceResolution");
        Assert.Equal("com.acme.tools", resolution.GetProperty("packageName").GetString());
        Assert.Equal("Packages/com.acme.tools", resolution.GetProperty("packageRoot").GetString());
        Assert.Equal(PackageSourceVisibilityStatus.Unbound, resolution.GetProperty("status").GetString());
        Assert.Equal(PackageSourceVisibilityReason.NotInProjectDescriptors, resolution.GetProperty("reason").GetString());
        Assert.Equal("Acme.Tools", resolution.GetProperty("expectedAssembly").GetString());
        Assert.Contains(
            "Regenerate Unity project descriptors",
            resolution.GetProperty("remedy").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void IncrementalAnalyze_PackageVisibilityInputChange_PublishesNewIdentity()
    {
        using var session = new GraphSession(_fs);

        using var first = JsonDocument.Parse(session.Load(_root, graphPath: null, rulesPath: null));
        var firstSnapshotId = session.SnapshotId.ToString();
        var firstGeneration = session.AnalysisGeneration;
        var firstAnalysisKey = first.RootElement
            .GetProperty("analysisIdentity")
            .GetProperty("analysisKey")
            .GetString();
        Assert.Equal(1, first.RootElement
            .GetProperty("packageSourceVisibility")
            .GetProperty("packageCount")
            .GetInt32());

        WriteUnboundEmbeddedPackage(_root, "com.acme.loose");

        using var second = JsonDocument.Parse(session.Load(
            _root,
            graphPath: null,
            rulesPath: null,
            incremental: true));

        Assert.Equal("incremental", second.RootElement.GetProperty("mode").GetString());
        Assert.NotEqual(firstSnapshotId, session.SnapshotId.ToString());
        Assert.True(session.AnalysisGeneration > firstGeneration);
        Assert.NotEqual(
            firstAnalysisKey,
            second.RootElement.GetProperty("analysisIdentity").GetProperty("analysisKey").GetString());
        Assert.Equal(2, second.RootElement
            .GetProperty("packageSourceVisibility")
            .GetProperty("packageCount")
            .GetInt32());
    }

    [Fact]
    public void Analyze_PackageDescriptorReadErrors_DoNotFailVisibilityReceipt()
    {
        using var session = new GraphSession(new ThrowingPackageDescriptorFileSystem(_fs, _root));

        using var document = JsonDocument.Parse(session.Load(_root, graphPath: null, rulesPath: null));

        Assert.True(document.RootElement.TryGetProperty("packageSourceVisibility", out var visibility));
        Assert.True(visibility.GetProperty("isUnityWorkspace").GetBoolean());
        Assert.Equal(1, visibility.GetProperty("packageCount").GetInt32());
    }

    private static void WriteUnityPackageFixture(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Library"));
        Directory.CreateDirectory(Path.Combine(root, "Packages", "com.acme.tools", "Runtime"));
        Directory.CreateDirectory(Path.Combine(root, "Packages", "com.acme.tools", "Editor"));

        File.WriteAllText(
            Path.Combine(root, "Packages", "manifest.json"),
            """
            {
              "dependencies": {
                "com.acme.tools": "file:Packages/com.acme.tools"
              }
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "Packages", "packages-lock.json"),
            """
            {
              "dependencies": {
                "com.acme.tools": {
                  "version": "file:Packages/com.acme.tools",
                  "source": "embedded"
                }
              }
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "Packages", "com.acme.tools", "package.json"),
            """
            { "name": "com.acme.tools", "version": "1.0.0" }
            """);
        File.WriteAllText(
            Path.Combine(root, "Packages", "com.acme.tools", "Runtime", "Acme.Tools.asmdef"),
            """
            { "name": "Acme.Tools" }
            """);
        File.WriteAllText(
            Path.Combine(root, "Packages", "com.acme.tools", "Runtime", "Included.cs"),
            "namespace Acme.Tools; public sealed class Included { }");
        File.WriteAllText(
            Path.Combine(root, "Packages", "com.acme.tools", "Editor", "Excluded.cs"),
            "namespace Acme.Tools.Editor; public sealed class Excluded { }");
        File.WriteAllText(
            Path.Combine(root, "Packages", "com.acme.tools", "Runtime", "Unbound.cs"),
            "namespace Acme.Tools; public sealed class Unbound { }");
        File.WriteAllText(
            Path.Combine(root, "Acme.Tools.csproj"),
            """
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <AssemblyName>Acme.Tools</AssemblyName>
                <TargetFrameworkVersion>v4.7.1</TargetFrameworkVersion>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="Packages\com.acme.tools\Runtime\Included.cs" />
                <Compile Include="Packages\com.acme.tools\Editor\Excluded.cs" />
              </ItemGroup>
            </Project>
            """);
    }

    private static void WriteUnboundEmbeddedPackage(string root, string packageName)
    {
        var runtime = Path.Combine(root, "Packages", packageName, "Runtime");
        Directory.CreateDirectory(runtime);
        File.WriteAllText(
            Path.Combine(root, "Packages", packageName, "package.json"),
            $$"""
            { "name": "{{packageName}}", "version": "1.0.0" }
            """);
        File.WriteAllText(
            Path.Combine(runtime, "Loose.cs"),
            $"namespace Acme.Loose; public sealed class Loose {{ }}");
    }

    private static void WriteManyUnboundEmbeddedPackage(string root, string packageName, int fileCount)
    {
        var runtime = Path.Combine(root, "Packages", packageName, "Runtime");
        Directory.CreateDirectory(runtime);
        File.WriteAllText(
            Path.Combine(root, "Packages", packageName, "package.json"),
            $$"""
            { "name": "{{packageName}}", "version": "1.0.0" }
            """);
        File.WriteAllText(
            Path.Combine(runtime, "Acme.Large.asmdef"),
            """
            { "name": "Acme.Large" }
            """);
        for (var index = 0; index < fileCount; index++)
        {
            File.WriteAllText(
                Path.Combine(runtime, $"Large{index:D3}.cs"),
                $"namespace Acme.Large; public sealed class Large{index:D3} {{ }}");
        }
    }

    private static ToolHandler CreateHandler(GraphSession session)
    {
        IMcpGraphProvider provider = new LifebloodMcpProvider(new TestBlastRadiusProvider());
        ISymbolResolver resolver = new LifebloodSymbolResolver();
        ISemanticSearchProvider search = new LifebloodSemanticSearchProvider();
        IDeadCodeAnalyzer deadCode = new LifebloodDeadCodeAnalyzer();
        IPartialViewBuilder partialView = new LifebloodPartialViewBuilder(new PhysicalFileSystem());
        Lifeblood.Application.Ports.Right.Invariants.IInvariantProvider invariants
            = new LifebloodInvariantProvider(new PhysicalFileSystem());
        var classifications = ToolRegistry.GetDefinitions()
            .Where(definition => definition.EnvelopeClassification != null)
            .ToDictionary(
                definition => definition.Name,
                definition => definition.EnvelopeClassification!,
                StringComparer.Ordinal);
        IResponseDecorator decorator = new LifebloodResponseDecorator(classifications);
        return new ToolHandler(
            session,
            provider,
            resolver,
            search,
            deadCode,
            partialView,
            invariants,
            decorator);
    }

    private static JsonElement? JsonArgs(object obj)
        => JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(obj));

    private sealed class TestBlastRadiusProvider : IBlastRadiusProvider
    {
        public BlastRadiusResult Analyze(Lifeblood.Domain.Graph.SemanticGraph graph, string targetSymbolId, int maxDepth = 10)
            => BlastRadiusAnalyzer.Analyze(graph, targetSymbolId, maxDepth);
    }

    private sealed class ThrowingPackageDescriptorFileSystem : IFileSystem
    {
        private readonly IFileSystem _inner;
        private readonly string _manifestPath;
        private readonly string _lockPath;

        public ThrowingPackageDescriptorFileSystem(IFileSystem inner, string root)
        {
            _inner = inner;
            _manifestPath = Path.GetFullPath(Path.Combine(root, "Packages", "manifest.json"));
            _lockPath = Path.GetFullPath(Path.Combine(root, "Packages", "packages-lock.json"));
        }

        public string ReadAllText(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (PathComparer.Equals(fullPath, _manifestPath) || PathComparer.Equals(fullPath, _lockPath))
                throw new UnauthorizedAccessException("Synthetic unreadable package descriptor.");
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

        private static StringComparer PathComparer { get; }
            = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    }
}

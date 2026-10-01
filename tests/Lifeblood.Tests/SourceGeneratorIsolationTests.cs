using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Left;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

public class SourceGeneratorIsolationTests
{
    [Theory]
    [InlineData("net8.0", "net10.0")]
    [InlineData("net10.0", "net8.0")]
    public void FrameworkVersions_GenerateIndependently_InEitherOrder(string first, string second)
    {
        foreach (var target in new[] { first, second, first })
        {
            var root = Path.Combine(Path.GetTempPath(), $"lifeblood-generator-version-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, "Test.csproj"),
                    $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{target}</TargetFramework></PropertyGroup></Project>");
                var module = Assert.Single(new RoslynModuleDiscovery(new PhysicalFileSystem()).DiscoverModules(root));
                Assert.Contains(module.SourceGeneratorAnalyzerPaths, path => path.EndsWith("System.Text.Json.SourceGeneration.dll"));
                var compilation = CSharpCompilation.Create("Test",
                    new[] { CSharpSyntaxTree.ParseText("""
                        using System.Text.Json.Serialization;
                        [JsonSerializable(typeof(string))]
                        public partial class Context : JsonSerializerContext { }
                        """) }, BclReferenceLoader.References.Value,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
                var generated = SourceGeneratorRunner.Run(compilation, module.SourceGeneratorAnalyzerPaths, module);
                Assert.NotEmpty(generated.GetTypeByMetadataName("Context")!.GetMembers("Default"));
                Assert.Empty(generated.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            }
            finally { Directory.Delete(root, true); }
        }
    }

    [Fact]
    public void MissingGenerator_RejectsAnalysisWithPathAndModule()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-generator-{Guid.NewGuid():N}.dll");
        var exception = Assert.ThrowsAny<Exception>(() => SourceGeneratorRunner.Run(
            CSharpCompilation.Create("Test"), new[] { missing }, new ModuleInfo { Name = "Test" }));
        Assert.Contains(missing, exception.ToString());
    }

    [Fact]
    public void CorruptGenerator_RejectsAnalysisWithPathAndModule()
    {
        var path = Path.Combine(Path.GetTempPath(), $"corrupt-generator-{Guid.NewGuid():N}.dll");
        try
        {
            File.WriteAllText(path, "not an assembly");
            var exception = Assert.Throws<InvalidOperationException>(() => SourceGeneratorRunner.Run(
                CSharpCompilation.Create("Test"), new[] { path }, new ModuleInfo { Name = "Test" }));
            Assert.Contains(path, exception.ToString());
            Assert.Contains("Test", exception.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ThrowingGenerator_RejectsPartialCompilation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"throwing-generator-{Guid.NewGuid():N}.dll");
        try
        {
            var generator = CSharpCompilation.Create(Path.GetFileNameWithoutExtension(path),
                new[] { CSharpSyntaxTree.ParseText("""
                    using Microsoft.CodeAnalysis;
                    [Generator]
                    public class ThrowingGenerator : ISourceGenerator
                    {
                        public void Initialize(GeneratorInitializationContext context) { }
                        public void Execute(GeneratorExecutionContext context)
                            => throw new System.InvalidOperationException("generator-execution-marker");
                    }
                    """) },
                BclReferenceLoader.References.Value.Append(MetadataReference.CreateFromFile(typeof(ISourceGenerator).Assembly.Location)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var emit = generator.Emit(path);
            Assert.True(emit.Success, string.Join("; ", emit.Diagnostics));
            var exception = Assert.Throws<InvalidOperationException>(() => SourceGeneratorRunner.Run(
                CSharpCompilation.Create("Test"), new[] { path }, new ModuleInfo { Name = "Test" }));
            Assert.Contains("generator-execution-marker", exception.Message);
            Assert.Contains("Test", exception.Message);
        }
        finally { File.Delete(path); }
    }
}

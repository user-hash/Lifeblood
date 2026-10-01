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
    [InlineData("net8.0", "net10.0", false)]
    [InlineData("net10.0", "net8.0", false)]
    [InlineData("net8.0", "net10.0", true)]
    [InlineData("net10.0", "net8.0", true)]
    public void FrameworkVersions_GenerateIndependently_InEitherOrder(string first, string second, bool shareScope)
    {
        using var scope = shareScope ? new SourceGeneratorRunner.Scope() : null;
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
                var generated = SourceGeneratorRunner.Run(compilation, module.SourceGeneratorAnalyzerPaths, module, scope);
                Assert.NotEmpty(generated.GetTypeByMetadataName("Context")!.GetMembers("Default"));
                Assert.Empty(generated.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            }
            finally { Directory.Delete(root, true); }
        }
    }

    [Fact]
    public void CompilationPass_ReusesCode_CreatesFreshGenerators_AndReleasesScope()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scoped-generator-{Guid.NewGuid():N}.dll");
        try
        {
            var generator = CSharpCompilation.Create(Path.GetFileNameWithoutExtension(path),
                new[] { CSharpSyntaxTree.ParseText("""
                    using Microsoft.CodeAnalysis;
                    [Generator]
                    public class ScopedGenerator : ISourceGenerator
                    {
                        private static int constructions;
                        private readonly int construction = ++constructions;
                        private int executions;
                        public void Initialize(GeneratorInitializationContext context) { }
                        public void Execute(GeneratorExecutionContext context)
                            => context.AddSource("Marker.g.cs", "public class Marker { public const int Construction = "
                                + construction + "; public const int Executions = " + ++executions + "; }");
                    }
                    """) },
                BclReferenceLoader.References.Value.Append(MetadataReference.CreateFromFile(typeof(ISourceGenerator).Assembly.Location)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var emit = generator.Emit(path);
            Assert.True(emit.Success, string.Join("; ", emit.Diagnostics));
            using (var scope = new SourceGeneratorRunner.Scope())
            {
                AssertGenerated(1, scope);
                AssertGenerated(2, scope);
            }
            using (var scope = new SourceGeneratorRunner.Scope())
            {
                AssertGenerated(1, scope);
                scope.Dispose();
                Assert.Throws<ObjectDisposedException>(() => SourceGeneratorRunner.Run(
                    CSharpCompilation.Create("Test"), new[] { path }, new ModuleInfo { Name = "Test" }, scope));
            }
            var releasedContext = LoadAndReleaseContext(path);
            for (var attempt = 0; attempt < 10 && releasedContext.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Assert.False(releasedContext.IsAlive);

            void AssertGenerated(int construction, SourceGeneratorRunner.Scope scope)
            {
                var compilation = CSharpCompilation.Create("Test", references: BclReferenceLoader.References.Value,
                    options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
                var generated = SourceGeneratorRunner.Run(compilation, new[] { path }, new ModuleInfo { Name = "Test" }, scope);
                Assert.Empty(generated.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
                var marker = generated.GetTypeByMetadataName("Marker")!;
                Assert.Equal(construction, ((IFieldSymbol)marker.GetMembers("Construction").Single()).ConstantValue);
                Assert.Equal(1, ((IFieldSymbol)marker.GetMembers("Executions").Single()).ConstantValue);
            }
        }
        finally { File.Delete(path); }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference LoadAndReleaseContext(string path)
    {
        using var scope = new SourceGeneratorRunner.Scope();
        var assembly = scope.GetLoader(new[] { path }).LoadFromPath(path);
        var context = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(assembly)!;
        Assert.True(context.IsCollectible);
        return new WeakReference(context);
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

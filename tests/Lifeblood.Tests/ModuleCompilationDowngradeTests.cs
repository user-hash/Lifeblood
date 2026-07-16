using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Left;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Ratchets the streaming compiler's downgrade boundary. Producing a compact
/// PE reference is an optimization; a temporarily inconsistent project
/// descriptor must still leave a usable compilation reference for downstream
/// semantic analysis instead of terminating the whole workspace scan.
/// </summary>
public sealed class ModuleCompilationDowngradeTests
{
    [Fact]
    public void RecoverableEmitFailurePolicy_ContainsRoslynInternalFault_AndPreservesFatalSignals()
    {
        Assert.True(ModuleCompilationBuilder.IsRecoverableEmitFailure(
            new NullReferenceException("Roslyn emitter fault")));
        Assert.True(ModuleCompilationBuilder.IsRecoverableEmitFailure(
            new InvalidOperationException("invalid error compilation")));
        Assert.False(ModuleCompilationBuilder.IsRecoverableEmitFailure(
            new OperationCanceledException()));
        Assert.False(ModuleCompilationBuilder.IsRecoverableEmitFailure(
            new OutOfMemoryException()));
        Assert.False(ModuleCompilationBuilder.IsRecoverableEmitFailure(
            new AccessViolationException()));
    }

    [Fact]
    public void ProcessInOrder_MissingNestedGenericArgument_FallsBackToCompilationReference()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lifeblood-downgrade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var foundationPath = Path.Combine(root, "FunctionPointer.cs");
            File.WriteAllText(foundationPath, """
                namespace Fixture;

                public readonly struct FunctionPointer<T>
                {
                    public T Invoke => default!;
                }
                """);

            var sourcePath = Path.Combine(root, "BrokenCache.cs");
            File.WriteAllText(sourcePath, """
                using Fixture;

                namespace Consumer;

                public static class Cache
                {
                    public static FunctionPointer<MissingKernel.ExecuteDelegate> Output;
                    public static MissingKernel.ExecuteDelegate OutputInvoke;

                    public static void Initialize()
                    {
                        OutputInvoke = Output.Invoke;
                    }
                }
                """);

            var foundation = new ModuleInfo
            {
                Name = "Foundation",
                FilePaths = new[] { foundationPath },
            };
            var broken = new ModuleInfo
            {
                Name = "BrokenModule",
                FilePaths = new[] { sourcePath },
                Dependencies = new[] { foundation.Name },
            };
            var carry = new Dictionary<string, MetadataReference>(StringComparer.Ordinal);
            var extractor = new CompilationTreeExtractor();
            var knownModules = new HashSet<string>(
                new[] { foundation.Name, broken.Name },
                StringComparer.Ordinal);

            new ModuleCompilationBuilder(new PhysicalFileSystem()).ProcessInOrder(
                new[] { broken, foundation },
                root,
                new AnalysisConfig(),
                processor: (module, compilation) =>
                {
                    _ = extractor.Extract(
                        compilation,
                        root,
                        module.Name,
                        profileName: "Editor",
                        profileTag: null,
                        ownsSymbols: true,
                        knownModules);
                    return true;
                },
                carryDowngraded: carry);

            var reference = carry[broken.Name];
            Assert.IsAssignableFrom<CompilationReference>(reference);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

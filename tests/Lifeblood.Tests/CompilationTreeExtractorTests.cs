using Lifeblood.Adapters.CSharp.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

public sealed class CompilationTreeExtractorTests
{
    [Fact]
    public void Extract_PreservesCompilationTreeOrder_AcrossParallelWorkers()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "lifeblood-tree-order"));
        var trees = Enumerable.Range(0, 16)
            .Select(index => CSharpSyntaxTree.ParseText(
                $"namespace Sample; public sealed class C{index} {{ public void M() {{ }} }}",
                path: Path.Combine(root, $"C{index:D2}.cs")))
            .ToArray();
        var compilation = CreateCompilation(trees);

        var results = new CompilationTreeExtractor().Extract(
            compilation,
            root,
            "Sample.Module",
            "Editor",
            profileTag: null,
            ownsSymbols: true,
            new HashSet<string>(StringComparer.Ordinal) { "Sample.Module" });

        Assert.Equal(trees.Select(tree => tree.FilePath), results.Select(result => result.TreePath));
        Assert.All(results, result =>
        {
            Assert.NotNull(result.FileSymbol);
            Assert.NotNull(result.Symbols);
            Assert.StartsWith("file:", result.FileId, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Extract_IncrementalFilter_AlwaysIncludesGeneratedTrees()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "lifeblood-tree-filter"));
        var unchangedPath = Path.Combine(root, "Unchanged.cs");
        var changedPath = Path.Combine(root, "Changed.cs");
        var generatedPath = "Generated.g.cs";
        var trees = new[]
        {
            CSharpSyntaxTree.ParseText("public sealed class Unchanged { }", path: unchangedPath),
            CSharpSyntaxTree.ParseText("public sealed class Changed { }", path: changedPath),
            CSharpSyntaxTree.ParseText("public sealed class Generated { }", path: generatedPath),
        };
        var compilation = CreateCompilation(trees);

        var results = new CompilationTreeExtractor().Extract(
            compilation,
            root,
            "Sample.Module",
            "Editor",
            profileTag: null,
            ownsSymbols: true,
            new HashSet<string>(StringComparer.Ordinal) { "Sample.Module" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { changedPath });

        Assert.Equal(new[] { changedPath, generatedPath }, results.Select(result => result.TreePath));
        Assert.False(results[0].PathIdentity.IsGenerated);
        Assert.True(results[1].PathIdentity.IsGenerated);
    }

    private static CSharpCompilation CreateCompilation(IEnumerable<SyntaxTree> trees)
        => CSharpCompilation.Create(
            "Sample.Module",
            trees,
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
}

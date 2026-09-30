using Lifeblood.Adapters.CSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

public sealed class SymbolAtPositionOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AmbiguousBasename_DoesNotDependOnCompilationOrder(bool reverse)
    {
        var modules = new[] { ("A", "Root/A/Shared.cs", "Alpha"), ("B", "Root/B/Shared.cs", "Bravo") };
        using var host = CreateHost(reverse ? modules.Reverse().ToArray() : modules);
        Assert.Null(host.GetSymbolAtPosition("Shared.cs", 1, 14));
        Assert.Equal("Bravo", host.GetSymbolAtPosition("Root/B/Shared.cs", 1, 14)!.Name);
        Assert.Equal("Bravo", host.GetSymbolAtPosition("B/Shared.cs", 1, 14)!.Name);
        Assert.Null(host.GetSymbolAtPosition("ared.cs", 1, 14));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 31)]
    [InlineData(1, int.MaxValue)]
    [InlineData(3, 1)]
    public void InvalidCoordinate_NeverBindsToAnotherLine(int line, int column)
    {
        var tree = CSharpSyntaxTree.ParseText("// sixteen chars\npublic class Bravo { }", path: "Shared.cs");
        using var host = new RoslynCompilationHost(new Dictionary<string, CSharpCompilation>
        {
            ["A"] = CSharpCompilation.Create("A", new[] { tree }),
        });
        Assert.Null(host.GetSymbolAtPosition("Shared.cs", line, column));
        Assert.Equal("Bravo", host.GetSymbolAtPosition("Shared.cs", 2, 14)!.Name);
    }

    [Fact]
    public void LinkedFile_RequiresUniqueCompilationOwnership()
    {
        using var host = CreateHost(("A", "Shared.cs", "Alpha"), ("B", "Shared.cs", "Bravo"));
        Assert.Null(host.GetSymbolAtPosition("Shared.cs", 1, 14));
    }

    private static RoslynCompilationHost CreateHost(params (string Module, string Path, string Name)[] modules)
        => new(modules.ToDictionary(m => m.Module, m => CSharpCompilation.Create(m.Module,
            new[] { CSharpSyntaxTree.ParseText($"public class {m.Name} {{ }}", path: m.Path) },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) })));
}

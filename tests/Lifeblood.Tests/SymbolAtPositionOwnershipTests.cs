using Lifeblood.Adapters.CSharp;
using Lifeblood.Domain.Results;
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
        var receipt = Lookup(host, "Shared.cs").PositionResolution;
        Assert.Equal(SourcePositionOutcome.FileNotResolved, receipt.Outcome);
        Assert.Equal(CompilationFileOwnershipOutcome.Ambiguous, receipt.FileOwnership.Outcome);
        Assert.Equal(new[] { "A", "B" }, receipt.FileOwnership.CandidateModules);
        Assert.Equal(new[] { "Root/A/Shared.cs", "Root/B/Shared.cs" }, receipt.FileOwnership.CandidateFilePaths);
        Assert.Null(receipt.LineCount);
        Assert.Null(receipt.LineLength);
        Assert.Equal("Bravo", Lookup(host, "Shared.cs", module: "B").Symbol!.Name);
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
        var receipt = Lookup(host, "Shared.cs", line, column).PositionResolution;
        Assert.Equal(SourcePositionOutcome.InvalidCoordinate, receipt.Outcome);
        Assert.Equal(line is < 1 or > 2 ? SourceCoordinateFailure.LineOutOfRange
            : SourceCoordinateFailure.ColumnOutOfRange, receipt.CoordinateFailure);
        Assert.Equal(2, receipt.LineCount);
        Assert.Equal(line is < 1 or > 2 ? (int?)null : 16, receipt.LineLength);
    }

    [Fact]
    public void LinkedFile_RequiresUniqueCompilationOwnership()
    {
        using var host = CreateHost(("A", "Shared.cs", "Alpha"), ("B", "Shared.cs", "Bravo"));
        Assert.Null(host.GetSymbolAtPosition("Shared.cs", 1, 14));
        Assert.Equal("Alpha", Lookup(host, "Shared.cs", module: "A").Symbol!.Name);
        Assert.Equal("Bravo", Lookup(host, "Shared.cs", module: "B").Symbol!.Name);
        Assert.Single(Lookup(host, "Shared.cs").PositionResolution.FileOwnership.CandidateFilePaths);
    }

    [Theory]
    [InlineData("Missing.cs", null, CompilationFileOwnershipOutcome.NotFound)]
    [InlineData("Shared.cs", "Missing", CompilationFileOwnershipOutcome.ModuleNotFound)]
    [InlineData("B/Shared.cs", "A", CompilationFileOwnershipOutcome.NotInModule)]
    public void MissingOwnership_HasNoGuessedCoordinateBounds(string path, string? module,
        CompilationFileOwnershipOutcome expected)
    {
        using var host = CreateHost(("A", "Root/A/Shared.cs", "Alpha"));
        var report = Lookup(host, path, module: module);
        Assert.Null(report.Symbol);
        Assert.Equal(SourcePositionOutcome.FileNotResolved, report.PositionResolution.Outcome);
        Assert.Equal(expected, report.PositionResolution.FileOwnership.Outcome);
        Assert.Empty(report.PositionResolution.FileOwnership.CandidateModules);
        Assert.Null(report.PositionResolution.LineCount);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void CoordinateBounds_UseUtf16CharactersAndExcludeTerminators(string newline)
    {
        const string comment = "// \U0001F600\t";
        const string declaration = "\tpublic class Bravo { }";
        var tree = CSharpSyntaxTree.ParseText(comment + newline + declaration + newline, path: "Shared.cs");
        var compilation = CSharpCompilation.Create("A", new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        using var host = new RoslynCompilationHost(new Dictionary<string, CSharpCompilation> { ["A"] = compilation });
        var terminator = Lookup(host, "Shared.cs", 1, comment.Length + 1);
        Assert.Equal(SourceCoordinateFailure.ColumnOutOfRange, terminator.PositionResolution.CoordinateFailure);
        Assert.Equal(6, terminator.PositionResolution.LineLength);
        Assert.Equal(3, terminator.PositionResolution.LineCount);
        var resolved = Lookup(host, "Shared.cs", 2, declaration.IndexOf("Bravo", StringComparison.Ordinal) + 1);
        Assert.Equal("Bravo", resolved.Symbol!.Name);
        Assert.Equal(SourcePositionOutcome.Resolved, resolved.PositionResolution.Outcome);
        Assert.Equal(declaration.Length, resolved.PositionResolution.LineLength);
        var eof = Lookup(host, "Shared.cs", 3, 1);
        Assert.Null(eof.Symbol);
        Assert.Equal(0, eof.PositionResolution.LineLength);
        Assert.Equal(SourceCoordinateFailure.ColumnOutOfRange, eof.PositionResolution.CoordinateFailure);
    }

    [Fact]
    public void ValidCoordinateWithoutSymbol_IsDistinctFromInvalidCoordinate()
    {
        var tree = CSharpSyntaxTree.ParseText("// comment only", path: "Shared.cs");
        using var host = new RoslynCompilationHost(new Dictionary<string, CSharpCompilation>
        { ["A"] = CSharpCompilation.Create("A", new[] { tree }) });
        var report = Lookup(host, "Shared.cs", 1, 4);
        Assert.Null(report.Symbol);
        Assert.Equal(SourcePositionOutcome.NoSymbol, report.PositionResolution.Outcome);
        Assert.Equal(SourceCoordinateFailure.None, report.PositionResolution.CoordinateFailure);
        Assert.Equal(CompilationFileOwnershipOutcome.Unique, report.PositionResolution.FileOwnership.Outcome);
    }

    private static SymbolAtPositionReport Lookup(RoslynCompilationHost host, string path,
        int line = 1, int column = 14, string? module = null)
        => host.GetSymbolAtPosition(new SymbolAtPositionRequest
        { FilePath = path, Line = line, Column = column, ModuleName = module });

    private static RoslynCompilationHost CreateHost(params (string Module, string Path, string Name)[] modules)
        => new(modules.ToDictionary(m => m.Module, m => CSharpCompilation.Create(m.Module,
            new[] { CSharpSyntaxTree.ParseText($"public class {m.Name} {{ }}", path: m.Path) },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))));
}

using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

public sealed class CompilationFileInputContractTests
{
    [Fact]
    public void FileChecksAndDiagnostics_UseTheResolvedTreeDefines()
    {
        var first = CSharpSyntaxTree.ParseText("public class A {}",
            new CSharpParseOptions(preprocessorSymbols: new[] { "FIRST" }), "A.cs");
        const string source = """
            #if SECOND
            public class B {}
            #else
            #error Wrong parse options were selected for B
            #endif
            """;
        var second = CSharpSyntaxTree.ParseText(source,
            new CSharpParseOptions(preprocessorSymbols: new[] { "SECOND" }), "B.cs");
        using var host = Host(first, second);

        var environment = host.GetCompilationEnvironment(new() { FilePath = "B.cs" });
        var check = host.CompileCheck(new CompileCheckRequest { FilePath = "B.cs", Code = source });

        Assert.True(check.Success, string.Join("; ", check.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Equal(new[] { "SECOND" }, check.DefinesActive);
        Assert.Equal(Assert.Single(environment.ParseEnvironments).DefinesActive, check.DefinesActive);
        Assert.Equal(new[] { "SECOND" }, host.GetDiagnosticsReport(new() { FilePath = "B.cs" }).DefinesActive);
        Assert.Equal(new[] { "FIRST", "SECOND" }, host.GetDiagnosticsReport(new() { ModuleName = "Module" }).DefinesActive);
        Assert.Equal(new[] { "FIRST" }, host.CompileCheck("return 1;", "Module").DefinesActive);
    }

    [Fact]
    public void EmptyExplicitFileInput_RemovesItsDeclarationInsteadOfUsingTheBaseline()
    {
        var declaration = CSharpSyntaxTree.ParseText("public class A { public static int Value => 7; }", path: "A.cs");
        var consumer = CSharpSyntaxTree.ParseText("public class B { public int Read() => A.Value; }", path: "B.cs");
        using var host = Host(declaration, consumer);

        var baseline = host.CompileCheck(new CompileCheckRequest { FilePath = "A.cs" });
        var empty = host.CompileCheck(new CompileCheckRequest { FilePath = "A.cs", Code = "" });

        Assert.True(baseline.Success);
        Assert.False(empty.Success);
        Assert.Contains(empty.Diagnostics, diagnostic => diagnostic.FilePath == "B.cs" && diagnostic.Id == "CS0103");
        Assert.True(host.CompileCheck(new CompileCheckRequest { FilePath = "A.cs" }).Success);
    }

    private static RoslynCompilationHost Host(params SyntaxTree[] trees)
        => new(new Dictionary<string, CSharpCompilation>
        {
            ["Module"] = CSharpCompilation.Create("Module", trees,
                new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)),
        });
}

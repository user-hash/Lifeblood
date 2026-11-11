using Lifeblood.Adapters.CSharp;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

public sealed class CompilerValidationTests
{
    private const string Iterator = """
        using System.Collections;
        public sealed class IteratorHost
        {
            public IEnumerator Run()
            {
                Processing.Extractor.InitialSliceResult initial = default;
                try
                {
                    initial = Processing.Extractor.Extract();
                    System.Console.WriteLine(initial.success);
                    yield return null;
                }
                finally { if(initial.success) System.Console.WriteLine("finished"); }
            }
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocationlessIteratorEmitErrorsKeepCompilationScopeForBothRelatedAndUnrelatedFiles(bool withTransitiveReference)
    {
        using var host = Host(withTransitiveReference);
        var compiler = host.GetCompilationEnvironment(new() { ModuleName = "Consumer" }).Compiler;
        Assert.DoesNotContain(host.GetDiagnosticsReport(new() { ModuleName = "Consumer" }).Diagnostics,
            diagnostic => diagnostic.Severity == Lifeblood.Domain.Results.DiagnosticSeverity.Error);
        foreach (var path in new[] { "Iterator.cs", "Clean.cs" })
        {
            var result = host.CompileCheck(new CompileCheckRequest { FilePath = path });
            Assert.Equal(withTransitiveReference, result.Success);
            var validation = Assert.IsType<CompilerValidationReceipt>(result.Validation);
            Assert.Equal("HostCompilerEmit", validation.Kind);
            Assert.Equal(compiler["version"], validation.Compiler["version"]);
            Assert.Equal("9.0", validation.LanguageVersion);
            Assert.False(validation.TargetBuildValidated);
            Assert.Equal(withTransitiveReference, validation.CompleteModuleEmitSucceeded);
            Assert.Equal(!withTransitiveReference, validation.LocationlessBindingComparisonPerformed);
            if (withTransitiveReference) { Assert.Empty(result.Diagnostics); Assert.Empty(validation.DiagnosticAttribution); continue; }
            Assert.Equal(2, result.Diagnostics.Length);
            Assert.All(result.Diagnostics, diagnostic => { Assert.Equal("CS0012", diagnostic.Id); Assert.Equal("", diagnostic.FilePath); });
            Assert.Equal(new[] { 0, 1 }, validation.DiagnosticAttribution.Select(item => item.DiagnosticIndex));
            Assert.All(validation.DiagnosticAttribution, item =>
            {
                Assert.Equal(CompilerDiagnosticScope.Compilation, item.Scope);
                Assert.Equal(CompilerDiagnosticPhase.EmitOnly, item.Phase);
                Assert.False(item.HasSourceLocation);
            });
            Assert.Contains(validation.Limitations, text => text.Contains("may predate", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void SourceErrorsKeepRequestedSourcePlacementAndNoEmitStartsForUnresolvedOwnership()
    {
        using var host = Host(withTransitiveReference: true);
        var result = host.CompileCheck(new CompileCheckRequest { FilePath = "Clean.cs", Code = "public class Clean { public void Run() { Missing(); } }" });
        Assert.False(result.Success);
        Assert.All(result.Validation!.DiagnosticAttribution, item =>
        {
            Assert.Equal(CompilerDiagnosticScope.RequestedSource, item.Scope);
            Assert.Equal(CompilerDiagnosticPhase.ReportedByEmit, item.Phase);
            Assert.True(item.HasSourceLocation);
        });
        Assert.False(result.Validation.LocationlessBindingComparisonPerformed);
        Assert.Null(host.CompileCheck(new CompileCheckRequest { FilePath = "absent.cs" }).Validation);
    }

    [Fact]
    public void SnippetEmitFailureKeepsCompilationScopeInsteadOfAssigningItToTheSnippet()
    {
        using var host = Host(withTransitiveReference: false);
        var result = host.CompileCheck("System.Console.WriteLine(1);", "Consumer");
        Assert.False(result.Success);
        Assert.Equal(2, result.Diagnostics.Count(diagnostic => diagnostic.Id == "CS0012"));
        Assert.All(result.Validation!.DiagnosticAttribution, item =>
        {
            Assert.Equal(CompilerDiagnosticScope.Compilation, item.Scope);
            Assert.Equal(CompilerDiagnosticPhase.EmitOnly, item.Phase);
            Assert.False(item.HasSourceLocation);
        });
    }

    private static RoslynCompilationHost Host(bool withTransitiveReference)
    {
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var bcl = new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "System.Collections.dll", "System.Console.dll" }
            .Select(name => (MetadataReference)MetadataReference.CreateFromFile(Path.Combine(runtime, name))).ToArray();
        var parse = new CSharpParseOptions(LanguageVersion.CSharp9);
        CSharpCompilation Compilation(string name, string code, IEnumerable<MetadataReference> references)
            => CSharpCompilation.Create(name, new[] { CSharpSyntaxTree.ParseText(code, parse, name + ".cs") }, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var algorithm = Compilation("Algorithms", "namespace Algorithms { public struct ExtractionResult { public float Value; } }", bcl);
        var algorithmPe = Emit(algorithm);
        var processing = Compilation("Processing", """
            namespace Processing
            {
                public static class Extractor
                {
                    public struct InitialSliceResult { public bool success; public Algorithms.ExtractionResult visualAnalysis; }
                    public static InitialSliceResult Extract() => default;
                }
            }
            """, bcl.Append(algorithmPe));
        var refs = bcl.Append(Emit(processing)).Concat(withTransitiveReference ? new[] { algorithmPe } : Array.Empty<MetadataReference>());
        var consumer = CSharpCompilation.Create("Consumer", new[]
        {
            CSharpSyntaxTree.ParseText(Iterator, parse, "Iterator.cs"),
            CSharpSyntaxTree.ParseText("public class Clean { public int Value => 7; }", parse, "Clean.cs"),
        }, refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return new RoslynCompilationHost(new Dictionary<string, CSharpCompilation> { ["Consumer"] = consumer });
    }

    private static MetadataReference Emit(CSharpCompilation compilation)
    {
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Assert.True(emit.Success, string.Join("; ", emit.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    }
}

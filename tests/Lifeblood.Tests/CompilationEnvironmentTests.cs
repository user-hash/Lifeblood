using System.Collections.Immutable;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

public sealed class CompilationEnvironmentTests
{
    [Fact]
    public void Observation_ReportsActualOptionsAndCompilerRatherThanHostDefaults()
    {
        var parse = new CSharpParseOptions(LanguageVersion.CSharp7_3, preprocessorSymbols: new[] { "UNITY_EDITOR", "CUSTOM" })
            .WithFeatures(new[] { new KeyValuePair<string, string>("runtime-async", "off") });
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable, checkOverflow: true);
        var compilation = CSharpCompilation.Create("Legacy", new[] { CSharpSyntaxTree.ParseText("public class C {}", parse, "C.cs") },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) }, options);
        using var host = Host(("Legacy", compilation));

        var report = host.GetCompilationEnvironment(new() { FilePath = "C.cs" });

        Assert.Equal("Available", report.Status);
        Assert.Equal("Legacy", report.ModuleName);
        Assert.Equal("True", report.Properties["allowUnsafe"]);
        Assert.Equal("Enable", report.Properties["nullableContext"]);
        Assert.Equal("True", report.Properties["checkOverflow"]);
        var environment = Assert.Single(report.ParseEnvironments);
        Assert.Equal("7.3", environment.Properties["languageVersion"]);
        Assert.Equal(new[] { "CUSTOM", "UNITY_EDITOR" }, environment.DefinesActive);
        Assert.Equal("off", environment.Features["runtime-async"]);
        Assert.Contains(".NET", report.Compiler["hostRuntime"]);
        Assert.Equal("Microsoft.CodeAnalysis.CSharp", report.Compiler["name"]);
        Assert.Equal(typeof(object).Assembly.Location, Assert.Single(report.References).FilePath);
        Assert.Same(compilation, ((Lifeblood.Adapters.CSharp.Internal.IRoslynLookup)host).Compilations["Legacy"]);
    }

    [Fact]
    public void ModuleAndFileScopes_PreserveDifferentParseOptionsInsteadOfTakingFirstTree()
    {
        var first = CSharpSyntaxTree.ParseText("public class A {}", new CSharpParseOptions(preprocessorSymbols: new[] { "FIRST" }), "A.cs");
        var second = CSharpSyntaxTree.ParseText("public class B {}", new CSharpParseOptions(preprocessorSymbols: new[] { "SECOND" }), "B.cs");
        using var host = Host(("Mixed", CSharpCompilation.Create("Mixed", new[] { first, second })));

        var module = host.GetCompilationEnvironment(new() { ModuleName = "Mixed" });
        var file = host.GetCompilationEnvironment(new() { FilePath = "B.cs" });

        Assert.Equal(2, module.ParseEnvironmentCount);
        Assert.Equal(new[] { "SECOND" }, Assert.Single(file.ParseEnvironments).DefinesActive);
        Assert.Equal(CompilationFileOwnershipOutcome.Unique, file.FileOwnership.Outcome);
    }

    [Fact]
    public void LinkedSource_OwnershipIsFailClosedAndModulePinSelectsRealOptions()
    {
        var tree = CSharpSyntaxTree.ParseText("public class Shared {}", path: "Shared.cs");
        using var host = Host(("A", CSharpCompilation.Create("A", new[] { tree })), ("B", CSharpCompilation.Create("B", new[] { tree })));

        var ambiguous = host.GetCompilationEnvironment(new() { FilePath = "Shared.cs" });
        var pinned = host.GetCompilationEnvironment(new() { FilePath = "Shared.cs", ModuleName = "B" });
        var missing = host.GetCompilationEnvironment(new() { FilePath = "Missing.cs", ModuleName = "A" });

        Assert.Equal("FileOwnershipUnresolved", ambiguous.Status);
        Assert.Equal(CompilationFileOwnershipOutcome.Ambiguous, ambiguous.FileOwnership.Outcome);
        Assert.Empty(ambiguous.Properties);
        Assert.Equal("B", pinned.ModuleName);
        Assert.Equal(CompilationFileOwnershipOutcome.NotInModule, missing.FileOwnership.Outcome);
        Assert.Equal("ModuleNotFound", host.GetCompilationEnvironment(new() { ModuleName = "Absent" }).Status);
    }

    [Fact]
    public void BoundedOutput_RetainsTotalAndOmittedCounts()
    {
        var reference = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var references = Enumerable.Range(0, 140).Select(i => reference.WithAliases(new[] { "alias" + i }));
        var parse = new CSharpParseOptions(preprocessorSymbols: Enumerable.Range(0, 300).Select(i => "D" + i))
            .WithFeatures(Enumerable.Range(0, 40).Select(i => KeyValuePair.Create("feature" + i, "off")));
        var warnings = Enumerable.Range(0, 140).ToImmutableDictionary(i => "LB" + i, _ => ReportDiagnostic.Suppress);
        var trees = Enumerable.Range(0, 10).Select(i => CSharpSyntaxTree.ParseText("public class C" + i + " {}",
            parse.WithPreprocessorSymbols(new[] { "SCOPE" + i }.Concat(parse.PreprocessorSymbolNames)), "C" + i + ".cs"));
        using var host = Host(("Many", CSharpCompilation.Create("Many", trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithSpecificDiagnosticOptions(warnings))));

        var report = host.GetCompilationEnvironment(new() { ModuleName = "Many", ReferenceLimit = 3 });
        var compact = host.GetCompilationEnvironment(new() { FilePath = "C0.cs", IncludeDefines = false, ReferenceLimit = 0 });

        Assert.Equal(140, report.ReferenceCount);
        Assert.Equal(137, report.OmittedReferenceCount);
        Assert.Equal(10, report.ParseEnvironmentCount);
        Assert.Equal(2, report.OmittedParseEnvironmentCount);
        Assert.Equal(12, report.OmittedDiagnosticOptionCount);
        var parseRow = report.ParseEnvironments[0];
        Assert.Equal(301, parseRow.DefinesActiveCount);
        Assert.Equal(45, parseRow.OmittedDefineCount);
        Assert.Equal(8, parseRow.OmittedFeatureCount);
        Assert.Equal(301, Assert.Single(compact.ParseEnvironments).OmittedDefineCount);
        Assert.Equal(140, compact.OmittedReferenceCount);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(129)]
    public void InvalidReferenceLimit_IsRejectedBeforeObservation(int limit)
    {
        using var host = Host();
        Assert.Throws<ArgumentOutOfRangeException>(() => host.GetCompilationEnvironment(new() { ModuleName = "A", ReferenceLimit = limit }));
    }

    [Fact]
    public void MissingExplicitScope_DoesNotSelectFirstModule()
    {
        using var host = Host();
        Assert.Throws<ArgumentException>(() => host.GetCompilationEnvironment(new()));
    }

    private static RoslynCompilationHost Host(params (string Name, CSharpCompilation Compilation)[] modules)
        => new(modules.ToDictionary(module => module.Name, module => module.Compilation, StringComparer.Ordinal));
}

using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Regression for the "Inconsistent language versions" failure class
/// surfaced by the 2026-05-14 pre-release audit on DAWG.
///
/// When a module declares a non-default <c>&lt;LangVersion&gt;</c> /
/// <c>&lt;Nullable&gt;</c> / <c>&lt;DefineConstants&gt;</c> (threaded
/// into the module's <see cref="CSharpParseOptions"/> per
/// INV-COMPFACT-001..003), every tree on the resulting
/// <see cref="CSharpCompilation"/> carries those options.
/// <c>compile_check</c> in both file-mode (tree replacement) and
/// snippet-mode (tree addition) MUST parse its new tree with the SAME
/// <see cref="CSharpParseOptions"/>; otherwise
/// <see cref="CSharpCompilation.ReplaceSyntaxTree"/> and
/// <see cref="CSharpCompilation.AddSyntaxTrees"/> throw
/// <see cref="ArgumentException"/> with
/// <c>"Inconsistent language versions (Parameter 'syntaxTrees')"</c>.
/// The truth envelope on <c>diagnose</c> / <c>compile_check</c>
/// (INV-DIAGNOSTIC-ENVELOPE-DEFINES-001) is load-bearing on these
/// tools working at all on workspaces with explicit LangVersion.
/// </summary>
public class CompileCheckParseOptionsParityTests
{
    private static MetadataReference[] BclReferences()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var refs = new List<MetadataReference>();
        if (runtimeDir != null)
        {
            foreach (var dll in new[] { "System.Runtime.dll", "netstandard.dll" })
            {
                var path = Path.Combine(runtimeDir, dll);
                if (File.Exists(path)) refs.Add(MetadataReference.CreateFromFile(path));
            }
        }
        refs.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        return refs.ToArray();
    }

    /// <summary>
    /// Build a compilation whose source tree is parsed with a non-default
    /// LangVersion (CSharp11 — explicit and stable across the .NET 8
    /// SDK). Any LangVersion other than the host's
    /// <see cref="LanguageVersion.Default"/> is sufficient to reproduce.
    /// </summary>
    private static IReadOnlyDictionary<string, CSharpCompilation> BuildOneModuleNonDefaultLangVersion(
        string source, string fileName)
    {
        var nonDefault = new CSharpParseOptions(LanguageVersion.CSharp11);
        var tree = CSharpSyntaxTree.ParseText(source, options: nonDefault, path: fileName);
        var comp = CSharpCompilation.Create(
            "ModuleNonDefault", new[] { tree }, BclReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal)
        {
            ["ModuleNonDefault"] = comp,
        };
    }

    private static IReadOnlyDictionary<string, CSharpCompilation> BuildOneModuleWithCompilerFeatures(
        string source, string fileName)
    {
        var options = new CSharpParseOptions(LanguageVersion.CSharp12)
            .WithFeatures(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["runtime-async"] = "on",
            });
        var tree = CSharpSyntaxTree.ParseText(source, options: options, path: fileName);
        var comp = CSharpCompilation.Create(
            "ModuleWithFeatures", new[] { tree }, BclReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal)
        {
            ["ModuleWithFeatures"] = comp,
        };
    }

    [Fact]
    public void CompileCheckFile_OnModuleWithNonDefaultLangVersion_DoesNotThrowInconsistentLanguageVersions()
    {
        const string source = "namespace App { public class Foo { public int X { get; set; } } }";
        const string path = "Foo.cs";
        using var host = new RoslynCompilationHost(BuildOneModuleNonDefaultLangVersion(source, path));

        // The contract: this call MUST NOT throw. Pre-fix it threw with
        // ArgumentException "Inconsistent language versions
        // (Parameter 'syntaxTrees')" at the ReplaceSyntaxTree call inside
        // RoslynCompilationHost.CompileCheckFile because the replacement
        // tree was parsed without the module's CSharpParseOptions.
        var result = host.CompileCheck(new CompileCheckRequest
        {
            FilePath = path,
            Code = source,
        });

        Assert.True(result.Success, $"compile_check failed unexpectedly: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
        Assert.True(result.ExistingTreeReplaced);
        Assert.Equal("ModuleNonDefault", result.ResolvedModule);
    }

    [Fact]
    public void CompileCheckSnippet_OnModuleWithNonDefaultLangVersion_DoesNotThrowInconsistentLanguageVersions()
    {
        const string source = "namespace App { public class Foo { } }";
        using var host = new RoslynCompilationHost(BuildOneModuleNonDefaultLangVersion(source, "Foo.cs"));

        // Snippet that requires wrapping (bare statement) — exercises
        // the SnippetWrapper.WrapAsMethodBody parse path which was the
        // second site of the inconsistent-options defect.
        var result = host.CompileCheck(new CompileCheckRequest
        {
            ModuleName = "ModuleNonDefault",
            Code = "var x = 1 + 1;",
        });

        Assert.True(result.Success, $"compile_check failed unexpectedly: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
        Assert.Equal("ModuleNonDefault", result.ResolvedModule);
    }

    [Fact]
    public void CompileCheckSnippet_CompleteUnit_DoesNotThrowInconsistentLanguageVersions()
    {
        const string source = "namespace App { public class Foo { } }";
        using var host = new RoslynCompilationHost(BuildOneModuleNonDefaultLangVersion(source, "Foo.cs"));

        // Snippet that is a complete compilation unit (passes through
        // SnippetWrapper without wrapping) — exercises the third site
        // of the defect at SnippetWrapper.cs:87 ParseText.
        var result = host.CompileCheck(new CompileCheckRequest
        {
            ModuleName = "ModuleNonDefault",
            Code = "namespace App { public class Bar { } }",
        });

        Assert.True(result.Success, $"compile_check failed unexpectedly: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
    }

    [Fact]
    public void CompileCheckFile_OnModuleWithRuntimeAsyncFeature_PreservesParseOptions()
    {
        const string source = "namespace App { public class Foo { public int X { get; set; } } }";
        const string path = "Foo.cs";
        using var host = new RoslynCompilationHost(BuildOneModuleWithCompilerFeatures(source, path));

        var result = host.CompileCheck(new CompileCheckRequest
        {
            FilePath = path,
            Code = source,
        });

        Assert.True(result.Success, $"compile_check failed unexpectedly: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
        Assert.True(result.ExistingTreeReplaced);
        Assert.Equal("ModuleWithFeatures", result.ResolvedModule);
    }

    [Fact]
    public void CompileCheckSnippet_OnModuleWithRuntimeAsyncFeature_PreservesParseOptions()
    {
        const string source = "namespace App { public class Foo { } }";
        using var host = new RoslynCompilationHost(BuildOneModuleWithCompilerFeatures(source, "Foo.cs"));

        var result = host.CompileCheck(new CompileCheckRequest
        {
            ModuleName = "ModuleWithFeatures",
            Code = "namespace App { public class Bar { } }",
        });

        Assert.True(result.Success, $"compile_check failed unexpectedly: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
        Assert.Equal("ModuleWithFeatures", result.ResolvedModule);
    }

    [Fact]
    public void Diagnose_OnModuleWithRuntimeAsyncFeature_PreservesLoadedCompilation()
    {
        const string source = "namespace App { public class Foo { } }";
        using var host = new RoslynCompilationHost(BuildOneModuleWithCompilerFeatures(source, "Foo.cs"));

        var report = host.GetDiagnosticsReport(new DiagnosticsRequest
        {
            ModuleName = "ModuleWithFeatures",
        });

        Assert.Equal("ModuleWithFeatures", report.ResolvedModule);
        Assert.Empty(report.Diagnostics);
    }
}

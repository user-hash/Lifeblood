using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Regression tests for INV-DIAGNOSTIC-ENVELOPE-DEFINES-001 — every
/// diagnostic / compile-check response surfaces the active preprocessor
/// scope it was bound under, so a consumer can tell Editor-only noise
/// apart from a release-build risk without re-running the host under a
/// different define set.
///
/// Two-module harness: an "Editor" module carrying <c>UNITY_EDITOR</c>
/// and a "Player" module carrying <c>UNITY_STANDALONE</c>. The defines
/// are real <see cref="CSharpParseOptions.PreprocessorSymbolNames"/>
/// passed at tree-parse time so we exercise the same code path Roslyn
/// uses on a live workspace, not a hand-rolled mock.
/// </summary>
public class DiagnosticEnvelopeDefinesTests
{
    private const string EditorModuleName = "Editor";
    private const string PlayerModuleName = "Player";
    private const string EditorDefine = "UNITY_EDITOR";
    private const string PlayerDefine = "UNITY_STANDALONE";

    private static MetadataReference[] BclReferences()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir == null) return new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        var refs = new List<MetadataReference>();
        foreach (var dll in new[] { "System.Runtime.dll", "netstandard.dll" })
        {
            var path = Path.Combine(runtimeDir, dll);
            if (File.Exists(path)) refs.Add(MetadataReference.CreateFromFile(path));
        }
        refs.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        return refs.ToArray();
    }

    private static IReadOnlyDictionary<string, CSharpCompilation> BuildTwoModule(
        string editorSource = "namespace Edit { public class E { } }",
        string playerSource = "namespace Play { public class P { } }",
        string editorFileName = "Edit.cs",
        string playerFileName = "Play.cs")
    {
        var bcl = BclReferences();

        var editorOpts = new CSharpParseOptions(preprocessorSymbols: new[] { EditorDefine });
        var editorTree = CSharpSyntaxTree.ParseText(editorSource, options: editorOpts, path: editorFileName);
        var editor = CSharpCompilation.Create(
            EditorModuleName, new[] { editorTree }, bcl,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var playerOpts = new CSharpParseOptions(preprocessorSymbols: new[] { PlayerDefine });
        var playerTree = CSharpSyntaxTree.ParseText(playerSource, options: playerOpts, path: playerFileName);
        var player = CSharpCompilation.Create(
            PlayerModuleName, new[] { playerTree }, bcl,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal)
        {
            [EditorModuleName] = editor,
            [PlayerModuleName] = player,
        };
    }

    [Fact]
    public void GetDiagnosticsReport_FileScope_ReportsOwningModuleDefines()
    {
        // File scope: defines reflect the file's owning compilation,
        // not the union — the caller's question is "what scope was
        // THIS file bound under?", which is exactly the owning module.
        using var host = new RoslynCompilationHost(BuildTwoModule());

        var report = host.GetDiagnosticsReport(new DiagnosticsRequest { FilePath = "Edit.cs" });

        Assert.Equal(EditorModuleName, report.ResolvedModule);
        Assert.Equal(new[] { EditorDefine }, report.DefinesActive);
    }

    [Fact]
    public void GetDiagnosticsReport_ModuleScope_ReportsThatModuleDefines()
    {
        using var host = new RoslynCompilationHost(BuildTwoModule());

        var report = host.GetDiagnosticsReport(new DiagnosticsRequest { ModuleName = PlayerModuleName });

        Assert.Equal(PlayerModuleName, report.ResolvedModule);
        Assert.Equal(new[] { PlayerDefine }, report.DefinesActive);
    }

    [Fact]
    public void GetDiagnosticsReport_ProjectWide_UnionsAcrossCompilations()
    {
        // No FilePath, no ModuleName → diagnostics span every loaded
        // compilation, so DefinesActive is the union (sorted ASCII
        // ordinal, deduplicated) of every compilation's parse-options
        // symbol set. The honest answer to "what defines did Lifeblood
        // bind these diagnostics under?" is the union, not an arbitrary
        // first compilation. ResolvedModule is empty because no single
        // module owns the scope.
        using var host = new RoslynCompilationHost(BuildTwoModule());

        var report = host.GetDiagnosticsReport(new DiagnosticsRequest());

        Assert.Equal("", report.ResolvedModule);
        Assert.Equal(new[] { EditorDefine, PlayerDefine }, report.DefinesActive);
    }

    [Fact]
    public void GetDiagnosticsReport_Defines_AreSortedAndDeduped()
    {
        // Stable wire form: even when the iteration order of the
        // underlying compilations dictionary varies, DefinesActive is
        // ASCII-ordinal sorted so cached / golden-test comparisons
        // stay deterministic across analyze runs.
        var bcl = BclReferences();
        var optsA = new CSharpParseOptions(preprocessorSymbols: new[] { "BBB", "AAA", "CCC" });
        var treeA = CSharpSyntaxTree.ParseText("namespace A { public class T { } }", options: optsA, path: "A.cs");
        var compA = CSharpCompilation.Create("ModA", new[] { treeA }, bcl);

        // Second module repeats "BBB" — verify dedup folds the union.
        var optsB = new CSharpParseOptions(preprocessorSymbols: new[] { "BBB", "DDD" });
        var treeB = CSharpSyntaxTree.ParseText("namespace B { public class T { } }", options: optsB, path: "B.cs");
        var compB = CSharpCompilation.Create("ModB", new[] { treeB }, bcl);

        using var host = new RoslynCompilationHost(new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal)
        {
            ["ModA"] = compA,
            ["ModB"] = compB,
        });

        var report = host.GetDiagnosticsReport(new DiagnosticsRequest());

        Assert.Equal(new[] { "AAA", "BBB", "CCC", "DDD" }, report.DefinesActive);
    }

    [Fact]
    public void GetDiagnosticsReport_UnknownModule_EmptyDefinesNotCrash()
    {
        // Bad moduleName: the diagnostics path already returns empty,
        // and DefinesActive matches — no scope resolved, no defines to
        // report. Caller is not punished with a crash for a typo.
        using var host = new RoslynCompilationHost(BuildTwoModule());

        var report = host.GetDiagnosticsReport(new DiagnosticsRequest { ModuleName = "DoesNotExist" });

        Assert.Empty(report.Diagnostics);
        Assert.Empty(report.DefinesActive);
    }

    [Fact]
    public void CompileCheckFile_ResultCarriesOwningModuleDefines()
    {
        using var host = new RoslynCompilationHost(BuildTwoModule());

        // Same content as the existing tree — should compile clean and
        // surface the owning module's defines on the result.
        var result = host.CompileCheck(new CompileCheckRequest
        {
            FilePath = "Edit.cs",
            Code = "namespace Edit { public class E { } }",
        });

        Assert.True(result.Success);
        Assert.Equal(EditorModuleName, result.ResolvedModule);
        Assert.Equal(new[] { EditorDefine }, result.DefinesActive);
    }

    [Fact]
    public void CompileCheckSnippet_ResultCarriesResolvedModuleDefines()
    {
        using var host = new RoslynCompilationHost(BuildTwoModule());

        // Snippet against Player module — defines should reflect Player,
        // not Editor or the union. Closes the "agent gets noise from the
        // wrong define set" failure mode that motivated LB-INBOX-008.
        var result = host.CompileCheck(new CompileCheckRequest
        {
            Code = "var x = 1;",
            ModuleName = PlayerModuleName,
        });

        Assert.Equal(PlayerModuleName, result.ResolvedModule);
        Assert.Equal(new[] { PlayerDefine }, result.DefinesActive);
    }
}

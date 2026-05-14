using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Regression tests for INV-COMPFACT-001..003 +
/// INV-DIAGNOSTIC-ENVELOPE-DEFINES-001 — csproj
/// <c>&lt;DefineConstants&gt;</c> must propagate into every parsed
/// syntax tree's <see cref="CSharpParseOptions.PreprocessorSymbolNames"/>
/// so that <c>#if</c>-guarded code participates in the compilation unit
/// and, downstream, in the symbol graph.
///
/// Pre-fix: <c>RoslynModuleDiscovery</c> never read
/// <c>&lt;DefineConstants&gt;</c> and <c>ModuleCompilationBuilder</c>
/// called <c>CSharpSyntaxTree.ParseText(text, path)</c> with no
/// <c>ParseOptions</c>. PreprocessorSymbolNames was always empty.
/// Every <c>#if</c>-guarded symbol referenced only inside the guard was
/// invisible to <c>find_references</c> / <c>dead_code</c> /
/// <c>blast_radius</c> (the L-LIM-001 trap empirically observed on
/// Unity-like workspaces whose csprojs declare platform-specific
/// preprocessor symbols).
///
/// Post-fix: discovery parses every <c>&lt;DefineConstants&gt;</c>,
/// stores onto <see cref="ModuleInfo.PreprocessorSymbols"/>, and the
/// compilation builder threads it through
/// <c>CSharpParseOptions.WithPreprocessorSymbols</c>.
/// </summary>
public class DefineConstantsPropagationTests
{
    [Fact]
    public void Discovery_ReadsDefineConstants_FromCsprojSemicolonList()
    {
        // Single csproj with two defines in the canonical MSBuild
        // semicolon-separated form. Discovery must parse, trim, and
        // surface them on ModuleInfo.PreprocessorSymbols.
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-defines-disc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Empty.cs"),
                "namespace Test; public class Empty { }");
            File.WriteAllText(Path.Combine(tempDir, "Test.csproj"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <AssemblyName>Test</AssemblyName>
    <DefineConstants>FEATURE_X;FEATURE_Y</DefineConstants>
  </PropertyGroup>
</Project>");

            var modules = new RoslynModuleDiscovery(new PhysicalFileSystem()).DiscoverModules(tempDir);

            Assert.Single(modules);
            // Defines surface as a deduplicated, trim-clean array. Order
            // of insertion is preserved (we go through the semicolon split
            // then dedup) — assert as a set so the test is order-agnostic
            // because callers consume it as the
            // CSharpParseOptions.PreprocessorSymbolNames set, not a list.
            Assert.Equal(
                new HashSet<string> { "FEATURE_X", "FEATURE_Y" },
                new HashSet<string>(modules[0].PreprocessorSymbols));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Discovery_TrimsWhitespace_AndDropsEmptyEntries()
    {
        // Authoring csprojs sometimes carries whitespace around the
        // semicolons (especially after a hand-edit), and an MSBuild
        // expansion of $(DefineConstants) on an empty parent property
        // produces a leading semicolon. The discovery side must
        // canonicalize before storing — otherwise WithPreprocessorSymbols
        // sees empty/whitespace tokens, which Roslyn treats as syntax
        // errors at parse time.
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-defines-trim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Empty.cs"),
                "namespace Test; public class Empty { }");
            File.WriteAllText(Path.Combine(tempDir, "Test.csproj"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <AssemblyName>Test</AssemblyName>
    <DefineConstants>;  ALPHA ; ;BETA;</DefineConstants>
  </PropertyGroup>
</Project>");

            var modules = new RoslynModuleDiscovery(new PhysicalFileSystem()).DiscoverModules(tempDir);

            Assert.Single(modules);
            Assert.Equal(
                new HashSet<string> { "ALPHA", "BETA" },
                new HashSet<string>(modules[0].PreprocessorSymbols));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Discovery_NoDefineConstants_ProducesEmptyArray()
    {
        // No <DefineConstants> element at all — the pre-fix default
        // shape. Symbols must be empty (not null), so consumers can use
        // .Length == 0 to decide whether to call WithPreprocessorSymbols
        // at all without a null-guard.
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-defines-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Empty.cs"),
                "namespace Test; public class Empty { }");
            File.WriteAllText(Path.Combine(tempDir, "Test.csproj"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <AssemblyName>Test</AssemblyName>
  </PropertyGroup>
</Project>");

            var modules = new RoslynModuleDiscovery(new PhysicalFileSystem()).DiscoverModules(tempDir);

            Assert.Single(modules);
            Assert.NotNull(modules[0].PreprocessorSymbols);
            Assert.Empty(modules[0].PreprocessorSymbols);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Compilation_ThreadsDefines_IntoEveryTreeParseOptions()
    {
        // End-to-end: csproj declares defines → discovery surfaces them
        // → compilation builder threads them into ParseText → every
        // syntax tree's parse options carry the symbol set.
        //
        // This is the load-bearing assertion: it pins down the WHOLE
        // pre-fix-to-post-fix delta. RoslynCompilationHost's
        // GetActiveDefines reads PreprocessorSymbolNames off the first
        // syntax tree of the chosen compilation
        // (RoslynCompilationHost.cs:139). If this property is empty
        // post-build, definesActive[] is always empty regardless of
        // csproj declarations and every diagnostic envelope lies.
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-defines-comp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Source uses #if so the symbol is observable post-compile —
            // a tree parsed without FEATURE_X drops the class entirely
            // and the compilation's GetTypeByMetadataName returns null.
            // Tree parsed WITH FEATURE_X includes the class.
            File.WriteAllText(Path.Combine(tempDir, "Gated.cs"), @"
namespace Test
{
#if FEATURE_X
    public class GatedByX { }
#endif
}");

            File.WriteAllText(Path.Combine(tempDir, "Test.csproj"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <AssemblyName>Test</AssemblyName>
    <DefineConstants>FEATURE_X</DefineConstants>
  </PropertyGroup>
</Project>");

            var analyzer = new RoslynWorkspaceAnalyzer(new PhysicalFileSystem());
            analyzer.AnalyzeWorkspace(tempDir, new AnalysisConfig { RetainCompilations = true });

            Assert.NotNull(analyzer.Compilations);
            Assert.True(analyzer.Compilations!.ContainsKey("Test"));
            var compilation = analyzer.Compilations["Test"];

            // Every syntax tree we authored must carry the symbol set
            // on its parse options. Use the symbol-name property
            // because it's what RoslynCompilationHost reads — testing
            // the consumer's read path, not an internal field.
            foreach (var tree in compilation.SyntaxTrees)
            {
                if (tree.FilePath.EndsWith("<ImplicitGlobalUsings>.cs")) continue;
                var opts = Assert.IsType<CSharpParseOptions>(tree.Options);
                Assert.Contains("FEATURE_X", opts.PreprocessorSymbolNames);
            }

            // Roslyn-canonical proof: the #if-guarded class actually
            // resolves at the semantic-model layer. If defines weren't
            // threaded, the type would be missing and this assertion
            // would fail with "Cannot find Test.GatedByX". This is the
            // strict load-bearing test — even if the parse-options
            // assertion above were satisfied through some other shape,
            // this one catches the user-observable behavior.
            var type = compilation.GetTypeByMetadataName("Test.GatedByX");
            Assert.NotNull(type);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Compilation_NoDefines_KeepsGuardedSymbolsOut()
    {
        // Mirror of the previous test with FEATURE_X NOT declared. The
        // #if-guarded class must be absent from the compilation. This
        // is the inverse pin: a pre-fix build would also pass this
        // test (no defines threaded → no symbol) but if a future
        // regression starts ALWAYS-defining FEATURE_X (e.g. by passing
        // a hard-coded default symbol set into ParseOptions), this
        // test fires.
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-defines-undef-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Gated.cs"), @"
namespace Test
{
#if FEATURE_X
    public class GatedByX { }
#endif
}");

            File.WriteAllText(Path.Combine(tempDir, "Test.csproj"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <AssemblyName>Test</AssemblyName>
  </PropertyGroup>
</Project>");

            var analyzer = new RoslynWorkspaceAnalyzer(new PhysicalFileSystem());
            analyzer.AnalyzeWorkspace(tempDir, new AnalysisConfig { RetainCompilations = true });

            Assert.NotNull(analyzer.Compilations);
            var compilation = analyzer.Compilations!["Test"];

            var type = compilation.GetTypeByMetadataName("Test.GatedByX");
            Assert.Null(type);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}

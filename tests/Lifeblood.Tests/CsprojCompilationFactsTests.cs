using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// LB-FOLLOWUP-001..003: end-to-end coverage that csproj
/// <c>&lt;LangVersion&gt;</c>, <c>&lt;Nullable&gt;</c>, and
/// <c>&lt;NoWarn&gt;</c> elements propagate from discovery into Roslyn
/// compilation/parse options. Same discover→store→consume pattern as
/// <c>&lt;DefineConstants&gt;</c> (BUG-2 / LB-TRACK-002) — see
/// <see cref="DefineConstantsPropagationTests"/> for the precedent.
///
/// Each csproj attribute lives as a typed field on <see cref="ModuleInfo"/>,
/// parsed once by <see cref="RoslynModuleDiscovery"/>, consumed once by
/// <c>ModuleCompilationBuilder</c>. NEVER re-derived from the csproj at
/// the compilation layer, NEVER reinvented with a homegrown parser —
/// Roslyn ships <c>LanguageVersionFacts.TryParse</c>,
/// <c>NullableContextOptions</c>, and <c>ReportDiagnostic.Suppress</c>
/// as the canonical primitives.
/// </summary>
public class CsprojCompilationFactsTests
{
    private static string CurrentTestTargetFramework()
    {
        var targetFrameworkName = AppContext.TargetFrameworkName;
        Assert.False(string.IsNullOrWhiteSpace(targetFrameworkName));

        var framework = new System.Runtime.Versioning.FrameworkName(targetFrameworkName!);
        Assert.Equal(".NETCoreApp", framework.Identifier);
        return $"net{framework.Version.Major}.{framework.Version.Minor}";
    }

    // ──────────────────────────────────────────────────────────────────
    // LB-FOLLOWUP-001 — <LangVersion>
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Discovery_ReadsLangVersion_FromCsproj()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-langver-disc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Empty.cs"),
                "namespace Test; public class Empty { }");
            File.WriteAllText(Path.Combine(tempDir, "Test.csproj"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <AssemblyName>Test</AssemblyName>
    <LangVersion>10</LangVersion>
  </PropertyGroup>
</Project>");

            var modules = new RoslynModuleDiscovery(new PhysicalFileSystem()).DiscoverModules(tempDir);

            Assert.Single(modules);
            Assert.Equal("10", modules[0].LanguageVersion);
        }
        finally { Directory.Delete(tempDir, true); }
    }

    [Fact]
    public void Discovery_NoLangVersion_ProducesEmptyString()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-langver-empty-{Guid.NewGuid():N}");
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
            Assert.Equal(string.Empty, modules[0].LanguageVersion);
        }
        finally { Directory.Delete(tempDir, true); }
    }

    [Fact]
    public void Compilation_ThreadsLangVersion_IntoTreeParseOptions()
    {
        // End-to-end: csproj <LangVersion>10</LangVersion> → discovery
        // surfaces it → compilation builder threads through Roslyn's
        // LanguageVersionFacts.TryParse → every syntax tree's parse
        // options pin LanguageVersion.CSharp10.
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-langver-comp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Empty.cs"),
                "namespace Test; public class Empty { }");
            File.WriteAllText(Path.Combine(tempDir, "Test.csproj"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <AssemblyName>Test</AssemblyName>
    <LangVersion>10</LangVersion>
  </PropertyGroup>
</Project>");

            var analyzer = new RoslynWorkspaceAnalyzer(new PhysicalFileSystem());
            analyzer.AnalyzeWorkspace(tempDir, new AnalysisConfig { RetainCompilations = true });
            var compilation = analyzer.Compilations!["Test"];

            foreach (var tree in compilation.SyntaxTrees)
            {
                if (tree.FilePath.EndsWith("<ImplicitGlobalUsings>.cs")) continue;
                var opts = Assert.IsType<CSharpParseOptions>(tree.Options);
                Assert.Equal(LanguageVersion.CSharp10, opts.LanguageVersion);
            }
        }
        finally { Directory.Delete(tempDir, true); }
    }

    // ──────────────────────────────────────────────────────────────────
    // LB-FOLLOWUP-002 — <Nullable>
    // ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("enable",      NullableContextOptions.Enable)]
    [InlineData("disable",     NullableContextOptions.Disable)]
    [InlineData("warnings",    NullableContextOptions.Warnings)]
    [InlineData("annotations", NullableContextOptions.Annotations)]
    public void Compilation_ThreadsNullable_FromCsprojValue(string csprojValue, NullableContextOptions expected)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-nullable-{csprojValue}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Empty.cs"),
                "namespace Test; public class Empty { }");
            File.WriteAllText(Path.Combine(tempDir, "Test.csproj"), $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <AssemblyName>Test</AssemblyName>
    <Nullable>{csprojValue}</Nullable>
  </PropertyGroup>
</Project>");

            var analyzer = new RoslynWorkspaceAnalyzer(new PhysicalFileSystem());
            analyzer.AnalyzeWorkspace(tempDir, new AnalysisConfig { RetainCompilations = true });
            var compilation = analyzer.Compilations!["Test"];

            Assert.Equal(expected, compilation.Options.NullableContextOptions);
        }
        finally { Directory.Delete(tempDir, true); }
    }

    [Fact]
    public void Compilation_NoNullable_DefaultsToDisable()
    {
        // Mirrors Roslyn's default for a project that declares no <Nullable>
        // property. INV-COMPFACT-001..003 / LB-FOLLOWUP-002.
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-nullable-default-{Guid.NewGuid():N}");
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

            var analyzer = new RoslynWorkspaceAnalyzer(new PhysicalFileSystem());
            analyzer.AnalyzeWorkspace(tempDir, new AnalysisConfig { RetainCompilations = true });
            var compilation = analyzer.Compilations!["Test"];

            Assert.Equal(NullableContextOptions.Disable, compilation.Options.NullableContextOptions);
        }
        finally { Directory.Delete(tempDir, true); }
    }

    // ──────────────────────────────────────────────────────────────────
    // LB-FOLLOWUP-003 — <NoWarn>
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Discovery_ReadsNoWarn_SemicolonList()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-nowarn-disc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Empty.cs"),
                "namespace Test; public class Empty { }");
            File.WriteAllText(Path.Combine(tempDir, "Test.csproj"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <AssemblyName>Test</AssemblyName>
    <NoWarn>CS8632;CS8765;CS1591</NoWarn>
  </PropertyGroup>
</Project>");

            var modules = new RoslynModuleDiscovery(new PhysicalFileSystem()).DiscoverModules(tempDir);

            Assert.Single(modules);
            // INV-DIAGNOSTIC-MSBUILD-IMPLICIT-NOWARN-001: the csproj-
            // declared set unions with the MSBuild csc-default baseline
            // (CS1701, CS1702). The user-declared entries are still all
            // present; baseline is additive, never replaces.
            var declared = new HashSet<string>(modules[0].NoWarnDiagnosticIds);
            Assert.Contains("CS8632", declared);
            Assert.Contains("CS8765", declared);
            Assert.Contains("CS1591", declared);
            Assert.Contains("CS1701", declared);
            Assert.Contains("CS1702", declared);
        }
        finally { Directory.Delete(tempDir, true); }
    }

    [Fact]
    public void Compilation_SuppressesNoWarnDiagnostics()
    {
        // End-to-end: csproj declares <NoWarn>CS8632</NoWarn> →
        // compilation's SpecificDiagnosticOptions maps CS8632 →
        // ReportDiagnostic.Suppress. INV-COMPFACT-001..003 / LB-FOLLOWUP-003.
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-nowarn-comp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Empty.cs"),
                "namespace Test; public class Empty { }");
            File.WriteAllText(Path.Combine(tempDir, "Test.csproj"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <AssemblyName>Test</AssemblyName>
    <NoWarn>CS8632;CS1591</NoWarn>
  </PropertyGroup>
</Project>");

            var analyzer = new RoslynWorkspaceAnalyzer(new PhysicalFileSystem());
            analyzer.AnalyzeWorkspace(tempDir, new AnalysisConfig { RetainCompilations = true });
            var compilation = analyzer.Compilations!["Test"];

            Assert.True(compilation.Options.SpecificDiagnosticOptions.TryGetValue("CS8632", out var cs8632Severity));
            Assert.Equal(ReportDiagnostic.Suppress, cs8632Severity);
            Assert.True(compilation.Options.SpecificDiagnosticOptions.TryGetValue("CS1591", out var cs1591Severity));
            Assert.Equal(ReportDiagnostic.Suppress, cs1591Severity);
        }
        finally { Directory.Delete(tempDir, true); }
    }

    [Fact]
    public void Discovery_ReadsCompilerFeatures_FromCsproj()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-features-disc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Empty.cs"),
                "namespace Test; public class Empty { }");
            File.WriteAllText(Path.Combine(tempDir, "Test.csproj"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <AssemblyName>Test</AssemblyName>
    <Features>runtime-async=on;other-feature=enabled</Features>
  </PropertyGroup>
</Project>");

            var modules = new RoslynModuleDiscovery(new PhysicalFileSystem()).DiscoverModules(tempDir);

            Assert.Single(modules);
            Assert.Equal("on", modules[0].CompilerFeatures["runtime-async"]);
            Assert.Equal("enabled", modules[0].CompilerFeatures["other-feature"]);
        }
        finally { Directory.Delete(tempDir, true); }
    }

    [Fact]
    public void Compilation_ThreadsCompilerFeatures_IntoParseOptions()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-features-comp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Empty.cs"),
                "namespace Test; public class Empty { }");
            File.WriteAllText(Path.Combine(tempDir, "Test.csproj"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <AssemblyName>Test</AssemblyName>
    <Features>runtime-async=on</Features>
  </PropertyGroup>
</Project>");

            var analyzer = new RoslynWorkspaceAnalyzer(new PhysicalFileSystem());
            analyzer.AnalyzeWorkspace(tempDir, new AnalysisConfig { RetainCompilations = true });
            var compilation = analyzer.Compilations!["Test"];

            foreach (var tree in compilation.SyntaxTrees)
            {
                if (tree.FilePath.EndsWith("<ImplicitGlobalUsings>.cs")) continue;
                var opts = Assert.IsType<CSharpParseOptions>(tree.Options);
                Assert.Equal("on", opts.Features["runtime-async"]);
            }
        }
        finally { Directory.Delete(tempDir, true); }
    }

    [Fact]
    public void Compilation_ThreadsCompilerFeatures_IntoSyntheticImplicitGlobalUsings()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-features-implicit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Empty.cs"),
                "namespace Test; public class Empty { }");
            File.WriteAllText(Path.Combine(tempDir, "Test.csproj"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <AssemblyName>Test</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
    <Features>runtime-async=on</Features>
  </PropertyGroup>
</Project>");

            var analyzer = new RoslynWorkspaceAnalyzer(new PhysicalFileSystem());
            analyzer.AnalyzeWorkspace(tempDir, new AnalysisConfig { RetainCompilations = true });
            var compilation = analyzer.Compilations!["Test"];
            var implicitTree = Assert.Single(compilation.SyntaxTrees,
                tree => tree.FilePath.EndsWith("<ImplicitGlobalUsings>.cs", StringComparison.Ordinal));
            var opts = Assert.IsType<CSharpParseOptions>(implicitTree.Options);

            Assert.Equal("on", opts.Features["runtime-async"]);
        }
        finally { Directory.Delete(tempDir, true); }
    }

    [Fact]
    public void Discovery_FindsFrameworkSourceGeneratorAnalyzers_ForTargetFramework()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-sg-disc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Empty.cs"),
                "namespace Test; public class Empty { }");
            File.WriteAllText(Path.Combine(tempDir, "Test.csproj"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <AssemblyName>Test</AssemblyName>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>");

            var modules = new RoslynModuleDiscovery(new PhysicalFileSystem()).DiscoverModules(tempDir);

            Assert.Single(modules);
            Assert.Equal("net8.0", modules[0].TargetFramework);
            Assert.Contains(
                modules[0].SourceGeneratorAnalyzerPaths,
                path => path.EndsWith("System.Text.Json.SourceGeneration.dll", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(tempDir, true); }
    }

    [Fact]
    public void Discovery_ParsesPreviewFrameworkPackVersions()
    {
        Assert.Equal(11, RoslynModuleDiscovery.ParseNetTargetFrameworkMajor("net11.0"));

        Assert.True(
            RoslynModuleDiscovery.TryParseVersion(
                "11.0.0-preview.4.26230.115",
                out var preview));
        Assert.Equal(11, preview.Major);
        Assert.Equal(0, preview.Minor);
        Assert.Equal(0, preview.Build);
    }

    [Fact]
    public void Compilation_RunsFrameworkSourceGenerators_ForJsonSerializerContext()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-sg-comp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Program.cs"), """
                using System.Text.Json.Serialization;

                namespace Test;

                public sealed record Payload(int Id);

                [JsonSerializable(typeof(Payload))]
                public partial class MyContext : JsonSerializerContext
                {
                }

                public static class Runner
                {
                    public static object Run() => MyContext.Default.Payload;
                }
                """);
            var targetFramework = CurrentTestTargetFramework();
            File.WriteAllText(Path.Combine(tempDir, "Test.csproj"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <AssemblyName>Test</AssemblyName>
    <TargetFramework>" + targetFramework + @"</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");

            var analyzer = new RoslynWorkspaceAnalyzer(new PhysicalFileSystem());
            analyzer.AnalyzeWorkspace(tempDir, new AnalysisConfig { RetainCompilations = true });
            var compilation = analyzer.Compilations!["Test"];

            var context = compilation.GetTypeByMetadataName("Test.MyContext");
            Assert.NotNull(context);
            Assert.NotEmpty(context!.GetMembers("Payload"));

            var errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .ToArray();
            Assert.DoesNotContain(errors, d => d.Id == "CS0117");
            Assert.Empty(errors);
        }
        finally { Directory.Delete(tempDir, true); }
    }
}

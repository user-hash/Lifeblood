using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Discovery-side regression suite for
/// INV-DIAGNOSTIC-MSBUILD-IMPLICIT-NOWARN-001. Every Lifeblood-discovered
/// module must carry MSBuild's csc-default <c>NoWarn</c> baseline
/// (<c>CS1701</c>, <c>CS1702</c>) on
/// <see cref="ModuleInfo.NoWarnDiagnosticIds"/> regardless of whether
/// the csproj itself declares <c>&lt;NoWarn&gt;</c>. Source of truth:
/// <c>Microsoft.CSharp.CurrentVersion.targets</c> sets
/// <c>&lt;NoWarn&gt;$(NoWarn);1701;1702&lt;/NoWarn&gt;</c> for every
/// csc invocation MSBuild produces. Not mirroring this leaks 7,000+
/// spurious cross-module TypeRef binding-redirect warnings into
/// diagnose output for any workspace that mixes BCL ref pack types with
/// NuGet contract-assembly TypeRefs (xunit / Microsoft.NET.Test.Sdk is
/// the canonical shape).
///
/// Sibling end-to-end pin: `BuildDiagnosticParityTests.LifebloodSelfDiagnose_NeverFiresParityClassDiagnostics`.
/// </summary>
public class MsbuildImplicitNoWarnBaselineTests
{
    [Fact]
    public void Discovery_PlainCsproj_StillCarriesImplicitBaseline()
    {
        // A csproj declaring no <NoWarn> still gets the MSBuild baseline.
        // Mirrors the dotnet build invocation: even with empty <NoWarn>,
        // csc receives /nowarn:1701,1702 from Microsoft.CSharp.CurrentVersion.targets.
        using var tempDir = new ScratchDir();
        tempDir.WriteFile("Plain.cs", "namespace P { public class C { } }");
        tempDir.WriteFile("Plain.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        var modules = new RoslynModuleDiscovery(new PhysicalFileSystem()).DiscoverModules(tempDir.Path);
        var module = Assert.Single(modules);

        Assert.Contains("CS1701", module.NoWarnDiagnosticIds);
        Assert.Contains("CS1702", module.NoWarnDiagnosticIds);
    }

    [Fact]
    public void Discovery_CsprojWithUserNoWarn_UnionsWithBaseline()
    {
        // User-declared <NoWarn> entries union with the baseline; neither
        // wins exclusively. CS8632 stays from the csproj; CS1701 + CS1702
        // come from the baseline.
        using var tempDir = new ScratchDir();
        tempDir.WriteFile("U.cs", "namespace U { public class C { } }");
        tempDir.WriteFile("U.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <NoWarn>CS8632</NoWarn>
              </PropertyGroup>
            </Project>
            """);

        var modules = new RoslynModuleDiscovery(new PhysicalFileSystem()).DiscoverModules(tempDir.Path);
        var module = Assert.Single(modules);

        Assert.Equal(
            new HashSet<string> { "CS8632", "CS1701", "CS1702" },
            new HashSet<string>(module.NoWarnDiagnosticIds));
    }

    [Fact]
    public void Discovery_UserNoWarnDuplicatingBaseline_StaysDeduped()
    {
        // If the user already declared CS1701 (defensive csproj), the
        // dedup-by-ordinal-ignore-case keeps a single entry. No double-key
        // in the downstream WithSpecificDiagnosticOptions dictionary.
        using var tempDir = new ScratchDir();
        tempDir.WriteFile("D.cs", "namespace D { public class C { } }");
        tempDir.WriteFile("D.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <NoWarn>CS1701;CS8632</NoWarn>
              </PropertyGroup>
            </Project>
            """);

        var modules = new RoslynModuleDiscovery(new PhysicalFileSystem()).DiscoverModules(tempDir.Path);
        var module = Assert.Single(modules);

        Assert.Equal(
            new HashSet<string> { "CS1701", "CS1702", "CS8632" },
            new HashSet<string>(module.NoWarnDiagnosticIds));
        Assert.Equal(3, module.NoWarnDiagnosticIds.Length);
    }

    [Fact]
    public void Compilation_ThreadsBaselineThroughToSpecificDiagnosticOptions()
    {
        // End-to-end: discovery baseline lands on the CSharpCompilation's
        // SpecificDiagnosticOptions as ReportDiagnostic.Suppress, the same
        // shape the existing INV-COMPFACT-001..003 NoWarn thread-through
        // produces for user-declared <NoWarn> entries.
        using var tempDir = new ScratchDir();
        tempDir.WriteFile("Plain.cs", "namespace P { public class C { } }");
        tempDir.WriteFile("Plain.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        var analyzer = new RoslynWorkspaceAnalyzer(new PhysicalFileSystem());
        analyzer.AnalyzeWorkspace(tempDir.Path, new AnalysisConfig { RetainCompilations = true });
        var compilation = analyzer.Compilations!["Plain"];

        Assert.True(compilation.Options.SpecificDiagnosticOptions.TryGetValue("CS1701", out var cs1701Severity));
        Assert.Equal(ReportDiagnostic.Suppress, cs1701Severity);
        Assert.True(compilation.Options.SpecificDiagnosticOptions.TryGetValue("CS1702", out var cs1702Severity));
        Assert.Equal(ReportDiagnostic.Suppress, cs1702Severity);
    }

    private sealed class ScratchDir : IDisposable
    {
        public string Path { get; }

        public ScratchDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"lifeblood-nowarn-baseline-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public void WriteFile(string relativePath, string content)
            => File.WriteAllText(System.IO.Path.Combine(Path, relativePath), content);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Discovery-layer regression tests for INV-MODULE-REFS-001 — the csproj
/// shape determines whether reference closure is transitive (SDK-style
/// MSBuild) or direct-only (old-format MSBuild 2003 schema / Unity
/// asmdef generator output).
///
/// The detection signal is csproj-shape only:
///   - Root xmlns = "http://schemas.microsoft.com/developer/msbuild/2003"
///     AND no Sdk attribute → DirectOnly.
///   - Otherwise (SDK-style, or unknown shape) → Transitive.
///
/// No path heuristics, no Unity-specific markers, no filename sniffing.
/// </summary>
public class ReferenceClosureModeDiscoveryTests
{
    private static ModuleInfo[] DiscoverFromCsproj(string csprojBody)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-refclosure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Probe.cs"),
                "namespace Probe; public class P { }");
            File.WriteAllText(Path.Combine(tempDir, "Probe.csproj"), csprojBody);

            var fs = new PhysicalFileSystem();
            return new RoslynModuleDiscovery(fs).DiscoverModules(tempDir);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void OldFormatMsBuild2003Schema_NoSdkAttribute_SetsDirectOnly()
    {
        // Unity asmdef-generated csprojs use the 2003 schema with explicit
        // <Compile Include> items and no Sdk attribute. The 2003-schema
        // MSBuild targets never close ProjectReference transitively — the
        // compile classpath is exactly the declared direct deps.
        var modules = DiscoverFromCsproj(@"<?xml version=""1.0"" encoding=""utf-8""?>
<Project ToolsVersion=""4.0"" DefaultTargets=""Build"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup>
    <AssemblyName>Probe</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include=""Probe.cs"" />
  </ItemGroup>
</Project>");

        Assert.Single(modules);
        Assert.Equal(ReferenceClosureMode.DirectOnly, modules[0].ReferenceClosure);
    }

    [Fact]
    public void SdkStyleCsproj_SetsTransitive()
    {
        // Modern .NET SDK-style csprojs (Lifeblood self, NuGet ecosystem)
        // declare the Sdk attribute and use no XML namespace. MSBuild for
        // these projects closes ProjectReference transitively, so the
        // compile classpath includes assemblies reached through public
        // surface — Lifeblood mirrors that.
        var modules = DiscoverFromCsproj(@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyName>Probe</AssemblyName>
  </PropertyGroup>
</Project>");

        Assert.Single(modules);
        Assert.Equal(ReferenceClosureMode.Transitive, modules[0].ReferenceClosure);
    }

    [Fact]
    public void OldFormatSchemaWithSdkAttribute_StaysTransitive()
    {
        // Defensive: the detection ANDs schema + absent-Sdk so a hypothetical
        // hybrid (2003-schema namespace but with an Sdk attribute) does not
        // flip to DirectOnly. Such a project is non-standard but should be
        // treated as transitive because the Sdk attribute pulls in the
        // SDK-style MSBuild targets, which DO close transitively.
        var modules = DiscoverFromCsproj(@"<?xml version=""1.0"" encoding=""utf-8""?>
<Project Sdk=""Microsoft.NET.Sdk"" ToolsVersion=""4.0"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyName>Probe</AssemblyName>
  </PropertyGroup>
</Project>");

        Assert.Single(modules);
        Assert.Equal(ReferenceClosureMode.Transitive, modules[0].ReferenceClosure);
    }

    [Fact]
    public void DefaultReferenceClosure_IsTransitive()
    {
        // ModuleInfo default preserves pre-fix behavior for any caller that
        // constructs an instance without setting ReferenceClosure (test
        // fixtures, ad-hoc graph synthesis, future adapters).
        var module = new ModuleInfo { Name = "X" };
        Assert.Equal(ReferenceClosureMode.Transitive, module.ReferenceClosure);
    }
}

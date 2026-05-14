using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Left;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// End-to-end regression tests for INV-MODULE-REFS-001 covering the canonical
/// failure shape: bare-name BCL calls (<c>Math.Min</c>, <c>Math.PI</c>) inside
/// a sibling-namespace module of a workspace that ALSO declares an
/// <c>Acme.Math</c> namespace in a separate, NOT-asmdef-referenced module.
///
/// Before the fix: Lifeblood's transitive-closure heuristic added the
/// sibling-namespace module's assembly to the consumer compilation regardless
/// of the asmdef reference graph; the sibling namespace shadowed
/// <c>System.Math</c> per C# §11.7.2 lookup precedence; diagnose emitted a
/// spurious CS0234 on every bare <c>Math.X</c> call that Unity ships clean.
///
/// After the fix: discovery flags old-format MSBuild 2003-schema csprojs as
/// <see cref="ReferenceClosureMode.DirectOnly"/>; the compilation builder
/// resolves references against the declared direct deps only; the consumer
/// compilation never sees the sibling-namespace module's assembly, and bare
/// <c>Math.X</c> binds to <see cref="System.Math"/> as Unity does.
/// </summary>
public class ReferenceClosureCompilationTests
{
    /// <summary>
    /// Builds a 3-module Unity-shape workspace on disk:
    ///   - MathLib    : declares <c>namespace Acme.Math</c>, no deps
    ///   - Bridge     : references MathLib (declares dep, has trivial source)
    ///   - Consumer   : references Bridge ONLY. Bare <c>Math.PI</c> usage in
    ///                  <c>namespace Acme.App</c>. This is the DAWG repro shape.
    ///
    /// All three csprojs use the old-format MSBuild 2003 schema so discovery
    /// flags them DirectOnly. The .sln ties them together.
    /// </summary>
    private static string BuildWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lifeblood-refclosure-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "Workspace.sln"), @"
Microsoft Visual Studio Solution File, Format Version 12.00
Project(""{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"") = ""MathLib"", ""MathLib\MathLib.csproj"", ""{11111111-1111-1111-1111-111111111111}""
EndProject
Project(""{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"") = ""Bridge"", ""Bridge\Bridge.csproj"", ""{22222222-2222-2222-2222-222222222222}""
EndProject
Project(""{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"") = ""Consumer"", ""Consumer\Consumer.csproj"", ""{33333333-3333-3333-3333-333333333333}""
EndProject
");

        var mathDir = Path.Combine(root, "MathLib");
        Directory.CreateDirectory(mathDir);
        File.WriteAllText(Path.Combine(mathDir, "Util.cs"),
            "namespace Acme.Math { public static class Util { public static int Five() => 5; } }");
        File.WriteAllText(Path.Combine(mathDir, "MathLib.csproj"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project ToolsVersion=""4.0"" DefaultTargets=""Build"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup>
    <AssemblyName>MathLib</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include=""Util.cs"" />
  </ItemGroup>
</Project>");

        var bridgeDir = Path.Combine(root, "Bridge");
        Directory.CreateDirectory(bridgeDir);
        File.WriteAllText(Path.Combine(bridgeDir, "Bridge.cs"),
            "namespace Acme.Bridge { public class B { public int Take() => Acme.Math.Util.Five(); } }");
        File.WriteAllText(Path.Combine(bridgeDir, "Bridge.csproj"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project ToolsVersion=""4.0"" DefaultTargets=""Build"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup>
    <AssemblyName>Bridge</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include=""Bridge.cs"" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include=""MathLib"" />
  </ItemGroup>
</Project>");

        var consumerDir = Path.Combine(root, "Consumer");
        Directory.CreateDirectory(consumerDir);
        // The repro: bare Math.PI in Acme.App namespace, sibling to Acme.Math.
        // No alias workaround. No MathLib reference declared. Unity ships
        // this shape clean every day on DAWG.
        File.WriteAllText(Path.Combine(consumerDir, "App.cs"), @"
using System;
namespace Acme.App
{
    public static class App
    {
        public static double Pi() => Math.PI;
        public static double MinPair(double a, double b) => Math.Min(a, b);
    }
}");
        File.WriteAllText(Path.Combine(consumerDir, "Consumer.csproj"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project ToolsVersion=""4.0"" DefaultTargets=""Build"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup>
    <AssemblyName>Consumer</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include=""App.cs"" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include=""Bridge"" />
  </ItemGroup>
</Project>");

        return root;
    }

    [Fact]
    public void OldFormatCsproj_BareMathInSiblingNamespace_BindsToSystemMath()
    {
        // The DAWG repro shape, distilled. With DirectOnly mode inferred from
        // the 2003-schema csprojs, the Consumer compilation never gets MathLib
        // as a metadata reference, so the Acme.Math namespace is invisible
        // and bare Math.PI / Math.Min bind cleanly to System.Math.
        var root = BuildWorkspace();
        try
        {
            var fs = new PhysicalFileSystem();
            var modules = new RoslynModuleDiscovery(fs).DiscoverModules(root);
            var consumer = modules.Single(m => m.Name == "Consumer");
            Assert.Equal(ReferenceClosureMode.DirectOnly, consumer.ReferenceClosure);
            Assert.Equal(new[] { "Bridge" }, consumer.Dependencies);

            var analyzer = new RoslynWorkspaceAnalyzer(fs);
            analyzer.AnalyzeWorkspace(root, new AnalysisConfig { RetainCompilations = true });

            Assert.NotNull(analyzer.Compilations);
            var consumerComp = analyzer.Compilations!["Consumer"];

            // MathLib MUST NOT appear in Consumer's referenced assemblies
            // when DirectOnly is in effect — that is the load-bearing
            // contract of the fix.
            Assert.DoesNotContain(consumerComp.SourceModule.ReferencedAssemblies,
                ra => ra.Name.Equals("MathLib", StringComparison.Ordinal));

            // CS0234 is what Lifeblood reported pre-fix on every bare Math.X
            // in a workspace where a sibling namespace shadowed System.Math.
            // No such diagnostic should appear here.
            var consumerErrors = consumerComp.GetDiagnostics()
                .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .Where(d => d.Location.SourceTree?.FilePath?.EndsWith("App.cs", StringComparison.Ordinal) == true)
                .ToArray();
            Assert.Empty(consumerErrors);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void TransitiveMode_BareMathInSiblingNamespace_ShadowsSystemMath()
    {
        // Contrast test: same source files, but the Consumer module is forced
        // into Transitive mode. This proves the fix is the closure-mode
        // branch and not an unrelated change — under Transitive mode, MathLib
        // gets pulled into Consumer's classpath and the spurious CS0234 fires
        // as it did pre-fix on Unity workspaces. The other two modules can
        // stay in their natural mode; only Consumer's choice matters for
        // its own bind behavior.
        var root = BuildWorkspace();
        try
        {
            var fs = new PhysicalFileSystem();
            var natural = new RoslynModuleDiscovery(fs).DiscoverModules(root);
            var forced = natural
                .Select(m => m.Name == "Consumer"
                    ? new ModuleInfo
                    {
                        Name = m.Name,
                        FilePaths = m.FilePaths,
                        Dependencies = m.Dependencies,
                        IsPure = m.IsPure,
                        ExternalDllPaths = m.ExternalDllPaths,
                        BclOwnership = m.BclOwnership,
                        AllowUnsafeCode = m.AllowUnsafeCode,
                        ImplicitUsings = m.ImplicitUsings,
                        ReferenceClosure = ReferenceClosureMode.Transitive,
                        Properties = m.Properties,
                    }
                    : m)
                .ToArray();

            var builder = new ModuleCompilationBuilder(fs);
            CSharpCompilation? consumerComp = null;
            builder.ProcessInOrder(forced, root,
                new AnalysisConfig { RetainCompilations = true },
                (mod, comp) =>
                {
                    if (mod.Name == "Consumer") consumerComp = (CSharpCompilation)comp;
                });

            Assert.NotNull(consumerComp);

            // Under Transitive mode, MathLib leaks into Consumer's classpath
            // — the very leak that produced the DAWG false positives.
            Assert.Contains(consumerComp!.SourceModule.ReferencedAssemblies,
                ra => ra.Name.Equals("MathLib", StringComparison.Ordinal));

            // And the predicted spurious CS0234 fires.
            var bareMathErrors = consumerComp.GetDiagnostics()
                .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .Where(d => d.Id == "CS0234")
                .ToArray();
            Assert.NotEmpty(bareMathErrors);
        }
        finally { Directory.Delete(root, true); }
    }
}

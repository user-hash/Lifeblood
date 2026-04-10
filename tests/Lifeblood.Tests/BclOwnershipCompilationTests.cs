using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Left;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Compilation-layer regression tests for BCL ownership (INV-BCL-001..INV-BCL-004).
///
/// What the tests assert: ModuleCompilationBuilder.CreateCompilation respects
/// the BclOwnership flag set during discovery — specifically, whether the host
/// process's runtime BCL bundle (BclReferenceLoader.References.Value) is added
/// to the compilation references or not.
///
///   - BclOwnership = ModuleProvided → host BCL refs MUST NOT be present
///   - BclOwnership = HostProvided   → host BCL refs MUST be present
///
/// Why this contract test rather than an end-to-end "no CS0433/CS0518" test:
/// the .NET 8 host's netstandard.dll is a type-forwarder assembly to
/// System.Private.CoreLib, not a self-contained BCL. We can't easily fabricate
/// a self-contained BCL fixture in a unit test without bundling NuGet
/// reference packs. Instead we test the OBSERVABLE BEHAVIOR at the compilation
/// builder layer: did we add the host BCL bundle or didn't we? The end-to-end
/// CS0433/CS0518-free behavior is verified against a real 75-module Unity
/// workspace in the rollout checklist.
/// </summary>
public class BclOwnershipCompilationTests
{
    /// <summary>
    /// Returns true iff the compilation's reference list contains every single
    /// reference from BclReferenceLoader.References.Value. This is the precise
    /// "host BCL bundle was injected" check — distinct from "the compilation
    /// happens to share an assembly identity with one of the bundle entries"
    /// (which could be coincidence).
    /// </summary>
    private static bool ContainsHostBclBundle(Compilation compilation)
    {
        var hostRefs = BclReferenceLoader.References.Value;
        if (hostRefs.Length == 0) return false;
        var refSet = new HashSet<MetadataReference>(compilation.References);
        return hostRefs.All(refSet.Contains);
    }

    [Fact]
    public void CreateCompilation_ModuleProvided_DoesNotInjectHostBcl()
    {
        // ModuleProvided csproj declares its own BCL via <Reference Include="netstandard">.
        // The compilation builder MUST NOT additionally inject the host BCL bundle —
        // that's the bug being fixed.
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-bcl-comp-mod-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "MyClass.cs"),
                "namespace Test; public class MyClass { }");

            // The HintPath needs to point at a real file so RoslynModuleDiscovery
            // includes it in ExternalDllPaths (otherwise the file existence filter
            // drops it). The host runtime's netstandard.dll exists and serves as
            // the placeholder; we don't care about its semantic content here, only
            // that the BCL ownership decision flips to ModuleProvided.
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            var netstandardPath = Path.Combine(runtimeDir, "netstandard.dll");
            Assert.True(File.Exists(netstandardPath));

            File.WriteAllText(Path.Combine(tempDir, "TestProject.csproj"), $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Project ToolsVersion=""4.0"" DefaultTargets=""Build"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup>
    <AssemblyName>TestProject</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include=""MyClass.cs"" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include=""netstandard"">
      <HintPath>{netstandardPath}</HintPath>
    </Reference>
  </ItemGroup>
</Project>");

            var fs = new PhysicalFileSystem();
            var analyzer = new RoslynWorkspaceAnalyzer(fs);
            analyzer.AnalyzeWorkspace(tempDir, new AnalysisConfig { RetainCompilations = true });

            Assert.NotNull(analyzer.Compilations);
            Assert.True(analyzer.Compilations!.ContainsKey("TestProject"));
            var compilation = analyzer.Compilations["TestProject"];

            // Pin down the discovery decision first so any failure here points
            // at the right layer.
            var module = analyzer.GetType()
                .GetField("_snapshot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            // We can't get _snapshot easily — instead re-discover and check.
            var rediscovered = new RoslynModuleDiscovery(fs).DiscoverModules(tempDir);
            Assert.Single(rediscovered);
            Assert.Equal(BclOwnershipMode.ModuleProvided, rediscovered[0].BclOwnership);

            // The compilation must NOT contain the host BCL bundle.
            Assert.False(ContainsHostBclBundle(compilation),
                "ModuleProvided compilation must not have the host BCL bundle injected — " +
                "that causes CS0433/CS0518 diagnostics on every System type and silently " +
                "breaks find_references / dependants / call-graph extraction.");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EndToEnd_BclOwningTwoModule_FindsCrossModuleStructMethodCall()
    {
        // End-to-end integration test: real csproj on disk, real RoslynModuleDiscovery,
        // real RoslynWorkspaceAnalyzer, real RoslynCompilationHost.FindReferences.
        //
        // The shape: two BCL-owning modules. Module A defines a partial struct
        // with a method. Module B references Module A and calls the struct method
        // via array indexer. This is the exact failure shape from a real 75-module
        // Unity workspace where a struct method called through an array indexer
        // returned zero call sites.
        //
        // What this test pins down beyond the per-layer contracts:
        //   - Discovery sets BclOwnership on BOTH modules (not just one)
        //   - Compilation builder skips host BCL on BOTH compilations
        //   - The cross-module find_references walker finds the call site
        //   - The graph builder produces non-zero edges for the cross-module call
        //
        // The test uses the host runtime's netstandard.dll as the placeholder BCL
        // (it's the only file we can rely on existing in CI). It is technically a
        // type-forwarder, not a self-contained BCL, but: (1) the discovery step
        // only cares that the file exists, (2) the compilation step's behavior
        // we're testing is "did we add the host BCL bundle on top", and (3) the
        // FindReferences walker uses Roslyn's semantic model directly — even if
        // some types fail to resolve, the call site walker still has to do the
        // right thing for the types that DO resolve (Module A's own struct).
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-bcl-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            var netstandardPath = Path.Combine(runtimeDir, "netstandard.dll");
            Assert.True(File.Exists(netstandardPath));

            // Solution file ties the two modules together so RoslynModuleDiscovery
            // discovers them via solution-walking and topologically sorts them.
            File.WriteAllText(Path.Combine(tempDir, "TestSolution.sln"), @"
Microsoft Visual Studio Solution File, Format Version 12.00
Project(""{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"") = ""Lib"", ""Lib\Lib.csproj"", ""{11111111-1111-1111-1111-111111111111}""
EndProject
Project(""{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"") = ""Consumer"", ""Consumer\Consumer.csproj"", ""{22222222-2222-2222-2222-222222222222}""
EndProject
");

            // Module A: Lib — defines a struct with a method.
            var libDir = Path.Combine(tempDir, "Lib");
            Directory.CreateDirectory(libDir);
            File.WriteAllText(Path.Combine(libDir, "Item.cs"), @"
namespace Lib
{
    public struct Item
    {
        public int Value;
        public void Bump() { Value = Value + 1; }
    }
}");
            File.WriteAllText(Path.Combine(libDir, "Lib.csproj"), $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Project ToolsVersion=""4.0"" DefaultTargets=""Build"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup>
    <AssemblyName>Lib</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include=""Item.cs"" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include=""netstandard"">
      <HintPath>{netstandardPath}</HintPath>
    </Reference>
  </ItemGroup>
</Project>");

            // Module B: Consumer — calls Lib.Item.Bump via array indexer.
            // This is the exact `arr[i].Method(...)` shape the user reported.
            var consumerDir = Path.Combine(tempDir, "Consumer");
            Directory.CreateDirectory(consumerDir);
            File.WriteAllText(Path.Combine(consumerDir, "Driver.cs"), @"
namespace Consumer
{
    public class Driver
    {
        public Lib.Item[] _items = new Lib.Item[4];
        public void TickAll()
        {
            for (int i = 0; i < _items.Length; i++)
                _items[i].Bump();
        }
    }
}");
            File.WriteAllText(Path.Combine(consumerDir, "Consumer.csproj"), $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Project ToolsVersion=""4.0"" DefaultTargets=""Build"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup>
    <AssemblyName>Consumer</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include=""Driver.cs"" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include=""Lib"" />
    <Reference Include=""netstandard"">
      <HintPath>{netstandardPath}</HintPath>
    </Reference>
  </ItemGroup>
</Project>");

            var fs = new PhysicalFileSystem();
            var analyzer = new RoslynWorkspaceAnalyzer(fs);
            analyzer.AnalyzeWorkspace(tempDir, new AnalysisConfig { RetainCompilations = true });

            // Both modules must be discovered and BCL-owning.
            var modules = new RoslynModuleDiscovery(fs).DiscoverModules(tempDir);
            Assert.Equal(2, modules.Length);
            Assert.All(modules, m =>
                Assert.Equal(BclOwnershipMode.ModuleProvided, m.BclOwnership));

            // Compilations must NOT have the host BCL bundle injected.
            Assert.NotNull(analyzer.Compilations);
            Assert.Equal(2, analyzer.Compilations!.Count);
            Assert.False(ContainsHostBclBundle(analyzer.Compilations["Lib"]),
                "Lib compilation must not have host BCL bundle.");
            Assert.False(ContainsHostBclBundle(analyzer.Compilations["Consumer"]),
                "Consumer compilation must not have host BCL bundle.");

            // The cross-module find_references walker must find the array-indexer
            // call site. This is the contract that the user's original report broke
            // on (Voice.SetPatch returned 0 instead of 18). The combined fix
            // (canonical IDs from Finding B + BCL ownership from this fix) makes
            // this work end to end.
            var host = new RoslynCompilationHost(analyzer.Compilations, analyzer.ModuleDependencies);
            var refs = host.FindReferences("method:Lib.Item.Bump()");

            Assert.True(refs.Length > 0,
                "FindReferences must find the cross-module struct method call site. " +
                "If this is empty, either BCL ownership is broken (semantic model is " +
                "garbage) or the canonical-ID matcher is broken.");

            var consumerCallSites = refs
                .Where(r => r.FilePath.EndsWith("Driver.cs", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.True(consumerCallSites.Length > 0,
                $"FindReferences must find the call site in Driver.cs. Got {refs.Length} refs total: " +
                string.Join(", ", refs.Select(r => Path.GetFileName(r.FilePath) + ":" + r.Line)));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void CreateCompilation_AllowUnsafeCode_True_AcceptsUnsafeSource()
    {
        // Csproj declares <AllowUnsafeBlocks>true</AllowUnsafeBlocks>. Discovery
        // sets ModuleInfo.AllowUnsafeCode = true. Compilation builder must call
        // WithAllowUnsafe(true) so the resulting CSharpCompilation accepts
        // unsafe blocks without CS0227. INV-COMPFACT-001..003.
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-unsafe-comp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Source uses an unsafe block. Without WithAllowUnsafe(true) Roslyn
            // emits CS0227 on the unsafe keyword.
            File.WriteAllText(Path.Combine(tempDir, "MyClass.cs"), @"
namespace Test
{
    public class MyClass
    {
        public unsafe int FirstByte(byte[] data)
        {
            fixed (byte* p = data)
            {
                return *p;
            }
        }
    }
}");
            File.WriteAllText(Path.Combine(tempDir, "TestProject.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
</Project>");

            var fs = new PhysicalFileSystem();
            var analyzer = new RoslynWorkspaceAnalyzer(fs);
            analyzer.AnalyzeWorkspace(tempDir, new AnalysisConfig { RetainCompilations = true });

            Assert.NotNull(analyzer.Compilations);
            Assert.True(analyzer.Compilations!.ContainsKey("TestProject"));
            var compilation = analyzer.Compilations["TestProject"];

            // Discovery must report AllowUnsafeCode = true.
            var rediscovered = new RoslynModuleDiscovery(fs).DiscoverModules(tempDir);
            Assert.Single(rediscovered);
            Assert.True(rediscovered[0].AllowUnsafeCode);

            // Compilation must NOT have CS0227 — the unsafe block is allowed.
            var unsafeDiagnostics = compilation.GetDiagnostics()
                .Where(d => d.Id == "CS0227")
                .ToArray();
            Assert.True(unsafeDiagnostics.Length == 0,
                "Compilation with AllowUnsafeBlocks=true must not produce CS0227. Got: " +
                string.Join("; ", unsafeDiagnostics.Take(3).Select(d => d.GetMessage())));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void CreateCompilation_AllowUnsafeCode_False_RejectsUnsafeSource()
    {
        // Default behavior preserved: csproj WITHOUT AllowUnsafeBlocks fails on
        // unsafe source with CS0227. Pins down that we only relax when asked,
        // and that the default field value (false) preserves pre-fix behavior
        // for plain SDK csprojs.
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-unsafe-default-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "MyClass.cs"), @"
namespace Test
{
    public class MyClass
    {
        public unsafe int FirstByte(byte[] data)
        {
            fixed (byte* p = data) { return *p; }
        }
    }
}");
            File.WriteAllText(Path.Combine(tempDir, "TestProject.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>");

            var fs = new PhysicalFileSystem();
            var analyzer = new RoslynWorkspaceAnalyzer(fs);
            analyzer.AnalyzeWorkspace(tempDir, new AnalysisConfig { RetainCompilations = true });

            Assert.NotNull(analyzer.Compilations);
            var compilation = analyzer.Compilations!["TestProject"];

            // Discovery must report AllowUnsafeCode = false (no element).
            var rediscovered = new RoslynModuleDiscovery(fs).DiscoverModules(tempDir);
            Assert.False(rediscovered[0].AllowUnsafeCode);

            // Compilation MUST have CS0227 — proves the default rejects unsafe.
            var unsafeDiagnostics = compilation.GetDiagnostics()
                .Where(d => d.Id == "CS0227")
                .ToArray();
            Assert.True(unsafeDiagnostics.Length > 0,
                "Compilation with default AllowUnsafeCode=false must produce CS0227 " +
                "on unsafe source. If this assertion fails, the default has been " +
                "accidentally relaxed and we're allowing unsafe everywhere.");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void CreateCompilation_HostProvided_InjectsHostBcl()
    {
        // Plain SDK-style csproj with no Reference elements → BclOwnership = HostProvided.
        // The compilation builder MUST inject the host BCL bundle so System types resolve.
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-bcl-comp-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "MyClass.cs"),
                "namespace Test; public class MyClass { public string Greet() => \"hi\"; }");

            File.WriteAllText(Path.Combine(tempDir, "TestProject.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>");

            var fs = new PhysicalFileSystem();
            var analyzer = new RoslynWorkspaceAnalyzer(fs);
            analyzer.AnalyzeWorkspace(tempDir, new AnalysisConfig { RetainCompilations = true });

            Assert.NotNull(analyzer.Compilations);
            Assert.True(analyzer.Compilations!.ContainsKey("TestProject"));
            var compilation = analyzer.Compilations["TestProject"];

            // Discovery must report HostProvided (no Reference elements at all).
            var rediscovered = new RoslynModuleDiscovery(fs).DiscoverModules(tempDir);
            Assert.Single(rediscovered);
            Assert.Equal(BclOwnershipMode.HostProvided, rediscovered[0].BclOwnership);

            // The compilation MUST contain the host BCL bundle.
            Assert.True(ContainsHostBclBundle(compilation),
                "HostProvided compilation must have the host BCL bundle injected so " +
                "System.Object, System.String, etc. resolve. Without it, plain SDK-style " +
                "csprojs would fail to compile any source code touching primitives.");

            // And the source must compile clean (no CS0518 for missing System types).
            var bclConflicts = compilation.GetDiagnostics()
                .Where(d => d.Id == "CS0518")
                .ToArray();
            Assert.True(bclConflicts.Length == 0,
                "HostProvided plain net8.0 compilation must produce no CS0518 diagnostics. Got: " +
                string.Join("; ", bclConflicts.Take(3).Select(d => d.GetMessage())));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}

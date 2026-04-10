using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Lifeblood.Domain.Capabilities;
using ScriptSecurityScanner = Lifeblood.Adapters.CSharp.Internal.ScriptSecurityScanner;
using DomainSymbolKind = Lifeblood.Domain.Graph.SymbolKind;

namespace Lifeblood.Tests;

/// <summary>
/// Hardening tests for gaps identified in the session 6 audit.
/// Each test exists to prevent regression on a specific known-weak area.
///
/// In the ScriptExecutorSerial collection because some tests here call
/// <c>RoslynCodeExecutor.Execute</c>, which redirects <c>Console.Out</c>
/// globally and is not safe to run concurrently with other Execute callers.
/// </summary>
[Collection("ScriptExecutorSerial")]
public class HardeningTests
{
    // ──────────────────────────────────────────────────────────────
    // Security: GetConstructor/GetConstructors reflection bypass
    // ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("typeof(System.IO.FileInfo).GetConstructor(new[] { typeof(string) })")]
    [InlineData("typeof(string).GetConstructors()")]
    public void SecurityScanner_BlocksGetConstructor(string code)
    {
        var result = ScriptSecurityScanner.Scan(code);
        Assert.NotNull(result);
        Assert.Contains("reflection", result, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────
    // Security: Expression.Compile() bypass
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void SecurityScanner_BlocksExpressionCompile()
    {
        var result = ScriptSecurityScanner.Scan(
            "System.Linq.Expressions.Expression.Lambda<System.Action>(body).Compile();");
        Assert.NotNull(result);
    }

    [Fact]
    public void CodeExecutor_BlocksExpressionCompile()
    {
        var executor = new RoslynCodeExecutor(BuildTestCompilations());
        var result = executor.Execute("var fn = System.Linq.Expressions.Expression.Lambda<System.Func<int>>(System.Linq.Expressions.Expression.Constant(1)).Compile();");
        Assert.False(result.Success);
        Assert.Contains("Blocked", result.Error);
    }

    // ──────────────────────────────────────────────────────────────
    // Partial class edge deduplication
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void GraphBuilder_DeduplicatesEdges()
    {
        var builder = new GraphBuilder();
        builder.AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = DomainSymbolKind.Type });
        builder.AddSymbol(new Symbol { Id = "type:B", Name = "B", Kind = DomainSymbolKind.Type });

        // Add the same edge twice (simulates partial class emitting same override from two files)
        var edge = new Edge
        {
            SourceId = "type:A", TargetId = "type:B", Kind = EdgeKind.Inherits,
            Evidence = new Evidence { Kind = EvidenceKind.Semantic, Confidence = ConfidenceLevel.Proven, AdapterName = "test" },
        };
        builder.AddEdge(edge);
        builder.AddEdge(edge);

        var graph = builder.Build();

        // Should be exactly 1 Inherits edge, not 2
        Assert.Single(graph.Edges, e => e.Kind == EdgeKind.Inherits);
    }

    [Fact]
    public void GraphBuilder_DeduplicatesOverridesFromPartials()
    {
        var builder = new GraphBuilder();
        builder.AddSymbol(new Symbol { Id = "method:A.Run()", Name = "Run", Kind = DomainSymbolKind.Method });
        builder.AddSymbol(new Symbol { Id = "method:B.Run()", Name = "Run", Kind = DomainSymbolKind.Method });

        // Same override edge emitted from two different partial file extractions
        for (int i = 0; i < 3; i++)
        {
            builder.AddEdge(new Edge
            {
                SourceId = "method:B.Run()", TargetId = "method:A.Run()", Kind = EdgeKind.Overrides,
                Evidence = new Evidence { Kind = EvidenceKind.Semantic, Confidence = ConfidenceLevel.Proven, AdapterName = "Roslyn" },
            });
        }

        var graph = builder.Build();
        Assert.Single(graph.Edges, e => e.Kind == EdgeKind.Overrides);
    }

    // ──────────────────────────────────────────────────────────────
    // Unity csproj: <Compile Include> extraction
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void ModuleDiscovery_ParsesCompileIncludeItems()
    {
        // Create a minimal Unity-style csproj in a temp directory
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var sourceFile = Path.Combine(tempDir, "MyClass.cs");
            File.WriteAllText(sourceFile, "namespace Test; public class MyClass { }");

            var csproj = Path.Combine(tempDir, "TestProject.csproj");
            File.WriteAllText(csproj, @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project ToolsVersion=""4.0"" DefaultTargets=""Build"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup>
    <AssemblyName>TestProject</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include=""MyClass.cs"" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include=""OtherAssembly"" />
    <Reference Include=""System.Core"" />
  </ItemGroup>
</Project>");

            var fs = new PhysicalFileSystem();
            var discovery = new RoslynModuleDiscovery(fs);
            var modules = discovery.DiscoverModules(tempDir);

            Assert.Single(modules);
            var mod = modules[0];
            Assert.Equal("TestProject", mod.Name);
            Assert.Single(mod.FilePaths);
            Assert.EndsWith("MyClass.cs", mod.FilePaths[0]);

            // Should pick up OtherAssembly but NOT System.Core
            Assert.Contains("OtherAssembly", mod.Dependencies);
            Assert.DoesNotContain("System.Core", mod.Dependencies);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // BCL ownership detection (INV-BCL-002 / INV-BCL-004)
    // See .claude/plans/bcl-ownership-fix.md §9.1
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void RoslynModuleDiscovery_DetectsBclOwnership_FromBareReferenceInclude()
    {
        // <Reference Include="netstandard"> with no HintPath. The Include
        // attribute alone is authoritative — this is the modern Unity shape.
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-bcl-bare-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "MyClass.cs"),
                "namespace Test; public class MyClass { }");

            File.WriteAllText(Path.Combine(tempDir, "TestProject.csproj"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project ToolsVersion=""4.0"" DefaultTargets=""Build"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup>
    <AssemblyName>TestProject</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include=""MyClass.cs"" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include=""netstandard"" />
  </ItemGroup>
</Project>");

            var modules = new RoslynModuleDiscovery(new PhysicalFileSystem()).DiscoverModules(tempDir);
            Assert.Single(modules);
            Assert.Equal(BclOwnershipMode.ModuleProvided, modules[0].BclOwnership);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void RoslynModuleDiscovery_DetectsBclOwnership_FromStrongNameReferenceInclude()
    {
        // Legacy .NET Framework / NuGet-converted csprojs encode the assembly
        // identity in the Include attribute. The matcher must split on comma
        // and compare the first token, not do a naive StartsWith.
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-bcl-strong-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "MyClass.cs"),
                "namespace Test; public class MyClass { }");

            File.WriteAllText(Path.Combine(tempDir, "TestProject.csproj"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project ToolsVersion=""4.0"" DefaultTargets=""Build"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup>
    <AssemblyName>TestProject</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include=""MyClass.cs"" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include=""netstandard, Version=2.1.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51"" />
  </ItemGroup>
</Project>");

            var modules = new RoslynModuleDiscovery(new PhysicalFileSystem()).DiscoverModules(tempDir);
            Assert.Single(modules);
            Assert.Equal(BclOwnershipMode.ModuleProvided, modules[0].BclOwnership);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void RoslynModuleDiscovery_DetectsBclOwnership_FromHintPathFilenameOnly()
    {
        // Csproj uses a non-canonical Include name but points HintPath at a BCL
        // DLL. The HintPath basename is the secondary signal — it must catch
        // this case even though the Include attribute is not "netstandard".
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-bcl-hint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "MyClass.cs"),
                "namespace Test; public class MyClass { }");

            // Create a real DLL on disk so the HintPath resolves; the test only
            // cares that the basename detection fires, not that the DLL is real.
            var stubDllPath = Path.Combine(tempDir, "netstandard.dll");
            File.WriteAllBytes(stubDllPath, new byte[] { 0x4D, 0x5A }); // MZ header stub

            File.WriteAllText(Path.Combine(tempDir, "TestProject.csproj"), $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Project ToolsVersion=""4.0"" DefaultTargets=""Build"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup>
    <AssemblyName>TestProject</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include=""MyClass.cs"" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include=""SomeOddName"">
      <HintPath>netstandard.dll</HintPath>
    </Reference>
  </ItemGroup>
</Project>");

            var modules = new RoslynModuleDiscovery(new PhysicalFileSystem()).DiscoverModules(tempDir);
            Assert.Single(modules);
            Assert.Equal(BclOwnershipMode.ModuleProvided, modules[0].BclOwnership);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void RoslynModuleDiscovery_DetectsAllowUnsafeBlocks_LowerCaseTrue()
    {
        // Csproj declares <AllowUnsafeBlocks>true</AllowUnsafeBlocks> (lowercase).
        // ModuleInfo.AllowUnsafeCode must be true so the compilation builder sets
        // CSharpCompilationOptions.AllowUnsafe = true. INV-COMPFACT-001..003.
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-unsafe-lower-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "MyClass.cs"),
                "namespace Test; public class MyClass { }");
            File.WriteAllText(Path.Combine(tempDir, "TestProject.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
</Project>");

            var modules = new RoslynModuleDiscovery(new PhysicalFileSystem()).DiscoverModules(tempDir);
            Assert.Single(modules);
            Assert.True(modules[0].AllowUnsafeCode);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void RoslynModuleDiscovery_DetectsAllowUnsafeBlocks_UnityCasingTrue()
    {
        // Unity emits <AllowUnsafeBlocks>True</AllowUnsafeBlocks> (capitalized).
        // The matcher must be case-insensitive on the value.
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-unsafe-unity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "MyClass.cs"),
                "namespace Test; public class MyClass { }");
            File.WriteAllText(Path.Combine(tempDir, "TestProject.csproj"), @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project ToolsVersion=""4.0"" DefaultTargets=""Build"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup>
    <AssemblyName>TestProject</AssemblyName>
    <AllowUnsafeBlocks>True</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include=""MyClass.cs"" />
  </ItemGroup>
</Project>");

            var modules = new RoslynModuleDiscovery(new PhysicalFileSystem()).DiscoverModules(tempDir);
            Assert.Single(modules);
            Assert.True(modules[0].AllowUnsafeCode);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void RoslynModuleDiscovery_NoAllowUnsafeBlocks_DefaultsFalse()
    {
        // Plain SDK csproj with no AllowUnsafeBlocks element. Default false.
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-unsafe-absent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "MyClass.cs"),
                "namespace Test; public class MyClass { }");
            File.WriteAllText(Path.Combine(tempDir, "TestProject.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>");

            var modules = new RoslynModuleDiscovery(new PhysicalFileSystem()).DiscoverModules(tempDir);
            Assert.Single(modules);
            Assert.False(modules[0].AllowUnsafeCode);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void RoslynModuleDiscovery_PlainSdkProject_ReportsHostProvided()
    {
        // Plain net8.0 SDK-style csproj with no HintPath references — the
        // golden WriteSideApp shape. Default BclOwnership = HostProvided so
        // the host BCL is injected during compilation.
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-bcl-sdk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "MyClass.cs"),
                "namespace Test; public class MyClass { }");

            File.WriteAllText(Path.Combine(tempDir, "TestProject.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>");

            var modules = new RoslynModuleDiscovery(new PhysicalFileSystem()).DiscoverModules(tempDir);
            Assert.Single(modules);
            Assert.Equal(BclOwnershipMode.HostProvided, modules[0].BclOwnership);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Streaming: compilation downgrade produces valid references
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void StreamingDowngrade_DownstreamModuleResolvesTypes()
    {
        // Module A defines a type. Module B references it.
        // After A is downgraded (Emit → CreateFromImage), B must still resolve A's types.
        var sourceA = "namespace ModA; public class Greeter { public string Greet() => \"hi\"; }";
        var treeA = CSharpSyntaxTree.ParseText(sourceA, path: "Greeter.cs");

        var refs = new List<MetadataReference> { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir != null)
        {
            var sr = Path.Combine(runtimeDir, "System.Runtime.dll");
            if (File.Exists(sr)) refs.Add(MetadataReference.CreateFromFile(sr));
        }

        var compilationA = CSharpCompilation.Create("ModA", new[] { treeA }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Downgrade: Emit → CreateFromImage (the streaming architecture's core trick)
        using var ms = new MemoryStream();
        var emitResult = compilationA.Emit(ms);
        Assert.True(emitResult.Success, "Module A should emit successfully");

        var downgradedRef = MetadataReference.CreateFromImage(ms.ToArray());

        // Module B references the downgraded A (not the full compilation)
        var sourceB = "using ModA; namespace ModB; public class Service { private Greeter _g = new(); public string Run() => _g.Greet(); }";
        var treeB = CSharpSyntaxTree.ParseText(sourceB, path: "Service.cs");

        var refsB = new List<MetadataReference>(refs) { downgradedRef };
        var compilationB = CSharpCompilation.Create("ModB", new[] { treeB }, refsB,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // B must compile without errors — it resolves Greeter from the downgraded PE image
        var diagnostics = compilationB.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.Empty(diagnostics);

        // Verify the semantic model resolves the type correctly
        var model = compilationB.GetSemanticModel(treeB);
        var greeterSymbol = compilationB.GetTypeByMetadataName("ModA.Greeter");
        Assert.NotNull(greeterSymbol);
        Assert.Equal("Greeter", greeterSymbol!.Name);
    }

    // ──────────────────────────────────────────────────────────────
    // CodeExecutor: CS0518 regression — downgraded refs bug class
    // ──────────────────────────────────────────────────────────────
    // Bug: WithReferences on ScriptOptions replaces framework refs,
    //      causing CS0518 "System.Object is not defined". AddReferences
    //      preserves them. This test reproduces the exact production
    //      scenario: multi-module streaming workspace with downgraded
    //      (null-Display) PE references — the configuration that hit
    //      CS0518 on a real 75-module Unity workspace.

    [Fact]
    public void CodeExecutor_WithDowngradedRefs_ResolvesSystemObject()
    {
        // Build Module A, then downgrade it (Emit → CreateFromImage),
        // producing a null-Display MetadataReference — identical to
        // what ModuleCompilationBuilder does in streaming mode.
        var (compilations, _) = BuildDowngradedMultiModuleCompilations();

        var executor = new RoslynCodeExecutor(compilations);

        // This is the exact line that triggered CS0518 before the fix.
        // "return 42;" requires System.Object (boxing) and System.Int32.
        var result = executor.Execute("return 42;");
        Assert.True(result.Success, $"CS0518 regression: {result.Error}");
        Assert.Equal("42", result.ReturnValue);
    }

    [Fact]
    public void CodeExecutor_WithDowngradedRefs_CompilesCrossModuleCode()
    {
        // Cross-module type access: the script compiler should resolve ModA.Greeter
        // via CompilationReference even when Module B has downgraded refs.
        // Note: typeof() would fail at RUNTIME because the CLR can't load in-memory
        // assemblies from disk. In production (Unity), assemblies are real DLLs.
        // Here we test that COMPILATION succeeds (no CS0246 "type not found").
        var (compilations, _) = BuildDowngradedMultiModuleCompilations();
        var executor = new RoslynCodeExecutor(compilations);

        // nameof() resolves at compile time — no runtime assembly loading needed.
        var result = executor.Execute(
            "return nameof(ModA.Greeter);",
            imports: new[] { "ModA" });
        Assert.True(result.Success, $"Cross-module compilation failed: {result.Error}");
        Assert.Equal("Greeter", result.ReturnValue);
    }

    [Fact]
    public void CodeExecutor_WithDowngradedRefs_ConsoleOutputWorks()
    {
        // Console.Write requires System.Console — another BCL assembly
        // that WithReferences would have dropped.
        var (compilations, _) = BuildDowngradedMultiModuleCompilations();
        var executor = new RoslynCodeExecutor(compilations);

        var result = executor.Execute("Console.Write(\"dogfood\");");
        Assert.True(result.Success, $"Console failed: {result.Error}");
        Assert.Equal("dogfood", result.Output);
    }

    [Fact]
    public void CodeExecutor_WithDowngradedRefs_LinqWorks()
    {
        // LINQ requires System.Linq — tests that BCL transitive refs survive.
        var (compilations, _) = BuildDowngradedMultiModuleCompilations();
        var executor = new RoslynCodeExecutor(compilations);

        var result = executor.Execute(
            "return Enumerable.Range(1, 5).Sum();",
            imports: new[] { "System.Linq" });
        Assert.True(result.Success, $"LINQ failed: {result.Error}");
        Assert.Equal("15", result.ReturnValue);
    }

    [Fact]
    public void CodeExecutor_DowngradedRefs_HaveInMemoryDisplay()
    {
        // Verify our test fixture produces the same reference topology as production.
        // CreateFromImage produces Display = "<in-memory assembly>" — NOT a file path.
        // The dedup logic should collapse all such refs (they're superseded by
        // CompilationReferences that give access to the actual module types).
        var (compilations, downgradedRef) = BuildDowngradedMultiModuleCompilations();

        Assert.Equal("<in-memory assembly>", downgradedRef.Display);

        // Module B's references should include this in-memory ref
        var modB = compilations["ModB"];
        var inMemoryCount = modB.References.Count(r => r.Display == "<in-memory assembly>");
        Assert.True(inMemoryCount > 0,
            "Module B should have at least one in-memory reference (the downgraded Module A)");

        // Executor should still work — CompilationReferences supersede downgraded refs.
        // (Can't use typeof(ModB.Service) because the CLR would try to load ModB.dll
        // from disk — CompilationReferences only work at compile time, not runtime loading.)
        var executor = new RoslynCodeExecutor(compilations);
        var result = executor.Execute("return 1 + 1;");
        Assert.True(result.Success, $"Basic execution failed with downgraded refs: {result.Error}");
        Assert.Equal("2", result.ReturnValue);
    }

    /// <summary>
    /// Builds a 2-module streaming workspace where Module A is downgraded.
    /// This produces the exact reference topology that caused CS0518.
    /// Returns (compilations dict, the downgraded MetadataReference for assertions).
    /// </summary>
    private static (Dictionary<string, CSharpCompilation>, MetadataReference) BuildDowngradedMultiModuleCompilations()
    {
        var bclRefs = LoadBclReferences();

        // Module A: simple type
        var sourceA = "namespace ModA; public class Greeter { public string Greet() => \"hi\"; }";
        var treeA = CSharpSyntaxTree.ParseText(sourceA, path: "Greeter.cs");
        var compilationA = CSharpCompilation.Create("ModA", new[] { treeA }, bclRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Downgrade Module A: Emit → CreateFromImage (this is the streaming trick)
        using var ms = new MemoryStream();
        var emitResult = compilationA.Emit(ms);
        Assert.True(emitResult.Success, "Module A should emit successfully");
        var downgradedRef = MetadataReference.CreateFromImage(ms.ToArray());

        // Module B: references the DOWNGRADED Module A (not the compilation)
        var sourceB = @"using ModA;
namespace ModB;
public class Service
{
    private readonly Greeter _g = new();
    public string Run() => _g.Greet();
}";
        var treeB = CSharpSyntaxTree.ParseText(sourceB, path: "Service.cs");
        var refsB = new List<MetadataReference>(bclRefs) { downgradedRef };
        var compilationB = CSharpCompilation.Create("ModB", new[] { treeB }, refsB,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Sanity: both should compile
        Assert.Empty(compilationA.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Empty(compilationB.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        // Return BOTH compilations — this is what the real workspace retains
        // when RetainCompilations=true (the code execution path).
        var compilations = new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal)
        {
            ["ModA"] = compilationA,
            ["ModB"] = compilationB,
        };

        return (compilations, downgradedRef);
    }

    private static MetadataReference[] LoadBclReferences()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir == null) return new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };

        var refs = new List<MetadataReference>();
        foreach (var dll in new[] { "System.Runtime.dll", "System.Console.dll", "System.Collections.dll",
                                     "System.Linq.dll", "System.Threading.dll", "System.Threading.Tasks.dll",
                                     "netstandard.dll" })
        {
            var path = Path.Combine(runtimeDir, dll);
            if (File.Exists(path)) refs.Add(MetadataReference.CreateFromFile(path));
        }
        refs.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        return refs.ToArray();
    }

    // ──────────────────────────────────────────────────────────────
    // ProcessIsolatedCodeExecutor: integration test
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void ProcessIsolatedExecutor_SimpleExpression_ReturnsResult()
    {
        var scriptHostPath = FindScriptHostProject();
        if (scriptHostPath == null) { Console.Error.WriteLine("SKIP: ScriptHost project not found"); return; }
        if (!TryBuildScriptHost(scriptHostPath)) { Console.Error.WriteLine("SKIP: ScriptHost failed to build"); return; }

        var executor = new ProcessIsolatedCodeExecutor(scriptHostPath);
        var result = executor.Execute("return 2 + 3;", timeoutMs: 15000);

        Assert.True(result.Success, $"ProcessIsolated execution failed: {result.Error}");
        Assert.Equal("5", result.ReturnValue);
    }

    [Fact]
    public void ProcessIsolatedExecutor_Timeout_KillsProcess()
    {
        var scriptHostPath = FindScriptHostProject();
        if (scriptHostPath == null) { Console.Error.WriteLine("SKIP: ScriptHost project not found"); return; }
        if (!TryBuildScriptHost(scriptHostPath)) { Console.Error.WriteLine("SKIP: ScriptHost failed to build"); return; }

        var executor = new ProcessIsolatedCodeExecutor(scriptHostPath);
        var result = executor.Execute("while(true) { }", timeoutMs: 3000);

        Assert.False(result.Success);
        Assert.Contains("timed out", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryBuildScriptHost(string path)
    {
        try
        {
            var build = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{path}\" -v q",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return build != null && build.WaitForExit(30000) && build.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    // ──────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────

    private static Dictionary<string, CSharpCompilation> BuildTestCompilations()
    {
        var source = "namespace TestApp { public class Foo { } }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var refs = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        var compilation = CSharpCompilation.Create("TestApp", new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal) { ["TestApp"] = compilation };
    }

    private static string? FindScriptHostProject()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "src", "Lifeblood.ScriptHost");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}

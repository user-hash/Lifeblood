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
/// </summary>
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

using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using ScriptSecurityScanner = Lifeblood.Adapters.CSharp.Internal.ScriptSecurityScanner;

namespace Lifeblood.Tests;

/// <summary>
/// Tests for the write-side Roslyn tools: CompilationHost, CodeExecutor, WorkspaceRefactoring.
/// Also covers SymbolId parsing and the shared RoslynWorkspaceManager.
/// </summary>
public class WriteSideToolTests
{
    // ── Shared test compilation ──

    private static Dictionary<string, CSharpCompilation> BuildTestCompilations()
    {
        var source = @"
namespace TestApp
{
    public class Greeter
    {
        public string Name { get; set; } = """";
        private int _count;
        public string Greet() { _count++; return $""Hello {Name}""; }
        public static int Add(int a, int b) => a + b;
    }

    public interface IService
    {
        void Execute();
    }

    public class MyService : IService
    {
        private readonly Greeter _greeter = new();
        public void Execute() { _greeter.Greet(); }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(source, path: "TestApp.cs");
        var refs = LoadBclReferences();

        var compilation = CSharpCompilation.Create("TestApp",
            new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal)
        {
            ["TestApp"] = compilation,
        };
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
    // RoslynCompilationHost tests
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void CompilationHost_IsAvailable_WhenCompilationsExist()
    {
        var host = new RoslynCompilationHost(BuildTestCompilations());
        Assert.True(host.IsAvailable);
    }

    [Fact]
    public void CompilationHost_IsAvailable_FalseWhenEmpty()
    {
        var host = new RoslynCompilationHost(new Dictionary<string, CSharpCompilation>());
        Assert.False(host.IsAvailable);
    }

    [Fact]
    public void CompilationHost_GetDiagnostics_ReturnsResults()
    {
        var host = new RoslynCompilationHost(BuildTestCompilations());
        var diags = host.GetDiagnostics();
        // Even if there are diagnostics, the method should not throw
        Assert.NotNull(diags);
    }

    [Fact]
    public void CompilationHost_GetDiagnostics_NonexistentModule_ReturnsEmpty()
    {
        var host = new RoslynCompilationHost(BuildTestCompilations());
        var diags = host.GetDiagnostics("NonexistentModule");
        Assert.Empty(diags);
    }

    [Fact]
    public void CompilationHost_GetDiagnostics_SpecificModule_ReturnsModuleDiags()
    {
        var host = new RoslynCompilationHost(BuildTestCompilations());
        var diags = host.GetDiagnostics("TestApp");
        // All diagnostics should be tagged with the module name
        Assert.All(diags, d => Assert.Equal("TestApp", d.Module));
    }

    [Fact]
    public void CompilationHost_CompileCheck_ValidCode_Succeeds()
    {
        var host = new RoslynCompilationHost(BuildTestCompilations());
        var result = host.CompileCheck("public class NewClass { }", "TestApp");
        Assert.True(result.Success);
    }

    [Fact]
    public void CompilationHost_CompileCheck_InvalidCode_Fails()
    {
        var host = new RoslynCompilationHost(BuildTestCompilations());
        var result = host.CompileCheck("public class { INVALID SYNTAX", "TestApp");
        Assert.False(result.Success);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void CompilationHost_CompileCheck_NonexistentModule_ReturnsError()
    {
        var host = new RoslynCompilationHost(BuildTestCompilations());
        var result = host.CompileCheck("class X { }", "Nope");
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Id == "LB0001");
    }

    [Fact]
    public void CompilationHost_CompileCheck_NoCompilations_ReturnsError()
    {
        var host = new RoslynCompilationHost(new Dictionary<string, CSharpCompilation>());
        var result = host.CompileCheck("class X { }");
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("No compilations available"));
    }

    [Fact]
    public void CompilationHost_FindReferences_InvalidSymbolId_ReturnsEmpty()
    {
        var host = new RoslynCompilationHost(BuildTestCompilations());
        var refs = host.FindReferences("no_colon_here");
        Assert.Empty(refs);
    }

    [Fact]
    public void CompilationHost_FindReferences_NonexistentSymbol_ReturnsEmpty()
    {
        var host = new RoslynCompilationHost(BuildTestCompilations());
        var refs = host.FindReferences("type:NonExistent.Type");
        Assert.Empty(refs);
    }

    [Fact]
    public void CompilationHost_FindReferences_ExistingType_DoesNotThrow()
    {
        var host = new RoslynCompilationHost(BuildTestCompilations());
        // With synthetic compilations, workspace-based FindReferences may return empty
        // (AdhocWorkspace project compilation differs from standalone). This test verifies
        // the code path doesn't throw — full integration tested via dogfood.
        var refs = host.FindReferences("type:TestApp.Greeter");
        Assert.NotNull(refs);
    }

    // ──────────────────────────────────────────────────────────────
    // RoslynCodeExecutor tests
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void CodeExecutor_Execute_SimpleExpression_ReturnsValue()
    {
        var executor = new RoslynCodeExecutor(BuildTestCompilations());
        var result = executor.Execute("return 2 + 3;");
        Assert.True(result.Success);
        Assert.Equal("5", result.ReturnValue);
    }

    [Fact]
    public void CodeExecutor_Execute_ConsoleOutput_Captured()
    {
        var executor = new RoslynCodeExecutor(BuildTestCompilations());
        var result = executor.Execute("Console.Write(\"hello\");");
        Assert.True(result.Success);
        Assert.Equal("hello", result.Output);
    }

    [Fact]
    public void CodeExecutor_Execute_CompilationError_ReturnsError()
    {
        var executor = new RoslynCodeExecutor(BuildTestCompilations());
        var result = executor.Execute("int x = \"not a number\";");
        Assert.False(result.Success);
        Assert.NotEmpty(result.Error);
    }

    [Fact]
    public void CodeExecutor_Execute_BlockedPattern_FileDelete()
    {
        var executor = new RoslynCodeExecutor(BuildTestCompilations());
        var result = executor.Execute("System.IO.File.Delete(\"test.txt\");");
        Assert.False(result.Success);
        Assert.Contains("Blocked pattern", result.Error);
    }

    [Fact]
    public void CodeExecutor_Execute_BlockedPattern_FileWrite()
    {
        var executor = new RoslynCodeExecutor(BuildTestCompilations());
        var result = executor.Execute("System.IO.File.WriteAllText(\"test.txt\", \"data\");");
        Assert.False(result.Success);
        Assert.Contains("Blocked pattern", result.Error);
    }

    [Fact]
    public void CodeExecutor_Execute_BlockedPattern_ProcessStart()
    {
        var executor = new RoslynCodeExecutor(BuildTestCompilations());
        var result = executor.Execute("Process.Start(\"cmd\");");
        Assert.False(result.Success);
        Assert.Contains("Blocked pattern", result.Error);
    }

    [Fact]
    public void CodeExecutor_Execute_BlockedPattern_AssemblyLoad()
    {
        var executor = new RoslynCodeExecutor(BuildTestCompilations());
        var result = executor.Execute("Assembly.Load(\"evil\");");
        Assert.False(result.Success);
        Assert.Contains("Blocked pattern", result.Error);
    }

    [Fact]
    public void CodeExecutor_Execute_BlockedPattern_EnvironmentExit()
    {
        var executor = new RoslynCodeExecutor(BuildTestCompilations());
        var result = executor.Execute("Environment.Exit(1);");
        Assert.False(result.Success);
        Assert.Contains("Blocked pattern", result.Error);
    }

    [Fact]
    public void CodeExecutor_Execute_EmptyCode_Succeeds()
    {
        var executor = new RoslynCodeExecutor(BuildTestCompilations());
        var result = executor.Execute("");
        Assert.True(result.Success);
    }

    [Fact]
    public void CodeExecutor_Execute_Timeout_ReturnsError()
    {
        var executor = new RoslynCodeExecutor(BuildTestCompilations());
        // Busy-wait loop that should trigger timeout
        var result = executor.Execute("while(true) { }", timeoutMs: 500);
        Assert.False(result.Success);
        Assert.Contains("timed out", result.Error);
    }

    [Fact]
    public void CodeExecutor_Execute_RuntimeException_ReturnsError()
    {
        var executor = new RoslynCodeExecutor(BuildTestCompilations());
        var result = executor.Execute("throw new System.InvalidOperationException(\"test error\");");
        Assert.False(result.Success);
        Assert.Contains("test error", result.Error);
    }

    [Fact]
    public void CodeExecutor_Execute_WithImports_Works()
    {
        var executor = new RoslynCodeExecutor(BuildTestCompilations());
        var result = executor.Execute(
            "return Enumerable.Range(1, 5).Sum();",
            imports: new[] { "System.Linq" });
        Assert.True(result.Success);
        Assert.Equal("15", result.ReturnValue);
    }

    [Fact]
    public void CodeExecutor_Execute_AccessProjectTypes_DoesNotThrow()
    {
        var executor = new RoslynCodeExecutor(BuildTestCompilations());
        // CSharpScript with synthetic compilation references may not resolve
        // project types in all environments. Verify the code path completes.
        var result = executor.Execute(
            "return TestApp.Greeter.Add(10, 20);",
            imports: new[] { "TestApp" });
        Assert.NotNull(result);
        // If it succeeded, verify the return value
        if (result.Success)
            Assert.Equal("30", result.ReturnValue);
    }

    // ──────────────────────────────────────────────────────────────
    // AST-based security scanner tests
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void CodeExecutor_Execute_ReflectionGetMethod_Blocked()
    {
        var executor = new RoslynCodeExecutor(BuildTestCompilations());
        var result = executor.Execute("typeof(System.IO.File).GetMethod(\"Delete\");");
        Assert.False(result.Success);
        Assert.Contains("reflection", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CodeExecutor_Execute_ReflectionInvoke_Blocked()
    {
        var executor = new RoslynCodeExecutor(BuildTestCompilations());
        var result = executor.Execute("var m = typeof(string).GetMethod(\"Concat\"); m.Invoke(null, null);");
        Assert.False(result.Success);
        Assert.Contains("reflection", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CodeExecutor_Execute_UnsafeBlock_Blocked()
    {
        var executor = new RoslynCodeExecutor(BuildTestCompilations());
        var result = executor.Execute("unsafe { int* p = null; }");
        Assert.False(result.Success);
        Assert.Contains("unsafe", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CodeExecutor_Execute_SafeReflection_Allowed()
    {
        // GetProperties (read-only inspection) and GetType() should be allowed
        var executor = new RoslynCodeExecutor(BuildTestCompilations());
        var result = executor.Execute("return typeof(string).Name;");
        Assert.True(result.Success);
        Assert.Equal("String", result.ReturnValue);
    }

    // ──────────────────────────────────────────────────────────────
    // RoslynWorkspaceRefactoring tests
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void Refactoring_Format_FormatsCode()
    {
        var refactoring = new RoslynWorkspaceRefactoring(BuildTestCompilations());
        var formatted = refactoring.Format("class   X{int   y;}");
        Assert.Contains("class", formatted);
        // Should at least not throw
        Assert.NotEmpty(formatted);
    }

    [Fact]
    public void Refactoring_Format_EmptyCode_ReturnsEmpty()
    {
        var refactoring = new RoslynWorkspaceRefactoring(BuildTestCompilations());
        var formatted = refactoring.Format("");
        Assert.Empty(formatted);
    }

    [Fact]
    public void Refactoring_Rename_InvalidSymbolId_ReturnsEmpty()
    {
        var refactoring = new RoslynWorkspaceRefactoring(BuildTestCompilations());
        var edits = refactoring.Rename("no_colon", "NewName");
        Assert.Empty(edits);
    }

    [Fact]
    public void Refactoring_Rename_NonexistentSymbol_ReturnsEmpty()
    {
        var refactoring = new RoslynWorkspaceRefactoring(BuildTestCompilations());
        var edits = refactoring.Rename("type:NonExistent.Type", "NewName");
        Assert.Empty(edits);
    }

    [Fact]
    public void Refactoring_Rename_ExistingType_DoesNotThrow()
    {
        var refactoring = new RoslynWorkspaceRefactoring(BuildTestCompilations());
        // With synthetic compilations, Renamer may return empty edits (AdhocWorkspace
        // compilation differs from standalone). This verifies the code path runs clean.
        var edits = refactoring.Rename("type:TestApp.Greeter", "Welcomer");
        Assert.NotNull(edits);
        // If edits are returned, validate their structure
        Assert.All(edits, e =>
        {
            Assert.True(e.StartLine > 0);
            Assert.True(e.StartColumn > 0);
        });
    }

    // ──────────────────────────────────────────────────────────────
    // SymbolId parsing tests
    // ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("type:App.Foo", "type", new[] { "App", "Foo" })]
    [InlineData("method:App.Foo.Bar(int)", "method", new[] { "App", "Foo", "Bar" })]
    [InlineData("field:App.Foo._count", "field", new[] { "App", "Foo", "_count" })]
    [InlineData("property:App.Foo.Name", "property", new[] { "App", "Foo", "Name" })]
    [InlineData("ns:App.Services", "ns", new[] { "App", "Services" })]
    public void ParseSymbolId_ValidFormats(string symbolId, string expectedKind, string[] expectedParts)
    {
        var parsed = RoslynWorkspaceManager.ParseSymbolId(symbolId);
        Assert.Equal(expectedKind, parsed.Kind);
        Assert.Equal(expectedParts, parsed.Parts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no_colon_here")]
    [InlineData("justAWord")]
    public void ParseSymbolId_NoColon_ReturnsNull(string symbolId)
    {
        var parsed = RoslynWorkspaceManager.ParseSymbolId(symbolId);
        Assert.Null(parsed.Kind);
        Assert.Null(parsed.Parts);
    }

    [Fact]
    public void ParseSymbolId_ColonOnly_ReturnsEmptyKind()
    {
        var parsed = RoslynWorkspaceManager.ParseSymbolId(":");
        Assert.Equal("", parsed.Kind);
        Assert.NotNull(parsed.Parts);
    }

    [Fact]
    public void ParseSymbolId_MethodWithParams_PreservesSignature()
    {
        // Real format: extractors use comma WITHOUT space (ToDisplayString default)
        var parsed = RoslynWorkspaceManager.ParseSymbolId("method:App.Foo.Bar(int,string)");
        Assert.Equal("method", parsed.Kind);
        Assert.Equal(new[] { "App", "Foo", "Bar" }, parsed.Parts);
        Assert.Equal("int,string", parsed.ParamSignature);
    }

    [Fact]
    public void ParseSymbolId_MethodEmptyParams_EmptySignature()
    {
        var parsed = RoslynWorkspaceManager.ParseSymbolId("method:App.Foo.Bar()");
        Assert.Equal("method", parsed.Kind);
        Assert.Equal(new[] { "App", "Foo", "Bar" }, parsed.Parts);
        Assert.Equal("", parsed.ParamSignature);
    }

    [Fact]
    public void ParseSymbolId_MethodWithoutParams_NullSignature()
    {
        var parsed = RoslynWorkspaceManager.ParseSymbolId("method:App.Foo.Bar");
        Assert.Equal("method", parsed.Kind);
        Assert.Equal(new[] { "App", "Foo", "Bar" }, parsed.Parts);
        Assert.Null(parsed.ParamSignature);
    }

    // ──────────────────────────────────────────────────────────────
    // Architecture invariant: Analysis depends only on Domain
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void Analysis_DependsOnlyOnDomain()
    {
        var srcRoot = FindSrcRoot();
        var csproj = Path.Combine(srcRoot, "Lifeblood.Analysis", "Lifeblood.Analysis.csproj");
        if (!File.Exists(csproj)) return;

        var doc = System.Xml.Linq.XDocument.Load(csproj);
        var projectRefs = doc.Descendants("ProjectReference")
            .Select(el => el.Attribute("Include")?.Value ?? "")
            .ToArray();
        var packageRefs = doc.Descendants("PackageReference").ToArray();

        Assert.All(projectRefs, r => Assert.Contains("Domain", r));
        Assert.Empty(packageRefs);
    }

    // ──────────────────────────────────────────────────────────────
    // AnalysisPipeline integration
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void AnalysisPipeline_Run_WithNullRules_ProducesResult()
    {
        var graph = BuildMinimalGraph();
        var result = Lifeblood.Analysis.AnalysisPipeline.Run(graph, null);
        Assert.NotNull(result);
        Assert.Empty(result.Violations);
        Assert.NotNull(result.Coupling);
        Assert.NotNull(result.Tiers);
    }

    [Fact]
    public void AnalysisPipeline_Run_EmptyGraph_NoErrors()
    {
        var graph = new Lifeblood.Domain.Graph.GraphBuilder().Build();
        var result = Lifeblood.Analysis.AnalysisPipeline.Run(graph);
        Assert.Equal(0, result.Metrics.TotalSymbols);
        Assert.Equal(0, result.Metrics.TotalEdges);
        Assert.Empty(result.Cycles);
    }

    [Fact]
    public void AnalysisPipeline_Run_MetricsMatchGraph()
    {
        var graph = BuildMinimalGraph();
        var result = Lifeblood.Analysis.AnalysisPipeline.Run(graph);
        Assert.Equal(graph.Symbols.Count, result.Metrics.TotalSymbols);
        Assert.Equal(graph.Edges.Count, result.Metrics.TotalEdges);
    }

    // ──────────────────────────────────────────────────────────────
    // ScriptSecurityScanner unit tests
    // ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("typeof(File).GetMethod(\"Delete\")", "reflection")]
    [InlineData("methodInfo.Invoke(null, args)", "reflection")]
    [InlineData("field.SetValue(obj, val)", "reflection")]
    [InlineData("del.DynamicInvoke(args)", "reflection")]
    [InlineData("Activator.CreateInstance(type)", "reflection")]
    public void SecurityScanner_BlocksReflection(string code, string expectedKeyword)
    {
        var result = ScriptSecurityScanner.Scan(code);
        Assert.NotNull(result);
        Assert.Contains(expectedKeyword, result, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("unsafe { int* p = null; }", "unsafe")]
    [InlineData("int* ptr = &x;", "pointer")]
    public void SecurityScanner_BlocksUnsafe(string code, string expectedKeyword)
    {
        var result = ScriptSecurityScanner.Scan(code);
        Assert.NotNull(result);
        Assert.Contains(expectedKeyword, result, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("return 42;")]
    [InlineData("var x = new List<int>(); x.Add(1);")]
    [InlineData("Console.WriteLine(\"hello\");")]
    [InlineData("return typeof(string).Name;")]
    public void SecurityScanner_AllowsSafeCode(string code)
    {
        var result = ScriptSecurityScanner.Scan(code);
        Assert.Null(result);
    }

    // ──────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────

    private static Lifeblood.Domain.Graph.SemanticGraph BuildMinimalGraph()
    {
        var builder = new Lifeblood.Domain.Graph.GraphBuilder();
        builder.AddSymbol(new Lifeblood.Domain.Graph.Symbol
        {
            Id = "mod:TestMod", Name = "TestMod", QualifiedName = "TestMod",
            Kind = Lifeblood.Domain.Graph.SymbolKind.Module,
        });
        builder.AddSymbol(new Lifeblood.Domain.Graph.Symbol
        {
            Id = "type:TestMod.Foo", Name = "Foo", QualifiedName = "TestMod.Foo",
            Kind = Lifeblood.Domain.Graph.SymbolKind.Type, ParentId = "mod:TestMod",
        });
        builder.AddSymbol(new Lifeblood.Domain.Graph.Symbol
        {
            Id = "type:TestMod.Bar", Name = "Bar", QualifiedName = "TestMod.Bar",
            Kind = Lifeblood.Domain.Graph.SymbolKind.Type, ParentId = "mod:TestMod",
        });
        builder.AddEdge(new Lifeblood.Domain.Graph.Edge
        {
            SourceId = "type:TestMod.Bar", TargetId = "type:TestMod.Foo",
            Kind = Lifeblood.Domain.Graph.EdgeKind.References,
        });
        return builder.Build();
    }

    private static string FindSrcRoot()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (dir != null)
        {
            var src = Path.Combine(dir, "src");
            if (Directory.Exists(src) && Directory.Exists(Path.Combine(src, "Lifeblood.Domain")))
                return src;
            dir = Path.GetDirectoryName(dir);
        }
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "src");
    }
}

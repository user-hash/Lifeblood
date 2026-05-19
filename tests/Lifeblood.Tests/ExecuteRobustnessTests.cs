using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using DomainSymbolKind = Lifeblood.Domain.Graph.SymbolKind;

namespace Lifeblood.Tests;

/// <summary>
/// Coverage for the v0.6.7 P4 execute-robustness work:
///
/// 1. UnityAssemblyResolver — probe-path discovery + diagnostics.
/// 2. RoslynCodeExecutor host-profile execution + warning surfaces.
/// 3. RoslynSemanticView Help / EdgesOfKind / SymbolsOfKind sandbox helpers.
/// </summary>
[Collection("ScriptExecutorSerial")]
public class ExecuteRobustnessTests
{
    // ──────────────────────────────────────────────────────────────────
    // 1. UnityAssemblyResolver
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void UnityAssemblyResolver_NoLibraryDir_ReturnsEmptyAndNoDiagnostics()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"lb-noLib-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var r = new UnityAssemblyResolver(new PhysicalFileSystem(), temp);
            Assert.Empty(r.GetAssemblyProbePaths());
            Assert.Empty(r.GetDiagnostics());
        }
        finally { Directory.Delete(temp, true); }
    }

    [Fact]
    public void UnityAssemblyResolver_LibraryWithoutArtifacts_ReturnsFriendlyDiagnostic()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"lb-emptyLib-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(temp, "Library"));
        try
        {
            var r = new UnityAssemblyResolver(new PhysicalFileSystem(), temp);
            Assert.Empty(r.GetAssemblyProbePaths());
            var diags = r.GetDiagnostics();
            Assert.Single(diags);
            Assert.Contains("Run a Unity build", diags[0]);
        }
        finally { Directory.Delete(temp, true); }
    }

    [Fact]
    public void UnityAssemblyResolver_FindsScriptAssembliesDlls()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"lb-sa-{Guid.NewGuid():N}");
        var sa = Path.Combine(temp, "Library", "ScriptAssemblies");
        Directory.CreateDirectory(sa);
        try
        {
            // Drop a marker DLL — minimal PE-shaped file is enough
            // because the resolver only enumerates paths, doesn't load.
            File.WriteAllBytes(Path.Combine(sa, "Assembly-CSharp.dll"), new byte[] { 0x4D, 0x5A });
            File.WriteAllBytes(Path.Combine(sa, "Other.dll"), new byte[] { 0x4D, 0x5A });
            var r = new UnityAssemblyResolver(new PhysicalFileSystem(), temp);
            var paths = r.GetAssemblyProbePaths();
            Assert.Equal(2, paths.Length);
            Assert.All(paths, p => Assert.EndsWith(".dll", p, StringComparison.OrdinalIgnoreCase));
            Assert.Empty(r.GetDiagnostics());
        }
        finally { Directory.Delete(temp, true); }
    }

    // ──────────────────────────────────────────────────────────────────
    // 2. RoslynCodeExecutor host-profile execution + warning surfaces
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Executor_HostProfile_NoTargetWarnings()
    {
        var view = MakeMinimalView();
        var exec = new RoslynCodeExecutor(view);
        var result = exec.Execute(new CodeExecutionRequest
        {
            Code = "1 + 1",
            TargetProfile = "host",
        });
        Assert.True(result.Success, "Host-profile execute against trivial expr must succeed: " + result.Error);
        Assert.Empty(result.TargetRuntimeWarnings);
    }

    [Fact]
    public void Executor_NonHostTargetProfile_RunsHostBcl_WithExplicitWarning()
    {
        var view = MakeMinimalView();
        var exec = new RoslynCodeExecutor(view);
        var result = exec.Execute(new CodeExecutionRequest
        {
            Code = "1 + 1",
            TargetProfile = "net-standard-2.1",
        });
        Assert.True(result.Success, "Host-BCL execute must still run when a non-host profile is requested: " + result.Error);
        Assert.NotEmpty(result.TargetRuntimeWarnings);
        Assert.Contains("informational", result.TargetRuntimeWarnings[0]);
        Assert.Contains("host scripting BCL", result.TargetRuntimeWarnings[0]);
        Assert.Contains("not implemented", result.TargetRuntimeWarnings[0]);
    }

    [Fact]
    public void Executor_UnknownTargetProfile_RunsHostBcl_WithExplicitWarning()
    {
        var view = MakeMinimalView();
        var exec = new RoslynCodeExecutor(view);
        var result = exec.Execute(new CodeExecutionRequest
        {
            Code = "1 + 1",
            TargetProfile = "made-up-profile",
        });
        Assert.True(result.Success, "Host-BCL execute must still run for unknown legacy profiles: " + result.Error);
        Assert.NotEmpty(result.TargetRuntimeWarnings);
        Assert.Contains("made-up-profile", result.TargetRuntimeWarnings[0]);
        Assert.Contains("host scripting BCL", result.TargetRuntimeWarnings[0]);
    }

    [Fact]
    public void Executor_LegacyOverload_DelegatesToTypedRequest_HostProfile()
    {
        var view = MakeMinimalView();
        var exec = new RoslynCodeExecutor(view);
        var result = exec.Execute("21 * 2");
        Assert.True(result.Success);
        Assert.Equal("42", result.ReturnValue);
    }

    [Fact]
    public void Executor_PassesRuntimeAssemblyDiagnostics_ThroughOnSuccess()
    {
        var view = MakeMinimalView();
        var stubResolver = new StubResolver(
            paths: System.Array.Empty<string>(),
            diags: new[] { "stub diag" });
        var exec = new RoslynCodeExecutor(view, stubResolver);
        var result = exec.Execute(new CodeExecutionRequest { Code = "1" });
        Assert.True(result.Success);
        Assert.Single(result.RuntimeAssemblyWarnings);
        Assert.Equal("stub diag", result.RuntimeAssemblyWarnings[0]);
    }

    [Fact]
    public void Executor_PassesRuntimeAssemblyDiagnostics_ThroughOnBlockedPattern()
    {
        var view = MakeMinimalView();
        var stubResolver = new StubResolver(
            paths: System.Array.Empty<string>(),
            diags: new[] { "stub diag" });
        var exec = new RoslynCodeExecutor(view, stubResolver);
        var result = exec.Execute(new CodeExecutionRequest { Code = "Process.Start(\"x\")" });
        Assert.False(result.Success);
        Assert.Contains("Blocked pattern", result.Error);
        Assert.Single(result.RuntimeAssemblyWarnings);
    }

    [Fact]
    public void Executor_RuntimeAssemblyProbe_SkipsNativePE_AndSurfacesDiagnostic()
    {
        // 2-byte MZ stub is a valid DOS magic but NOT a managed assembly.
        // AssemblyName.GetAssemblyName throws BadImageFormatException, which
        // the executor must catch and skip — without surfacing CS0009 at
        // compile time. Mirrors the IL2CPP GameAssembly.dll case observed on
        // a Unity workspace (2026-05-19): native PE wrongly injected as
        // managed ref, broke every script run. INV-EXECUTE-001 managed-PE gate.
        var temp = Path.Combine(Path.GetTempPath(), $"lb-nativePE-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var nativeStub = Path.Combine(temp, "GameAssembly.dll");
        File.WriteAllBytes(nativeStub, new byte[] { 0x4D, 0x5A });
        try
        {
            var view = MakeMinimalView();
            var stubResolver = new StubResolver(
                paths: new[] { nativeStub },
                diags: System.Array.Empty<string>());
            var exec = new RoslynCodeExecutor(view, stubResolver);
            var result = exec.Execute(new CodeExecutionRequest { Code = "1 + 1" });

            Assert.True(result.Success,
                "Execute must succeed when native-PE candidates are filtered: " + result.Error);
            Assert.Equal("2", result.ReturnValue);
            Assert.Contains(result.RuntimeAssemblyWarnings, w => w.Contains("non-managed PE"));
            Assert.Contains(result.RuntimeAssemblyWarnings, w => w.Contains("GameAssembly.dll"));
        }
        finally { Directory.Delete(temp, true); }
    }

    [Fact]
    public void Executor_RuntimeAssemblyProbe_SkipsRuntimeBclCandidates_AndSurfacesDiagnostic()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"lb-runtimeBcl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var bclCandidate = Path.Combine(temp, "mscorlib.dll");
        EmitManagedAssembly("mscorlib", bclCandidate);
        try
        {
            var view = MakeMinimalView();
            var stubResolver = new StubResolver(
                paths: new[] { bclCandidate },
                diags: System.Array.Empty<string>());
            var exec = new RoslynCodeExecutor(view, stubResolver);
            var result = exec.Execute(new CodeExecutionRequest { Code = "1 + 1" });

            Assert.True(result.Success,
                "Execute must keep the host scripting BCL authoritative: " + result.Error);
            Assert.Equal("2", result.ReturnValue);
            Assert.Contains(result.RuntimeAssemblyWarnings, w => w.Contains("runtime BCL/contract"));
            Assert.Contains(result.RuntimeAssemblyWarnings, w => w.Contains("mscorlib.dll"));
        }
        finally { Directory.Delete(temp, true); }
    }

    [Fact]
    public void ScriptReferenceSetBuilder_RuntimeProbe_DeduplicatesManagedAssemblyIdentity()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"lb-runtimeDup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var first = Path.Combine(temp, "UnityEngine.CoreModule.A.dll");
        var second = Path.Combine(temp, "UnityEngine.CoreModule.B.dll");
        EmitManagedAssembly("UnityEngine.CoreModule", first);
        EmitManagedAssembly("UnityEngine.CoreModule", second);
        try
        {
            var builder = new ScriptReferenceSetBuilder(
                MakeMinimalView().Compilations,
                new StubResolver(new[] { first, second }, System.Array.Empty<string>()));

            var referenceSet = builder.Build("host");
            var runtimeRefs = referenceSet.References
                .OfType<PortableExecutableReference>()
                .Count(r => string.Equals(r.FilePath, first, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(r.FilePath, second, StringComparison.OrdinalIgnoreCase));

            Assert.Equal(1, runtimeRefs);
        }
        finally { Directory.Delete(temp, true); }
    }

    // ──────────────────────────────────────────────────────────────────
    // 3. RoslynSemanticView sandbox helpers (Help / EdgesOfKind / SymbolsOfKind)
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void View_Help_ContainsExpectedSections()
    {
        var view = MakeMinimalView();
        var help = view.Help;
        Assert.Contains("Available globals", help);
        Assert.Contains("EdgeKind names", help);
        Assert.Contains("SymbolKind names", help);
        Assert.Contains("Common queries", help);
    }

    [Fact]
    public void View_SymbolsOfKind_StringName_FiltersCorrectly()
    {
        var graph = TwoTypeOneMethodGraph();
        var view = new RoslynSemanticView(
            new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal),
            graph,
            new Dictionary<string, string[]>(StringComparer.Ordinal));
        var types = view.SymbolsOfKind("Type").ToArray();
        Assert.Equal(2, types.Length);
        var methods = view.SymbolsOfKind("Method").ToArray();
        Assert.Single(methods);
    }

    [Fact]
    public void View_SymbolsOfKind_UnknownName_ReturnsEmptySequence()
    {
        var view = MakeMinimalView();
        Assert.Empty(view.SymbolsOfKind("NotAKind"));
        Assert.Empty(view.SymbolsOfKind(""));
    }

    [Fact]
    public void View_EdgesOfKind_StringName_FiltersCorrectly()
    {
        var graph = TwoTypeOneMethodGraph();
        var view = new RoslynSemanticView(
            new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal),
            graph,
            new Dictionary<string, string[]>(StringComparer.Ordinal));
        var calls = view.EdgesOfKind("Calls").ToArray();
        Assert.Empty(calls); // no Calls edges in this fixture
        var contains = view.EdgesOfKind("Contains").ToArray();
        Assert.NotEmpty(contains);
    }

    [Fact]
    public void View_EdgesOfKind_UnknownName_ReturnsEmpty()
    {
        var view = MakeMinimalView();
        Assert.Empty(view.EdgesOfKind("Bogus"));
    }

    // ──────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────

    private static RoslynSemanticView MakeMinimalView()
        => new RoslynSemanticView(
            new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal),
            new SemanticGraph(),
            new Dictionary<string, string[]>(StringComparer.Ordinal));

    private static SemanticGraph TwoTypeOneMethodGraph()
    {
        var ev = new Evidence
        {
            Kind = EvidenceKind.Semantic,
            AdapterName = "test",
            Confidence = ConfidenceLevel.Proven,
        };
        return new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Foo", Name = "Foo", QualifiedName = "N.Foo", Kind = DomainSymbolKind.Type, FilePath = "Foo.cs" })
            .AddSymbol(new Symbol { Id = "type:N.Bar", Name = "Bar", QualifiedName = "N.Bar", Kind = DomainSymbolKind.Type, FilePath = "Bar.cs" })
            .AddSymbol(new Symbol { Id = "method:N.Foo.Run()", Name = "Run", QualifiedName = "N.Foo.Run", Kind = DomainSymbolKind.Method, ParentId = "type:N.Foo" })
            .AddEdge(new Edge { SourceId = "type:N.Foo", TargetId = "method:N.Foo.Run()", Kind = EdgeKind.Contains, Evidence = ev })
            .Build();
    }

    private static void EmitManagedAssembly(string assemblyName, string outputPath)
    {
        var tree = CSharpSyntaxTree.ParseText("namespace Probe { public sealed class Marker { } }");
        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { tree },
            BasicReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var emit = compilation.Emit(outputPath);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
    }

    private static MetadataReference[] BasicReferences()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir == null)
            return new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };

        var refs = new List<MetadataReference>();
        foreach (var dll in new[] { "System.Runtime.dll", "netstandard.dll" })
        {
            var path = Path.Combine(runtimeDir, dll);
            if (File.Exists(path))
                refs.Add(MetadataReference.CreateFromFile(path));
        }
        refs.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        return refs.ToArray();
    }

    private sealed class StubResolver : IRuntimeAssemblyResolver
    {
        private readonly string[] _paths;
        private readonly string[] _diags;
        public StubResolver(string[] paths, string[] diags) { _paths = paths; _diags = diags; }
        public string[] GetAssemblyProbePaths() => _paths;
        public string[] GetDiagnostics() => _diags;
    }
}

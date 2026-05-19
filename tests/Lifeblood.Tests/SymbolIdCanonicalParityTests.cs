using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Domain.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// F1c — canonical symbol-id parity across the four id paths.
///
/// Lifeblood reproduces method-id construction in at least four sites:
///   1. <c>RoslynSymbolExtractor</c> — declaration emitter (graph-side).
///   2. <c>RoslynEdgeExtractor.GetMethodId</c> — edge emitter (graph-side).
///   3. <c>RoslynCompilationHost.BuildSymbolId</c> — find_references / rename
///      consumer side.
///   4. <c>RoslynWorkspaceManager.ParseSymbolId</c> — string → walker input
///      for the consumer-side lookup.
///
/// Drift between these paths surfaces as "find_references finds the symbol
/// but dependants returns 0" or vice versa. The 2026-05-15 correctness
/// masterplan Stage 1 named <c>.ctor</c> / <c>.cctor</c> as the specific
/// failure mode: <c>ParseSymbolId.Split('.')</c> on <c>method:NS.T..ctor()</c>
/// produces <c>["NS", "T", "", "ctor"]</c> — an empty middle part — so the
/// walker fails at <c>GetMembers("")</c>. <c>.cctor</c> has the same shape.
///
/// LB-TRACK-20260519-023 / INV-CANONICAL-ID-PARITY-001.
/// </summary>
public class SymbolIdCanonicalParityTests
{
    private static MetadataReference[] BclRefs()
    {
        var runtimeDir = System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir == null)
            return new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        var refs = new System.Collections.Generic.List<MetadataReference>();
        foreach (var dll in new[]
        {
            "System.Runtime.dll", "System.Console.dll", "System.Collections.dll",
            "netstandard.dll",
        })
        {
            var path = System.IO.Path.Combine(runtimeDir, dll);
            if (System.IO.File.Exists(path)) refs.Add(MetadataReference.CreateFromFile(path));
        }
        refs.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        return refs.ToArray();
    }

    private static RoslynCompilationHost BuildHost(string source, string path = "Test.cs")
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: path);
        var compilation = CSharpCompilation.Create("TestModule",
            new[] { tree }, BclRefs(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return new RoslynCompilationHost(
            new System.Collections.Generic.Dictionary<string, CSharpCompilation>(System.StringComparer.Ordinal)
            {
                ["TestModule"] = compilation,
            });
    }

    [Fact]
    public void FindReferences_InstanceConstructor_ResolvesUnderDotCtorId()
    {
        // Instance constructor canonical id: `method:App.Service..ctor()`.
        // Pre-fix, ParseSymbolId.Split('.') on the qualified name
        // "App.Service..ctor" produces ["App", "Service", "", "ctor"] —
        // the empty middle segment makes the namespace/type walk fail at
        // GetMembers(""), so find_references returns zero refs.
        var src = @"
namespace App;
public class Service
{
    public Service() { }
}
public class Caller
{
    public Service Make() => new Service();
}";

        using var host = BuildHost(src);
        var refs = host.FindReferences("method:App.Service..ctor()");

        Assert.True(refs.Length > 0,
            "find_references on `method:App.Service..ctor()` must resolve; " +
            "ParseSymbolId Split('.') ambiguity dropped to zero refs pre-fix.");
    }

    [Fact]
    public void ParseSymbolId_DotCtor_SplitsContainerThenLiteralCtorName()
    {
        // White-box parser ratchet: confirm the `..ctor` suffix preserves
        // the literal `.ctor` member name rather than splitting into an
        // empty middle segment. Reaches the internal parser via
        // InternalsVisibleTo on Lifeblood.Adapters.CSharp.
        var parsed = RoslynWorkspaceManager.ParseSymbolId("method:App.Service..ctor()");

        Assert.Equal("method", parsed.Kind);
        Assert.NotNull(parsed.Parts);
        Assert.Equal(new[] { "App", "Service", ".ctor" }, parsed.Parts);
    }

    [Fact]
    public void ParseSymbolId_DotCctor_SplitsContainerThenLiteralCctorName()
    {
        // Static-ctor flavor of the same parser ratchet. Ordering matters
        // in the parser: ".cctor" check must precede ".ctor" because the
        // ".cctor" suffix also ends in ".ctor".
        var parsed = RoslynWorkspaceManager.ParseSymbolId("method:App.Registry..cctor()");

        Assert.Equal("method", parsed.Kind);
        Assert.NotNull(parsed.Parts);
        Assert.Equal(new[] { "App", "Registry", ".cctor" }, parsed.Parts);
    }

    [Fact]
    public void ParseSymbolId_NestedTypeCtor_PreservesContainerPathPlusLiteralCtor()
    {
        // Nested-type containment plus .ctor — the parser must keep both
        // the multi-dot container path AND the literal trailing .ctor.
        var parsed = RoslynWorkspaceManager.ParseSymbolId("method:App.Outer.Inner..ctor()");

        Assert.Equal(new[] { "App", "Outer", "Inner", ".ctor" }, parsed.Parts);
    }

    [Fact]
    public void ResolveSymbol_StaticConstructor_ResolvesUnderDotCctorId()
    {
        // End-to-end ratchet: ParseSymbolId + FindInCompilation must agree
        // for .cctor lookup. The static constructor has no explicit source
        // invocation site (the CLR triggers it), so find_references against
        // it returns zero use-site refs — but symbol resolution itself must
        // succeed, which is the load-bearing path for tools that consume
        // the resolved IMethodSymbol directly (rename, dependants on the
        // graph side, etc.).
        var src = @"
namespace App;
public class Registry
{
    static Registry() { }
    public static readonly int X = 1;
}";
        var tree = CSharpSyntaxTree.ParseText(src, path: "Test.cs");
        var compilation = CSharpCompilation.Create("TestModule",
            new[] { tree }, BclRefs(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var parsed = RoslynWorkspaceManager.ParseSymbolId("method:App.Registry..cctor()");
        var resolved = RoslynWorkspaceManager.FindInCompilation(compilation, parsed);

        Assert.NotNull(resolved);
        Assert.IsAssignableFrom<IMethodSymbol>(resolved);
        Assert.Equal(MethodKind.StaticConstructor, ((IMethodSymbol)resolved!).MethodKind);
    }

    [Fact]
    public void FindReferences_RegularMethod_StillResolvesPostCtorFix()
    {
        // Parity guard: the .ctor/.cctor parsing fix must NOT regress the
        // common single-dot member case.
        var src = @"
namespace App;
public class Service
{
    public void Run() { }
}
public class Caller
{
    public void Invoke(Service s) => s.Run();
}";

        using var host = BuildHost(src);
        var refs = host.FindReferences("method:App.Service.Run()");

        Assert.True(refs.Length > 0,
            "find_references on a regular method id must keep resolving; " +
            "the .ctor parsing fix must not regress common-case lookup.");
    }

    [Fact]
    public void FindReferences_NestedTypeMember_StillResolvesPostCtorFix()
    {
        // Parity guard: nested types use dot-separated containment too.
        // The .ctor parsing fix must keep multi-dot type paths intact.
        var src = @"
namespace App;
public class Outer
{
    public class Inner
    {
        public void Run() { }
    }
}
public class Caller
{
    public void Invoke(Outer.Inner i) => i.Run();
}";

        using var host = BuildHost(src);
        var refs = host.FindReferences("method:App.Outer.Inner.Run()");

        Assert.True(refs.Length > 0,
            "find_references on a nested-type method id must keep resolving.");
    }
}

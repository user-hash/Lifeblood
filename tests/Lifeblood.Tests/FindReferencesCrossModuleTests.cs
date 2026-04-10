using Lifeblood.Adapters.CSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Cross-module FindReferences contract tests.
///
/// Lifeblood compiles modules in topological order and downgrades each finished
/// compilation to a PE metadata reference (see ModuleCompilationBuilder.DowngradeCompilation).
/// Downstream modules see upstream symbols as PURE METADATA — different from the source
/// symbols that the resolver returns when looking up the target by symbol ID.
///
/// FindReferences was matching by `ISymbol.ToDisplayString()` across this source/metadata
/// boundary, which is fragile (nullability, formatting, attribute round-trips can diverge).
/// The robust check is to compute the canonical Lifeblood symbol ID via the same builder
/// the graph uses, then compare strings — no display-string game.
///
/// These tests use intentionally generic, language-feature-driven scenarios. None of them
/// model a specific consumer codebase. They probe what the walker has to handle for ANY
/// project: methods on classes, methods on structs, members reached through varied receiver
/// shapes, and overload disambiguation across the source/metadata boundary.
/// </summary>
public class FindReferencesCrossModuleTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // Compilation harness
    // ─────────────────────────────────────────────────────────────────────────

    private static MetadataReference[] BclReferences()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir == null) return new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };

        var refs = new List<MetadataReference>();
        foreach (var dll in new[]
        {
            "System.Runtime.dll", "System.Console.dll", "System.Collections.dll",
            "System.Linq.dll", "System.Threading.dll", "netstandard.dll",
        })
        {
            var path = Path.Combine(runtimeDir, dll);
            if (File.Exists(path)) refs.Add(MetadataReference.CreateFromFile(path));
        }
        refs.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        return refs.ToArray();
    }

    /// <summary>
    /// Build two compilations where the consumer references the library as a real PE image
    /// (matching ModuleCompilationBuilder.DowngradeCompilation). This is the EXACT cross-module
    /// shape Lifeblood produces at runtime — `compilation.ToMetadataReference()` is NOT a valid
    /// substitute because it preserves live symbol identities.
    /// </summary>
    private static (Dictionary<string, CSharpCompilation> compilations,
                    Dictionary<string, string[]> deps)
        BuildTwoModule(string libSource, string consumerSource,
                       string libName = "Lib", string consumerName = "Consumer")
    {
        var bcl = BclReferences();

        var libTree = CSharpSyntaxTree.ParseText(libSource, path: $"{libName}.cs");
        var libCompilation = CSharpCompilation.Create(
            libName, new[] { libTree }, bcl,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var libErrors = libCompilation.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToArray();
        Assert.True(libErrors.Length == 0,
            $"{libName} fixture failed to compile: " +
            string.Join("; ", libErrors.Select(d => d.GetMessage())));

        // Emit to a real PE image — same path as ModuleCompilationBuilder.
        MetadataReference libPeRef;
        using (var ms = new MemoryStream())
        {
            var emit = libCompilation.Emit(ms);
            Assert.True(emit.Success,
                $"{libName} emit failed: " + string.Join("; ", emit.Diagnostics.Select(d => d.GetMessage())));
            libPeRef = MetadataReference.CreateFromImage(ms.ToArray());
        }

        var consumerRefs = new List<MetadataReference>(bcl) { libPeRef };
        var consumerTree = CSharpSyntaxTree.ParseText(consumerSource, path: $"{consumerName}.cs");
        var consumerCompilation = CSharpCompilation.Create(
            consumerName, new[] { consumerTree }, consumerRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var consumerErrors = consumerCompilation.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToArray();
        Assert.True(consumerErrors.Length == 0,
            $"{consumerName} fixture failed to compile: " +
            string.Join("; ", consumerErrors.Select(d => d.GetMessage())));

        var compilations = new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal)
        {
            [libName] = libCompilation,
            [consumerName] = consumerCompilation,
        };
        var deps = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [libName] = Array.Empty<string>(),
            [consumerName] = new[] { libName },
        };
        return (compilations, deps);
    }

    private static int CountInFile(Lifeblood.Domain.Results.ReferenceLocation[] refs, string fileName)
        => refs.Count(r => r.FilePath.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

    // ─────────────────────────────────────────────────────────────────────────
    // Cross-module method calls — class
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FindReferences_ClassMethod_CalledFromAnotherModule_IsFound()
    {
        var (comps, deps) = BuildTwoModule(
            libSource: @"
namespace Lib
{
    public class Service
    {
        public void Run() { }
    }
}",
            consumerSource: @"
using Lib;
namespace App
{
    public class Caller
    {
        public void Invoke() { new Service().Run(); }
    }
}");

        using var host = new RoslynCompilationHost(comps, deps);
        var refs = host.FindReferences("method:Lib.Service.Run()");

        Assert.True(refs.Length > 0, "Cross-module class method call not found.");
        Assert.True(CountInFile(refs, "Consumer.cs") > 0,
            "Reference in Consumer.cs missing — walker failed across the source/metadata boundary.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Cross-module method calls — struct (the failing case in real projects)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FindReferences_StructMethod_CalledFromAnotherModule_IsFound()
    {
        var (comps, deps) = BuildTwoModule(
            libSource: @"
namespace Lib
{
    public struct Item
    {
        public int Value;
        public void Bump() { Value++; }
    }
}",
            consumerSource: @"
using Lib;
namespace App
{
    public class Driver
    {
        public void Tick(Item it) { it.Bump(); }
    }
}");

        using var host = new RoslynCompilationHost(comps, deps);
        var refs = host.FindReferences("method:Lib.Item.Bump()");

        Assert.True(refs.Length > 0,
            "Cross-module struct method call not found — display-string match likely diverged " +
            "across source/metadata boundary.");
        Assert.True(CountInFile(refs, "Consumer.cs") > 0,
            "Reference in Consumer.cs missing.");
    }

    [Fact]
    public void FindReferences_PartialStructMethod_CalledFromAnotherModule_IsFound()
    {
        // Same as above but the struct is partial across two declarations within the same
        // library. Roslyn's source-merge of partials can produce subtle display-string
        // differences relative to the metadata round-trip — this test pins that down.
        var (comps, deps) = BuildTwoModule(
            libSource: @"
namespace Lib
{
    public partial struct Item
    {
        public int Value;
    }
    public partial struct Item
    {
        public void Bump() { Value++; }
    }
}",
            consumerSource: @"
using Lib;
namespace App
{
    public class Driver
    {
        public void Tick(Item it) { it.Bump(); }
    }
}");

        using var host = new RoslynCompilationHost(comps, deps);
        var refs = host.FindReferences("method:Lib.Item.Bump()");
        Assert.True(refs.Length > 0,
            "Partial-struct cross-module method call not found.");
        Assert.True(CountInFile(refs, "Consumer.cs") > 0);
    }

    [Fact]
    public void FindReferences_StructMethod_ViaArrayIndexerReceiver_IsFound()
    {
        // The receiver shape that triggered the bug report: `arr[i].Method(...)` where `arr`
        // is a struct array in a different module than the struct definition.
        var (comps, deps) = BuildTwoModule(
            libSource: @"
namespace Lib
{
    public struct Item
    {
        public int Value;
        public void Bump() { Value++; }
    }
}",
            consumerSource: @"
using Lib;
namespace App
{
    public class Driver
    {
        private readonly Item[] _items = new Item[4];
        public void TickAll()
        {
            for (int i = 0; i < _items.Length; i++)
                _items[i].Bump();
        }
    }
}");

        using var host = new RoslynCompilationHost(comps, deps);
        var refs = host.FindReferences("method:Lib.Item.Bump()");
        Assert.True(refs.Length > 0,
            "Cross-module struct method called via array indexer not found.");
        Assert.True(CountInFile(refs, "Consumer.cs") > 0);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Overload disambiguation across source/metadata boundary
    // ─────────────────────────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────────────────────────
    // Symbol resolver — never silently match the wrong overload
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FindReferences_NonexistentSignatureOnExistingType_ReturnsEmpty()
    {
        // Regression for the silent-fallback bug in FindInCompilation: when the parameter
        // signature does not match any overload on the resolved type, the resolver MUST
        // return null (so FindReferences yields zero results) instead of falling back to
        // `methods[0]` and silently returning call sites of an unrelated method.
        //
        // Symptom in the user's session: querying find_references for `Voice.Reset()` —
        // a method that does NOT exist on Voice — returned `UpdateEnvelopes()` call sites.
        var (comps, deps) = BuildTwoModule(
            libSource: @"
namespace Lib
{
    public class Service
    {
        public void Run() { }
        public void Run(int n) { }
        public void OtherMethod() { }
    }
}",
            consumerSource: @"
using Lib;
namespace App
{
    public class Caller
    {
        public void Use() { new Service().OtherMethod(); }
    }
}");

        using var host = new RoslynCompilationHost(comps, deps);

        // Ask for a method whose name exists but whose param signature does NOT
        // match any overload. With the silent-fallback bug, this returns OtherMethod's
        // call sites. With the fix, it must return zero.
        var refs = host.FindReferences("method:Lib.Service.Run(string)");

        Assert.Empty(refs);
    }

    [Fact]
    public void FindReferences_NonexistentMethodNameOnExistingType_ReturnsEmpty()
    {
        // Same shape as above but with a method name that DOES NOT EXIST on the type
        // at all. The user's exact failing query: `Voice.Reset()` on a type that has
        // ResetClampCounter and ResetUnisonOscillators but no `Reset` method.
        var (comps, deps) = BuildTwoModule(
            libSource: @"
namespace Lib
{
    public class Service
    {
        public void RunOnce() { }
        public void RunTwice() { }
    }
}",
            consumerSource: @"
using Lib;
namespace App
{
    public class Caller
    {
        public void Use() { new Service().RunOnce(); }
    }
}");

        using var host = new RoslynCompilationHost(comps, deps);

        // No method literally named `Run` on Service. The fallback must NOT match
        // RunOnce or RunTwice — those are entirely different members.
        var refs = host.FindReferences("method:Lib.Service.Run()");

        Assert.Empty(refs);
    }

    [Fact]
    public void FindReferences_StructMethod_WithLibraryDefinedParameterType_IsFound()
    {
        // The variant that triggered the user-reported failure: the method's parameter type
        // is ANOTHER struct defined in the same library module. After PE round-trip, both
        // the method's symbol AND its parameter type symbol are pure metadata, and the
        // display-string match has historically failed in this exact shape.
        var (comps, deps) = BuildTwoModule(
            libSource: @"
namespace Lib
{
    public struct Config { public int Id; }
    public partial struct Item
    {
        public int Value;
    }
    public partial struct Item
    {
        public void Configure(Config cfg) { Value = cfg.Id; }
    }
}",
            consumerSource: @"
using Lib;
namespace App
{
    public class Driver
    {
        private readonly Item[] _items = new Item[4];
        public void ConfigureAll(Config cfg)
        {
            for (int i = 0; i < _items.Length; i++)
                _items[i].Configure(cfg);
        }
    }
}");

        using var host = new RoslynCompilationHost(comps, deps);
        var refs = host.FindReferences("method:Lib.Item.Configure(Lib.Config)");

        Assert.True(refs.Length > 0,
            "Cross-module struct method whose parameter type is also defined in the library was not found. " +
            "This is the failing case from the original bug report.");
        Assert.True(CountInFile(refs, "Consumer.cs") > 0,
            "Reference in Consumer.cs missing.");
    }

    [Fact]
    public void FindReferences_OverloadedMethod_DisambiguatesAcrossModules()
    {
        // Two overloads of the same name in the library. The walker must distinguish
        // them by parameter signature, not just by name. Display-string-based matching
        // historically returned the wrong overload here.
        var (comps, deps) = BuildTwoModule(
            libSource: @"
namespace Lib
{
    public class Math
    {
        public int Add(int a, int b) => a + b;
        public double Add(double a, double b) => a + b;
    }
}",
            consumerSource: @"
using Lib;
namespace App
{
    public class Caller
    {
        public void Use()
        {
            var m = new Math();
            m.Add(1, 2);          // int overload
            m.Add(1.0, 2.0);      // double overload
        }
    }
}");

        using var host = new RoslynCompilationHost(comps, deps);
        var intRefs = host.FindReferences("method:Lib.Math.Add(int,int)");
        var dblRefs = host.FindReferences("method:Lib.Math.Add(double,double)");

        Assert.True(intRefs.Length > 0, "int overload of Add not found.");
        Assert.True(dblRefs.Length > 0, "double overload of Add not found.");

        // Each overload's references must be on its own call line — they must NOT bleed
        // into each other.
        Assert.DoesNotContain(intRefs, r => r.SpanText.Contains("1.0"));
        Assert.DoesNotContain(dblRefs, r => r.SpanText.Contains("1, 2"));
    }
}

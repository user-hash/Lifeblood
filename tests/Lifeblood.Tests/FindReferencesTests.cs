using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Regression tests for RoslynCompilationHost.FindReferences and the
/// skipped-file surface. Pins:
///   - C2: ContainingSymbolId populated on every reference location
///   - C3: Kind tagged as Declaration or Usage
///   - A3: Logical-reference dedup emits exactly one entry per physical occurrence
/// and
///   - C4: Skipped files surface with machine-readable reason codes
///
/// Uses the same two-compilation PE harness as
/// <see cref="FindReferencesCrossModuleTests"/> to exercise the real
/// source/metadata boundary.
/// </summary>
public class FindReferencesTests
{
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
        MetadataReference libPeRef;
        using (var ms = new MemoryStream())
        {
            libCompilation.Emit(ms);
            libPeRef = MetadataReference.CreateFromImage(ms.ToArray());
        }
        var consumerRefs = new List<MetadataReference>(bcl) { libPeRef };
        var consumerTree = CSharpSyntaxTree.ParseText(consumerSource, path: $"{consumerName}.cs");
        var consumerCompilation = CSharpCompilation.Create(
            consumerName, new[] { consumerTree }, consumerRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return (
            new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal)
            {
                [libName] = libCompilation,
                [consumerName] = consumerCompilation,
            },
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [libName] = System.Array.Empty<string>(),
                [consumerName] = new[] { libName },
            });
    }

    // ─────────────────────────────────────────────────────────────────────
    // C2: ContainingSymbolId populated
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void FindReferences_Usage_CarriesContainingMethodSymbolId()
    {
        var (comps, deps) = BuildTwoModule(
            libSource: @"
namespace Lib
{
    public class Service { public void Run() { } }
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

        var usages = refs.Where(r => r.Kind == ReferenceKind.Usage).ToArray();
        Assert.NotEmpty(usages);
        // Every usage must carry the canonical ID of its enclosing member.
        Assert.All(usages, r => Assert.False(
            string.IsNullOrEmpty(r.ContainingSymbolId),
            $"Reference at {r.FilePath}:{r.Line} has empty ContainingSymbolId. C2 invariant broken."));
        // And that ID must point to App.Caller.Invoke, the enclosing method.
        Assert.Contains(usages, r => r.ContainingSymbolId == "method:App.Caller.Invoke()");
    }

    // ─────────────────────────────────────────────────────────────────────
    // C3: Kind tagging
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void FindReferences_WithIncludeDeclarations_TagsDeclarationKind()
    {
        var (comps, deps) = BuildTwoModule(
            libSource: @"
namespace Lib
{
    public class Service { public void Run() { } }
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
        var refs = host.FindReferences("method:Lib.Service.Run()", new FindReferencesOptions { IncludeDeclarations = true });

        var declarations = refs.Where(r => r.Kind == ReferenceKind.Declaration).ToArray();
        var usages = refs.Where(r => r.Kind == ReferenceKind.Usage).ToArray();

        Assert.NotEmpty(declarations);
        Assert.NotEmpty(usages);
        Assert.All(declarations, r => Assert.Equal("(declaration)", r.SpanText));
    }

    [Fact]
    public void FindReferences_WithoutIncludeDeclarations_EmitsOnlyUsages()
    {
        var (comps, deps) = BuildTwoModule(
            libSource: @"
namespace Lib
{
    public class Service { public void Run() { } }
}",
            consumerSource: @"
using Lib;
namespace App
{
    public class Caller { public void Invoke() { new Service().Run(); } }
}");

        using var host = new RoslynCompilationHost(comps, deps);
        var refs = host.FindReferences("method:Lib.Service.Run()");

        Assert.All(refs, r => Assert.Equal(ReferenceKind.Usage, r.Kind));
    }

    // ─────────────────────────────────────────────────────────────────────
    // A3: Logical-reference dedup — one hit per physical occurrence
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void FindReferences_InvocationAndIdentifier_DedupToOneOccurrence()
    {
        // A single call-site `svc.Run()` produces two Roslyn syntax nodes
        // whose `GetSymbolInfo` resolves to the same target: the invocation
        // expression `svc.Run()` and the identifier token `Run`. Before the
        // A3 dedup, both were emitted as separate ReferenceLocations on
        // the same line with different columns, doubling the count. After
        // normalization of the called expression collapses them to one.
        var (comps, deps) = BuildTwoModule(
            libSource: @"
namespace Lib
{
    public class Service { public void Run() { } }
}",
            consumerSource: @"
using Lib;
namespace App
{
    public class Caller
    {
        private readonly Service _svc = new Service();
        public void Invoke()
        {
            _svc.Run();
        }
    }
}");

        using var host = new RoslynCompilationHost(comps, deps);
        var refs = host.FindReferences("method:Lib.Service.Run()")
            .Where(r => r.FilePath.EndsWith("Consumer.cs", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // ONE hit per logical call-site. Before A3 this was 2.
        Assert.Single(refs);
        Assert.Equal("method:App.Caller.Invoke()", refs[0].ContainingSymbolId);
    }

    [Theory]
    [InlineData("_svc.Run(); _svc.Run();", 2)]
    [InlineData("_svc.\nRun();", 1)]
    [InlineData("_svc?.Run(); _svc?.Run();", 2)]
    public void FindReferences_PreservesDistinctOccurrences(string calls, int count)
    {
        var (comps, deps) = BuildTwoModule(
            "namespace Lib { public class Service { public void Run() { } } }",
            $"using Lib; class Caller {{ Service _svc = new(); void Invoke() {{ {calls} }} }}");
        using var host = new RoslynCompilationHost(comps, deps);
        var references = host.FindReferences("method:Lib.Service.Run()");
        Assert.Equal(count, references.Length);
        Assert.Equal(count, references.Select(r => (r.Line, r.Column)).Distinct().Count());
    }

    [Fact]
    public void FindReferences_TopLevelInvocation_IsOneOccurrence()
    {
        var (comps, deps) = BuildTwoModule(
            "namespace Lib { public class Service { public void Run() { } } }",
            "new Lib.Service().Run(); new Lib.Service().Run();");
        using var host = new RoslynCompilationHost(comps, deps);
        Assert.Equal(2, host.FindReferences("method:Lib.Service.Run()").Length);
    }

    [Fact]
    public void FindReferences_NestedSameTargetCalls_StayDistinct()
    {
        var (comps, deps) = BuildTwoModule(
            "namespace Lib { public class Service { public static int Run(int n) => n; } }",
            "class Caller { int Invoke() => Lib.Service.Run(Lib.Service.Run(0)); }");
        using var host = new RoslynCompilationHost(comps, deps);
        Assert.Equal(2, host.FindReferences("method:Lib.Service.Run(int)").Length);
    }

    [Theory]
    [InlineData("implicit", "int a = value; int b = value;")]
    [InlineData("explicit", "int a = (int)value; int b = (int)value;")]
    public void FindReferences_UserDefinedConversion_PreservesEveryUse(string conversion, string assignments)
    {
        var (comps, deps) = BuildTwoModule(
            $"namespace Lib {{ public class Value {{ public static {conversion} operator int(Value value) => 1; }} }}",
            $"class Caller {{ void Invoke(Lib.Value value) {{ {assignments} }} }}");
        using var host = new RoslynCompilationHost(comps, deps);
        var id = $"method:Lib.Value.op_{(conversion == "implicit" ? "Implicit" : "Explicit")}(Lib.Value)";
        var references = host.FindReferences(id, new FindReferencesOptions { IncludeDeclarations = true });
        var usages = references.Where(r => r.Kind == ReferenceKind.Usage).ToArray();
        Assert.Equal(2, usages.Length);
        Assert.Equal(2, usages.Select(r => (r.Line, r.Column)).Distinct().Count());
        Assert.All(usages, r => Assert.Equal("method:Caller.Invoke(Lib.Value)", r.ContainingSymbolId));
        Assert.Single(references, r => r.Kind == ReferenceKind.Declaration);
    }

    [Fact]
    public void FindReferences_Cancellation_IsHonoredByRetainedScan()
    {
        var (comps, deps) = BuildTwoModule(
            "namespace Lib { public class Service { public void Run() { } } }",
            "class Caller { void Invoke() => new Lib.Service().Run(); }");
        using var host = new RoslynCompilationHost(comps, deps);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => host.FindReferences(
            "method:Lib.Service.Run()", new FindReferencesOptions { CancellationToken = cancellation.Token }));
    }

    [Fact]
    public void MultiTarget_OneSyntaxPassAndOneConversionPassPreserveSingleTargetResults()
    {
        var (comps, deps) = BuildTwoModule(
            "namespace Lib { public class A { public void Run() {} public void Other() {} public static implicit operator int(A a) => 1; } public class B { public static implicit operator int(B b) => 2; } }",
            "class Caller { void Invoke(Lib.A a, Lib.B b) { a.Run(); a.Other(); a.Run(); int x = a; int y = b; } }");
        Assert.DoesNotContain(comps.Values.SelectMany(c => c.GetDiagnostics()), d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        using var host = new RoslynCompilationHost(comps, deps);
        var ids = new[] { "method:Lib.A.Run()", "method:Lib.A.Other()", "type:Lib.A", "method:Lib.A.op_Implicit(Lib.A)", "method:Lib.B.op_Implicit(Lib.B)" };
        var options = new FindReferencesOptions { IncludeDeclarations = true };
        var separate = ids.Select(id => host.FindReferences(new FindReferencesRequest { SymbolIds = new[] { id } }, options)).ToArray();
        var combined = host.FindReferences(new FindReferencesRequest { SymbolIds = ids }, options);
        Assert.Equal(ids, combined.Symbols.Select(row => row.SymbolId));
        Assert.All(combined.Symbols, row => Assert.True(row.Resolved));
        for (var i = 0; i < ids.Length; i++)
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(separate[i].Symbols[0].Locations),
                System.Text.Json.JsonSerializer.Serialize(combined.Symbols[i].Locations));
        Assert.Equal(2, combined.Work.SyntaxTreeVisits);
        Assert.Equal(separate.Take(3).Sum(row => row.Work.SyntaxBindingCount), combined.Work.SyntaxBindingCount * 3);
        Assert.Equal(1, combined.Work.ConversionScanCount);
        Assert.Equal(2, separate.Sum(row => row.Work.ConversionScanCount));
        Assert.Equal(2, combined.Symbols[0].Locations.Count(location => location.Kind == ReferenceKind.Usage));
        Assert.Single(combined.Symbols[3].Locations, location => location.Kind == ReferenceKind.Usage);
        Assert.Single(combined.Symbols[4].Locations, location => location.Kind == ReferenceKind.Usage);
    }

    [Fact]
    public void MultiTarget_DuplicatesAndUnresolvedTargetsPreserveOrderWithoutAnotherScan()
    {
        var (comps, deps) = BuildTwoModule("namespace Lib { public class A { public void Run() {} } }", "class Caller { void Invoke() { new Lib.A().Run(); } }");
        using var host = new RoslynCompilationHost(comps, deps);
        var id = "method:Lib.A.Run()";
        var single = host.FindReferences(new FindReferencesRequest { SymbolIds = new[] { id } }, FindReferencesOptions.Default);
        var many = host.FindReferences(new FindReferencesRequest { SymbolIds = new[] { id, "type:Missing", id } }, FindReferencesOptions.Default);
        Assert.Equal(single.Work.SyntaxBindingCount, many.Work.SyntaxBindingCount);
        Assert.Same(many.Symbols[0].Locations, many.Symbols[2].Locations);
        Assert.False(many.Symbols[1].Resolved);
        Assert.Empty(many.Symbols[1].Locations);
        var maximum = host.FindReferences(new FindReferencesRequest { SymbolIds = Enumerable.Repeat(id, 32).ToArray() }, FindReferencesOptions.Default);
        Assert.Equal(32, maximum.Symbols.Length);
        Assert.Equal(single.Work.SyntaxBindingCount, maximum.Work.SyntaxBindingCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public void MultiTarget_InvalidBoundsRejectBeforeScanning(int count)
    {
        using var host = new RoslynCompilationHost(new Dictionary<string, CSharpCompilation>());
        Assert.Throws<ArgumentOutOfRangeException>(() => host.FindReferences(
            new FindReferencesRequest { SymbolIds = Enumerable.Repeat("type:Missing", count).ToArray() }, FindReferencesOptions.Default));
    }

    // ─────────────────────────────────────────────────────────────────────
    // C4: SkippedFile surface
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Analyze_CsprojListingMissingCsFile_SurfacesFileNotFoundSkipped()
    {
        // A csproj that lists a .cs file which doesn't exist on disk should
        // produce a SkippedFile entry with reason=file-not-found. Without
        // this surface the analyzer silently drops the file and the user
        // has no way to discover the discrepancy.
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-skipped-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Present.cs"),
                "namespace T; public class Present { }");
            // Note: Missing.cs listed in csproj but NOT created on disk.
            File.WriteAllText(Path.Combine(tempDir, "TestProject.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include=""Present.cs"" />
    <Compile Include=""Missing.cs"" />
  </ItemGroup>
</Project>");

            var fs = new PhysicalFileSystem();
            var analyzer = new RoslynWorkspaceAnalyzer(fs);
            analyzer.AnalyzeWorkspace(tempDir, new AnalysisConfig());

            var skipped = analyzer.SkippedFiles;
            Assert.Contains(skipped, s =>
                s.Reason == SkipReason.FileNotFound &&
                s.FilePath.EndsWith("Missing.cs", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
    }
}

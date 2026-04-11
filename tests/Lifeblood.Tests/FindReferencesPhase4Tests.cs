using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Phase 4 regression tests for RoslynCompilationHost.FindReferences and
/// the skipped-file surface. Pins:
///   - C2: ContainingSymbolId populated on every reference location
///   - C3: Kind tagged as Declaration or Usage
///   - A3: Logical-reference dedup emits exactly one entry per (file, line, container)
/// and
///   - C4: Skipped files surface with machine-readable reason codes
///
/// Uses the same two-compilation PE harness as
/// <see cref="FindReferencesCrossModuleTests"/> to exercise the real
/// source/metadata boundary.
/// </summary>
public class FindReferencesPhase4Tests
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
    // A3: Logical-reference dedup — one hit per (file, line, container)
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void FindReferences_InvocationAndIdentifier_DedupToOneHitPerLine()
    {
        // A single call-site `svc.Run()` produces two Roslyn syntax nodes
        // whose `GetSymbolInfo` resolves to the same target: the invocation
        // expression `svc.Run()` and the identifier token `Run`. Before the
        // A3 dedup, both were emitted as separate ReferenceLocations on
        // the same line with different columns, doubling the count. After
        // A3, the logical-reference key (file, line, containingSymbolId,
        // referencedSymbolId) collapses them to one.
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

    // ─────────────────────────────────────────────────────────────────────
    // C4: SkippedFile surface
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Analyze_CsprojListingMissingCsFile_SurfacesFileNotFoundSkipped()
    {
        // A csproj that lists a .cs file which doesn't exist on disk should
        // produce a SkippedFile entry with reason=file-not-found. Before
        // Phase 4 / C4, the analyzer silently dropped the file and the
        // user had no way to discover the discrepancy.
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

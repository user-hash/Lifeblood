using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Left;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Regression tests for canonical method-ID determinism — INV-CANONICAL-001.
///
/// Background (dogfood finding NEW-01, 2026-04-11):
/// Self-analysis of the Lifeblood repo discovered two symbols with identical
/// method shape — <c>ISymbolResolver.Resolve(SemanticGraph, string)</c> and
/// <c>LifebloodSymbolResolver.Resolve(SemanticGraph, string)</c> — storing
/// DIFFERENT canonical IDs:
/// <list type="bullet">
///   <item><c>method:...ISymbolResolver.Resolve(Lifeblood.Domain.Graph.SemanticGraph,string)</c> ← fully qualified (correct)</item>
///   <item><c>method:...LifebloodSymbolResolver.Resolve(SemanticGraph,string)</c> ← namespace dropped (BUG)</item>
/// </list>
///
/// Root cause (architectural, not cosmetic):
/// Lifeblood.Connectors.Mcp.csproj declares only a direct ProjectReference to
/// Lifeblood.Application. It does NOT directly reference Lifeblood.Domain,
/// even though its source uses <c>using Lifeblood.Domain.Graph;</c>. MSBuild's
/// transitive ProjectReference flow makes this work at <c>dotnet build</c>
/// time, but Lifeblood's own <see cref="Internal.ModuleCompilationBuilder"/>
/// was collecting ONLY each module's direct <see cref="ModuleInfo.Dependencies"/>
/// when building each Roslyn compilation. With Domain missing from the
/// reference set for the Connectors.Mcp compilation, Roslyn could not bind
/// <c>SemanticGraph</c> and downgraded it to an ERROR type symbol with an
/// empty <c>ContainingNamespace</c>. <see cref="CanonicalSymbolFormat.BuildParamSignature"/>
/// then emitted just <c>"SemanticGraph"</c> instead of the fully-qualified
/// <c>"Lifeblood.Domain.Graph.SemanticGraph"</c>.
///
/// The fix: <see cref="Internal.ModuleCompilationBuilder.ComputeTransitiveDependencies"/>
/// walks every module's dependency closure so the compilation sees every
/// transitively reachable assembly's PE reference. This matches MSBuild's
/// own behavior and makes Roslyn symbol resolution work in the same shape
/// the build system already relied on.
///
/// These tests pin the invariant both structurally (unit test on the
/// closure computation) and end-to-end (real three-module workspace on
/// disk, real Roslyn compilation, real symbol extraction).
/// </summary>
public class CanonicalSymbolFormatTests
{
    // ─────────────────────────────────────────────────────────────────────
    // Structural test: transitive dependency closure
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeTransitiveDependencies_FlatChain_ReturnsFullClosure()
    {
        // A → B → C. Asking for A's transitive deps must return {B, C}.
        var core = new ModuleInfo
        {
            Name = "Core",
            FilePaths = System.Array.Empty<string>(),
            Dependencies = System.Array.Empty<string>(),
        };
        var middle = new ModuleInfo
        {
            Name = "Middle",
            FilePaths = System.Array.Empty<string>(),
            Dependencies = new[] { "Core" },
        };
        var outer = new ModuleInfo
        {
            Name = "Outer",
            FilePaths = System.Array.Empty<string>(),
            Dependencies = new[] { "Middle" },
        };
        var lookup = new Dictionary<string, ModuleInfo>(StringComparer.Ordinal)
        {
            ["Core"] = core, ["Middle"] = middle, ["Outer"] = outer,
        };

        var closure = ModuleCompilationBuilder.ComputeTransitiveDependencies(outer, lookup);

        Assert.Equal(new[] { "Core", "Middle" }, closure.OrderBy(x => x));
    }

    [Fact]
    public void ComputeTransitiveDependencies_Diamond_ReturnsDeduplicatedClosure()
    {
        // A, B both → Core. D → A, B. Asking for D must return {A, B, Core}
        // and must NOT visit Core twice.
        var core = new ModuleInfo { Name = "Core", FilePaths = System.Array.Empty<string>(), Dependencies = System.Array.Empty<string>() };
        var a = new ModuleInfo { Name = "A", FilePaths = System.Array.Empty<string>(), Dependencies = new[] { "Core" } };
        var b = new ModuleInfo { Name = "B", FilePaths = System.Array.Empty<string>(), Dependencies = new[] { "Core" } };
        var d = new ModuleInfo { Name = "D", FilePaths = System.Array.Empty<string>(), Dependencies = new[] { "A", "B" } };
        var lookup = new Dictionary<string, ModuleInfo>(StringComparer.Ordinal)
        {
            ["Core"] = core, ["A"] = a, ["B"] = b, ["D"] = d,
        };

        var closure = ModuleCompilationBuilder.ComputeTransitiveDependencies(d, lookup);

        Assert.Equal(new[] { "A", "B", "Core" }, closure.OrderBy(x => x));
    }

    [Fact]
    public void ComputeTransitiveDependencies_Cycle_DoesNotInfinitelyRecurse()
    {
        // Pathological: A → B → A. Must terminate with {A, B} (or subset).
        // Matches the cycle-break contract on TopologicalSort.
        var a = new ModuleInfo { Name = "A", FilePaths = System.Array.Empty<string>(), Dependencies = new[] { "B" } };
        var b = new ModuleInfo { Name = "B", FilePaths = System.Array.Empty<string>(), Dependencies = new[] { "A" } };
        var lookup = new Dictionary<string, ModuleInfo>(StringComparer.Ordinal)
        {
            ["A"] = a, ["B"] = b,
        };

        var closure = ModuleCompilationBuilder.ComputeTransitiveDependencies(a, lookup);

        Assert.Contains("B", closure);
        // Self may or may not appear; the only hard guarantee is termination.
    }

    [Fact]
    public void ComputeTransitiveDependencies_UnknownModuleName_IsSilentlySkipped()
    {
        // If a module lists a dependency that isn't in the lookup (typo, missing csproj,
        // BCL name), the closure still terminates and includes the known portion. The
        // name is recorded in the closure but its own dependencies are not walked.
        var a = new ModuleInfo { Name = "A", FilePaths = System.Array.Empty<string>(), Dependencies = new[] { "Ghost" } };
        var lookup = new Dictionary<string, ModuleInfo>(StringComparer.Ordinal) { ["A"] = a };

        var closure = ModuleCompilationBuilder.ComputeTransitiveDependencies(a, lookup);

        Assert.Contains("Ghost", closure);
    }

    // ─────────────────────────────────────────────────────────────────────
    // End-to-end: three-module workspace proves the canonical ID fix
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnalyzeWorkspace_ThreeModuleTransitiveChain_ProducesCanonicalMethodIds()
    {
        // Structure:
        //   Core   — defines public type BarType
        //   Middle — direct ref → Core; defines IFace { void M(BarType x); }
        //   Outer  — direct ref → Middle ONLY; defines Impl : IFace { public void M(BarType x) { } }
        //
        // Outer's csproj does NOT directly reference Core. This is the exact shape
        // Lifeblood.Connectors.Mcp used against Lifeblood.Domain when the bug was
        // discovered. Without the transitive-deps fix, `BarType` in Outer's source
        // becomes a Roslyn error type symbol and Impl.M gets ID
        //     method:Outer.Impl.M(BarType)
        // while IFace.M gets ID
        //     method:Middle.IFace.M(Core.BarType)
        // — a silent canonical-ID drift across identical method shapes.
        //
        // With the fix, both methods produce the fully-qualified `(Core.BarType)`
        // parameter signature and the canonical IDs agree on the namespace.

        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-canonical-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Core module ─────────────────────────────────────────────
            var coreDir = Path.Combine(tempDir, "Core");
            Directory.CreateDirectory(coreDir);
            File.WriteAllText(Path.Combine(coreDir, "BarType.cs"), @"
namespace Core
{
    public class BarType
    {
        public int Value;
    }
}");
            File.WriteAllText(Path.Combine(coreDir, "Core.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>");

            // Middle module ───────────────────────────────────────────
            var middleDir = Path.Combine(tempDir, "Middle");
            Directory.CreateDirectory(middleDir);
            File.WriteAllText(Path.Combine(middleDir, "IFace.cs"), @"
using Core;
namespace Middle
{
    public interface IFace
    {
        void M(BarType x);
    }
}");
            File.WriteAllText(Path.Combine(middleDir, "Middle.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include=""..\Core\Core.csproj"" />
  </ItemGroup>
</Project>");

            // Outer module — ONLY references Middle, NOT Core ─────────
            var outerDir = Path.Combine(tempDir, "Outer");
            Directory.CreateDirectory(outerDir);
            File.WriteAllText(Path.Combine(outerDir, "Impl.cs"), @"
using Core;
using Middle;
namespace Outer
{
    public class Impl : IFace
    {
        public void M(BarType x) { }
    }
}");
            File.WriteAllText(Path.Combine(outerDir, "Outer.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include=""..\Middle\Middle.csproj"" />
  </ItemGroup>
</Project>");

            // Run real analyzer against the temp workspace.
            var fs = new PhysicalFileSystem();
            var analyzer = new RoslynWorkspaceAnalyzer(fs);
            var graph = analyzer.AnalyzeWorkspace(tempDir, new AnalysisConfig());

            // Both method IDs must exist AND must agree on the parameter signature.
            var interfaceMethodId = "method:Middle.IFace.M(Core.BarType)";
            var implMethodId = "method:Outer.Impl.M(Core.BarType)";

            Assert.True(graph.GetSymbol(interfaceMethodId) != null,
                $"Middle.IFace.M canonical ID missing. Expected fully-qualified param type. " +
                $"Available methods: {string.Join("; ", EnumerateMethodIds(graph))}");

            Assert.True(graph.GetSymbol(implMethodId) != null,
                $"Outer.Impl.M canonical ID missing. Expected fully-qualified param type " +
                $"`Core.BarType` (transitive dependency). Instead got: " +
                $"{string.Join("; ", EnumerateMethodIds(graph).Where(s => s.Contains(".Impl.M")))}. " +
                "This is the NEW-01 regression — transitive module dependencies are not " +
                "being passed to the Roslyn compilation and parameter types are falling " +
                "back to error-symbol display.");

            // Defensive: the non-canonical form MUST NOT exist.
            Assert.Null(graph.GetSymbol("method:Outer.Impl.M(BarType)"));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
    }

    private static IEnumerable<string> EnumerateMethodIds(Lifeblood.Domain.Graph.SemanticGraph graph)
        => graph.Symbols
            .Where(s => s.Id.StartsWith("method:", StringComparison.Ordinal))
            .Select(s => s.Id);
}

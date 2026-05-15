using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Ratchet wall for INV-DIAGNOSTIC-PARITY-001. Lifeblood's
/// <c>diagnose</c> output on a workspace that <c>dotnet build</c> calls
/// clean must itself be clean within the canonical parity class. The
/// fixture under test is Lifeblood's own source tree — every commit on
/// <c>main</c> must build green under MSBuild before reaching this
/// suite, so any parity-class diagnostic firing here is by definition
/// a Lifeblood-side false positive.
///
/// Canonical parity diagnostic IDs:
///   CS0122  inaccessible due to protection level     (IVT propagation)
///   CS0117  type does not contain member             (IVT downstream)
///   CS0234  namespace/type does not exist            (reference closure)
///   CS1503  cannot convert argument type             (IVT downstream)
///   CS1701  assembly reference identity mismatch     (binding redirects)
///   CS1702  assembly reference identity mismatch     (binding redirects)
///   CS1705  assembly references higher version       (binding redirects)
///   CS1729  type does not contain matching ctor      (IVT downstream)
///
/// Empirical history that this wall pins:
///   * 7,537 × CS1701 on Lifeblood.Tests pre-INV-DIAGNOSTIC-NUGET-BINDING-PARITY-001
///     (assembly identity unification, W1-A).
///   * 223 × CS0122 on Lifeblood.Tests pre-INV-DIAGNOSTIC-IVT-PARITY-001
///     (InternalsVisibleTo synthesis, W1-B).
///   * N × CS0234 on Unity-shaped workspaces pre-INV-MODULE-REFS-001
///     (System.Math sibling-namespace collision, LB-INBOX-007).
///
/// Adding a new parity class to this ratchet costs one ID string in the
/// set below; removing any without written justification + user approval
/// violates INV-WORK-008.
/// </summary>
public class BuildDiagnosticParityTests
{
    /// <summary>
    /// IDs that <c>dotnet build</c> never produces against a clean
    /// workspace but a misconfigured Lifeblood compile host can. Every
    /// member here corresponds to one historical Lifeblood-side
    /// regression class that an INV in <c>csharp-adapter.md</c> /
    /// <c>module-refs</c> already prevents at the compilation seam.
    /// </summary>
    private static readonly HashSet<string> CanonicalParityClass = new(StringComparer.Ordinal)
    {
        "CS0122", "CS0117", "CS0234", "CS1503",
        "CS1701", "CS1702", "CS1705", "CS1729",
    };

    [Fact]
    public void LifebloodSelfDiagnose_NeverFiresParityClassDiagnostics()
    {
        // Locate the Lifeblood repo root by walking up from the test
        // assembly until we find Lifeblood.sln. Mirrors the same anchor
        // strategy used by LiveSelfAnalyzeDriftTests and DocsTests.
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "Lifeblood.sln")))
            current = current.Parent;
        Assert.NotNull(current);
        var projectRoot = current!.FullName;

        var fs = new PhysicalFileSystem();
        var modules = new RoslynModuleDiscovery(fs).DiscoverModules(projectRoot);
        Assert.NotEmpty(modules);

        // Build every module's CSharpCompilation in dependency order
        // with retention on so the host has them available for diagnose.
        // Mirrors the production composition path through
        // RoslynWorkspaceAnalyzer without paying for symbol / edge
        // extraction — the wall asserts on diagnostics, not on graph
        // shape, so the lighter form is the right cost.
        var compilations = new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal);
        var retained = new ModuleCompilationBuilder(fs).ProcessInOrder(
            modules,
            projectRoot,
            new AnalysisConfig { RetainCompilations = true },
            (m, c) => compilations[m.Name] = c);
        Assert.NotNull(retained);

        using var host = new RoslynCompilationHost(retained!);
        var diagnostics = host.GetDiagnostics();

        var parityClassHits = diagnostics
            .Where(d => CanonicalParityClass.Contains(d.Id))
            .ToArray();

        if (parityClassHits.Length == 0) return;

        // Per-module breakdown when the wall fires so a future regression
        // surfaces the right pointer immediately — which module, which
        // diagnostic ID, which canonical INV the gap correlates with.
        var grouped = parityClassHits
            .GroupBy(d => (d.Module, d.Id))
            .OrderByDescending(g => g.Count())
            .Take(20)
            .Select(g => $"  {g.Key.Module} {g.Key.Id} × {g.Count()} — sample msg: {g.First().Message}")
            .ToArray();

        Assert.Fail(
            $"INV-DIAGNOSTIC-PARITY-001 violated: Lifeblood diagnose emitted "
            + $"{parityClassHits.Length} parity-class diagnostics on a workspace dotnet build "
            + $"calls clean. Top buckets:\n" + string.Join("\n", grouped));
    }
}

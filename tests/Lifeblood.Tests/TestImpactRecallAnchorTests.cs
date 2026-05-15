using Lifeblood.Adapters.CSharp;
using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Left;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Recall anchor for `lifeblood_test_impact` against Lifeblood self.
/// Picks production targets with known test coverage and asserts the
/// matching test fixture class shows up in the affected-classes list.
/// Closes the W2-F gate from the v0.7.6 prep masterplan: now that the
/// graph carries target-typed-`new(MethodGroup)` edges
/// (`INV-EXTRACT-METHOD-GROUP-CANDIDATE-001`), generic-call
/// canonical-id parity (`INV-EXTRACT-METHOD-ORIGINAL-DEFINITION-001`),
/// and synthesized-ctor surfaces for initializer edges
/// (`INV-EXTRACT-SYNTHESIZED-CTOR-001`), the read-side analyzer should
/// resolve every recently-shipped Wave W1 + W2 fixture to its proper
/// test class.
///
/// DAWG-side recall measurement is deferred to the v0.7.6 fresh-MCP
/// redeploy gate per the v0.7.6 masterplan. An Advisory heuristic
/// layer on top of `TestImpactAnalyzer` is NOT shipped in this wave —
/// the user decision (2026-05-15) was to defer the Advisory layer
/// until measurement against DAWG ratchets fires below 95% recall
/// after the redeploy.
/// </summary>
public class TestImpactRecallAnchorTests
{
    [Theory]
    [InlineData("type:Lifeblood.Adapters.CSharp.Internal.MetadataReferenceDeduplicator",
                "Lifeblood.Tests.MetadataReferenceDeduplicationTests")]
    [InlineData("field:Lifeblood.Adapters.CSharp.RoslynModuleDiscovery.MsbuildImplicitNoWarnBaseline",
                "Lifeblood.Tests.MsbuildImplicitNoWarnBaselineTests")]
    [InlineData("type:Lifeblood.Adapters.CSharp.RoslynStaticTableExtractor",
                "Lifeblood.Tests.StaticTableExtractorTests")]
    public void TestImpact_WavePinnedTargets_ReachExpectedFixture(string targetSymbolId, string expectedTestClass)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "Lifeblood.sln")))
            current = current.Parent;
        Assert.NotNull(current);

        var analyzer = new RoslynWorkspaceAnalyzer(new PhysicalFileSystem());
        var graph = analyzer.AnalyzeWorkspace(current!.FullName, new AnalysisConfig());

        var report = TestImpactAnalyzer.AnalyzeSymbol(graph, targetSymbolId);

        Assert.NotNull(report);
        Assert.Contains(report!.AffectedTestClasses,
            c => c.TypeId == "type:" + expectedTestClass);
    }
}

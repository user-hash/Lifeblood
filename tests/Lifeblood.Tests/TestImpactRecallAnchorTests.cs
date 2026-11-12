using Lifeblood.Adapters.CSharp;
using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Left;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Recall anchor for `lifeblood_test_impact` against Lifeblood self.
/// Picks production targets with known test coverage and asserts the
/// matching test fixture class shows up in the affected-classes list.
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

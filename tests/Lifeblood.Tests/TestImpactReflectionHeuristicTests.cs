using Lifeblood.Analysis;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Regression tests for INV-TEST-IMPACT-REFLECTION-001..003 — opt-in
/// source-text reflection heuristic on <see cref="TestImpactAnalyzer"/>
/// surfaces ratchet / reflection tests that the BFS over Calls /
/// References incoming edges cannot see. Every fixture builds a tiny
/// synthetic graph + an in-memory source-reader closure so the test
/// stays self-contained (no IFileSystem mocks, no on-disk fixtures).
/// </summary>
public class TestImpactReflectionHeuristicTests
{
    private const string TargetFqn = "Acme.Sample.TargetType";
    private const string TargetTypeId = "type:Acme.Sample.TargetType";
    private const string TestTypeId = "type:Acme.Tests.RatchetTests";
    private const string TestMethodId = "method:Acme.Tests.RatchetTests.SomeReflectionTest()";
    private const string TestFilePath = "Tests/RatchetTests.cs";

    private static SemanticGraph BuildGraph()
    {
        var target = new Symbol
        {
            Id = TargetTypeId,
            Name = "TargetType",
            QualifiedName = TargetFqn,
            Kind = SymbolKind.Type,
            FilePath = "Src/TargetType.cs",
        };
        var testType = new Symbol
        {
            Id = TestTypeId,
            Name = "RatchetTests",
            QualifiedName = "Acme.Tests.RatchetTests",
            Kind = SymbolKind.Type,
            FilePath = TestFilePath,
        };
        var testMethod = new Symbol
        {
            Id = TestMethodId,
            Name = "SomeReflectionTest",
            QualifiedName = "Acme.Tests.RatchetTests.SomeReflectionTest",
            Kind = SymbolKind.Method,
            FilePath = TestFilePath,
            ParentId = TestTypeId,
            Properties = new Dictionary<string, string>
            {
                [SymbolPropertyKeys.Attributes] = "Fact",
            },
        };
        return new SemanticGraph(new[] { target, testType, testMethod }, Array.Empty<Edge>());
    }

    [Fact]
    public void Heuristic_FqnLiteralInTestFile_SurfacesAsReflectionHit()
    {
        var graph = BuildGraph();
        Func<string, string?> reader = path => path == TestFilePath
            ? "// Test uses Type.GetType(\"Acme.Sample.TargetType\") to ratchet."
            : null;

        var options = new TestImpactOptions { IncludeReflectionHeuristic = true };
        var report = TestImpactAnalyzer.AnalyzeSymbol(graph, TargetTypeId, options, reader);

        Assert.Equal(1, report.ReflectionHeuristicHits);
        Assert.Equal(0, report.SemanticEdgeHits);
        var row = Assert.Single(report.AffectedTestClasses);
        Assert.Equal(TestImpactHitKind.ReflectionHeuristic, row.Kind);
        Assert.Equal(TestTypeId, row.TypeId);
    }

    [Fact]
    public void Heuristic_NameofOperationInSource_SurfacesAsReflectionHit()
    {
        // nameof(TargetType) at runtime is a constant string "TargetType",
        // but at the SOURCE-TEXT layer the substring "TargetType" appears.
        // The heuristic accepts this match because "TargetType" is the
        // unique short name in the synthetic graph.
        var graph = BuildGraph();
        Func<string, string?> reader = path => path == TestFilePath
            ? "// Test uses nameof(TargetType) for the assertion message."
            : null;

        var options = new TestImpactOptions { IncludeReflectionHeuristic = true };
        var report = TestImpactAnalyzer.AnalyzeSymbol(graph, TargetTypeId, options, reader);

        Assert.Equal(1, report.ReflectionHeuristicHits);
        Assert.Equal(TestImpactHitKind.ReflectionHeuristic, report.AffectedTestClasses[0].Kind);
    }

    [Fact]
    public void Heuristic_ShortNameWithoutNamespaceContext_DoesNotMatch()
    {
        // Add a second symbol with the same short name to defeat the
        // uniqueness gate. Without the FQN OR the namespace in the test
        // file, the short-name match must be rejected.
        var baseGraph = BuildGraph();
        var duplicate = new Symbol
        {
            Id = "type:Other.Namespace.TargetType",
            Name = "TargetType",
            QualifiedName = "Other.Namespace.TargetType",
            Kind = SymbolKind.Type,
            FilePath = "Other/TargetType.cs",
        };
        var symbols = new List<Symbol>(baseGraph.Symbols) { duplicate }.ToArray();
        var graph = new SemanticGraph(symbols, Array.Empty<Edge>());

        Func<string, string?> reader = path => path == TestFilePath
            ? "// Test contains the bare word TargetType but no namespace."
            : null;

        var options = new TestImpactOptions { IncludeReflectionHeuristic = true };
        var report = TestImpactAnalyzer.AnalyzeSymbol(graph, TargetTypeId, options, reader);

        Assert.Equal(0, report.ReflectionHeuristicHits);
        Assert.Empty(report.AffectedTestClasses);
    }

    [Fact]
    public void NoHeuristic_RetainsExistingWireShape()
    {
        var graph = BuildGraph();
        var report = TestImpactAnalyzer.AnalyzeSymbol(graph, TargetTypeId);

        Assert.Equal(0, report.ReflectionHeuristicHits);
        Assert.Empty(report.Limitations);
        Assert.Equal(0, report.SemanticEdgeHits);
        // Existing wire-shape fields preserved.
        Assert.Equal(TargetTypeId, report.Target);
        Assert.Equal(TestImpactTargetKind.Symbol, report.TargetKind);
        Assert.NotNull(report.RecommendedFilters);
        Assert.NotNull(report.AffectedTestClasses);
    }

    [Fact]
    public void Limitations_PopulatedWhenHeuristicActive_EmptyWhenDisabled()
    {
        var graph = BuildGraph();
        Func<string, string?> reader = path => path == TestFilePath
            ? "// uses Acme.Sample.TargetType reflectively"
            : null;

        var off = TestImpactAnalyzer.AnalyzeSymbol(graph, TargetTypeId);
        Assert.Empty(off.Limitations);

        var on = TestImpactAnalyzer.AnalyzeSymbol(
            graph, TargetTypeId, new TestImpactOptions { IncludeReflectionHeuristic = true }, reader);
        Assert.Single(on.Limitations);
        Assert.Contains("ReflectionHeuristic", on.Limitations[0]);
    }
}

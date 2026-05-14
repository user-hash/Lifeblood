using Lifeblood.Analysis;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Regression tests for INV-TEST-IMPACT-001 —
/// <see cref="TestImpactAnalyzer"/> reports which test classes
/// transitively depend on a target symbol or file, classified by hop
/// distance and grouped by containing type. Closes LB-TRACK-20260514-007.
/// </summary>
public class TestImpactAnalyzerTests
{
    private static Symbol Method(string id, string name, string parentType, string filePath, params string[] attributes)
        => new Symbol
        {
            Id = id,
            Name = name,
            QualifiedName = parentType + "." + name,
            Kind = SymbolKind.Method,
            ParentId = parentType,
            FilePath = filePath,
            Properties = attributes.Length == 0
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["attributes"] = string.Join(";", attributes) },
        };

    private static Symbol Type(string id, string name, string filePath)
        => new Symbol
        {
            Id = id,
            Name = name,
            QualifiedName = id.StartsWith("type:") ? id.Substring(5) : id,
            Kind = SymbolKind.Type,
            FilePath = filePath,
        };

    [Fact]
    public void AnalyzeSymbol_NoTests_EmptyReport()
    {
        // No test methods anywhere → empty result, not null.
        var graph = new GraphBuilder()
            .AddSymbol(Type("type:Acme.Foo", "Foo", "src/Foo.cs"))
            .AddSymbol(Type("type:Acme.Bar", "Bar", "src/Bar.cs"))
            .AddEdge(new Edge { SourceId = "type:Acme.Bar", TargetId = "type:Acme.Foo", Kind = EdgeKind.References })
            .Build();

        var report = TestImpactAnalyzer.AnalyzeSymbol(graph, "type:Acme.Foo");

        Assert.Equal("type:Acme.Foo", report.Target);
        Assert.Equal(TestImpactTargetKind.Symbol, report.TargetKind);
        Assert.Empty(report.AffectedTestClasses);
        Assert.Equal(0, report.TotalTestMethodCount);
        Assert.Equal(0, report.DirectTestClassCount);
    }

    [Fact]
    public void AnalyzeSymbol_DirectTestCaller_DirectConfidence()
    {
        // FooTests.TestFoo directly references type:Acme.Foo —
        // distance 1, Confidence.Direct.
        var graph = new GraphBuilder()
            .AddSymbol(Type("type:Acme.Foo", "Foo", "src/Foo.cs"))
            .AddSymbol(Type("type:Acme.Tests.FooTests", "FooTests", "tests/FooTests.cs"))
            .AddSymbol(Method("method:Acme.Tests.FooTests.TestFoo()", "TestFoo",
                "type:Acme.Tests.FooTests", "tests/FooTests.cs", "Test"))
            .AddEdge(new Edge
            {
                SourceId = "method:Acme.Tests.FooTests.TestFoo()",
                TargetId = "type:Acme.Foo",
                Kind = EdgeKind.References,
            })
            .Build();

        var report = TestImpactAnalyzer.AnalyzeSymbol(graph, "type:Acme.Foo");

        var row = Assert.Single(report.AffectedTestClasses);
        Assert.Equal("FooTests", row.Name);
        Assert.Equal(1, row.MinDistance);
        Assert.Equal(TestImpactConfidence.Direct, row.Confidence);
        Assert.Equal(new[] { "TestFoo" }, row.TestMethodNames);
        Assert.Equal(1, report.DirectTestClassCount);
        Assert.Equal(1, report.TotalTestMethodCount);
    }

    [Fact]
    public void AnalyzeSymbol_OneHopThroughIntermediate_OneHopConfidence()
    {
        // Test → intermediate Service → target. Distance 2.
        var graph = new GraphBuilder()
            .AddSymbol(Type("type:Acme.Foo", "Foo", "src/Foo.cs"))
            .AddSymbol(Type("type:Acme.Service", "Service", "src/Service.cs"))
            .AddSymbol(Type("type:Acme.Tests.ServiceTests", "ServiceTests", "tests/ServiceTests.cs"))
            .AddSymbol(Method("method:Acme.Tests.ServiceTests.TestService()", "TestService",
                "type:Acme.Tests.ServiceTests", "tests/ServiceTests.cs", "Test"))
            .AddEdge(new Edge { SourceId = "type:Acme.Service", TargetId = "type:Acme.Foo", Kind = EdgeKind.References })
            .AddEdge(new Edge { SourceId = "method:Acme.Tests.ServiceTests.TestService()", TargetId = "type:Acme.Service", Kind = EdgeKind.References })
            .Build();

        var report = TestImpactAnalyzer.AnalyzeSymbol(graph, "type:Acme.Foo");

        var row = Assert.Single(report.AffectedTestClasses);
        Assert.Equal(2, row.MinDistance);
        Assert.Equal(TestImpactConfidence.OneHop, row.Confidence);
        Assert.Equal(0, report.DirectTestClassCount);
    }

    [Fact]
    public void AnalyzeSymbol_TestsGroupedByContainingType()
    {
        // Two test methods in the SAME test class, both reference the
        // target — folded into ONE row with both method names.
        var graph = new GraphBuilder()
            .AddSymbol(Type("type:Acme.Foo", "Foo", "src/Foo.cs"))
            .AddSymbol(Type("type:Acme.Tests.FooTests", "FooTests", "tests/FooTests.cs"))
            .AddSymbol(Method("method:Acme.Tests.FooTests.TestOne()", "TestOne", "type:Acme.Tests.FooTests", "tests/FooTests.cs", "Test"))
            .AddSymbol(Method("method:Acme.Tests.FooTests.TestTwo()", "TestTwo", "type:Acme.Tests.FooTests", "tests/FooTests.cs", "Test"))
            .AddEdge(new Edge { SourceId = "method:Acme.Tests.FooTests.TestOne()", TargetId = "type:Acme.Foo", Kind = EdgeKind.References })
            .AddEdge(new Edge { SourceId = "method:Acme.Tests.FooTests.TestTwo()", TargetId = "type:Acme.Foo", Kind = EdgeKind.References })
            .Build();

        var report = TestImpactAnalyzer.AnalyzeSymbol(graph, "type:Acme.Foo");

        var row = Assert.Single(report.AffectedTestClasses);
        Assert.Equal(new[] { "TestOne", "TestTwo" }, row.TestMethodNames);
        Assert.Equal(2, report.TotalTestMethodCount);
    }

    [Fact]
    public void AnalyzeSymbol_NonTestMethods_Excluded()
    {
        // FooTests.Helper has no [Test] attribute — it's a helper, not a
        // test case. It should NOT appear in the report, even though it
        // references the target.
        var graph = new GraphBuilder()
            .AddSymbol(Type("type:Acme.Foo", "Foo", "src/Foo.cs"))
            .AddSymbol(Type("type:Acme.Tests.FooTests", "FooTests", "tests/FooTests.cs"))
            .AddSymbol(Method("method:Acme.Tests.FooTests.Helper()", "Helper", "type:Acme.Tests.FooTests", "tests/FooTests.cs")) // no attrs
            .AddEdge(new Edge { SourceId = "method:Acme.Tests.FooTests.Helper()", TargetId = "type:Acme.Foo", Kind = EdgeKind.References })
            .Build();

        var report = TestImpactAnalyzer.AnalyzeSymbol(graph, "type:Acme.Foo");

        Assert.Empty(report.AffectedTestClasses);
    }

    [Fact]
    public void AnalyzeSymbol_LifecycleAttributesNotCountedAsTests()
    {
        // [SetUp] / [OneTimeSetUp] are NOT test cases — they participate
        // in test fixture lifecycle but the user-facing "which tests
        // touch this?" answer should not list them.
        var graph = new GraphBuilder()
            .AddSymbol(Type("type:Acme.Foo", "Foo", "src/Foo.cs"))
            .AddSymbol(Type("type:Acme.Tests.FooTests", "FooTests", "tests/FooTests.cs"))
            .AddSymbol(Method("method:Acme.Tests.FooTests.Init()", "Init", "type:Acme.Tests.FooTests", "tests/FooTests.cs", "SetUp"))
            .AddSymbol(Method("method:Acme.Tests.FooTests.InitClass()", "InitClass", "type:Acme.Tests.FooTests", "tests/FooTests.cs", "OneTimeSetUp"))
            .AddEdge(new Edge { SourceId = "method:Acme.Tests.FooTests.Init()", TargetId = "type:Acme.Foo", Kind = EdgeKind.References })
            .AddEdge(new Edge { SourceId = "method:Acme.Tests.FooTests.InitClass()", TargetId = "type:Acme.Foo", Kind = EdgeKind.References })
            .Build();

        var report = TestImpactAnalyzer.AnalyzeSymbol(graph, "type:Acme.Foo");

        Assert.Empty(report.AffectedTestClasses);
    }

    [Fact]
    public void AnalyzeSymbol_RecognizesUnityTestAndXUnitFact()
    {
        // [UnityTest] (Unity Test Framework) and [Fact] (xUnit) both
        // mark test methods — same surface as [Test] (NUnit).
        var graph = new GraphBuilder()
            .AddSymbol(Type("type:Acme.Foo", "Foo", "src/Foo.cs"))
            .AddSymbol(Type("type:Acme.Tests.UnityFooTests", "UnityFooTests", "tests/UnityFooTests.cs"))
            .AddSymbol(Type("type:Acme.Tests.XunitFooTests", "XunitFooTests", "tests/XunitFooTests.cs"))
            .AddSymbol(Method("method:Acme.Tests.UnityFooTests.Play()", "Play", "type:Acme.Tests.UnityFooTests", "tests/UnityFooTests.cs", "UnityTest"))
            .AddSymbol(Method("method:Acme.Tests.XunitFooTests.X()", "X", "type:Acme.Tests.XunitFooTests", "tests/XunitFooTests.cs", "Fact"))
            .AddEdge(new Edge { SourceId = "method:Acme.Tests.UnityFooTests.Play()", TargetId = "type:Acme.Foo", Kind = EdgeKind.References })
            .AddEdge(new Edge { SourceId = "method:Acme.Tests.XunitFooTests.X()", TargetId = "type:Acme.Foo", Kind = EdgeKind.References })
            .Build();

        var report = TestImpactAnalyzer.AnalyzeSymbol(graph, "type:Acme.Foo");

        Assert.Equal(2, report.AffectedTestClasses.Length);
        Assert.Equal(2, report.DirectTestClassCount);
    }

    [Fact]
    public void AnalyzeSymbol_RecommendedFiltersAreInDistanceOrder()
    {
        // Direct caller listed BEFORE transitive caller, regardless of
        // alphabetical order.
        var graph = new GraphBuilder()
            .AddSymbol(Type("type:Acme.Foo", "Foo", "src/Foo.cs"))
            .AddSymbol(Type("type:Acme.Service", "Service", "src/Service.cs"))
            .AddSymbol(Type("type:Acme.Tests.AaaTests", "AaaTests", "tests/AaaTests.cs"))
            .AddSymbol(Type("type:Acme.Tests.ZzzTests", "ZzzTests", "tests/ZzzTests.cs"))
            .AddSymbol(Method("method:Acme.Tests.AaaTests.T()", "T", "type:Acme.Tests.AaaTests", "tests/AaaTests.cs", "Test"))
            .AddSymbol(Method("method:Acme.Tests.ZzzTests.T()", "T", "type:Acme.Tests.ZzzTests", "tests/ZzzTests.cs", "Test"))
            // AaaTests is the transitive caller (depth 2).
            .AddEdge(new Edge { SourceId = "type:Acme.Service", TargetId = "type:Acme.Foo", Kind = EdgeKind.References })
            .AddEdge(new Edge { SourceId = "method:Acme.Tests.AaaTests.T()", TargetId = "type:Acme.Service", Kind = EdgeKind.References })
            // ZzzTests is the direct caller (depth 1).
            .AddEdge(new Edge { SourceId = "method:Acme.Tests.ZzzTests.T()", TargetId = "type:Acme.Foo", Kind = EdgeKind.References })
            .Build();

        var report = TestImpactAnalyzer.AnalyzeSymbol(graph, "type:Acme.Foo");

        Assert.Equal(new[] { "ZzzTests", "AaaTests" },
            report.AffectedTestClasses.Select(c => c.Name).ToArray());
        Assert.Equal("FullyQualifiedName~Acme.Tests.ZzzTests", report.RecommendedFilters[0]);
        Assert.Equal("FullyQualifiedName~Acme.Tests.AaaTests", report.RecommendedFilters[1]);
    }

    [Fact]
    public void AnalyzeFile_MultiSourceBFS_AggregatesAcrossSymbols()
    {
        // File `src/Foo.cs` contains TWO types. A test references one,
        // a different test references the other. File mode unions
        // both test classes as impact.
        var graph = new GraphBuilder()
            .AddSymbol(Type("type:Acme.FooA", "FooA", "src/Foo.cs"))
            .AddSymbol(Type("type:Acme.FooB", "FooB", "src/Foo.cs"))
            .AddSymbol(Type("type:Acme.Tests.FooATests", "FooATests", "tests/FooATests.cs"))
            .AddSymbol(Type("type:Acme.Tests.FooBTests", "FooBTests", "tests/FooBTests.cs"))
            .AddSymbol(Method("method:Acme.Tests.FooATests.T()", "T", "type:Acme.Tests.FooATests", "tests/FooATests.cs", "Test"))
            .AddSymbol(Method("method:Acme.Tests.FooBTests.T()", "T", "type:Acme.Tests.FooBTests", "tests/FooBTests.cs", "Test"))
            .AddEdge(new Edge { SourceId = "method:Acme.Tests.FooATests.T()", TargetId = "type:Acme.FooA", Kind = EdgeKind.References })
            .AddEdge(new Edge { SourceId = "method:Acme.Tests.FooBTests.T()", TargetId = "type:Acme.FooB", Kind = EdgeKind.References })
            .Build();

        var report = TestImpactAnalyzer.AnalyzeFile(graph, "src/Foo.cs");

        Assert.Equal(TestImpactTargetKind.File, report.TargetKind);
        Assert.Equal(2, report.AffectedTestClasses.Length);
        Assert.Equal(2, report.DirectTestClassCount);
    }
}

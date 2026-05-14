using Lifeblood.Analysis;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Xunit;

namespace Lifeblood.Tests;

public class TierClassifierTests
{
    [Fact]
    public void Classify_PureLeaf_IsPure()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "mod:Domain", Name = "Domain", Kind = SymbolKind.Module })
            .Build();

        var tiers = TierClassifier.Classify(graph);
        var domain = tiers.First(t => t.SymbolId == "mod:Domain");
        Assert.Equal(ArchitectureTier.Pure, domain.Tier);
    }

    [Fact]
    public void Classify_BoundaryModule_IsBoundary()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "mod:Domain", Name = "Domain", Kind = SymbolKind.Module })
            .AddSymbol(new Symbol { Id = "mod:App", Name = "App", Kind = SymbolKind.Module })
            .AddSymbol(new Symbol { Id = "mod:Infra", Name = "Infra", Kind = SymbolKind.Module })
            .AddEdge(new Edge { SourceId = "mod:App", TargetId = "mod:Domain", Kind = EdgeKind.DependsOn })
            .AddEdge(new Edge { SourceId = "mod:Infra", TargetId = "mod:App", Kind = EdgeKind.DependsOn })
            .Build();

        var tiers = TierClassifier.Classify(graph);
        var app = tiers.First(t => t.SymbolId == "mod:App");
        Assert.Equal(ArchitectureTier.Boundary, app.Tier);
    }

    [Fact]
    public void Classify_RuntimeModule_IsRuntime()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "mod:Domain", Name = "Domain", Kind = SymbolKind.Module })
            .AddSymbol(new Symbol { Id = "mod:Infra", Name = "Infra", Kind = SymbolKind.Module })
            .AddEdge(new Edge { SourceId = "mod:Infra", TargetId = "mod:Domain", Kind = EdgeKind.DependsOn })
            .Build();

        var tiers = TierClassifier.Classify(graph);
        var infra = tiers.First(t => t.SymbolId == "mod:Infra");
        Assert.Equal(ArchitectureTier.Runtime, infra.Tier);
    }

    [Fact]
    public void Classify_TestModule_IsTooling()
    {
        // Tooling detection is semantic: the test module contains a Type
        // whose method carries a [Test] attribute. The extractor populates
        // Properties["attributes"] from Roslyn's ISymbol.GetAttributes() —
        // we feed the same shape here.
        //
        // Pre-fix this test passed solely because the module's Name
        // contained "Test" as a substring, which also matched unrelated
        // types like Testable / Manifest / LatestState. The post-fix
        // semantic catches a real test fixture and rejects the
        // false-positives — see Classify_NameContainsTestSubstringButNoTestMember_NotTooling.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "mod:Domain.Tests", Name = "Domain.Tests", Kind = SymbolKind.Module })
            .AddSymbol(new Symbol { Id = "mod:Domain", Name = "Domain", Kind = SymbolKind.Module })
            .AddSymbol(new Symbol { Id = "type:Domain.Tests.FooTests", Name = "FooTests", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol
            {
                Id = "method:Domain.Tests.FooTests.It_Works()",
                Name = "It_Works",
                Kind = SymbolKind.Method,
                ParentId = "type:Domain.Tests.FooTests",
                Properties = new Dictionary<string, string> { ["attributes"] = "Test" },
            })
            .AddEdge(new Edge { SourceId = "mod:Domain.Tests", TargetId = "type:Domain.Tests.FooTests", Kind = EdgeKind.Contains })
            .AddEdge(new Edge { SourceId = "type:Domain.Tests.FooTests", TargetId = "method:Domain.Tests.FooTests.It_Works()", Kind = EdgeKind.Contains })
            .AddEdge(new Edge { SourceId = "mod:Domain.Tests", TargetId = "mod:Domain", Kind = EdgeKind.DependsOn })
            .Build();

        var tiers = TierClassifier.Classify(graph);
        var tests = tiers.First(t => t.SymbolId == "mod:Domain.Tests");
        Assert.Equal(ArchitectureTier.Tooling, tests.Tier);
    }

    [Fact]
    public void Classify_TypeWithTestMethod_IsTooling()
    {
        // A type owning a [Test]-attributed method is itself a test
        // fixture and classifies as Tooling without the Module wrapper.
        // Pins the per-Type semantic so a future change can't silently
        // restrict the classifier to Module-only.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:Domain.Tests.FooTests", Name = "FooTests", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:Domain.Foo", Name = "Foo", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol
            {
                Id = "method:Domain.Tests.FooTests.T()",
                Name = "T",
                Kind = SymbolKind.Method,
                ParentId = "type:Domain.Tests.FooTests",
                Properties = new Dictionary<string, string> { ["attributes"] = "Test" },
            })
            .AddEdge(new Edge { SourceId = "type:Domain.Tests.FooTests", TargetId = "method:Domain.Tests.FooTests.T()", Kind = EdgeKind.Contains })
            // Outgoing non-Contains edge so the Pure short-circuit doesn't fire.
            .AddEdge(new Edge { SourceId = "type:Domain.Tests.FooTests", TargetId = "type:Domain.Foo", Kind = EdgeKind.References })
            .Build();

        var tiers = TierClassifier.Classify(graph);
        var fooTests = tiers.First(t => t.SymbolId == "type:Domain.Tests.FooTests");
        Assert.Equal(ArchitectureTier.Tooling, fooTests.Tier);
    }

    [Fact]
    public void Classify_NameContainsTestSubstringButNoTestMember_NotTooling()
    {
        // The pre-fix false-positive landmine: a Type named `Testable` /
        // `Manifest` / `LatestState` matched `Name.Contains("Test")`
        // and got bucketed as Tooling despite having zero test methods.
        // Post-fix the classifier reads attribute markers, not name
        // substrings — so a regular production type with "Test" in its
        // name is correctly classified by its dependency shape.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:Domain.Testable", Name = "Testable", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:Domain.Foo", Name = "Foo", Kind = SymbolKind.Type })
            // Outgoing non-Contains edge so Pure doesn't fire; no
            // incoming edge so Runtime wins over Boundary. The test
            // doesn't care which non-Tooling tier the classifier picks,
            // only that Tooling does NOT.
            .AddEdge(new Edge { SourceId = "type:Domain.Testable", TargetId = "type:Domain.Foo", Kind = EdgeKind.References })
            .Build();

        var tiers = TierClassifier.Classify(graph);
        var testable = tiers.First(t => t.SymbolId == "type:Domain.Testable");
        Assert.NotEqual(ArchitectureTier.Tooling, testable.Tier);
    }

    [Fact]
    public void Classify_LifecycleAttributesAlone_NotTooling()
    {
        // A type whose only attributed method is [SetUp] / [OneTimeSetUp]
        // (NUnit lifecycle, not a test case) must NOT classify as Tooling.
        // Lifecycle attributes participate in fixture execution but are
        // not assertion-bearing test cases. Mirrors the policy
        // TestImpactAnalyzer follows for "which tests touch X".
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:Domain.Helper", Name = "Helper", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:Domain.Foo", Name = "Foo", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol
            {
                Id = "method:Domain.Helper.Init()",
                Name = "Init",
                Kind = SymbolKind.Method,
                ParentId = "type:Domain.Helper",
                Properties = new Dictionary<string, string> { ["attributes"] = "SetUp" },
            })
            .AddEdge(new Edge { SourceId = "type:Domain.Helper", TargetId = "method:Domain.Helper.Init()", Kind = EdgeKind.Contains })
            .AddEdge(new Edge { SourceId = "type:Domain.Helper", TargetId = "type:Domain.Foo", Kind = EdgeKind.References })
            .Build();

        var tiers = TierClassifier.Classify(graph);
        var helper = tiers.First(t => t.SymbolId == "type:Domain.Helper");
        Assert.NotEqual(ArchitectureTier.Tooling, helper.Tier);
    }
}

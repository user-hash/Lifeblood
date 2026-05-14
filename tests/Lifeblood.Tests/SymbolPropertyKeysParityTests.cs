using System.Collections.Generic;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Analysis;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// BUG-4 regression net (LB-FOLLOWUP-004 / INV-PROPERTY-KEY-PARITY-001).
///
/// Pin the writer ↔ reader contract for <see cref="Symbol.Properties"/>:
///   * <see cref="RoslynSymbolExtractor"/> writes the attributes payload
///     at key <see cref="SymbolPropertyKeys.Attributes"/>.
///   * <see cref="TierClassifier"/>, <see cref="TestImpactAnalyzer"/>,
///     and <see cref="UnityReachabilityAdapter"/> read at the same key.
///
/// BUG-4 was the failure mode where the reader checked an
/// extractor-never-written key (<c>"isTooling"</c>) and the entire
/// Tooling-tier classification silently degraded to false. The
/// behavioral fact below catches the same shape automatically: if any
/// reader drifts off the canonical key, this test fires before the
/// regression reaches a release.
/// </summary>
public class SymbolPropertyKeysParityTests
{
    private static readonly Evidence Ev = new()
    {
        Kind = EvidenceKind.Semantic,
        AdapterName = "test",
        Confidence = ConfidenceLevel.Proven,
    };

    [Fact]
    public void AttributesKey_ConstantValue_IsAttributes()
    {
        // Pre-existing graph corpora (every cached graph.json on disk)
        // already key the attributes payload under the literal string
        // "attributes". The constant is the canonical reference for new
        // code, but the literal value MUST not change — that would
        // invalidate every cached extractor output in the wild.
        Assert.Equal("attributes", SymbolPropertyKeys.Attributes);
    }

    [Fact]
    public void AttributesKey_AllThreeReaders_RecognizeExtractorPayload()
    {
        // Build one minimal graph using SymbolPropertyKeys.Attributes
        // (the exact constant the extractor writes), then drive every
        // attributes-aware reader against it and assert each fires. If
        // any reader drifts off the canonical key, exactly one of the
        // three asserts below flips — pinpointing the regression.
        var testMethod = new Symbol
        {
            Id = "method:Acme.FooTests.Bar()",
            Name = "Bar",
            QualifiedName = "Acme.FooTests.Bar",
            Kind = SymbolKind.Method,
            ParentId = "type:Acme.FooTests",
            Visibility = Visibility.Internal,
            Properties = new Dictionary<string, string>
            {
                [SymbolPropertyKeys.Attributes] = "Test",
            },
        };
        // Reader 1: TierClassifier — Tooling tier fires on test-attribute
        // descendant. TierClassifier short-circuits to Pure when the
        // checked symbol itself has no outgoing non-Contains edge, so the
        // fixture adds an outgoing References edge from the type (matches
        // the pattern in Classify_TypeWithTestMethod_IsTooling).
        var graphWithOutgoing = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:Acme.FooTests", Name = "FooTests", Kind = SymbolKind.Type, FilePath = "tests/FooTests.cs" })
            .AddSymbol(new Symbol { Id = "type:Sut", Name = "Sut", Kind = SymbolKind.Type })
            .AddSymbol(testMethod)
            .AddEdge(new Edge { SourceId = "type:Acme.FooTests", TargetId = "method:Acme.FooTests.Bar()", Kind = EdgeKind.Contains, Evidence = Ev })
            .AddEdge(new Edge { SourceId = "type:Acme.FooTests", TargetId = "type:Sut", Kind = EdgeKind.References, Evidence = Ev })
            .Build();
        var tiers = TierClassifier.Classify(graphWithOutgoing);
        var fooTestsTier = tiers.First(t => t.SymbolId == "type:Acme.FooTests");
        Assert.Equal(ArchitectureTier.Tooling, fooTestsTier.Tier);

        // Reader 2: TestImpactAnalyzer — BFS walks incoming non-Contains
        // edges from the target, classifies hits as test methods via the
        // extractor-written attributes key. Build a SUT type the test
        // method Calls, then ask "what tests depend on type:Sut?" — the
        // walker must recognize Bar's [Test] marker to count it.
        var impactGraph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:Acme.FooTests", Name = "FooTests", QualifiedName = "Acme.FooTests", Kind = SymbolKind.Type, FilePath = "tests/FooTests.cs" })
            .AddSymbol(new Symbol { Id = "type:Sut", Name = "Sut", QualifiedName = "Acme.Sut", Kind = SymbolKind.Type })
            .AddSymbol(testMethod)
            .AddEdge(new Edge { SourceId = "type:Acme.FooTests", TargetId = "method:Acme.FooTests.Bar()", Kind = EdgeKind.Contains, Evidence = Ev })
            .AddEdge(new Edge { SourceId = "method:Acme.FooTests.Bar()", TargetId = "type:Sut", Kind = EdgeKind.Calls, Evidence = Ev })
            .Build();
        var impact = TestImpactAnalyzer.AnalyzeSymbol(impactGraph, "type:Sut");
        Assert.True(impact.TotalTestMethodCount >= 1,
            "TestImpactAnalyzer must recognize the [Test] attribute on the extractor-written key.");

        // Reader 3: UnityReachabilityAdapter — non-Unity test attributes
        // are NOT entrypoints, so use a Unity entrypoint attribute (the
        // adapter shares the same key/parsing seam — drift on the key
        // would manifest identically). One graph, two attribute sets:
        // the parity is on the key, not the attribute set.
        var unitySym = new Symbol
        {
            Id = "method:App.Boot.Init()",
            Name = "Init",
            Kind = SymbolKind.Method,
            ParentId = "type:App.Boot",
            Properties = new Dictionary<string, string>
            {
                [SymbolPropertyKeys.Attributes] = "RuntimeInitializeOnLoadMethod",
            },
        };
        var unityGraph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:App.Boot", Name = "Boot", QualifiedName = "App.Boot", Kind = SymbolKind.Type })
            .AddSymbol(unitySym)
            .Build();
        var adapter = new UnityReachabilityAdapter();
        Assert.True(adapter.IsRuntimeReachable(unityGraph, unitySym, out _),
            "UnityReachabilityAdapter must recognize entrypoint attributes on the extractor-written key.");
    }
}

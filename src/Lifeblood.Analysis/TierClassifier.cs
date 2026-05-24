using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Analysis;

/// <summary>
/// Classifies symbols into architecture tiers: Pure, Boundary, Runtime, Tooling.
/// Tooling detection is semantic: a Type qualifies if any of its directly-owned
/// methods carries an extractor-recorded test attribute (NUnit / Unity Test
/// Framework / xUnit); a Module qualifies if any of its contained types
/// qualifies under the same rule. The classifier reads
/// <see cref="Symbol.Properties"/> <c>"attributes"</c> which the extractor
/// populates from Roslyn's <c>ISymbol.GetAttributes()</c> at extraction time —
/// the Roslyn-canonical "this method is a test case" answer, not a
/// name-substring guess. INV-ANALYSIS-001: Stateless. INV-ANALYSIS-002: Read-only.
/// </summary>
public static class TierClassifier
{
    /// <summary>
    /// Method-level attribute names that mark a method as a test case.
    /// Kept in lock-step with <c>TestImpactAnalyzer.TestCaseAttributes</c>;
    /// they serve different policies (test fixture detection vs. dispatch-
    /// entrypoint detection) but the recognized attribute set is identical
    /// today. Consolidation into a shared
    /// <c>Lifeblood.Analysis.TestAttributeNames</c> is its own atom — split
    /// pending a third caller surfacing.
    /// </summary>
    private static readonly HashSet<string> TestCaseAttributes = new(StringComparer.Ordinal)
    {
        "Test",
        "TestCase",
        "TestCaseSource",
        "Theory",
        "UnityTest",
        "Fact",
        "Xunit.Fact",
        "Xunit.Theory",
    };

    public static TierAssignment[] Classify(SemanticGraph graph)
    {
        var results = new List<TierAssignment>();

        foreach (var symbol in graph.Symbols)
        {
            if (symbol.Kind != SymbolKind.Module && symbol.Kind != SymbolKind.Type)
                continue;

            var tier = ClassifySymbol(graph, symbol);
            results.Add(new TierAssignment
            {
                SymbolId = symbol.Id,
                Tier = tier.tier,
                Reason = tier.reason,
            });
        }

        return results.ToArray();
    }

    private static (ArchitectureTier tier, string reason) ClassifySymbol(SemanticGraph graph, Symbol symbol)
    {
        bool hasOutgoing = false;
        bool hasIncoming = false;

        foreach (int idx in graph.GetOutgoingEdgeIndexes(symbol.Id))
        {
            if (graph.Edges[idx].Kind != EdgeKind.Contains)
            {
                hasOutgoing = true;
                break;
            }
        }

        foreach (int idx in graph.GetIncomingEdgeIndexes(symbol.Id))
        {
            if (graph.Edges[idx].Kind != EdgeKind.Contains)
            {
                hasIncoming = true;
                break;
            }
        }

        // Pure: no outgoing non-Contains edges (leaf node)
        if (!hasOutgoing)
            return (ArchitectureTier.Pure, "No outgoing dependencies");

        // Tooling: the symbol (or any of its Contains-descendants for a
        // Module) carries an extractor-recorded test attribute. The
        // attribute set is populated by the extractor from Roslyn's
        // ISymbol.GetAttributes() — same canonical source TestImpactAnalyzer
        // and UnityReachabilityAdapter read for their own test-detection
        // policies. INV-TEST-IMPACT-001 (shared evidence shape).
        if (HasTestCaseDescendant(graph, symbol))
            return (ArchitectureTier.Tooling, "Contains a method tagged with a test-framework attribute");

        // Boundary: has both incoming and outgoing (mediator)
        if (hasIncoming && hasOutgoing)
            return (ArchitectureTier.Boundary, "Has both incoming and outgoing dependencies");

        // Runtime: has outgoing but no incoming
        return (ArchitectureTier.Runtime, "Has outgoing dependencies, no dependants");
    }

    /// <summary>
    /// Return true iff <paramref name="root"/> itself or any of its
    /// transitively-contained members carries an extractor-recorded test-case
    /// attribute. Walk is BFS over outgoing Contains edges, bounded by a
    /// visited set so a malformed graph with a Contains cycle can't loop
    /// forever. Methods are leaf candidates; types and modules act as
    /// containers. The set of recognized attribute names matches
    /// <see cref="TestCaseAttributes"/>.
    /// </summary>
    private static bool HasTestCaseDescendant(SemanticGraph graph, Symbol root)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { root.Id };
        var queue = new Queue<string>();
        queue.Enqueue(root.Id);

        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            var current = graph.GetSymbol(currentId);
            if (current == null) continue;

            if (current.Kind == SymbolKind.Method && IsTestCase(current))
                return true;

            foreach (var idx in graph.GetOutgoingEdgeIndexes(currentId))
            {
                var edge = graph.Edges[idx];
                if (edge.Kind != EdgeKind.Contains) continue;
                if (visited.Add(edge.TargetId))
                    queue.Enqueue(edge.TargetId);
            }
        }

        return false;
    }

    /// <summary>
    /// Return true iff <paramref name="method"/>'s
    /// <see cref="Symbol.Properties"/> <c>"attributes"</c> entry, parsed as
    /// the semicolon-separated set the extractor emits, contains any name in
    /// <see cref="TestCaseAttributes"/>. Lifecycle attributes (<c>SetUp</c>,
    /// <c>OneTimeSetUp</c>, <c>TearDown</c>, <c>OneTimeTearDown</c>,
    /// <c>UnitySetUp</c>, <c>UnityTearDown</c>) are intentionally NOT
    /// recognized — they participate in fixture lifecycle but are not the
    /// assertion-bearing test cases the classifier counts as "this is a
    /// test fixture."
    /// </summary>
    private static bool IsTestCase(Symbol method)
    {
        if (method.Properties == null) return false;
        if (!method.Properties.TryGetValue(SymbolPropertyKeys.Attributes, out var attrs) || string.IsNullOrEmpty(attrs))
            return false;
        foreach (var name in attrs.Split(';'))
            if (TestCaseAttributes.Contains(name))
                return true;
        return false;
    }
}

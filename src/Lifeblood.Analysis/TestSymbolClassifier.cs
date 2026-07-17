using Lifeblood.Domain.Graph;

namespace Lifeblood.Analysis;

/// <summary>
/// One Analysis-owned authority for identifying assertion-bearing test methods
/// in the semantic graph. Coverage projections and test-impact traversal share
/// this classifier instead of drifting attribute vocabularies.
/// </summary>
internal static class TestSymbolClassifier
{
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

    internal static bool IsTestMethod(Symbol symbol)
    {
        if (symbol.Kind != SymbolKind.Method || symbol.Properties == null) return false;
        if (!symbol.Properties.TryGetValue(SymbolPropertyKeys.Attributes, out var attributes)
            || string.IsNullOrEmpty(attributes))
            return false;
        return attributes.Split(';').Any(TestCaseAttributes.Contains);
    }

    internal static bool IsTestSymbol(SemanticGraph graph, string? symbolId)
    {
        if (string.IsNullOrEmpty(symbolId)) return false;
        var symbol = graph.GetSymbol(symbolId);
        if (symbol == null) return false;
        if (IsTestMethod(symbol)) return true;

        var containingTypeId = FindContainingTypeId(graph, symbol);
        if (containingTypeId == null) return false;
        foreach (var edgeIndex in graph.GetOutgoingEdgeIndexes(containingTypeId))
        {
            var edge = graph.Edges[edgeIndex];
            if (edge.Kind != EdgeKind.Contains) continue;
            var child = graph.GetSymbol(edge.TargetId);
            if (child != null && IsTestMethod(child)) return true;
        }
        return false;
    }

    private static string? FindContainingTypeId(SemanticGraph graph, Symbol symbol)
    {
        if (symbol.Kind == SymbolKind.Type) return symbol.Id;
        var seen = new HashSet<string>(StringComparer.Ordinal) { symbol.Id };
        var parentId = symbol.ParentId;
        while (!string.IsNullOrEmpty(parentId) && seen.Add(parentId))
        {
            var parent = graph.GetSymbol(parentId);
            if (parent == null) return null;
            if (parent.Kind == SymbolKind.Type) return parent.Id;
            parentId = parent.ParentId;
        }
        return null;
    }
}

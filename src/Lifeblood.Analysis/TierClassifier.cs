using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Analysis;

/// <summary>
/// Classifies symbols into architecture tiers: Pure, Boundary, Runtime, Tooling.
/// Based on dependency direction and external reference patterns.
/// INV-ANALYSIS-001: Stateless. INV-ANALYSIS-002: Read-only.
/// </summary>
public static class TierClassifier
{
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

        // Tooling: check for test/tool markers in name or properties
        if (symbol.Name.Contains("Test", StringComparison.OrdinalIgnoreCase)
            || symbol.Name.Contains("Tool", StringComparison.OrdinalIgnoreCase)
            || symbol.Properties.ContainsKey("isTooling"))
            return (ArchitectureTier.Tooling, "Test or tooling marker detected");

        // Boundary: has both incoming and outgoing (mediator)
        if (hasIncoming && hasOutgoing)
            return (ArchitectureTier.Boundary, "Has both incoming and outgoing dependencies");

        // Runtime: has outgoing but no incoming
        return (ArchitectureTier.Runtime, "Has outgoing dependencies, no dependants");
    }
}

using Lifeblood.Domain.Capabilities;

namespace Lifeblood.Domain.Graph;

/// <summary>
/// Constructs a SemanticGraph with proper containment hierarchy.
/// Synthesizes Contains edges from Symbol.ParentId relationships.
/// Deduplicates symbols by ID (last-write-wins policy).
/// Sorts output deterministically (INV-PIPE-001).
/// </summary>
public sealed class GraphBuilder
{
    private readonly Dictionary<string, Symbol> _symbols = new(StringComparer.Ordinal);
    private readonly List<Edge> _edges = new();

    /// <summary>
    /// Adds a symbol. If a symbol with the same ID already exists, it is replaced
    /// (last-write-wins). This handles partial types: multiple declarations of the
    /// same type produce the same ID, and the last one seen wins. Adapters are
    /// responsible for merging partial declarations before calling AddSymbol if
    /// they need richer merge semantics.
    /// </summary>
    public GraphBuilder AddSymbol(Symbol symbol)
    {
        _symbols[symbol.Id] = symbol;
        return this;
    }

    public GraphBuilder AddSymbols(IEnumerable<Symbol> symbols)
    {
        foreach (var s in symbols) _symbols[s.Id] = s;
        return this;
    }

    public GraphBuilder AddEdge(Edge edge)
    {
        _edges.Add(edge);
        return this;
    }

    public GraphBuilder AddEdges(IEnumerable<Edge> edges)
    {
        _edges.AddRange(edges);
        return this;
    }

    /// <summary>
    /// Builds the graph. Synthesizes Contains edges from Symbol.ParentId where
    /// both parent and child exist and no explicit Contains edge already connects them.
    /// </summary>
    public SemanticGraph Build()
    {
        var allEdges = new List<Edge>(_edges.Count + _symbols.Count);
        var containsPairs = new HashSet<(string, string)>();

        // Index existing Contains edges to avoid duplicates
        foreach (var edge in _edges)
        {
            allEdges.Add(edge);
            if (edge.Kind == EdgeKind.Contains)
                containsPairs.Add((edge.SourceId, edge.TargetId));
        }

        // Synthesize Contains edges from ParentId
        foreach (var symbol in _symbols.Values)
        {
            if (string.IsNullOrEmpty(symbol.ParentId)) continue;
            if (!_symbols.ContainsKey(symbol.ParentId)) continue;
            if (containsPairs.Contains((symbol.ParentId, symbol.Id))) continue;

            allEdges.Add(new Edge
            {
                SourceId = symbol.ParentId,
                TargetId = symbol.Id,
                Kind = EdgeKind.Contains,
                Evidence = new Evidence
                {
                    Kind = EvidenceKind.Inferred,
                    AdapterName = "GraphBuilder",
                    Confidence = ConfidenceLevel.Proven,
                },
            });
            containsPairs.Add((symbol.ParentId, symbol.Id));
        }

        // INV-PIPE-001: Deterministic output. Sort canonically by ID/source+target
        // so identical input always produces identical output regardless of
        // dictionary iteration order or file discovery order.
        var sortedSymbols = _symbols.Values
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .ToArray();

        var sortedEdges = allEdges
            .OrderBy(e => e.SourceId, StringComparer.Ordinal)
            .ThenBy(e => e.TargetId, StringComparer.Ordinal)
            .ThenBy(e => e.Kind)
            .ToArray();

        return new SemanticGraph
        {
            Symbols = sortedSymbols,
            Edges = sortedEdges,
        };
    }
}

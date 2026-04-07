namespace Lifeblood.Core.Graph;

/// <summary>
/// The unified semantic graph. Language-agnostic representation of an entire codebase.
/// Built by GraphBuilder from adapter ParseResults, consumed by all analyzers.
///
/// INV-CORE-003: All analysis algorithms operate on this, never on source code.
/// INV-ANALYSIS-002: Analyzers do not modify the graph. Analysis is read-only.
/// </summary>
public sealed class SemanticGraph
{
    public Symbol[] Symbols { get; init; } = Array.Empty<Symbol>();
    public Edge[] Edges { get; init; } = Array.Empty<Edge>();

    // ── Indexes (built once, used by all analyzers) ──

    private Dictionary<string, Symbol>? _symbolById;
    private Dictionary<string, List<int>>? _outgoingEdges; // symbolId → edge indexes
    private Dictionary<string, List<int>>? _incomingEdges; // symbolId → edge indexes

    /// <summary>Look up a symbol by ID. O(1) after first call.</summary>
    public Symbol? GetSymbol(string id)
    {
        EnsureIndexes();
        return _symbolById!.TryGetValue(id, out var s) ? s : null;
    }

    /// <summary>All edges where the given symbol is the source (outgoing dependencies).</summary>
    public ReadOnlySpan<int> GetOutgoingEdgeIndexes(string symbolId)
    {
        EnsureIndexes();
        return _outgoingEdges!.TryGetValue(symbolId, out var list)
            ? System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list)
            : ReadOnlySpan<int>.Empty;
    }

    /// <summary>All edges where the given symbol is the target (incoming dependants).</summary>
    public ReadOnlySpan<int> GetIncomingEdgeIndexes(string symbolId)
    {
        EnsureIndexes();
        return _incomingEdges!.TryGetValue(symbolId, out var list)
            ? System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list)
            : ReadOnlySpan<int>.Empty;
    }

    /// <summary>All symbols of a given kind.</summary>
    public IEnumerable<Symbol> SymbolsOfKind(SymbolKind kind)
    {
        for (int i = 0; i < Symbols.Length; i++)
            if (Symbols[i].Kind == kind)
                yield return Symbols[i];
    }

    /// <summary>All edges of a given kind.</summary>
    public IEnumerable<Edge> EdgesOfKind(EdgeKind kind)
    {
        for (int i = 0; i < Edges.Length; i++)
            if (Edges[i].Kind == kind)
                yield return Edges[i];
    }

    /// <summary>Direct children of a symbol (via Contains edges).</summary>
    public IEnumerable<Symbol> ChildrenOf(string symbolId)
    {
        EnsureIndexes();
        foreach (int idx in GetOutgoingEdgeIndexes(symbolId))
        {
            if (Edges[idx].Kind == EdgeKind.Contains)
            {
                var child = GetSymbol(Edges[idx].TargetId);
                if (child != null) yield return child;
            }
        }
    }

    private void EnsureIndexes()
    {
        if (_symbolById != null) return;

        _symbolById = new Dictionary<string, Symbol>(Symbols.Length, StringComparer.Ordinal);
        _outgoingEdges = new Dictionary<string, List<int>>(Symbols.Length, StringComparer.Ordinal);
        _incomingEdges = new Dictionary<string, List<int>>(Symbols.Length, StringComparer.Ordinal);

        for (int i = 0; i < Symbols.Length; i++)
            _symbolById[Symbols[i].Id] = Symbols[i];

        for (int i = 0; i < Edges.Length; i++)
        {
            if (!_outgoingEdges.TryGetValue(Edges[i].SourceId, out var outList))
            {
                outList = new List<int>(4);
                _outgoingEdges[Edges[i].SourceId] = outList;
            }
            outList.Add(i);

            if (!_incomingEdges.TryGetValue(Edges[i].TargetId, out var inList))
            {
                inList = new List<int>(4);
                _incomingEdges[Edges[i].TargetId] = inList;
            }
            inList.Add(i);
        }
    }
}

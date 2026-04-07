namespace Lifeblood.Domain.Graph;

/// <summary>
/// The universal semantic graph. Language-agnostic.
/// INV-GRAPH-004: Read-only after construction. Analyzers do not modify it.
/// </summary>
public sealed class SemanticGraph
{
    public Symbol[] Symbols { get; init; } = Array.Empty<Symbol>();
    public Edge[] Edges { get; init; } = Array.Empty<Edge>();

    private Dictionary<string, Symbol>? _symbolById;
    private Dictionary<string, List<int>>? _outgoing;
    private Dictionary<string, List<int>>? _incoming;

    public Symbol? GetSymbol(string id)
    {
        EnsureIndexes();
        return _symbolById!.TryGetValue(id, out var s) ? s : null;
    }

    public ReadOnlySpan<int> GetOutgoingEdgeIndexes(string symbolId)
    {
        EnsureIndexes();
        return _outgoing!.TryGetValue(symbolId, out var list)
            ? System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list)
            : ReadOnlySpan<int>.Empty;
    }

    public ReadOnlySpan<int> GetIncomingEdgeIndexes(string symbolId)
    {
        EnsureIndexes();
        return _incoming!.TryGetValue(symbolId, out var list)
            ? System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list)
            : ReadOnlySpan<int>.Empty;
    }

    public IEnumerable<Symbol> SymbolsOfKind(SymbolKind kind)
    {
        for (int i = 0; i < Symbols.Length; i++)
            if (Symbols[i].Kind == kind)
                yield return Symbols[i];
    }

    public IEnumerable<Symbol> ChildrenOf(string symbolId)
    {
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
        _symbolById = new(Symbols.Length, StringComparer.Ordinal);
        _outgoing = new(Symbols.Length, StringComparer.Ordinal);
        _incoming = new(Symbols.Length, StringComparer.Ordinal);

        for (int i = 0; i < Symbols.Length; i++)
            _symbolById[Symbols[i].Id] = Symbols[i];

        for (int i = 0; i < Edges.Length; i++)
        {
            AddToIndex(_outgoing, Edges[i].SourceId, i);
            AddToIndex(_incoming, Edges[i].TargetId, i);
        }
    }

    private static void AddToIndex(Dictionary<string, List<int>> idx, string key, int value)
    {
        if (!idx.TryGetValue(key, out var list))
        {
            list = new List<int>(4);
            idx[key] = list;
        }
        list.Add(value);
    }
}

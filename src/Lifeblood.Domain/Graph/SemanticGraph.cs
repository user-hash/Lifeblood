namespace Lifeblood.Domain.Graph;

/// <summary>
/// The universal semantic graph. Language-agnostic.
/// INV-GRAPH-004: Read-only after construction. Analyzers do not modify it.
/// </summary>
public sealed class SemanticGraph
{
    public Symbol[] Symbols { get; init; } = Array.Empty<Symbol>();
    public Edge[] Edges { get; init; } = Array.Empty<Edge>();

    private volatile GraphIndexes? _indexes;

    public Symbol? GetSymbol(string id)
    {
        var idx = GetIndexes();
        return idx.SymbolById.TryGetValue(id, out var s) ? s : null;
    }

    public ReadOnlySpan<int> GetOutgoingEdgeIndexes(string symbolId)
    {
        var idx = GetIndexes();
        return idx.Outgoing.TryGetValue(symbolId, out var list)
            ? System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list)
            : ReadOnlySpan<int>.Empty;
    }

    public ReadOnlySpan<int> GetIncomingEdgeIndexes(string symbolId)
    {
        var idx = GetIndexes();
        return idx.Incoming.TryGetValue(symbolId, out var list)
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

    private GraphIndexes GetIndexes()
    {
        var existing = _indexes;
        if (existing != null) return existing;

        var built = BuildIndexes();
        Interlocked.CompareExchange(ref _indexes, built, null);
        return _indexes!;
    }

    private GraphIndexes BuildIndexes()
    {
        var symbolById = new Dictionary<string, Symbol>(Symbols.Length, StringComparer.Ordinal);
        var outgoing = new Dictionary<string, List<int>>(Symbols.Length, StringComparer.Ordinal);
        var incoming = new Dictionary<string, List<int>>(Symbols.Length, StringComparer.Ordinal);

        for (int i = 0; i < Symbols.Length; i++)
            symbolById[Symbols[i].Id] = Symbols[i];

        for (int i = 0; i < Edges.Length; i++)
        {
            AddToIndex(outgoing, Edges[i].SourceId, i);
            AddToIndex(incoming, Edges[i].TargetId, i);
        }

        return new GraphIndexes(symbolById, outgoing, incoming);
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

    private sealed class GraphIndexes(
        Dictionary<string, Symbol> symbolById,
        Dictionary<string, List<int>> outgoing,
        Dictionary<string, List<int>> incoming)
    {
        public Dictionary<string, Symbol> SymbolById { get; } = symbolById;
        public Dictionary<string, List<int>> Outgoing { get; } = outgoing;
        public Dictionary<string, List<int>> Incoming { get; } = incoming;
    }
}

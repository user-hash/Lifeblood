namespace Lifeblood.Domain.Graph;

/// <summary>
/// The universal semantic graph. Language-agnostic.
/// INV-GRAPH-004: Read-only after construction. Analyzers do not modify it.
/// </summary>
public sealed class SemanticGraph
{
    private readonly Symbol[] _symbols;
    private readonly Edge[] _edges;

    public SemanticGraph()
    {
        _symbols = Array.Empty<Symbol>();
        _edges = Array.Empty<Edge>();
    }

    internal SemanticGraph(Symbol[] symbols, Edge[] edges)
    {
        _symbols = symbols;
        _edges = edges;
    }

    public IReadOnlyList<Symbol> Symbols => _symbols;
    public IReadOnlyList<Edge> Edges => _edges;

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
        for (int i = 0; i < _symbols.Length; i++)
            if (_symbols[i].Kind == kind)
                yield return _symbols[i];
    }

    /// <summary>
    /// Find every symbol whose <see cref="Symbol.Name"/> equals <paramref name="name"/>
    /// (case-insensitive). Returns an empty list when no symbol matches.
    ///
    /// Backed by a lazily-built short-name index. Used by <c>ISymbolResolver</c>'s
    /// short-name resolution path so users can query by short type/method name
    /// without knowing the canonical fully-qualified ID. See INV-RESOLVER-002 in
    /// CLAUDE.md.
    /// </summary>
    public IReadOnlyList<Symbol> FindByShortName(string name)
    {
        var idx = GetIndexes();
        return idx.SymbolsByShortName.TryGetValue(name, out var list)
            ? list
            : (IReadOnlyList<Symbol>)Array.Empty<Symbol>();
    }

    public List<Symbol> ChildrenOf(string symbolId)
    {
        var children = new List<Symbol>();
        var indexes = GetIndexes();
        if (!indexes.Outgoing.TryGetValue(symbolId, out var edgeIndexes))
            return children;

        for (int i = 0; i < edgeIndexes.Count; i++)
        {
            int idx = edgeIndexes[i];
            if (_edges[idx].Kind == EdgeKind.Contains)
            {
                var child = GetSymbol(_edges[idx].TargetId);
                if (child != null) children.Add(child);
            }
        }
        return children;
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
        var symbolById = new Dictionary<string, Symbol>(_symbols.Length, StringComparer.Ordinal);
        var outgoing = new Dictionary<string, List<int>>(_symbols.Length, StringComparer.Ordinal);
        var incoming = new Dictionary<string, List<int>>(_symbols.Length, StringComparer.Ordinal);
        // Short-name index: case-insensitive bucket of every symbol whose Name
        // matches a short identifier the user might type. Multiple symbols can
        // share a short name across namespaces, so the value is a list, not
        // a single Symbol. Built once, lazily, alongside the other indexes.
        var symbolsByShortName = new Dictionary<string, List<Symbol>>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < _symbols.Length; i++)
        {
            symbolById[_symbols[i].Id] = _symbols[i];

            var shortName = _symbols[i].Name;
            if (!string.IsNullOrEmpty(shortName))
            {
                if (!symbolsByShortName.TryGetValue(shortName, out var bucket))
                {
                    bucket = new List<Symbol>(2);
                    symbolsByShortName[shortName] = bucket;
                }
                bucket.Add(_symbols[i]);
            }
        }

        for (int i = 0; i < _edges.Length; i++)
        {
            AddToIndex(outgoing, _edges[i].SourceId, i);
            AddToIndex(incoming, _edges[i].TargetId, i);
        }

        return new GraphIndexes(symbolById, outgoing, incoming, symbolsByShortName);
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
        Dictionary<string, List<int>> incoming,
        Dictionary<string, List<Symbol>> symbolsByShortName)
    {
        public Dictionary<string, Symbol> SymbolById { get; } = symbolById;
        public Dictionary<string, List<int>> Outgoing { get; } = outgoing;
        public Dictionary<string, List<int>> Incoming { get; } = incoming;
        public Dictionary<string, List<Symbol>> SymbolsByShortName { get; } = symbolsByShortName;
    }
}

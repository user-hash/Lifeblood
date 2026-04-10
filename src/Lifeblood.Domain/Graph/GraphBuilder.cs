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

    // Tracks every distinct ParentId observed for a given symbol id across
    // multiple AddSymbol calls. Required for partial-type unification:
    // partial classes/structs/interfaces produce one Symbol record per
    // declaration site, all with the same Id but DIFFERENT ParentId values
    // (one per containing file). Last-write-wins on _symbols loses every
    // ParentId except the most recent — but the resolver needs ALL of them
    // to enumerate every partial declaration file. We accumulate the
    // (id → set-of-parent-ids) mapping here and synthesize one Contains
    // edge per unique parent in Build(). See INV-RESOLVER-003 in CLAUDE.md.
    private readonly Dictionary<string, HashSet<string>> _allParentIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Adds a symbol. If a symbol with the same ID already exists, the
    /// SYMBOL RECORD is replaced (last-write-wins) but the symbol's
    /// <see cref="Symbol.ParentId"/> is also recorded into a per-id parent
    /// set so partial-type declarations can be reconstructed in <see cref="Build"/>.
    /// </summary>
    public GraphBuilder AddSymbol(Symbol symbol)
    {
        _symbols[symbol.Id] = symbol;
        TrackParent(symbol);
        return this;
    }

    public GraphBuilder AddSymbols(IEnumerable<Symbol> symbols)
    {
        foreach (var s in symbols)
        {
            _symbols[s.Id] = s;
            TrackParent(s);
        }
        return this;
    }

    private void TrackParent(Symbol symbol)
    {
        if (string.IsNullOrEmpty(symbol.ParentId)) return;
        if (symbol.ParentId == symbol.Id) return;
        if (!_allParentIds.TryGetValue(symbol.Id, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            _allParentIds[symbol.Id] = set;
        }
        set.Add(symbol.ParentId);
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
    /// Derives file-level References edges from symbol-level edges between different files.
    /// </summary>
    public SemanticGraph Build()
    {
        var allEdges = new List<Edge>(_edges.Count + _symbols.Count);
        var containsPairs = new HashSet<(string, string)>();

        // Only include edges where both source and target exist as symbols.
        // Dangling edges (e.g., references to external System.* types) are dropped —
        // they inflate coupling metrics and pollute cycle detection.
        foreach (var edge in _edges)
        {
            if (!_symbols.ContainsKey(edge.SourceId) || !_symbols.ContainsKey(edge.TargetId))
                continue;

            allEdges.Add(edge);
            if (edge.Kind == EdgeKind.Contains)
                containsPairs.Add((edge.SourceId, edge.TargetId));
        }

        // Synthesize Contains edges from EVERY observed ParentId, not just the
        // last-write-wins value on the surviving Symbol record. For partial
        // types this produces one (file → type) Contains edge per partial
        // declaration file — the resolver walks these incoming edges to
        // reconstruct the full DeclarationFilePaths read model in
        // SymbolResolutionResult. See INV-RESOLVER-003 in CLAUDE.md.
        foreach (var (childId, parentIds) in _allParentIds)
        {
            if (!_symbols.ContainsKey(childId)) continue;

            foreach (var parentId in parentIds)
            {
                if (parentId == childId) continue; // self-reference guard
                if (!_symbols.ContainsKey(parentId)) continue;
                if (containsPairs.Contains((parentId, childId))) continue;

                allEdges.Add(new Edge
                {
                    SourceId = parentId,
                    TargetId = childId,
                    Kind = EdgeKind.Contains,
                    Evidence = new Evidence
                    {
                        Kind = EvidenceKind.Inferred,
                        AdapterName = "GraphBuilder",
                        Confidence = ConfidenceLevel.Proven,
                    },
                });
                containsPairs.Add((parentId, childId));
            }
        }

        // INV-PIPE-001: Deterministic output. Sort canonically by ID/source+target
        // so identical input always produces identical output regardless of
        // dictionary iteration order or file discovery order.
        var sortedSymbols = _symbols.Values
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .ToArray();

        // Deduplicate all edges by (source, target, kind).
        // Partial classes cause the same edge (especially Overrides, Inherits, Implements)
        // to be emitted once per partial file — typeSymbol.GetMembers() returns all members
        // including from other partial declarations, and each file's Extract() call has
        // its own dedup set. The builder is the single source of truth for deduplication.
        var dedupedEdges = new Dictionary<(string, string, EdgeKind), Edge>();
        foreach (var edge in allEdges)
        {
            var key = (edge.SourceId, edge.TargetId, edge.Kind);
            dedupedEdges.TryAdd(key, edge); // first-write-wins, consistent with symbol dedup
        }

        // Derive file-level edges from symbol-level edges.
        // For each non-Contains edge between symbols in different files,
        // accumulate a file→file References edge with an edgeCount property.
        // Evidence: Inferred (derived truth, not primary).
        DeriveFileEdges(dedupedEdges);

        var sortedEdges = dedupedEdges.Values
            .OrderBy(e => e.SourceId, StringComparer.Ordinal)
            .ThenBy(e => e.TargetId, StringComparer.Ordinal)
            .ThenBy(e => e.Kind)
            .ToArray();

        return new SemanticGraph(sortedSymbols, sortedEdges);
    }

    private void DeriveFileEdges(Dictionary<(string, string, EdgeKind), Edge> dedupedEdges)
    {
        // Build symbolId → fileId reverse index using Symbol.FilePath.
        // This is more accurate than walking ParentId for partial classes,
        // where a type's ParentId points to one file but members live in others.
        var symbolToFile = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var symbol in _symbols.Values)
        {
            if (symbol.Kind == SymbolKind.File)
            {
                symbolToFile[symbol.Id] = symbol.Id;
            }
            else if (!string.IsNullOrEmpty(symbol.FilePath))
            {
                var fileId = "file:" + symbol.FilePath.Replace('\\', '/');
                if (_symbols.ContainsKey(fileId))
                    symbolToFile[symbol.Id] = fileId;
            }
        }

        // Accumulate cross-file edge counts
        var fileEdgeCounts = new Dictionary<(string sourceFileId, string targetFileId), int>();
        foreach (var edge in dedupedEdges.Values)
        {
            if (edge.Kind == EdgeKind.Contains) continue;

            if (!symbolToFile.TryGetValue(edge.SourceId, out var srcFile)) continue;
            if (!symbolToFile.TryGetValue(edge.TargetId, out var tgtFile)) continue;
            if (string.Equals(srcFile, tgtFile, StringComparison.Ordinal)) continue;

            var key = (srcFile, tgtFile);
            fileEdgeCounts.TryGetValue(key, out var count);
            fileEdgeCounts[key] = count + 1;
        }

        // Emit file-level References edges
        foreach (var ((src, tgt), count) in fileEdgeCounts)
        {
            var edgeKey = (src, tgt, EdgeKind.References);
            dedupedEdges.TryAdd(edgeKey, new Edge
            {
                SourceId = src,
                TargetId = tgt,
                Kind = EdgeKind.References,
                Evidence = new Evidence
                {
                    Kind = EvidenceKind.Inferred,
                    AdapterName = "GraphBuilder",
                    Confidence = ConfidenceLevel.Proven,
                },
                Properties = new Dictionary<string, string>
                {
                    ["edgeCount"] = count.ToString(),
                },
            });
        }
    }
}

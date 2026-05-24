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

        // Deduplicate all edges by semantic identity. Edges with the same
        // source, target, and coarse EdgeKind can still carry distinct roles
        // (for example native parameterType + returnType edges from one C
        // function to the same struct), so edge properties participate through
        // EdgeIdentity.
        // Partial classes cause the same edge (especially Overrides, Inherits, Implements)
        // to be emitted once per partial file — typeSymbol.GetMembers() returns all members
        // including from other partial declarations, and each file's Extract() call has
        // its own dedup set. The builder is the single source of truth for deduplication.
        // INV-MULTI-DEFINE-EDGE-PROFILES-001. Dedup keeps first-write-wins for
        // every field EXCEPT Profiles[]: when the same (source, target, kind)
        // edge is observed under multiple define profiles, UNION the profile
        // sets so the merged edge carries every contributing profile name.
        // Single-profile analyze: every edge has Profiles=null, union is null.
        var dedupedEdges = new Dictionary<EdgeIdentityKey, Edge>();
        foreach (var edge in allEdges)
        {
            var key = EdgeIdentity.KeyFor(edge);
            if (dedupedEdges.TryGetValue(key, out var existing))
            {
                var merged = EdgeProfileMerger.MergeProfiles(existing, edge);
                if (!ReferenceEquals(merged, existing))
                    dedupedEdges[key] = merged;
            }
            else
            {
                dedupedEdges[key] = edge;
            }
        }

        // Derive file-level edges from symbol-level edges.
        // For each non-Contains edge between symbols in different files,
        // accumulate a file→file References edge with an edgeCount property.
        // Evidence: Inferred (derived truth, not primary).
        FileEdgeDeriver.AddDerivedFileEdges(_symbols, dedupedEdges);

        var sortedEdges = dedupedEdges.Values
            .OrderBy(e => e.SourceId, StringComparer.Ordinal)
            .ThenBy(e => e.TargetId, StringComparer.Ordinal)
            .ThenBy(e => e.Kind)
            .ToArray();

        return new SemanticGraph(sortedSymbols, sortedEdges);
    }

}

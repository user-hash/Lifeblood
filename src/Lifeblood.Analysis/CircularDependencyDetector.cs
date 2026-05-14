using Lifeblood.Domain.Graph;
using Lifeblood.Domain.PathClassification;
using Lifeblood.Domain.Results;

namespace Lifeblood.Analysis;

/// <summary>
/// Detects cycles in the dependency graph using Tarjan's SCC algorithm.
/// Only considers non-Contains edges. INV-ANALYSIS-002: Read-only.
/// </summary>
public static class CircularDependencyDetector
{
    /// <summary>
    /// Classify the cycles produced by <see cref="Detect"/> into triage
    /// buckets. Walks each SCC once, peeks at participating symbols'
    /// file paths and Contains-chain ancestry, assigns one
    /// <see cref="CycleBucket"/> per cycle. Precedence rules live on the
    /// enum's doc comment. Pure read of the graph; never mutates.
    /// INV-CYCLE-TAXONOMY-001 / LB-TRACK-20260514-008.
    /// </summary>
    public static CycleDescriptor[] DetectClassified(SemanticGraph graph)
    {
        var cycles = Detect(graph);
        if (cycles.Length == 0) return Array.Empty<CycleDescriptor>();

        var results = new CycleDescriptor[cycles.Length];
        for (int i = 0; i < cycles.Length; i++)
            results[i] = Classify(graph, cycles[i]);
        return results;
    }

    /// <summary>
    /// Classify one cycle. Returns the most authoritative matching
    /// bucket; precedence Generated &gt; Partial &gt; LikelyReal.
    /// </summary>
    private static CycleDescriptor Classify(SemanticGraph graph, string[] cycleSymbols)
    {
        // Bucket 1 (highest precedence): generated / static-analysis
        // artifact — any cycle member's file path matches the
        // generated-code pattern set. Short-circuits on first hit.
        for (int i = 0; i < cycleSymbols.Length; i++)
        {
            var sym = graph.GetSymbol(cycleSymbols[i]);
            if (sym != null && PathBucketClassifier.IsGenerated(sym.FilePath))
                return new CycleDescriptor
                {
                    Symbols = cycleSymbols,
                    Bucket = CycleBucket.GeneratedOrStaticAnalysisArtifact,
                };
        }

        // Bucket 2: partial-class cluster — every member of the SCC
        // resolves to a non-empty set of "candidate enclosing types"
        // and the intersection across every member is non-empty. The
        // candidate set per member captures the Roslyn-canonical
        // partial-class signature (INamedTypeSymbol.DeclaringSyntaxReferences
        // pointing at multiple files) using the graph's existing
        // Contains-edge encoding:
        //
        //   Type member:                  { self.Id }
        //   Method / Field / Property:    { walk up Contains to first Type ancestor }
        //   File member:                  { every Type the file declares via outgoing Contains }
        //
        // The set-intersection shape generalizes correctly across all
        // SCC compositions Lifeblood observes empirically:
        //   * method ↔ method cycles inside one partial type (the
        //     original case the classifier was written for)
        //   * file ↔ file cycles where every file is a partial decl
        //     of the same type (the case the file-symbol classifier
        //     used to miss — pre-fix every such cycle bucketed as
        //     LikelyRealLoop because the walk-up returned null on
        //     File roots)
        //   * mixed-kind SCCs that happen to share an enclosing type
        // INV-CYCLE-TAXONOMY-001.
        HashSet<string>? sharedEnclosingTypes = null;
        for (int i = 0; i < cycleSymbols.Length; i++)
        {
            var candidates = EnclosingTypesOf(graph, cycleSymbols[i]);
            if (candidates.Count == 0) { sharedEnclosingTypes = null; break; }
            if (sharedEnclosingTypes == null)
            {
                sharedEnclosingTypes = candidates;
            }
            else
            {
                sharedEnclosingTypes.IntersectWith(candidates);
                if (sharedEnclosingTypes.Count == 0) break;
            }
        }
        if (sharedEnclosingTypes != null && sharedEnclosingTypes.Count > 0)
            return new CycleDescriptor
            {
                Symbols = cycleSymbols,
                Bucket = CycleBucket.PartialClassCluster,
            };

        return new CycleDescriptor
        {
            Symbols = cycleSymbols,
            Bucket = CycleBucket.LikelyRealLoop,
        };
    }

    /// <summary>
    /// Compute the set of Type symbol ids that could "enclose"
    /// <paramref name="symbolId"/> for partial-class classification.
    /// Three shapes per <see cref="SymbolKind"/>:
    ///
    ///   <see cref="SymbolKind.Type"/>:           the type itself.
    ///   Method / Field / Property / Event:        the unique Type
    ///     ancestor reached by walking incoming Contains edges (the
    ///     symbol's containing type). Empty set when the symbol is
    ///     file-scope or namespace-scope and has no Type ancestor.
    ///   <see cref="SymbolKind.File"/>:           every Type the file
    ///     declares, surfaced via outgoing Contains edges. Files are
    ///     graph roots — they have no Contains-parent — so the
    ///     pre-fix incoming-edge walk returned null for every file
    ///     node and the classifier missed every file-level
    ///     partial-class SCC. INV-CYCLE-TAXONOMY-001 (post-fix).
    ///   Other kinds (Module, Namespace, Parameter, etc.): empty.
    ///
    /// Cycle-safe via a per-call visited set. Returns a fresh mutable
    /// HashSet because <see cref="Classify"/> intersects it in place.
    /// </summary>
    private static HashSet<string> EnclosingTypesOf(SemanticGraph graph, string symbolId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var current = graph.GetSymbol(symbolId);
        if (current == null) return result;

        if (current.Kind == SymbolKind.Type)
        {
            result.Add(current.Id);
            return result;
        }

        if (current.Kind == SymbolKind.File)
        {
            // Files don't have a single enclosing type — they may
            // declare zero, one, or many types. Surface every Type the
            // file declares so a file-SCC where every file declares
            // the same partial type still produces a non-empty
            // intersection across cycle members.
            foreach (var idx in graph.GetOutgoingEdgeIndexes(current.Id))
            {
                var edge = graph.Edges[idx];
                if (edge.Kind != EdgeKind.Contains) continue;
                var target = graph.GetSymbol(edge.TargetId);
                if (target?.Kind == SymbolKind.Type)
                    result.Add(target.Id);
            }
            return result;
        }

        // Members (Method / Field / Property / Event / etc.). Walk
        // up Contains edges until we hit a Type ancestor; capture
        // that single ancestor. Bounded by the visited set so a
        // malformed graph with a Contains cycle can't loop forever.
        var visited = new HashSet<string>(StringComparer.Ordinal) { current.Id };
        var cursor = current;
        while (true)
        {
            string? parentId = null;
            foreach (var idx in graph.GetIncomingEdgeIndexes(cursor.Id))
            {
                var edge = graph.Edges[idx];
                if (edge.Kind == EdgeKind.Contains)
                {
                    parentId = edge.SourceId;
                    break;
                }
            }
            if (parentId == null || !visited.Add(parentId)) return result;

            var parent = graph.GetSymbol(parentId);
            if (parent == null) return result;
            if (parent.Kind == SymbolKind.Type)
            {
                result.Add(parent.Id);
                return result;
            }
            cursor = parent;
        }
    }

    public static string[][] Detect(SemanticGraph graph)
    {
        // Build adjacency from non-Contains edges
        var adj = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var symbol in graph.Symbols)
            adj[symbol.Id] = new List<string>();

        foreach (var edge in graph.Edges)
        {
            if (edge.Kind == EdgeKind.Contains) continue;
            if (adj.ContainsKey(edge.SourceId))
                adj[edge.SourceId].Add(edge.TargetId);
        }

        // Tarjan's SCC
        int index = 0;
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowlinks = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var sccs = new List<string[]>();

        foreach (var v in adj.Keys)
        {
            if (!indices.ContainsKey(v))
                StrongConnect(v, adj, ref index, indices, lowlinks, onStack, stack, sccs);
        }

        // Only return SCCs with more than one member (actual cycles)
        return sccs.Where(scc => scc.Length > 1).ToArray();
    }

    private static void StrongConnect(
        string v,
        Dictionary<string, List<string>> adj,
        ref int index,
        Dictionary<string, int> indices,
        Dictionary<string, int> lowlinks,
        HashSet<string> onStack,
        Stack<string> stack,
        List<string[]> sccs)
    {
        indices[v] = index;
        lowlinks[v] = index;
        index++;
        stack.Push(v);
        onStack.Add(v);

        if (adj.TryGetValue(v, out var neighbors))
        {
            foreach (var w in neighbors)
            {
                if (!indices.ContainsKey(w))
                {
                    StrongConnect(w, adj, ref index, indices, lowlinks, onStack, stack, sccs);
                    lowlinks[v] = Math.Min(lowlinks[v], lowlinks[w]);
                }
                else if (onStack.Contains(w))
                {
                    lowlinks[v] = Math.Min(lowlinks[v], indices[w]);
                }
            }
        }

        if (lowlinks[v] == indices[v])
        {
            var scc = new List<string>();
            string w;
            do
            {
                w = stack.Pop();
                onStack.Remove(w);
                scc.Add(w);
            } while (w != v);

            sccs.Add(scc.ToArray());
        }
    }
}

using Lifeblood.Domain.Graph;
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
            if (sym != null && IsGeneratedOrStaticAnalysisPath(sym.FilePath))
                return new CycleDescriptor
                {
                    Symbols = cycleSymbols,
                    Bucket = CycleBucket.GeneratedOrStaticAnalysisArtifact,
                };
        }

        // Bucket 2: partial-class cluster — every member resolves up
        // the Contains-chain to the same enclosing Type symbol. Captures
        // intra-type mutual-recursion / method-pair cycles (the SCC
        // surfaces them but they're not architectural loops).
        string? sharedEnclosingType = null;
        bool allShareEnclosingType = true;
        for (int i = 0; i < cycleSymbols.Length; i++)
        {
            var enclosing = WalkUpToEnclosingType(graph, cycleSymbols[i]);
            if (enclosing == null) { allShareEnclosingType = false; break; }
            if (sharedEnclosingType == null) sharedEnclosingType = enclosing;
            else if (!string.Equals(enclosing, sharedEnclosingType, StringComparison.Ordinal))
            { allShareEnclosingType = false; break; }
        }
        if (allShareEnclosingType && sharedEnclosingType != null)
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
    /// True when <paramref name="filePath"/> looks like build artifact
    /// or source-generator output. Segment-aware on the lowercase
    /// POSIX-normalized form so a folder named <c>obj</c> at the root
    /// matches the same as a nested <c>/obj/</c>, and a filename
    /// containing the word "generated" does not trigger unless the
    /// dotted pattern matches. Mirrors the policy already enforced in
    /// <c>LifebloodDeadCodeAnalyzer.ClassifyBucket</c>'s Generated tier
    /// (INV-DEADCODE-TRIAGE-001). The duplicate logic is intentional
    /// for now: extracting a shared <c>Lifeblood.Analysis.PathBucketClassifier</c>
    /// across the dead-code analyzer, MCP provider, and this detector
    /// is its own atom (three current callers + drifted definitions —
    /// see <c>LifebloodMcpProvider.ClassifyBucket</c> for the older
    /// substring-based variant).
    /// </summary>
    private static bool IsGeneratedOrStaticAnalysisPath(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        var lower = filePath.Replace('\\', '/').ToLowerInvariant();

        if (lower.EndsWith(".g.cs", StringComparison.Ordinal)) return true;
        if (lower.Contains(".generated.")) return true;

        foreach (var segment in lower.Split('/'))
            if (segment == "obj" || segment == "bin" || segment == "generated")
                return true;

        return false;
    }

    /// <summary>
    /// Walk <paramref name="symbolId"/> up the Contains-chain to the
    /// first ancestor whose <see cref="Symbol.Kind"/> is
    /// <see cref="SymbolKind.Type"/>. Returns the type's id, or the
    /// original symbol's id if it already IS a Type, or null when no
    /// type ancestor exists (file-scope members, namespace-scope, etc.).
    /// Cycle-safe via a visited-set bounded to the graph's incoming-edge
    /// fanout.
    /// </summary>
    private static string? WalkUpToEnclosingType(SemanticGraph graph, string symbolId)
    {
        var current = graph.GetSymbol(symbolId);
        if (current == null) return null;
        if (current.Kind == SymbolKind.Type) return current.Id;

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
            if (parentId == null || !visited.Add(parentId)) return null;

            var parent = graph.GetSymbol(parentId);
            if (parent == null) return null;
            if (parent.Kind == SymbolKind.Type) return parent.Id;
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

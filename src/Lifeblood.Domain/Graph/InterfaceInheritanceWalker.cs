namespace Lifeblood.Domain.Graph;

/// <summary>
/// Transitive walk over interface inheritance edges. Shared by analyzers
/// that need the composite / aggregate interface surface
/// (<c>port_health</c>, <c>authority_report</c>). Implements the read-side
/// semantic that pairs with INV-EXTRACT-IFACE-INHERIT-001 (extractor side).
///
/// Edge-kind semantics (post-F3c extractor):
/// <list type="bullet">
/// <item><c>interface : interface</c> emits <see cref="EdgeKind.Inherits"/>
///   (one interface extending another is inheritance, not implementation).</item>
/// <item><c>class : interface</c> and <c>struct : interface</c> emit
///   <see cref="EdgeKind.Implements"/>.</item>
/// <item><c>class : class</c> base type emits <see cref="EdgeKind.Inherits"/>.</item>
/// </list>
/// </summary>
public static class InterfaceInheritanceWalker
{
    /// <summary>
    /// Transitive outgoing <see cref="EdgeKind.Inherits"/> walk starting at
    /// <paramref name="typeId"/>. Returns the distinct set of inherited
    /// interface canonical ids, sorted ordinal so the wire shape is
    /// deterministic. Excludes the start node itself.
    ///
    /// Intended for interface sources — on a class source this returns
    /// the transitive base-class chain (which is also an Inherits walk).
    /// Callers that need only interface-typed targets should filter by
    /// <c>Symbol.Properties["typeKind"] == "interface"</c> on each result.
    /// </summary>
    public static string[] CollectTransitiveInherited(SemanticGraph graph, string typeId)
    {
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(typeId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (int idx in graph.GetOutgoingEdgeIndexes(current))
            {
                var edge = graph.Edges[idx];
                if (edge.Kind != EdgeKind.Inherits) continue;
                if (!seen.Add(edge.TargetId)) continue;
                queue.Enqueue(edge.TargetId);
            }
        }
        var arr = new string[seen.Count];
        int i = 0;
        foreach (var id in seen) arr[i++] = id;
        System.Array.Sort(arr, System.StringComparer.Ordinal);
        return arr;
    }

    /// <summary>
    /// Distinct outgoing edges representing "interface this type directly
    /// satisfies": <see cref="EdgeKind.Implements"/> for class/struct sources
    /// and <see cref="EdgeKind.Inherits"/> for interface sources. Sorted
    /// ordinal for deterministic output.
    ///
    /// Branches on SOURCE typeKind rather than filtering by target kind:
    /// each source kind has exactly one valid edge kind for "interfaces I
    /// directly satisfy" (post-F3c extractor invariant), so no target lookup
    /// is needed. Sources without a <c>typeKind</c> property (hand-built
    /// test graphs, older JSON imports) default to <see cref="EdgeKind.Implements"/>
    /// — matches the pre-F3c semantic and preserves backward compatibility.
    /// </summary>
    public static string[] CollectDirectInterfaceContracts(SemanticGraph graph, string typeId)
    {
        var source = graph.GetSymbol(typeId);
        var sourceIsInterface = source?.Properties != null
            && source.Properties.TryGetValue("typeKind", out var sk)
            && string.Equals(sk, "interface", System.StringComparison.Ordinal);
        var walkKind = sourceIsInterface ? EdgeKind.Inherits : EdgeKind.Implements;

        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (int idx in graph.GetOutgoingEdgeIndexes(typeId))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind != walkKind) continue;
            seen.Add(edge.TargetId);
        }
        var arr = new string[seen.Count];
        int i = 0;
        foreach (var id in seen) arr[i++] = id;
        System.Array.Sort(arr, System.StringComparer.Ordinal);
        return arr;
    }
}

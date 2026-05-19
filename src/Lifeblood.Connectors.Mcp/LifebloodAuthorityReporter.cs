using Lifeblood.Application.Ports.Right;
using Lifeblood.Domain.Graph;

namespace Lifeblood.Connectors.Mcp;

/// <summary>
/// Reference implementation of <see cref="IAuthorityReporter"/>.
/// Single graph walk produces every field of <see cref="AuthorityReport"/>;
/// the analyzer holds no state between calls.
///
/// Lives in <c>Lifeblood.Connectors.Mcp</c> because <c>Lifeblood.Analysis</c>
/// has no Application-port dependency. INV-CONN-001.
/// </summary>
public sealed class LifebloodAuthorityReporter : IAuthorityReporter
{
    public AuthorityReport Analyze(SemanticGraph graph, string typeId)
    {
        var typeSym = graph.GetSymbol(typeId);
        if (typeSym == null || typeSym.Kind != SymbolKind.Type)
        {
            return new AuthorityReport
            {
                TypeId = typeId,
                ForwarderRatio = -1.0,
            };
        }

        // 1. Implemented interfaces — outgoing edges from the type itself
        //    to interface-typed targets. Walks Implements (class/struct →
        //    interface) AND Inherits (interface extending interface, post-F3c)
        //    and filters by target typeKind so the metric semantic is
        //    "interfaces this type directly satisfies" regardless of source
        //    kind. NOT method-level Implements edges.
        var distinctIfaces = InterfaceInheritanceWalker
            .CollectDirectInterfaceContracts(graph, typeId);

        // 2. Owned public surface — Contains edges from the type to
        //    public-visibility members (Method/Property/Field/Event,
        //    nested types excluded). Also collects every method to
        //    drive forwarder-ratio counting in step 4.
        int ownedPublicSurface = 0;
        int totalMethodCount = 0;
        int pureForwarderCount = 0;
        bool sawAnyClassification = false;

        foreach (int idx in graph.GetOutgoingEdgeIndexes(typeId))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind != EdgeKind.Contains) continue;
            var member = graph.GetSymbol(edge.TargetId);
            if (member == null) continue;
            if (member.Kind == SymbolKind.Type) continue; // nested type; not a member
            if (member.Kind == SymbolKind.Method ||
                member.Kind == SymbolKind.Property ||
                member.Kind == SymbolKind.Field)
            {
                if (member.Visibility == Visibility.Public) ownedPublicSurface++;
            }
            if (member.Kind == SymbolKind.Method)
            {
                totalMethodCount++;
                if (member.Properties != null
                    && member.Properties.TryGetValue("classification", out var cls))
                {
                    sawAnyClassification = true;
                    if (string.Equals(cls, "PureForwarder", System.StringComparison.Ordinal))
                        pureForwarderCount++;
                }
            }
        }

        // 3. Per-interface breakdown — direct + inherited member surface
        //    (composite traversal via the shared Domain walker, F3e).
        //    Consumers reach the interface or any of its members across
        //    the aggregate set when composite. INV-AUTHORITY-COMPOSITE-001.
        var perInterface = new System.Collections.Generic.List<InterfaceUsage>(distinctIfaces.Length);
        foreach (var ifaceId in distinctIfaces)
        {
            // Direct: members the interface itself declares.
            int directMembers = CountDirectMembers(graph, ifaceId);

            // Inherited: transitive Inherits closure; sum each parent's
            // direct member count. Distinct interface ids by construction
            // (the walker dedups); member ids could in principle repeat
            // across diamond inheritance, but C# disallows that at the
            // signature level so distinct-by-iface is sufficient here.
            var inheritedIfaces = InterfaceInheritanceWalker
                .CollectTransitiveInherited(graph, ifaceId);
            int inheritedMembers = 0;
            foreach (var parentId in inheritedIfaces)
                inheritedMembers += CountDirectMembers(graph, parentId);

            int aggregateMembers = directMembers + inheritedMembers;

            // Consumers: incoming Calls / References into the interface
            // itself or into any of its direct members. Walk inherited
            // parents the same way — a caller of an inherited member
            // is reaching the composite contract too.
            var consumers = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
            CollectIncoming(graph, ifaceId, consumers);
            CollectMemberConsumers(graph, ifaceId, consumers);
            foreach (var parentId in inheritedIfaces)
            {
                CollectIncoming(graph, parentId, consumers);
                CollectMemberConsumers(graph, parentId, consumers);
            }

            var ifaceSym = graph.GetSymbol(ifaceId);
            perInterface.Add(new InterfaceUsage
            {
                InterfaceId = ifaceId,
                InterfaceName = ifaceSym?.Name ?? StripTypePrefix(ifaceId),
                DirectMemberCount = directMembers,
                InheritedMemberCount = inheritedMembers,
                AggregateMemberCount = aggregateMembers,
                MemberCount = aggregateMembers, // backwards-compatible alias
                InheritedInterfaces = inheritedIfaces,
                IsCompositeInterface = inheritedMembers > 0,
                ConsumerCount = consumers.Count,
            });
        }

        // 4. Forwarder ratio — fall back to -1.0 sentinel when the
        //    extractor didn't record method classification (older
        //    snapshots / non-C# adapters).
        double forwarderRatio;
        if (!sawAnyClassification || totalMethodCount == 0)
            forwarderRatio = -1.0;
        else
            forwarderRatio = (double)pureForwarderCount / totalMethodCount;

        // 5. Planning-verdict evidence (S7, INV-AUTHORITY-PLANNING-COMPOSITION-001).
        //    Partition incoming consumers by owning module: same-module
        //    use is the adapter-shim shape, cross-module use is the
        //    boundary-contract shape. HasSingleImplementer surfaces
        //    interface targets whose contract has exactly one source
        //    implementer (candidate for direct concrete binding).
        var (crossAssembly, sameAssembly) = ClassifyConsumersByAssembly(graph, typeId);
        bool? hasSingleImplementer = ComputeHasSingleImplementer(graph, typeId);

        return new AuthorityReport
        {
            TypeId = typeId,
            ImplementedInterfaceCount = distinctIfaces.Length,
            OwnedPublicSurface = ownedPublicSurface,
            PerInterface = perInterface.ToArray(),
            ForwarderRatio = System.Math.Round(forwarderRatio, 3),
            TotalMethodCount = totalMethodCount,
            PureForwarderCount = pureForwarderCount,
            CrossAssemblyConsumerCount = crossAssembly,
            SameAssemblyConsumerCount = sameAssembly,
            HasSingleImplementer = hasSingleImplementer,
        };
    }

    /// <summary>
    /// Partition consumers (incoming Calls / References on the type and
    /// every direct member) by the source's owning module. Returns
    /// (crossAssemblyModuleCount, sameAssemblyConsumerCount) where the
    /// former counts DISTINCT modules other than the target's own and
    /// the latter counts DISTINCT consumer symbols within the same
    /// module. Returns (0, 0) when the target's own module cannot be
    /// resolved (defensive — graphs imported from JSON without module
    /// containment chains land here).
    /// </summary>
    private static (int CrossAssembly, int SameAssembly) ClassifyConsumersByAssembly(
        SemanticGraph graph, string typeId)
    {
        var ownModule = FindContainingModule(graph, typeId);
        if (ownModule == null) return (0, 0);

        var crossModules = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
        var sameAssemblySymbols = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);

        void Consume(string nodeId)
        {
            foreach (int idx in graph.GetIncomingEdgeIndexes(nodeId))
            {
                var edge = graph.Edges[idx];
                if (edge.Kind != EdgeKind.Calls && edge.Kind != EdgeKind.References) continue;
                var sourceModule = FindContainingModule(graph, edge.SourceId);
                if (sourceModule == null) continue;
                if (string.Equals(sourceModule, ownModule, System.StringComparison.Ordinal))
                    sameAssemblySymbols.Add(edge.SourceId);
                else
                    crossModules.Add(sourceModule);
            }
        }

        Consume(typeId);
        foreach (int idx in graph.GetOutgoingEdgeIndexes(typeId))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind != EdgeKind.Contains) continue;
            var member = graph.GetSymbol(edge.TargetId);
            if (member == null || member.Kind == SymbolKind.Type) continue;
            Consume(edge.TargetId);
        }

        return (crossModules.Count, sameAssemblySymbols.Count);
    }

    /// <summary>
    /// True iff the type is an interface AND exactly one source-defined
    /// type carries an outgoing <see cref="EdgeKind.Implements"/> edge
    /// into it. Null when the target is not an interface (no semantic
    /// concept of "single implementer" for class/struct/enum targets).
    /// </summary>
    private static bool? ComputeHasSingleImplementer(SemanticGraph graph, string typeId)
    {
        var sym = graph.GetSymbol(typeId);
        if (sym == null) return null;
        if (sym.Properties == null) return null;
        if (!sym.Properties.TryGetValue("typeKind", out var kind)) return null;
        if (!string.Equals(kind, "interface", System.StringComparison.Ordinal)) return null;

        var implementers = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
        foreach (int idx in graph.GetIncomingEdgeIndexes(typeId))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind != EdgeKind.Implements) continue;
            implementers.Add(edge.SourceId);
            if (implementers.Count > 1) return false; // short-circuit
        }
        return implementers.Count == 1;
    }

    /// <summary>
    /// Walk the <see cref="Symbol.ParentId"/> chain until a
    /// <see cref="SymbolKind.Module"/> node is reached. Returns its id,
    /// or null when no module ancestor exists (orphaned symbol, JSON-
    /// imported graph without containment chain). Hard depth cap
    /// (16 hops) guards against pathological cycles.
    /// </summary>
    private static string? FindContainingModule(SemanticGraph graph, string symbolId)
    {
        string cursor = symbolId;
        for (int hops = 0; hops < 16; hops++)
        {
            var sym = graph.GetSymbol(cursor);
            if (sym == null) return null;
            if (sym.Kind == SymbolKind.Module) return sym.Id;
            if (string.IsNullOrEmpty(sym.ParentId)) return null;
            cursor = sym.ParentId;
        }
        return null;
    }

    private static void CollectIncoming(
        SemanticGraph graph,
        string targetId,
        System.Collections.Generic.HashSet<string> sink)
    {
        foreach (int idx in graph.GetIncomingEdgeIndexes(targetId))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind != EdgeKind.Calls && edge.Kind != EdgeKind.References) continue;
            sink.Add(edge.SourceId);
        }
    }

    /// <summary>
    /// Count outgoing Contains edges that target a non-Type symbol —
    /// the interface's direct member declarations. Mirrors the pre-F3e
    /// per-interface walk; factored out so the composite traversal can
    /// reuse it for each parent without duplicating the filter logic.
    /// </summary>
    private static int CountDirectMembers(SemanticGraph graph, string typeId)
    {
        int count = 0;
        foreach (int idx in graph.GetOutgoingEdgeIndexes(typeId))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind != EdgeKind.Contains) continue;
            var member = graph.GetSymbol(edge.TargetId);
            if (member == null) continue;
            if (member.Kind == SymbolKind.Type) continue; // nested type
            count++;
        }
        return count;
    }

    /// <summary>
    /// Collect consumers reaching any direct member of <paramref name="typeId"/>
    /// via incoming Calls / References edges. Skips nested-type
    /// members for parity with <see cref="CountDirectMembers"/>.
    /// </summary>
    private static void CollectMemberConsumers(
        SemanticGraph graph,
        string typeId,
        System.Collections.Generic.HashSet<string> sink)
    {
        foreach (int idx in graph.GetOutgoingEdgeIndexes(typeId))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind != EdgeKind.Contains) continue;
            var member = graph.GetSymbol(edge.TargetId);
            if (member == null) continue;
            if (member.Kind == SymbolKind.Type) continue;
            CollectIncoming(graph, edge.TargetId, sink);
        }
    }

    private static string StripTypePrefix(string id)
        => id.StartsWith("type:", System.StringComparison.Ordinal) ? id.Substring(5) : id;
}

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

        return new AuthorityReport
        {
            TypeId = typeId,
            ImplementedInterfaceCount = distinctIfaces.Length,
            OwnedPublicSurface = ownedPublicSurface,
            PerInterface = perInterface.ToArray(),
            ForwarderRatio = System.Math.Round(forwarderRatio, 3),
            TotalMethodCount = totalMethodCount,
            PureForwarderCount = pureForwarderCount,
        };
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

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

        // 3. Per-interface breakdown — for each implemented interface,
        //    count its members (Contains edges from the interface) and
        //    consumers (incoming Calls edges into the interface).
        var perInterface = new System.Collections.Generic.List<InterfaceUsage>(distinctIfaces.Length);
        foreach (var ifaceId in distinctIfaces)
        {
            int members = 0;
            foreach (int idx in graph.GetOutgoingEdgeIndexes(ifaceId))
            {
                var edge = graph.Edges[idx];
                if (edge.Kind != EdgeKind.Contains) continue;
                var member = graph.GetSymbol(edge.TargetId);
                if (member == null) continue;
                if (member.Kind == SymbolKind.Type) continue; // nested type
                members++;
            }

            // Consumers: any incoming Calls / References edge into the
            // interface itself or into one of its members.
            var consumers = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
            CollectIncoming(graph, ifaceId, consumers);
            foreach (int idx in graph.GetOutgoingEdgeIndexes(ifaceId))
            {
                var edge = graph.Edges[idx];
                if (edge.Kind != EdgeKind.Contains) continue;
                CollectIncoming(graph, edge.TargetId, consumers);
            }

            var ifaceSym = graph.GetSymbol(ifaceId);
            perInterface.Add(new InterfaceUsage
            {
                InterfaceId = ifaceId,
                InterfaceName = ifaceSym?.Name ?? StripTypePrefix(ifaceId),
                MemberCount = members,
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

    private static string StripTypePrefix(string id)
        => id.StartsWith("type:", System.StringComparison.Ordinal) ? id.Substring(5) : id;
}

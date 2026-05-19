using Lifeblood.Domain.Graph;

namespace Lifeblood.Application.Ports.Right;

/// <summary>
/// Right-side analyzer that quantifies how much "authority" a type
/// holds in the architecture. The output answers a triage question
/// the host-extraction sessions on real Unity workspaces kept asking by hand: when a
/// host/owner type implements N interfaces, how much real surface
/// does it own vs how much is delegation? A port whose host
/// implements many interfaces but exposes only forwarder methods
/// is a candidate for splitting; a host with concentrated public
/// surface is doing real work and shouldn't be cut up.
///
/// Pure read port. Stateless per INV-ANALYSIS-001. See INV-AUTHORITY-001.
/// </summary>
public interface IAuthorityReporter
{
    AuthorityReport Analyze(SemanticGraph graph, string typeId);
}

/// <summary>
/// One authority report. Counts come from a single graph walk so
/// every field is consistent with the others (no time travel between
/// fields).
/// </summary>
public sealed class AuthorityReport
{
    /// <summary>
    /// Canonical id of the type the report describes.
    /// </summary>
    public required string TypeId { get; init; }

    /// <summary>
    /// Number of distinct interfaces this type directly satisfies.
    /// For class/struct sources, read from outgoing
    /// <see cref="EdgeKind.Implements"/> edges. For interface sources
    /// (interface extending interfaces), read from outgoing
    /// <see cref="EdgeKind.Inherits"/> edges (post-F3c extractor wire
    /// shape, see INV-EXTRACT-IFACE-INHERIT-001).
    /// </summary>
    public int ImplementedInterfaceCount { get; init; }

    /// <summary>
    /// Total number of public-surface members owned by this type
    /// (methods + properties + fields + events with Public visibility,
    /// not counting nested types). Counts the SHIPPED public surface,
    /// not derived/forwarded interfaces.
    /// </summary>
    public int OwnedPublicSurface { get; init; }

    /// <summary>
    /// Per-implemented-interface breakdown. Each entry names one
    /// interface, the number of members the host carries that satisfy
    /// the interface contract, and the number of distinct callers that
    /// reach the interface via <see cref="EdgeKind.Calls"/> edges.
    /// </summary>
    public InterfaceUsage[] PerInterface { get; init; } = System.Array.Empty<InterfaceUsage>();

    /// <summary>
    /// Ratio of "thin/forwarder methods" to total methods on the type,
    /// in the closed range [0.0, 1.0]. Computed from
    /// <see cref="Symbol.Properties"/>["classification"] (recorded by
    /// the extractor when forwarder detection runs). 0.0 means no
    /// classified methods or all RealLogic; 1.0 means every classified
    /// method is a PureForwarder. Sentinel value <c>-1.0</c> when no
    /// classification data is present in the graph (older snapshots).
    /// </summary>
    public double ForwarderRatio { get; init; }

    /// <summary>
    /// Total number of methods on the type. Denominator for
    /// <see cref="ForwarderRatio"/>.
    /// </summary>
    public int TotalMethodCount { get; init; }

    /// <summary>
    /// Number of methods classified as <c>PureForwarder</c> by the
    /// extractor. Numerator for <see cref="ForwarderRatio"/>.
    /// </summary>
    public int PureForwarderCount { get; init; }

    /// <summary>
    /// Distinct number of source modules OTHER THAN the type's own
    /// module that have any incoming graph edge into the type or any
    /// of its direct members (Calls / References / Inherits / Implements).
    /// High value = the type IS a published boundary contract; low /
    /// zero value (with high <see cref="SameAssemblyConsumerCount"/>) =
    /// candidate for collapsing into the same module as its consumers.
    /// INV-AUTHORITY-PLANNING-COMPOSITION-001 evidence.
    /// </summary>
    public int CrossAssemblyConsumerCount { get; init; }

    /// <summary>
    /// Distinct number of source symbols WITHIN the type's own module
    /// that have any incoming graph edge into the type or any of its
    /// direct members. Pairs with <see cref="CrossAssemblyConsumerCount"/>
    /// — concentrated same-module + zero cross-module use is the
    /// adapter-shim shape, distributed cross-module use is the boundary
    /// shape.
    /// </summary>
    public int SameAssemblyConsumerCount { get; init; }

    /// <summary>
    /// True iff the type is an interface AND exactly one source-defined
    /// type in the loaded workspace carries an <see cref="EdgeKind.Implements"/>
    /// edge into it. Null when the type is not an interface (no semantic
    /// concept of "single implementer" for class/struct/enum targets).
    /// A single-implementer interface paired with high
    /// <see cref="CrossAssemblyConsumerCount"/> is a candidate adapter
    /// shim (consumers could bind to the concrete type and retire the
    /// interface). INV-AUTHORITY-PLANNING-COMPOSITION-001 evidence.
    /// </summary>
    public bool? HasSingleImplementer { get; init; }
}

/// <summary>
/// One implemented-interface entry on an <see cref="AuthorityReport"/>.
///
/// F3e extends the per-interface row with composite / inherited surface
/// (<see cref="DirectMemberCount"/>, <see cref="InheritedMemberCount"/>,
/// <see cref="AggregateMemberCount"/>, <see cref="InheritedInterfaces"/>,
/// <see cref="IsCompositeInterface"/>). When the satisfied interface is
/// itself composite (extends one or more sub-interfaces, e.g. ABG-style
/// concern-composite facades), the aggregate fields surface the
/// inherited contract's real load-bearing member count alongside the
/// pre-F3e direct count. INV-AUTHORITY-COMPOSITE-001.
/// </summary>
public sealed class InterfaceUsage
{
    public required string InterfaceId { get; init; }
    public string InterfaceName { get; init; } = "";

    /// <summary>
    /// Members the satisfied interface declares directly (pre-F3e
    /// member count). Equals <see cref="AggregateMemberCount"/> for
    /// non-composite interfaces. Backwards-compatible alias for callers
    /// that pre-date F3e is exposed as <see cref="MemberCount"/>.
    /// </summary>
    public int DirectMemberCount { get; init; }

    /// <summary>
    /// Members reached by transitively walking the satisfied interface's
    /// outgoing <see cref="EdgeKind.Inherits"/> edges and summing the
    /// direct member count of each inherited sub-interface. Distinct by
    /// canonical id across the closure.
    /// </summary>
    public int InheritedMemberCount { get; init; }

    /// <summary>
    /// <see cref="DirectMemberCount"/> + <see cref="InheritedMemberCount"/>.
    /// </summary>
    public int AggregateMemberCount { get; init; }

    /// <summary>
    /// Total members the host carries that satisfy the interface
    /// contract. Equals <see cref="AggregateMemberCount"/>. Retains its
    /// pre-F3e name as a backwards-compatible alias.
    /// </summary>
    public int MemberCount { get; init; }

    /// <summary>
    /// Canonical ids of every sub-interface this interface inherits
    /// from, transitively. Empty for non-composite interfaces. Sorted
    /// ordinal so the wire shape is deterministic.
    /// </summary>
    public string[] InheritedInterfaces { get; init; } = System.Array.Empty<string>();

    /// <summary>
    /// True iff <see cref="InheritedMemberCount"/> &gt; 0.
    /// </summary>
    public bool IsCompositeInterface { get; init; }

    /// <summary>
    /// Distinct callers reaching the interface or any of its members
    /// (across the aggregate set when composite) via
    /// <see cref="EdgeKind.Calls"/> / <see cref="EdgeKind.References"/>
    /// edges.
    /// </summary>
    public int ConsumerCount { get; init; }
}

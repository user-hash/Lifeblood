using Lifeblood.Domain.Graph;

namespace Lifeblood.Application.Ports.Right;

/// <summary>
/// Right-side analyzer that reports the "health" of a type acting as a port —
/// how many of its members are actually consumed somewhere in the graph. A
/// port whose members are mostly consumed is healthy; one whose members are
/// mostly orphaned is a candidate for retirement.
///
/// Pure read port. Stateless per INV-ANALYSIS-001.
///
/// Replaces the pre-F3a inline <c>HandlePortHealth</c> body in
/// <c>ToolHandler.cs</c>. INV-PORT-HEALTH-ANALYZER-SEAM-001 /
/// LB-TRACK-20260519-022.
/// </summary>
public interface IPortHealthAnalyzer
{
    /// <summary>
    /// Analyze the port-health of the type identified by
    /// <paramref name="typeId"/>. Returns <c>null</c> when the symbol does
    /// not exist or is not of kind <see cref="SymbolKind.Type"/>.
    /// </summary>
    PortHealthReport? Analyze(SemanticGraph graph, string typeId);
}

/// <summary>
/// One port-health report. All counts come from a single graph walk so
/// every field is consistent with the others.
///
/// F3b extends the report with composite / inherited-interface fields
/// (<see cref="DirectMemberCount"/>, <see cref="InheritedMemberCount"/>,
/// <see cref="AggregateMemberCount"/>, <see cref="InheritedInterfaces"/>,
/// <see cref="IsCompositeInterface"/>). Liveness (<see cref="LiveMembers"/>,
/// <see cref="LivenessPct"/>, <see cref="Verdict"/>) is computed across the
/// aggregate member set so composite ports no longer mislabel as
/// <c>vestigial</c> when their inherited contracts carry the real surface.
/// INV-PORT-HEALTH-COMPOSITE-001.
/// </summary>
public sealed class PortHealthReport
{
    /// <summary>Canonical id of the type the report describes.</summary>
    public required string TypeId { get; init; }

    /// <summary>
    /// Total number of members the verdict was computed against — equals
    /// <see cref="AggregateMemberCount"/>. Direct members for a non-composite
    /// type, direct + inherited for a composite. Backwards-compatible
    /// alias for callers that pre-date F3b.
    /// </summary>
    public int MemberCount { get; init; }

    /// <summary>
    /// Members with at least one non-<see cref="EdgeKind.Contains"/>
    /// incoming edge OR an outgoing <see cref="EdgeKind.Implements"/>
    /// edge (interface implementers are reachable through their
    /// contract). Counted across the aggregate set when composite.
    /// </summary>
    public int LiveMembers { get; init; }

    /// <summary>Members not counted live by the same criteria.</summary>
    public int DeadMembers { get; init; }

    /// <summary>
    /// <see cref="LiveMembers"/> / <see cref="MemberCount"/>, rounded to
    /// three decimals. <c>0.0</c> when the type has no members.
    /// </summary>
    public double LivenessPct { get; init; }

    /// <summary>
    /// Health verdict derived from <see cref="LivenessPct"/>:
    /// <c>empty</c> (no members), <c>healthy</c> (≥0.75), <c>mixed</c>
    /// (≥0.25), or <c>vestigial</c> (&lt;0.25).
    /// </summary>
    public required string Verdict { get; init; }

    /// <summary>Canonical ids of every live member (aggregate set).</summary>
    public required string[] Live { get; init; }

    /// <summary>Canonical ids of every dead member (aggregate set).</summary>
    public required string[] Dead { get; init; }

    /// <summary>
    /// Count of members declared directly on this type (the pre-F3b
    /// member count). For a composite interface this is usually 0 or
    /// small; the load-bearing surface lives in inherited contracts.
    /// </summary>
    public int DirectMemberCount { get; init; }

    /// <summary>
    /// Count of members reached by walking outgoing
    /// <see cref="EdgeKind.Inherits"/> edges transitively and summing
    /// the direct member count of each inherited interface. Distinct by
    /// canonical id across the transitive closure.
    /// </summary>
    public int InheritedMemberCount { get; init; }

    /// <summary>
    /// <see cref="DirectMemberCount"/> + <see cref="InheritedMemberCount"/>.
    /// </summary>
    public int AggregateMemberCount { get; init; }

    /// <summary>
    /// Canonical ids of every interface this type inherits from,
    /// transitively. Empty for non-composite types. Sorted ordinal so
    /// the wire shape is deterministic.
    /// </summary>
    public required string[] InheritedInterfaces { get; init; }

    /// <summary>
    /// <c>true</c> iff this type inherits from at least one other type
    /// via outgoing <see cref="EdgeKind.Inherits"/>. Composite ports
    /// commonly carry 0 direct members and reach all their surface
    /// through inherited sub-ports.
    /// </summary>
    public bool IsCompositeInterface { get; init; }
}

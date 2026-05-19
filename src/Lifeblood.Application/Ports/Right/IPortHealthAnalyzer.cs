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
/// </summary>
public sealed class PortHealthReport
{
    /// <summary>Canonical id of the type the report describes.</summary>
    public required string TypeId { get; init; }

    /// <summary>
    /// Total number of directly-contained non-nested members on the type
    /// (the same set the analyzer evaluates for liveness).
    /// </summary>
    public int MemberCount { get; init; }

    /// <summary>
    /// Members with at least one non-<see cref="EdgeKind.Contains"/>
    /// incoming edge OR an outgoing <see cref="EdgeKind.Implements"/>
    /// edge (interface implementers are reachable through their
    /// contract).
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

    /// <summary>Canonical ids of every live member.</summary>
    public required string[] Live { get; init; }

    /// <summary>Canonical ids of every dead member.</summary>
    public required string[] Dead { get; init; }
}

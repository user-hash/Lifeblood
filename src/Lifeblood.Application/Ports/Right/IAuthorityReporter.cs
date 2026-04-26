using Lifeblood.Domain.Graph;

namespace Lifeblood.Application.Ports.Right;

/// <summary>
/// Right-side analyzer that quantifies how much "authority" a type
/// holds in the architecture. The output answers a triage question
/// the DAWG ABG-extraction sessions kept asking by hand: when a
/// host/owner type implements N interfaces, how much real surface
/// does it own vs how much is delegation? A port whose host
/// implements many interfaces but exposes only forwarder methods
/// is a candidate for splitting; a host with concentrated public
/// surface is doing real work and shouldn't be cut up.
///
/// Pure read port. Stateless per INV-ANALYSIS-001. Phase P5
/// (2026-04-26). See INV-AUTHORITY-001 in CLAUDE.md.
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
    /// Number of distinct interfaces this type implements (transitive,
    /// distinct by canonical id). Read from outgoing
    /// <see cref="EdgeKind.Implements"/> edges on the type itself.
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
}

/// <summary>
/// One implemented-interface entry on an <see cref="AuthorityReport"/>.
/// </summary>
public sealed class InterfaceUsage
{
    public required string InterfaceId { get; init; }
    public string InterfaceName { get; init; } = "";
    public int MemberCount { get; init; }
    public int ConsumerCount { get; init; }
}

using Lifeblood.Application.Ports.Right.Invariants;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// MCP-only compact view of the complete Application-owned invariant audit.
/// It never reparses sources or becomes a second ledger authority; it only
/// filters zero-declaration rows from the already reconciled source counts.
/// </summary>
internal sealed record InvariantAuditSourceProjection
{
    public required string Mode { get; init; }
    public required int DiscoveredSourceCount { get; init; }
    public required int ReturnedSourceCount { get; init; }
    public required int ZeroDeclarationSourceCount { get; init; }
    public required int OmittedSourceCount { get; init; }
    public required bool Truncated { get; init; }
    public required InvariantSourceCount[] SourceCounts { get; init; }

    public static InvariantAuditSourceProjection BuildNonzero(InvariantAudit audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        var sourceCounts = audit.SourceCounts
            .Where(source => source.Count > 0)
            .ToArray();
        return new InvariantAuditSourceProjection
        {
            Mode = "nonzero",
            DiscoveredSourceCount = audit.SourceCounts.Length,
            ReturnedSourceCount = sourceCounts.Length,
            ZeroDeclarationSourceCount = audit.SourceCounts.Length - sourceCounts.Length,
            OmittedSourceCount = audit.SourceCounts.Length - sourceCounts.Length,
            Truncated = false,
            SourceCounts = sourceCounts,
        };
    }
}

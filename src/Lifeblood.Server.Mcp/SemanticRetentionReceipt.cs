namespace Lifeblood.Server.Mcp;

/// <summary>
/// Publication-owned lifecycle receipt. It describes one leased hot owner,
/// not the process heap or retired semantic bundles held by active readers.
/// Rebuild costs remain in the analyze usage receipt.
/// </summary>
public sealed record SemanticRetentionReceipt(
    string Policy,
    string Action,
    string Reason,
    string PublicationSnapshotId,
    string? HotSnapshotId,
    string? DisplacedHotSnapshotId,
    int HotSemanticOwnerCount,
    bool CompilationServicesAvailable,
    string? RecoveryCostReceipt,
    string? PublishingClientLeaseId,
    string? DisplacedClientLeaseId);

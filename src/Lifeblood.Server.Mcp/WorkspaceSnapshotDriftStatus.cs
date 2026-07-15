namespace Lifeblood.Server.Mcp;

/// <summary>
/// Bounded live-input comparison for one retained publication. Status is one
/// of notChecked, current, drifted, or unavailable.
/// </summary>
public sealed record WorkspaceSnapshotDriftStatus(
    string Status,
    bool LiveInputChecked,
    string? CurrentAnalysisKey = null,
    string? CurrentSourceFingerprint = null,
    string? Detail = null)
{
    public static WorkspaceSnapshotDriftStatus NotChecked { get; } = new(
        "notChecked",
        LiveInputChecked: false);
}

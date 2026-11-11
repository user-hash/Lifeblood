using Lifeblood.Application.Ports.Left;

namespace Lifeblood.Server.Mcp;

/// <summary>One request's automatic-refresh outcome; never retained as session freshness.</summary>
public sealed record AutoRefreshReceipt(
    string Status,
    string Mode,
    string SnapshotBefore,
    string SnapshotAfter,
    int? ChangedFileCount,
    string? FallbackReason,
    string? FallbackDetail,
    AnalysisRefreshTrigger? Trigger,
    object? AcceptedChanges,
    string? FailureDetail);

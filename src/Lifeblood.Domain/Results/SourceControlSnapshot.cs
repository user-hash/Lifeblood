namespace Lifeblood.Domain.Results;

/// <summary>
/// Neutral, wire-safe source-control evidence. Empty strings and nullable
/// dirty state keep partial failures explicit without inventing provenance.
/// </summary>
public sealed record SourceControlSnapshot(
    string AttemptedPath,
    string RepositoryRoot,
    string CommitHash,
    string ShortCommitHash,
    bool? Dirty,
    string State,
    string Source,
    string LatestSemanticVersionTag,
    int DirtyEntryCount,
    bool DirtyEntryCountCapped,
    string[] DirtyEntries,
    bool DirtyEntriesTruncated,
    string FailureReason)
{
    public static SourceControlSnapshot Unavailable(
        string? attemptedPath,
        string source,
        string failureReason = "")
        => new(
            AttemptedPath: attemptedPath ?? "",
            RepositoryRoot: "",
            CommitHash: "",
            ShortCommitHash: "",
            Dirty: null,
            State: "unknown",
            Source: source,
            LatestSemanticVersionTag: "",
            DirtyEntryCount: 0,
            DirtyEntryCountCapped: false,
            DirtyEntries: Array.Empty<string>(),
            DirtyEntriesTruncated: false,
            FailureReason: failureReason);
}

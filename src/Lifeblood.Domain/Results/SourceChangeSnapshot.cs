namespace Lifeblood.Domain.Results;

/// <summary>
/// Caller-selected source-control comparison scope. The vocabulary is neutral:
/// adapters decide how their source-control system materializes each scope.
/// </summary>
public enum SourceChangeScope
{
    WorkingTree,
    Staged,
    SinceCommit,
    ExplicitFiles,
}

/// <summary>Inclusive current-side line span owned by a source change.</summary>
public sealed record SourceLineRange(int StartLine, int EndLine);

/// <summary>
/// One current-side file in a source-change receipt. Paths are adapter-
/// canonical absolute paths so a language-neutral classifier can join them to
/// diagnostic locations without rediscovering repository semantics.
/// </summary>
public sealed record SourceChangedFile(
    string FilePath,
    string RepositoryRelativePath,
    bool IsNew,
    bool HasLineEvidence,
    SourceLineRange[] ChangedLines);

/// <summary>
/// Neutral, bounded source-change evidence. A failed or truncated capture is
/// explicitly incomplete; consumers must not turn partial evidence into an
/// ownership claim.
/// </summary>
public sealed record SourceChangeSnapshot(
    SourceChangeScope Scope,
    string RepositoryRoot,
    string BaseRevision,
    string CurrentRevision,
    string Source,
    bool EvidenceComplete,
    bool OutputTruncated,
    SourceChangedFile[] Files,
    string FailureReason)
{
    public static SourceChangeSnapshot Unavailable(
        SourceChangeScope scope,
        string source,
        string failureReason = "")
        => new(
            Scope: scope,
            RepositoryRoot: "",
            BaseRevision: "",
            CurrentRevision: "",
            Source: source,
            EvidenceComplete: false,
            OutputTruncated: false,
            Files: Array.Empty<SourceChangedFile>(),
            FailureReason: failureReason);
}

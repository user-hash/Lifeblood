namespace Lifeblood.Domain.Results;

/// <summary>Caller-reported artifact/debug identity; importing this does not authenticate an artifact or debug file.</summary>
public sealed class NativeArtifactEvidence
{
    public string ArtifactId { get; init; } = "";
    public string BuildId { get; init; } = "";
    public string DebugId { get; init; } = "";
    public string DebugFormat { get; init; } = "";
    public NativeSourceDocument[] Documents { get; init; } = Array.Empty<NativeSourceDocument>();
}

public sealed class NativeSourceDocument
{
    public string WorkspaceFilePath { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string ModuleName { get; init; } = "";
    public string ProfileScope { get; init; } = "";
    public string ChecksumKind { get; init; } = "";
    public string ChecksumAlgorithm { get; init; } = "";
    public string Checksum { get; init; } = "";
}

/// <summary>Canonical workspace-relative identity projected at the host boundary from an observed retained declaration.</summary>
public sealed record NativeSourceObservation(
    string WorkspaceFilePath,
    string ModuleName,
    string ProfileScope,
    string ChecksumAlgorithm,
    string Checksum);

public sealed class NativeSourceJoinReport
{
    public required string Status { get; init; }
    public string EvidenceOrigin { get; init; } = "CallerReported";
    public bool NativeArtifactVerified { get; init; }
    public required string CaptureId { get; init; }
    public required string CaptureSourceName { get; init; }
    public required string CaptureBuildId { get; init; }
    public required int ArtifactCount { get; init; }
    public required NativeSourceJoinRow[] Documents { get; init; }
    public string[] Limitations { get; init; } = Array.Empty<string>();
}

public sealed record NativeSourceJoinRow(
    string ArtifactId,
    string BuildId,
    string DebugId,
    string DebugFormat,
    NativeSourceDocument Document,
    string Outcome,
    string? ObservedChecksum);

public static class NativeSourceJoinOutcome
{
    public const string MatchedSourceChecksum = "MatchedSourceChecksum";
    public const string MismatchedSourceChecksum = "MismatchedSourceChecksum";
    public const string MissingIdentity = "MissingIdentity";
    public const string BuildMismatch = "BuildMismatch";
    public const string ProfileMismatch = "ProfileMismatch";
    public const string ChecksumDomainMismatch = "ChecksumDomainMismatch";
    public const string UnsupportedChecksumAlgorithm = "UnsupportedChecksumAlgorithm";
    public const string InvalidChecksum = "InvalidChecksum";
    public const string SourceNotObserved = "SourceNotObserved";
    public const string AmbiguousSourceIdentity = "AmbiguousSourceIdentity";
    public const string IncompleteSemanticScan = "IncompleteSemanticScan";
}

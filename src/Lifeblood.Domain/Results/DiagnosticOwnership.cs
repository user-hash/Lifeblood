namespace Lifeblood.Domain.Results;

/// <summary>
/// Conservative ownership classification for one already-produced diagnostic.
/// This is change-location ownership, not proof of diagnostic causality.
/// </summary>
public enum DiagnosticOwnership
{
    IntroducedByDiff,
    PreExistingTouchedFile,
    PreExistingUnrelated,
    UnknownOwnership,
}

/// <summary>
/// Index-based classification avoids duplicating the full diagnostic payload
/// when a connector projects grouped ownership results.
/// </summary>
public sealed record DiagnosticOwnershipClassification(
    int DiagnosticIndex,
    DiagnosticOwnership Ownership,
    string Evidence);

public sealed record DiagnosticOwnershipReport(
    DiagnosticOwnershipClassification[] Classifications);

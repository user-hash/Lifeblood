using Lifeblood.Domain.Workspaces;

namespace Lifeblood.Application.UseCases;

/// <summary>
/// Optimistic read contract for one immutable workspace publication. The MCP
/// edge parses wire values, while Application owns comparison semantics.
/// </summary>
public sealed record WorkspaceSnapshotPrecondition
{
    public WorkspaceSnapshotPrecondition(
        SnapshotId? expectedSnapshotId,
        long? expectedAnalysisGeneration)
    {
        if (expectedSnapshotId == null && expectedAnalysisGeneration == null)
            throw new ArgumentException("At least one snapshot precondition is required.");
        if (expectedAnalysisGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedAnalysisGeneration));

        ExpectedSnapshotId = expectedSnapshotId;
        ExpectedAnalysisGeneration = expectedAnalysisGeneration;
    }

    public SnapshotId? ExpectedSnapshotId { get; }

    public long? ExpectedAnalysisGeneration { get; }

    public WorkspaceSnapshotMismatch? Compare(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var snapshotMatches = ExpectedSnapshotId == null
            || ExpectedSnapshotId == snapshot.SnapshotId;
        var generationMatches = ExpectedAnalysisGeneration == null
            || ExpectedAnalysisGeneration == snapshot.AnalysisGeneration;
        return snapshotMatches && generationMatches
            ? null
            : new WorkspaceSnapshotMismatch(
                ExpectedSnapshotId,
                ExpectedAnalysisGeneration,
                snapshot.SnapshotId,
                snapshot.AnalysisGeneration,
                snapshot.Identity);
    }
}

public sealed record WorkspaceSnapshotMismatch(
    SnapshotId? ExpectedSnapshotId,
    long? ExpectedAnalysisGeneration,
    SnapshotId ActualSnapshotId,
    long ActualAnalysisGeneration,
    WorkspaceAnalysisIdentity? ActualAnalysisIdentity);

/// <summary>
/// Transport-neutral request to select one retained publication and/or apply
/// an optimistic precondition after that publication has been leased.
/// </summary>
public sealed record WorkspaceSnapshotReadRequest
{
    public WorkspaceSnapshotReadRequest(
        SnapshotId? selectedSnapshotId,
        WorkspaceSnapshotPrecondition? precondition)
    {
        if (selectedSnapshotId == null && precondition == null)
            throw new ArgumentException("A snapshot selection or precondition is required.");
        if (selectedSnapshotId?.IsNone == true)
            throw new ArgumentException("A selected snapshot id cannot be empty.", nameof(selectedSnapshotId));
        if (selectedSnapshotId != null
            && precondition?.ExpectedSnapshotId != null
            && selectedSnapshotId != precondition.ExpectedSnapshotId)
        {
            throw new ArgumentException(
                "Selected and expected snapshot identities must match.",
                nameof(precondition));
        }

        SelectedSnapshotId = selectedSnapshotId;
        Precondition = precondition;
    }

    public SnapshotId? SelectedSnapshotId { get; }

    public WorkspaceSnapshotPrecondition? Precondition { get; }
}

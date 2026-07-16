using Lifeblood.Domain.Results;

namespace Lifeblood.Application.Ports.Infrastructure;

/// <summary>
/// Captures bounded source-control provenance for a caller-selected workspace.
/// Application owns the neutral receipt; adapters own repository discovery and
/// source-control process details.
/// </summary>
public interface ISourceControlSnapshotProvider
{
    SourceControlSnapshot Capture(string? startPath);

    /// <summary>
    /// Captures bounded current-side file/line evidence for a caller-selected
    /// comparison. The provider never compiles source or classifies diagnostics.
    /// </summary>
    SourceChangeSnapshot CaptureChanges(SourceChangeRequest request);
}

public sealed record SourceChangeRequest
{
    public string? StartPath { get; init; }
    public SourceChangeScope Scope { get; init; }
    public string SinceCommit { get; init; } = "";
    public string[] TouchedFiles { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Safe default for hosts and focused tests that do not compose a concrete
/// source-control adapter.
/// </summary>
public sealed class UnavailableSourceControlSnapshotProvider : ISourceControlSnapshotProvider
{
    public static readonly UnavailableSourceControlSnapshotProvider Instance = new();

    private UnavailableSourceControlSnapshotProvider()
    {
    }

    public SourceControlSnapshot Capture(string? startPath)
        => SourceControlSnapshot.Unavailable(startPath, "providerNotConfigured");

    public SourceChangeSnapshot CaptureChanges(SourceChangeRequest request)
        => SourceChangeSnapshot.Unavailable(
            request.Scope,
            "providerNotConfigured",
            "Source-change provider is not configured.");
}

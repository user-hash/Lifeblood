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
}

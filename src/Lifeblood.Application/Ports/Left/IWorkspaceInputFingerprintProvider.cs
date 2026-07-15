using Lifeblood.Domain.Workspaces;

namespace Lifeblood.Application.Ports.Left;

/// <summary>
/// Left-side preflight port for content-authoritative analysis identity. The
/// adapter owns which source and descriptor inputs can affect its graph; the
/// host consumes the receipt without rediscovering language-specific files.
/// </summary>
public interface IWorkspaceInputFingerprintProvider
{
    WorkspaceAnalysisInputs CaptureAnalysisInputs(string projectRoot, AnalysisConfig config);
}

public sealed class WorkspaceAnalysisInputs
{
    public WorkspaceAnalysisInputs(
        IEnumerable<string> effectiveDefineProfiles,
        SourceFingerprint sourceFingerprint)
    {
        ArgumentNullException.ThrowIfNull(effectiveDefineProfiles);
        EffectiveDefineProfiles = Array.AsReadOnly(effectiveDefineProfiles.ToArray());
        SourceFingerprint = sourceFingerprint ?? throw new ArgumentNullException(nameof(sourceFingerprint));
    }

    public IReadOnlyList<string> EffectiveDefineProfiles { get; }

    public SourceFingerprint SourceFingerprint { get; }
}

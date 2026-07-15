namespace Lifeblood.Domain.Workspaces;

/// <summary>
/// Stable, bounded projection of one canonical analysis identity. Responses,
/// snapshot inventory, and analyze receipts serialize this descriptor instead
/// of independently rebuilding workspace/spec/source fields.
/// </summary>
public sealed record WorkspaceAnalysisDescriptor(
    string WorkspaceKey,
    string BaseKey,
    string AnalysisKey,
    string SpecFingerprint,
    string SourceFingerprint,
    string SourceContentFingerprint,
    string DescriptorFingerprint,
    int SourceFileCount,
    int DescriptorFileCount,
    string RuleFingerprint,
    string RetentionMode,
    string DescriptorPolicy,
    IReadOnlyList<string> DefineProfiles,
    IReadOnlyList<string> ExcludePathGlobs)
{
    public static WorkspaceAnalysisDescriptor From(WorkspaceAnalysisIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new WorkspaceAnalysisDescriptor(
            identity.Workspace.Value,
            identity.BaseKey.Value,
            identity.AnalysisKey.Value,
            identity.Spec.Fingerprint.Value,
            identity.Source.Fingerprint.Value,
            identity.Source.SourceContent.Value,
            identity.Source.Descriptors.Value,
            identity.Source.SourceFileCount,
            identity.Source.DescriptorFileCount,
            identity.Spec.RuleSet.Fingerprint.Value,
            identity.Spec.RetentionMode.ToString(),
            identity.Spec.DescriptorPolicy.ToString(),
            identity.Spec.DefineProfiles,
            identity.Spec.ExcludePathGlobs);
    }
}

using Lifeblood.Domain.Workspaces;

namespace Lifeblood.Adapters.CSharp.Internal;

internal static class WorkspaceInputFingerprintBuilder
{
    public static SourceFingerprint Build(
        WorkspaceSourcePathMap sourcePaths,
        IReadOnlyDictionary<string, ContentFingerprint> sourceFiles,
        params IReadOnlyDictionary<string, ContentFingerprint>[] descriptorSets)
    {
        var descriptors = new Dictionary<string, ContentFingerprint>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptorSet in descriptorSets)
        {
            foreach (var (path, fingerprint) in descriptorSet)
                descriptors[path] = fingerprint;
        }

        return new SourceFingerprint(
            sourceFiles.Select(pair => new FingerprintEntry(
                sourcePaths.ToWorkspacePath(pair.Key),
                pair.Value)),
            descriptors.Select(pair => new FingerprintEntry(
                sourcePaths.ToWorkspacePath(pair.Key),
                pair.Value)));
    }
}

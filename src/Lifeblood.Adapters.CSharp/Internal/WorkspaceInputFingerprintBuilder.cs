using Lifeblood.Domain.Workspaces;

namespace Lifeblood.Adapters.CSharp.Internal;

internal static class WorkspaceInputFingerprintBuilder
{
    public static SourceFingerprint Build(
        string projectRoot,
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
                NormalizePath(projectRoot, pair.Key),
                pair.Value)),
            descriptors.Select(pair => new FingerprintEntry(
                NormalizePath(projectRoot, pair.Key),
                pair.Value)));
    }

    private static string NormalizePath(string projectRoot, string path)
    {
        var fullRoot = Path.GetFullPath(projectRoot);
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(fullRoot, fullPath).Replace('\\', '/');
        return Path.IsPathRooted(relative)
            ? fullPath.Replace('\\', '/')
            : relative;
    }
}

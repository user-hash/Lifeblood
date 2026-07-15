namespace Lifeblood.Application.Ports.Left;

/// <summary>
/// Canonical, adapter-neutral evidence for the source files accepted by one
/// incremental analysis attempt. Paths are normalized project-relative POSIX
/// paths; counts are derived projections rather than independent state.
/// </summary>
public sealed class AcceptedChangeSet
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private AcceptedChangeSet(
        ChangeScanMode scanMode,
        bool fullFallback,
        string[] reanalyzedSourceFiles,
        string[] mtimeTouchedSourceFiles,
        string[] contentChangedSourceFiles,
        string[] descriptorForcedSourceFiles,
        string[] deletedSourceFiles)
    {
        ScanMode = scanMode;
        FullFallback = fullFallback;
        ReanalyzedSourceFiles = reanalyzedSourceFiles;
        MtimeTouchedSourceFiles = mtimeTouchedSourceFiles;
        ContentChangedSourceFiles = contentChangedSourceFiles;
        DescriptorForcedSourceFiles = descriptorForcedSourceFiles;
        DeletedSourceFiles = deletedSourceFiles;

        ChangedSourceFiles = reanalyzedSourceFiles
            .Concat(deletedSourceFiles)
            .Distinct(PathComparer)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        EvidenceSourceFiles = ChangedSourceFiles
            .Concat(mtimeTouchedSourceFiles)
            .Concat(contentChangedSourceFiles)
            .Concat(descriptorForcedSourceFiles)
            .Concat(deletedSourceFiles)
            .Distinct(PathComparer)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    public static AcceptedChangeSet Empty { get; } = Create(ChangeScanMode.FilesystemPrefilter);

    public ChangeScanMode ScanMode { get; }

    public bool FullFallback { get; }

    public IReadOnlyList<string> ReanalyzedSourceFiles { get; }

    public IReadOnlyList<string> MtimeTouchedSourceFiles { get; }

    public IReadOnlyList<string> ContentChangedSourceFiles { get; }

    public IReadOnlyList<string> DescriptorForcedSourceFiles { get; }

    public IReadOnlyList<string> DeletedSourceFiles { get; }

    public IReadOnlyList<string> ChangedSourceFiles { get; }

    public IReadOnlyList<string> EvidenceSourceFiles { get; }

    public int ChangedFileCount => ChangedSourceFiles.Count;

    public int MtimeTouchedFileCount => MtimeTouchedSourceFiles.Count;

    public int ContentChangedFileCount => ContentChangedSourceFiles.Count;

    public static AcceptedChangeSet Create(
        ChangeScanMode scanMode,
        bool fullFallback = false,
        IEnumerable<string>? reanalyzedSourceFiles = null,
        IEnumerable<string>? mtimeTouchedSourceFiles = null,
        IEnumerable<string>? contentChangedSourceFiles = null,
        IEnumerable<string>? descriptorForcedSourceFiles = null,
        IEnumerable<string>? deletedSourceFiles = null)
    {
        if (fullFallback != (scanMode == ChangeScanMode.FullFallback))
        {
            throw new ArgumentException(
                "FullFallback must be true exactly when scanMode is FullFallback.",
                nameof(fullFallback));
        }

        var reanalyzed = Normalize(reanalyzedSourceFiles, nameof(reanalyzedSourceFiles));
        var mtimeTouched = Normalize(mtimeTouchedSourceFiles, nameof(mtimeTouchedSourceFiles));
        var contentChanged = Normalize(contentChangedSourceFiles, nameof(contentChangedSourceFiles));
        var descriptorForced = Normalize(descriptorForcedSourceFiles, nameof(descriptorForcedSourceFiles));
        var deleted = Normalize(deletedSourceFiles, nameof(deletedSourceFiles));
        var reanalyzedSet = new HashSet<string>(reanalyzed, PathComparer);

        EnsureSubset(contentChanged, reanalyzedSet, nameof(contentChangedSourceFiles));
        EnsureSubset(descriptorForced, reanalyzedSet, nameof(descriptorForcedSourceFiles));
        if (deleted.Any(reanalyzedSet.Contains))
        {
            throw new ArgumentException(
                "Deleted source files cannot also be reanalyzed.",
                nameof(deletedSourceFiles));
        }

        return new AcceptedChangeSet(
            scanMode,
            fullFallback,
            reanalyzed,
            mtimeTouched,
            contentChanged,
            descriptorForced,
            deleted);
    }

    private static string[] Normalize(IEnumerable<string>? paths, string parameterName)
    {
        if (paths == null)
            return Array.Empty<string>();

        var normalized = new HashSet<string>(PathComparer);
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw new ArgumentException("Accepted-change paths cannot be empty.", parameterName);

            var path = raw.Trim().Replace('\\', '/');
            while (path.StartsWith("./", StringComparison.Ordinal))
                path = path[2..];
            if (path.Length == 0
                || Path.IsPathRooted(path)
                || path.Equals("..", StringComparison.Ordinal)
                || path.StartsWith("../", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Accepted-change path '{raw}' must be project-relative.",
                    parameterName);
            }

            normalized.Add(path);
        }

        return normalized.OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    private static void EnsureSubset(
        IEnumerable<string> subset,
        HashSet<string> reanalyzed,
        string parameterName)
    {
        var orphan = subset.FirstOrDefault(path => !reanalyzed.Contains(path));
        if (orphan != null)
        {
            throw new ArgumentException(
                $"Accepted-change path '{orphan}' must also be present in reanalyzedSourceFiles.",
                parameterName);
        }
    }
}

/// <summary>How the adapter bounded source-file change discovery.</summary>
public enum ChangeScanMode
{
    FilesystemPrefilter,
    AuthoritativeChangedSet,
    FullFallback,
}

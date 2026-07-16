namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Projects physical compiler inputs onto stable workspace-owned source paths.
/// The project root is the default mount; adapters can add logical mounts for
/// sources such as Unity <c>file:</c> packages that physically live outside it.
/// Consumers must not independently call <see cref="Path.GetRelativePath(string, string)"/>
/// for source identity because that would reintroduce traversal paths for
/// mounted sources.
/// </summary>
internal sealed class WorkspaceSourcePathMap
{
    private readonly string _projectRoot;
    private readonly WorkspaceSourceMount[] _mounts;

    private WorkspaceSourcePathMap(string projectRoot, IEnumerable<WorkspaceSourceMount> mounts)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
        _mounts = mounts
            .Select(mount => new WorkspaceSourceMount(
                Path.GetFullPath(mount.PhysicalRoot),
                NormalizeLogicalRoot(mount.LogicalRoot)))
            .OrderByDescending(mount => mount.PhysicalRoot.Length)
            .ThenBy(mount => mount.LogicalRoot, StringComparer.Ordinal)
            .ToArray();
    }

    public static WorkspaceSourcePathMap Create(
        string projectRoot,
        IEnumerable<WorkspaceSourceMount>? mounts = null)
        => new(projectRoot, mounts ?? Array.Empty<WorkspaceSourceMount>());

    public string ToWorkspacePath(string physicalPath)
    {
        var fullPath = Path.GetFullPath(physicalPath);
        // Explicit adapter mounts are more specific than the default project
        // mount. This also keeps a file package stored elsewhere inside the
        // repository under its Unity identity (Packages/<name>/...), rather
        // than leaking its storage directory into graph and receipt paths.
        foreach (var mount in _mounts)
        {
            if (!TryGetDescendantPath(mount.PhysicalRoot, fullPath, out var mountedRelative))
                continue;

            return mountedRelative.Length == 0
                ? mount.LogicalRoot
                : $"{mount.LogicalRoot}/{mountedRelative}";
        }

        if (TryGetDescendantPath(_projectRoot, fullPath, out var projectRelative))
            return projectRelative;

        // Keep unknown external inputs explicit. AcceptedChangeSet remains the
        // application authority that rejects traversal identities; known
        // adapter mounts must be registered above instead of being hidden.
        return Path.GetRelativePath(_projectRoot, fullPath).Replace('\\', '/');
    }

    private static bool TryGetDescendantPath(
        string root,
        string candidate,
        out string relative)
    {
        relative = Path.GetRelativePath(root, candidate).Replace('\\', '/');
        if (string.Equals(relative, ".", StringComparison.Ordinal))
            relative = "";
        return !Path.IsPathRooted(relative)
               && !string.Equals(relative, "..", StringComparison.Ordinal)
               && !relative.StartsWith("../", StringComparison.Ordinal);
    }

    private static string NormalizeLogicalRoot(string logicalRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalRoot);
        var normalized = logicalRoot.Trim().Replace('\\', '/').Trim('/');
        if (Path.IsPathRooted(normalized)
            || string.Equals(normalized, "..", StringComparison.Ordinal)
            || normalized.StartsWith("../", StringComparison.Ordinal)
            || normalized.Contains("/../", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Workspace source mount '{logicalRoot}' must be a traversal-free logical root.",
                nameof(logicalRoot));
        }

        return normalized;
    }
}

internal sealed record WorkspaceSourceMount(string PhysicalRoot, string LogicalRoot);

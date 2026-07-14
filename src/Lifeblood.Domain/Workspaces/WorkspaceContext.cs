namespace Lifeblood.Domain.Workspaces;

/// <summary>
/// Explicit physical workspace context for operations that must resolve graph-
/// relative paths against source on disk. The composition boundary owns path
/// canonicalization; Domain carries the resulting root without consulting
/// ambient process state.
/// </summary>
public sealed record WorkspaceContext
{
    public WorkspaceContext(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Workspace root path cannot be empty.", nameof(rootPath));

        RootPath = rootPath;
    }

    public string RootPath { get; }
}

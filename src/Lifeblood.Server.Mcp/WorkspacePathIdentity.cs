namespace Lifeblood.Server.Mcp;

using Lifeblood.Domain.Workspaces;

/// <summary>
/// Canonical path identity for the shared-host boundary. Transport keying and
/// analyze admission use this one seam so they cannot disagree about which
/// workspace a daemon owns.
/// </summary>
internal static class WorkspacePathIdentity
{
    public static WorkspaceKey CreateWorkspaceKey(string key)
    {
        var root = ResolveWorkspaceRoot(key);
        var identityMaterial = OperatingSystem.IsWindows()
            ? root.ToUpperInvariant()
            : root;
        return WorkspaceKey.FromCanonicalIdentity(identityMaterial);
    }

    public static string ResolveWorkspaceRoot(string key)
    {
        var fullPath = Normalize(key);
        var startingDirectory = File.Exists(fullPath)
            ? Path.GetDirectoryName(fullPath) ?? fullPath
            : fullPath;

        for (var directory = new DirectoryInfo(startingDirectory); directory != null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return Normalize(directory.FullName);
            }
        }

        return Normalize(startingDirectory);
    }

    public static string ResolveFromWorkspace(string workspaceRoot, string path)
        => Normalize(Path.IsPathRooted(path) ? path : Path.Combine(workspaceRoot, path));

    public static bool Equal(string? left, string right)
        => !string.IsNullOrWhiteSpace(left)
           && string.Equals(
               Normalize(left),
               Normalize(right),
               PathComparison);

    public static bool Contains(string workspaceRoot, string path)
    {
        var relative = Path.GetRelativePath(Normalize(workspaceRoot), Normalize(path));
        return !Path.IsPathRooted(relative)
               && !string.Equals(relative, "..", StringComparison.Ordinal)
               && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

/// <summary>
/// Shared-daemon request policy. It normalizes relative analyze inputs against
/// the bound workspace and rejects requests that could replace the singleton
/// session with another workspace's graph.
/// </summary>
internal sealed class WorkspaceBindingPolicy
{
    public WorkspaceBindingPolicy(string workspaceRoot)
    {
        WorkspaceRoot = WorkspacePathIdentity.ResolveWorkspaceRoot(workspaceRoot);
    }

    public string WorkspaceRoot { get; }

    public AnalyzeToolRequest Bind(AnalyzeToolRequest request)
    {
        var projectPath = NormalizeOptional(request.ProjectPath);
        var graphPath = NormalizeOptional(request.GraphPath);

        if (projectPath != null && !WorkspacePathIdentity.Equal(projectPath, WorkspaceRoot))
        {
            throw new WorkspaceBindingException(
                $"Shared daemon is bound to workspace '{WorkspaceRoot}' and cannot analyze project '{projectPath}'.");
        }

        if (graphPath != null && !WorkspacePathIdentity.Contains(WorkspaceRoot, graphPath))
        {
            throw new WorkspaceBindingException(
                $"Shared daemon is bound to workspace '{WorkspaceRoot}' and cannot load graph '{graphPath}'.");
        }

        return request with
        {
            ProjectPath = projectPath,
            GraphPath = graphPath,
        };
    }

    private string? NormalizeOptional(string? path)
        => string.IsNullOrWhiteSpace(path)
            ? path
            : WorkspacePathIdentity.ResolveFromWorkspace(WorkspaceRoot, path);
}

internal sealed class WorkspaceBindingException : InvalidOperationException
{
    public WorkspaceBindingException(string message)
        : base(message)
    {
    }
}

namespace Lifeblood.Core.Ports;

/// <summary>
/// Discovers module/assembly/package structure of a project.
/// Optional per adapter. If not provided, the CLI treats the whole project as one module.
/// </summary>
public interface IProjectDiscovery
{
    /// <summary>
    /// Discover all modules in a project.
    /// </summary>
    /// <param name="projectRoot">Root directory of the project.</param>
    /// <returns>Module metadata including file lists and dependency info.</returns>
    ModuleInfo[] DiscoverModules(string projectRoot);
}

/// <summary>
/// Metadata about a compilation module (assembly, package, crate).
/// </summary>
public sealed class ModuleInfo
{
    /// <summary>Module name (e.g., "MyApp.Domain", "myapp-core").</summary>
    public string Name { get; init; } = "";

    /// <summary>Source files belonging to this module (relative paths).</summary>
    public string[] FilePaths { get; init; } = Array.Empty<string>();

    /// <summary>Names of modules this one depends on.</summary>
    public string[] Dependencies { get; init; } = Array.Empty<string>();

    /// <summary>True if this module has zero platform/engine references.</summary>
    public bool IsPure { get; init; }

    /// <summary>True if this module is test/editor only.</summary>
    public bool IsTooling { get; init; }

    /// <summary>Language-specific metadata.</summary>
    public Dictionary<string, string> Properties { get; init; } = new();
}

namespace Lifeblood.Application.Ports.Left;

public interface IModuleDiscovery
{
    ModuleInfo[] DiscoverModules(string projectRoot);
}

public sealed class ModuleInfo
{
    public string Name { get; init; } = "";
    public string[] FilePaths { get; init; } = Array.Empty<string>();
    public string[] Dependencies { get; init; } = Array.Empty<string>();
    public bool IsPure { get; init; }
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Absolute paths to external DLLs referenced via HintPath in the project file.
    /// These are non-NuGet, non-module DLLs (e.g., Unity engine assemblies) that Roslyn
    /// needs as metadata references for accurate compilation and diagnostics.
    /// </summary>
    public string[] ExternalDllPaths { get; init; } = Array.Empty<string>();
}

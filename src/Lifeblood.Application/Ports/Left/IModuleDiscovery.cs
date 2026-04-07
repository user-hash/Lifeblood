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
}

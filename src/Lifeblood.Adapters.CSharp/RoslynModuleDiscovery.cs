using System.Xml.Linq;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Left;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Discovers C# modules from .sln and .csproj files.
/// Parses project XML directly — no MSBuild dependency required.
/// </summary>
public sealed class RoslynModuleDiscovery : IModuleDiscovery
{
    private readonly IFileSystem _fs;

    public RoslynModuleDiscovery(IFileSystem fs) => _fs = fs;

    public ModuleInfo[] DiscoverModules(string projectRoot)
    {
        // Try .sln first
        var slnFiles = _fs.FindFiles(projectRoot, "*.sln", recursive: false);
        if (slnFiles.Length > 0)
            return DiscoverFromSolution(slnFiles[0], projectRoot);

        // Fall back to .csproj files
        var csprojFiles = _fs.FindFiles(projectRoot, "*.csproj", recursive: true);
        return csprojFiles.Select(f => ParseProject(f, projectRoot)).Where(m => m != null).ToArray()!;
    }

    private ModuleInfo[] DiscoverFromSolution(string slnPath, string projectRoot)
    {
        var slnDir = Path.GetDirectoryName(slnPath)!;
        var modules = new List<ModuleInfo>();

        foreach (var line in _fs.ReadLines(slnPath))
        {
            // Match: Project("{...}") = "Name", "Path.csproj", "{...}"
            if (!line.StartsWith("Project(")) continue;

            var parts = line.Split('"');
            if (parts.Length < 6) continue;

            var relativePath = parts[5];
            if (!relativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) continue;

            var fullPath = Path.GetFullPath(Path.Combine(slnDir, relativePath));
            if (!_fs.FileExists(fullPath)) continue;

            var module = ParseProject(fullPath, projectRoot);
            if (module != null) modules.Add(module);
        }

        return modules.ToArray();
    }

    private ModuleInfo? ParseProject(string csprojPath, string projectRoot)
    {
        try
        {
            var xml = _fs.ReadAllText(csprojPath);
            var doc = XDocument.Parse(xml);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            var projectDir = Path.GetDirectoryName(csprojPath)!;

            // Assembly name
            var assemblyName = doc.Descendants()
                .FirstOrDefault(el => el.Name.LocalName == "AssemblyName")?.Value
                ?? Path.GetFileNameWithoutExtension(csprojPath);

            // Source files — sorted for deterministic output (INV-PIPE-001)
            var sourceFiles = _fs.FindFiles(projectDir, "*.cs", recursive: true)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();

            // Project references → dependencies by AssemblyName (not filename)
            var deps = doc.Descendants()
                .Where(el => el.Name.LocalName == "ProjectReference")
                .Select(el => el.Attribute("Include")?.Value)
                .Where(v => v != null)
                .Select(v => ResolveReferencedAssemblyName(v!, projectDir))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Pure detection: no PackageReference or assembly Reference
            bool isPure = !doc.Descendants().Any(el =>
                el.Name.LocalName == "PackageReference"
                || el.Name.LocalName == "Reference");

            return new ModuleInfo
            {
                Name = assemblyName,
                FilePaths = sourceFiles,
                Dependencies = deps,
                IsPure = isPure,
                Properties = new Dictionary<string, string>
                {
                    ["projectFile"] = Path.GetRelativePath(projectRoot, csprojPath).Replace('\\', '/'),
                },
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves a ProjectReference path to the referenced project's AssemblyName.
    /// Falls back to filename if the referenced .csproj can't be read.
    /// </summary>
    private string ResolveReferencedAssemblyName(string referencePath, string projectDir)
    {
        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(projectDir, referencePath));
            if (!_fs.FileExists(fullPath))
                return Path.GetFileNameWithoutExtension(referencePath);

            var xml = _fs.ReadAllText(fullPath);
            var refDoc = XDocument.Parse(xml);
            var asmName = refDoc.Descendants()
                .FirstOrDefault(el => el.Name.LocalName == "AssemblyName")?.Value;

            return asmName ?? Path.GetFileNameWithoutExtension(referencePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return Path.GetFileNameWithoutExtension(referencePath);
        }
    }
}

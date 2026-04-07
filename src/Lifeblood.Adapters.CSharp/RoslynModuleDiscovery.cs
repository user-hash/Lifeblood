using System.Xml.Linq;
using Lifeblood.Application.Ports.Left;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Discovers C# modules from .sln and .csproj files.
/// Parses project XML directly — no MSBuild dependency required.
/// </summary>
public sealed class RoslynModuleDiscovery : IModuleDiscovery
{
    public ModuleInfo[] DiscoverModules(string projectRoot)
    {
        // Try .sln first
        var slnFiles = Directory.GetFiles(projectRoot, "*.sln", SearchOption.TopDirectoryOnly);
        if (slnFiles.Length > 0)
            return DiscoverFromSolution(slnFiles[0], projectRoot);

        // Fall back to .csproj files
        var csprojFiles = Directory.GetFiles(projectRoot, "*.csproj", SearchOption.AllDirectories);
        return csprojFiles.Select(f => ParseProject(f, projectRoot)).Where(m => m != null).ToArray()!;
    }

    private ModuleInfo[] DiscoverFromSolution(string slnPath, string projectRoot)
    {
        var slnDir = Path.GetDirectoryName(slnPath)!;
        var modules = new List<ModuleInfo>();

        foreach (var line in File.ReadLines(slnPath))
        {
            // Match: Project("{...}") = "Name", "Path.csproj", "{...}"
            if (!line.StartsWith("Project(")) continue;

            var parts = line.Split('"');
            if (parts.Length < 6) continue;

            var relativePath = parts[5];
            if (!relativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) continue;

            var fullPath = Path.GetFullPath(Path.Combine(slnDir, relativePath));
            if (!File.Exists(fullPath)) continue;

            var module = ParseProject(fullPath, projectRoot);
            if (module != null) modules.Add(module);
        }

        return modules.ToArray();
    }

    private ModuleInfo? ParseProject(string csprojPath, string projectRoot)
    {
        try
        {
            var doc = XDocument.Load(csprojPath);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            var projectDir = Path.GetDirectoryName(csprojPath)!;

            // Assembly name
            var assemblyName = doc.Descendants(ns + "AssemblyName").FirstOrDefault()?.Value
                ?? doc.Descendants("AssemblyName").FirstOrDefault()?.Value
                ?? Path.GetFileNameWithoutExtension(csprojPath);

            // Source files
            var sourceFiles = Directory.GetFiles(projectDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .ToArray();

            // Project references → dependencies
            var deps = doc.Descendants(ns + "ProjectReference")
                .Concat(doc.Descendants("ProjectReference"))
                .Select(el => el.Attribute("Include")?.Value)
                .Where(v => v != null)
                .Select(v => Path.GetFileNameWithoutExtension(v!))
                .ToArray();

            // Pure detection (noEngineReferences or similar)
            bool isPure = !doc.Descendants().Any(el =>
                el.Name.LocalName == "PackageReference" ||
                el.Name.LocalName == "Reference");

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
        catch
        {
            return null;
        }
    }
}

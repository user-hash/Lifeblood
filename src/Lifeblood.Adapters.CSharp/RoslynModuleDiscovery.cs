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
            // Two strategies:
            //   SDK-style projects (modern .NET): no <Compile> items, scan filesystem.
            //   Old-format projects (Unity-generated): explicit <Compile Include="..."/> items.
            // Unity csproj files list every .cs file explicitly. Scanning the filesystem
            // would be catastrophically slow (75 projects × full recursive scan of project root).
            var compileItems = doc.Descendants()
                .Where(el => el.Name.LocalName == "Compile")
                .Select(el => el.Attribute("Include")?.Value)
                .Where(v => v != null && v.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .Select(v => Path.GetFullPath(Path.Combine(projectDir, v!)))
                .Where(_fs.FileExists)
                .ToArray();

            string[] sourceFiles;
            if (compileItems.Length > 0)
            {
                // Old-format project with explicit Compile items (Unity, legacy .NET Framework).
                // Trust the csproj — do NOT scan the filesystem. Unity regenerates csprojs
                // frequently, and scanning 75 projects rooted under the same Assets/ tree
                // causes 75 recursive scans of the entire project (~minutes of hang).
                sourceFiles = compileItems
                    .OrderBy(f => f, StringComparer.Ordinal)
                    .ToArray();
            }
            else
            {
                // SDK-style project — no Compile items, use filesystem scan
                var filesOnDisk = _fs.FindFiles(projectDir, "*.cs", recursive: true)
                    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                             && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                             && !f.Contains("/bin/") && !f.Contains("/obj/"))
                    .ToArray();
                sourceFiles = filesOnDisk
                    .OrderBy(f => f, StringComparer.Ordinal)
                    .ToArray();
            }

            // Project references → dependencies by AssemblyName (not filename)
            // SDK-style: <ProjectReference Include="..."/>
            // Unity-style: no ProjectReference, but <Reference Include="AssemblyName"/>
            var projectRefs = doc.Descendants()
                .Where(el => el.Name.LocalName == "ProjectReference")
                .Select(el => el.Attribute("Include")?.Value)
                .Where(v => v != null)
                .Select(v => ResolveReferencedAssemblyName(v!, projectDir));

            // For Unity projects: <Reference Include="Nebulae.BeatGrid.Domain">
            // These are assembly references to other project assemblies in the same solution.
            var assemblyRefs = doc.Descendants()
                .Where(el => el.Name.LocalName == "Reference")
                .Select(el => el.Attribute("Include")?.Value)
                .Where(v => v != null && !v.StartsWith("System", StringComparison.Ordinal)
                          && !v.StartsWith("Microsoft", StringComparison.Ordinal)
                          && !v.StartsWith("Unity", StringComparison.Ordinal)
                          && !v.StartsWith("Mono", StringComparison.Ordinal)
                          && !v.StartsWith("mscorlib", StringComparison.Ordinal)
                          && !v.StartsWith("netstandard", StringComparison.Ordinal)
                          && !v.StartsWith("nunit", StringComparison.OrdinalIgnoreCase)
                          && !v.StartsWith("Newtonsoft", StringComparison.Ordinal));

            var deps = projectRefs.Concat(assemblyRefs!.Select(v => v!))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // External DLL references via HintPath.
            // These are non-module, non-NuGet assemblies (e.g., Unity engine DLLs)
            // that Roslyn needs as metadata references for accurate compilation.
            var externalDlls = doc.Descendants()
                .Where(el => el.Name.LocalName == "Reference")
                .Select(el => el.Elements()
                    .FirstOrDefault(c => c.Name.LocalName == "HintPath")?.Value)
                .Where(v => v != null)
                .Select(v => Path.GetFullPath(Path.Combine(projectDir, v!)))
                .Where(_fs.FileExists)
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
                ExternalDllPaths = externalDlls,
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

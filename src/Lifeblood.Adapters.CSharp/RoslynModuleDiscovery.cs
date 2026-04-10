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

            // .sln files conventionally use Windows backslashes in project paths,
            // but this code runs cross-platform. Normalize to the host's native
            // directory separator before combining, otherwise Path.Combine on
            // Linux/macOS treats "Lib\Lib.csproj" as a single filename containing
            // a literal backslash and FileExists always returns false.
            var fullPath = Path.GetFullPath(Path.Combine(slnDir, NormalizePathSeparators(relativePath)));
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
                .Select(v => Path.GetFullPath(Path.Combine(projectDir, NormalizePathSeparators(v!))))
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

            // For Unity projects: <Reference Include="Some.AssemblyName">
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
            // HintPath values often contain Windows backslashes even in csprojs
            // generated on non-Windows tooling — normalize before combining.
            var externalDlls = doc.Descendants()
                .Where(el => el.Name.LocalName == "Reference")
                .Select(el => el.Elements()
                    .FirstOrDefault(c => c.Name.LocalName == "HintPath")?.Value)
                .Where(v => v != null)
                .Select(v => Path.GetFullPath(Path.Combine(projectDir, NormalizePathSeparators(v!))))
                .Where(_fs.FileExists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // BCL ownership detection (INV-BCL-002 / INV-BCL-004).
            // Walk every <Reference> element and ask whether it declares a base
            // class library. If ANY reference does, this module ships its own BCL
            // and the compilation builder must not inject the host BCL bundle.
            // See .claude/plans/bcl-ownership-fix.md §3 for the signal hierarchy
            // and §4 for why this lives in discovery (single source of truth).
            bool ownsBcl = doc.Descendants()
                .Where(el => el.Name.LocalName == "Reference")
                .Any(ReferenceDeclaresBcl);

            // Compilation fact: <AllowUnsafeBlocks> (INV-COMPFACT-001..003).
            // Csproj-driven compilation options follow the same discover→store→
            // consume pattern as BCL ownership: parse here once, store on
            // ModuleInfo, consume in ModuleCompilationBuilder. NEVER re-derive
            // at the compilation layer.
            //
            // The MSBuild property name is "AllowUnsafeBlocks". Csproj allows
            // either case ("true" / "True"); Unity emits "True". The match is
            // case-insensitive on the value.
            bool allowUnsafeCode = doc.Descendants()
                .Where(el => el.Name.LocalName == "AllowUnsafeBlocks")
                .Select(el => el.Value)
                .Any(v => string.Equals(v?.Trim(), "true", StringComparison.OrdinalIgnoreCase));

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
                BclOwnership = ownsBcl
                    ? BclOwnershipMode.ModuleProvided
                    : BclOwnershipMode.HostProvided,
                AllowUnsafeCode = allowUnsafeCode,
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

    // ────────────────────────────────────────────────────────────────────────
    // BCL ownership detection helpers (INV-BCL-002 / INV-BCL-004)
    //
    // A module is "BCL-owning" iff its csproj declares a base class library
    // via either:
    //   (1) a <Reference Include="netstandard|mscorlib|System.Runtime"> element,
    //       parsed as an assembly identity (handles both the bare form used by
    //       modern Unity csprojs and the strong-name form used by legacy
    //       .NET Framework / NuGet-converted csprojs), OR
    //   (2) a <Reference> with a <HintPath> child whose file basename
    //       (sans .dll extension, case-insensitive) is one of those names.
    //
    // The Include-attribute signal is primary because it's the most-authoritative
    // declaration the csproj makes. The HintPath signal is the backstop for
    // csprojs that omit Include or use a non-canonical Include name.
    //
    // Detection logic lives ONLY here. ModuleCompilationBuilder consumes the
    // resulting BclOwnership field and never re-derives it from filenames.
    // ────────────────────────────────────────────────────────────────────────

    private static bool ReferenceDeclaresBcl(XElement referenceElement)
    {
        var include = referenceElement.Attribute("Include")?.Value ?? "";
        var simpleName = ParseAssemblyIdentitySimpleName(include);
        if (IsBclSimpleName(simpleName)) return true;

        var hintPath = referenceElement.Elements()
            .FirstOrDefault(c => c.Name.LocalName == "HintPath")?.Value;
        if (string.IsNullOrEmpty(hintPath)) return false;

        var hintBasename = Path.GetFileNameWithoutExtension(hintPath);
        return IsBclSimpleName(hintBasename);
    }

    /// <summary>
    /// Extract the simple assembly name from an Include attribute value.
    /// Handles both the bare form (<c>"netstandard"</c>) and the strong-name
    /// form (<c>"netstandard, Version=2.1.0.0, Culture=neutral, PublicKeyToken=..."</c>).
    /// </summary>
    internal static string ParseAssemblyIdentitySimpleName(string includeValue)
    {
        if (string.IsNullOrWhiteSpace(includeValue)) return "";
        var commaIdx = includeValue.IndexOf(',');
        var firstToken = commaIdx < 0 ? includeValue : includeValue.Substring(0, commaIdx);
        return firstToken.Trim();
    }

    /// <summary>
    /// Returns true iff the assembly's simple name is one of the three base
    /// class libraries Lifeblood recognizes for ownership detection.
    /// <c>System.Private.CoreLib</c> is intentionally excluded — it's the
    /// .NET 8 implementation assembly, not a reference assembly that any
    /// csproj declares.
    /// </summary>
    internal static bool IsBclSimpleName(string name) =>
        name.Equals("netstandard",    StringComparison.OrdinalIgnoreCase)
     || name.Equals("mscorlib",       StringComparison.OrdinalIgnoreCase)
     || name.Equals("System.Runtime", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves a ProjectReference path to the referenced project's AssemblyName.
    /// Falls back to filename if the referenced .csproj can't be read.
    /// </summary>
    private string ResolveReferencedAssemblyName(string referencePath, string projectDir)
    {
        // ProjectReference Include values come straight out of the csproj XML
        // and conventionally use Windows backslashes ("..\Other\Other.csproj").
        // On Linux and macOS Path.Combine does not split on backslashes, so the
        // result is a single filename containing a literal backslash and
        // FileExists always returns false. Normalize before combining. Without
        // this fix, every cross-module ProjectReference on non-Windows hosts
        // silently resolves to the fallback filename-only assembly name, which
        // breaks cross-assembly edge extraction and every downstream
        // find_references / dependants query that relies on it.
        var normalized = NormalizePathSeparators(referencePath);
        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(projectDir, normalized));
            if (!_fs.FileExists(fullPath))
                return Path.GetFileNameWithoutExtension(normalized);

            var xml = _fs.ReadAllText(fullPath);
            var refDoc = XDocument.Parse(xml);
            var asmName = refDoc.Descendants()
                .FirstOrDefault(el => el.Name.LocalName == "AssemblyName")?.Value;

            return asmName ?? Path.GetFileNameWithoutExtension(normalized);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return Path.GetFileNameWithoutExtension(normalized);
        }
    }

    /// <summary>
    /// Normalize both Windows (<c>\</c>) and posix (<c>/</c>) path separators
    /// to the host's native <see cref="Path.DirectorySeparatorChar"/>. Csproj
    /// and .sln files conventionally use backslashes regardless of the
    /// platform that generated them, which breaks <see cref="Path.Combine"/>
    /// on Linux and macOS because the backslash is a legal filename
    /// character there. One helper, used at every site that takes a raw path
    /// out of csproj/sln XML and combines it with a directory.
    /// </summary>
    private static string NormalizePathSeparators(string path) =>
        path.Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
}

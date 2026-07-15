using System.Text.Json;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.PathClassification;
using Lifeblood.Domain.Results;
using Lifeblood.Domain.Workspaces;

namespace Lifeblood.Adapters.CSharp.Internal;

internal static class UnityPackageSourceVisibilityBuilder
{
    private const string PackageSourceInventoryFingerprintDomain =
        "lifeblood.unity-package-source-inventory.v1";

    public static PackageSourceVisibilityReport? Build(
        IFileSystem fs,
        string projectRoot,
        ModuleInfo[] modules,
        AnalysisConfig config)
    {
        var packagesDir = Path.Combine(projectRoot, "Packages");
        var manifestPath = Path.Combine(packagesDir, "manifest.json");
        var lockPath = Path.Combine(packagesDir, "packages-lock.json");
        var hasPackageDescriptors =
            fs.DirectoryExists(packagesDir)
            || fs.FileExists(manifestPath)
            || fs.FileExists(lockPath);
        if (!hasPackageDescriptors)
            return null;

        var packages = DiscoverPackages(fs, projectRoot, packagesDir, manifestPath, lockPath);
        var moduleBySourcePath = BuildCompiledSourceIndex(modules, fs);
        var excludeGlobs = PathGlobMatcher.Compile(config.ExcludePathGlobs);
        var descriptorPaths = new List<string>();
        if (fs.FileExists(manifestPath)) descriptorPaths.Add(Relative(projectRoot, manifestPath));
        if (fs.FileExists(lockPath)) descriptorPaths.Add(Relative(projectRoot, lockPath));

        var shapedPackages = packages.Values
            .Where(package => fs.DirectoryExists(package.RootPath))
            .Select(package => ShapePackage(fs, projectRoot, package, moduleBySourcePath, excludeGlobs))
            .Where(package => package.SourceFileCount > 0 || package.AssemblyDefinitionCount > 0)
            .OrderBy(package => package.RootPath, StringComparer.Ordinal)
            .ToArray();

        return new PackageSourceVisibilityReport
        {
            IsUnityWorkspace = true,
            DescriptorPaths = descriptorPaths
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray(),
            Packages = shapedPackages,
        };
    }

    public static IReadOnlyDictionary<string, ContentFingerprint> CaptureInputFingerprints(
        IFileSystem fs,
        string projectRoot)
    {
        var packagesDir = Path.Combine(projectRoot, "Packages");
        var manifestPath = Path.Combine(packagesDir, "manifest.json");
        var lockPath = Path.Combine(packagesDir, "packages-lock.json");
        var hasPackageDescriptors =
            fs.DirectoryExists(packagesDir)
            || fs.FileExists(manifestPath)
            || fs.FileExists(lockPath);
        var inputs = new Dictionary<string, ContentFingerprint>(PathComparer);
        if (!hasPackageDescriptors)
            return inputs;

        CaptureTextInput(fs, manifestPath, inputs);
        CaptureTextInput(fs, lockPath, inputs);

        var packages = DiscoverPackages(fs, projectRoot, packagesDir, manifestPath, lockPath);
        foreach (var package in packages.Values
            .Where(package => fs.DirectoryExists(package.RootPath))
            .OrderBy(package => package.RootPath, StringComparer.Ordinal))
        {
            CaptureTextInput(fs, Path.Combine(package.RootPath, "package.json"), inputs);
            foreach (var sourcePath in TryFindFiles(fs, package.RootPath, "*.cs"))
            {
                inputs[Path.GetFullPath(sourcePath)] = ContentFingerprint.ComputeUtf8(
                    PackageSourceInventoryFingerprintDomain,
                    "present");
            }
        }

        return inputs;
    }

    private static Dictionary<string, MutablePackage> DiscoverPackages(
        IFileSystem fs,
        string projectRoot,
        string packagesDir,
        string manifestPath,
        string lockPath)
    {
        var packages = new Dictionary<string, MutablePackage>(PathComparer);
        if (fs.FileExists(manifestPath))
            ReadManifest(fs, projectRoot, packagesDir, manifestPath, packages);
        if (fs.FileExists(lockPath))
            ReadPackagesLock(fs, projectRoot, packagesDir, lockPath, packages);
        DiscoverEmbeddedPackageDirectories(fs, packagesDir, packages);
        return packages;
    }

    private static void ReadManifest(
        IFileSystem fs,
        string projectRoot,
        string packagesDir,
        string manifestPath,
        Dictionary<string, MutablePackage> packages)
    {
        try
        {
            using var doc = JsonDocument.Parse(fs.ReadAllText(manifestPath));
            if (!doc.RootElement.TryGetProperty("dependencies", out var deps)
                || deps.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var dep in deps.EnumerateObject())
            {
                var embeddedRoot = Path.GetFullPath(Path.Combine(packagesDir, dep.Name));
                if (fs.DirectoryExists(embeddedRoot))
                    AddPackage(packages, dep.Name, embeddedRoot, "manifest");

                var value = dep.Value.ValueKind == JsonValueKind.String
                    ? dep.Value.GetString()
                    : null;
                var fileRoot = ResolveFileDependency(fs, projectRoot, packagesDir, value);
                if (fileRoot != null && fs.DirectoryExists(fileRoot))
                    AddPackage(packages, dep.Name, fileRoot, "manifest-file");
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void ReadPackagesLock(
        IFileSystem fs,
        string projectRoot,
        string packagesDir,
        string lockPath,
        Dictionary<string, MutablePackage> packages)
    {
        try
        {
            using var doc = JsonDocument.Parse(fs.ReadAllText(lockPath));
            if (!doc.RootElement.TryGetProperty("dependencies", out var deps)
                || deps.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var dep in deps.EnumerateObject())
            {
                if (dep.Value.ValueKind != JsonValueKind.Object)
                    continue;

                var source = dep.Value.TryGetProperty("source", out var sourceElement)
                    && sourceElement.ValueKind == JsonValueKind.String
                    ? sourceElement.GetString()
                    : null;
                if (string.Equals(source, "embedded", StringComparison.OrdinalIgnoreCase))
                {
                    var embeddedRoot = Path.GetFullPath(Path.Combine(packagesDir, dep.Name));
                    if (fs.DirectoryExists(embeddedRoot))
                        AddPackage(packages, dep.Name, embeddedRoot, "packages-lock");
                }

                var version = dep.Value.TryGetProperty("version", out var versionElement)
                    && versionElement.ValueKind == JsonValueKind.String
                    ? versionElement.GetString()
                    : null;
                var fileRoot = ResolveFileDependency(fs, projectRoot, packagesDir, version);
                if (fileRoot != null && fs.DirectoryExists(fileRoot))
                    AddPackage(packages, dep.Name, fileRoot, "packages-lock-file");
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DiscoverEmbeddedPackageDirectories(
        IFileSystem fs,
        string packagesDir,
        Dictionary<string, MutablePackage> packages)
    {
        if (!fs.DirectoryExists(packagesDir))
            return;

        foreach (var packageJson in TryFindFiles(fs, packagesDir, "package.json"))
        {
            var root = Path.GetDirectoryName(packageJson);
            if (string.IsNullOrEmpty(root))
                continue;

            var relative = Relative(packagesDir, root);
            if (relative.Contains('/', StringComparison.Ordinal))
                continue;

            var name = ReadJsonStringProperty(fs, packageJson, "name") ?? Path.GetFileName(root);
            AddPackage(packages, name, root, "embedded-directory");
        }
    }

    private static PackageSourceVisibilityPackage ShapePackage(
        IFileSystem fs,
        string projectRoot,
        MutablePackage package,
        IReadOnlyDictionary<string, string> moduleBySourcePath,
        IReadOnlyList<System.Text.RegularExpressions.Regex> excludeGlobs)
    {
        var packageRoot = Relative(projectRoot, package.RootPath);
        var assemblyDefinitions = TryFindFiles(fs, package.RootPath, "*.asmdef")
            .Select(path => new PackageAssemblyDefinition
            {
                Name = ReadJsonStringProperty(fs, path, "name") ?? Path.GetFileNameWithoutExtension(path),
                Path = Relative(projectRoot, path),
            })
            .OrderBy(asmdef => asmdef.Path, StringComparer.Ordinal)
            .ToArray();

        var assemblyByDirectory = assemblyDefinitions
            .Select(asmdef => new AssemblyRoot(
                Path.GetFullPath(Path.Combine(projectRoot, Path.GetDirectoryName(asmdef.Path) ?? "")),
                asmdef.Name))
            .OrderByDescending(item => item.Directory.Length)
            .ToArray();

        var files = TryFindFiles(fs, package.RootPath, "*.cs")
            .Select(path =>
            {
                var absolute = Path.GetFullPath(path);
                var relative = Relative(projectRoot, absolute);
                var expectedAssembly = FindExpectedAssembly(absolute, assemblyByDirectory);
                if (PathGlobMatcher.MatchesAny(excludeGlobs, relative))
                {
                    return new PackageSourceVisibilityFile
                    {
                        PackageName = package.Name,
                        PackageRoot = packageRoot,
                        Path = relative,
                        Status = PackageSourceVisibilityStatus.Excluded,
                        Reason = PackageSourceVisibilityReason.AnalysisExcludePath,
                        ExpectedAssembly = expectedAssembly,
                    };
                }

                if (moduleBySourcePath.TryGetValue(absolute, out var moduleName))
                {
                    return new PackageSourceVisibilityFile
                    {
                        PackageName = package.Name,
                        PackageRoot = packageRoot,
                        Path = relative,
                        Status = PackageSourceVisibilityStatus.Included,
                        Reason = PackageSourceVisibilityReason.CompilationMembership,
                        ModuleName = moduleName,
                        ExpectedAssembly = expectedAssembly,
                    };
                }

                return new PackageSourceVisibilityFile
                {
                    PackageName = package.Name,
                    PackageRoot = packageRoot,
                    Path = relative,
                    Status = PackageSourceVisibilityStatus.Unbound,
                    Reason = PackageSourceVisibilityReason.NotInProjectDescriptors,
                    ExpectedAssembly = expectedAssembly,
                };
            })
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();

        return new PackageSourceVisibilityPackage
        {
            Name = package.Name,
            RootPath = packageRoot,
            DescriptorSources = package.DescriptorSources
                .OrderBy(source => source, StringComparer.Ordinal)
                .ToArray(),
            AssemblyDefinitions = assemblyDefinitions,
            Files = files,
        };
    }

    private static Dictionary<string, string> BuildCompiledSourceIndex(ModuleInfo[] modules, IFileSystem fs)
    {
        var index = new Dictionary<string, string>(PathComparer);
        foreach (var module in modules)
        {
            foreach (var file in module.FilePaths)
            {
                var path = Path.GetFullPath(file);
                if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    && fs.FileExists(path))
                {
                    index[path] = module.Name;
                }
            }
        }

        return index;
    }

    private static string? FindExpectedAssembly(
        string sourcePath,
        IReadOnlyList<AssemblyRoot> assemblyByDirectory)
    {
        var sourceDirectory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrEmpty(sourceDirectory))
            return null;

        foreach (var item in assemblyByDirectory)
        {
            var directory = item.Directory;
            if (PathComparer.Equals(sourceDirectory, directory)
                || sourceDirectory.StartsWith(directory + Path.DirectorySeparatorChar, PathComparison)
                || sourceDirectory.Replace('\\', '/').StartsWith(directory.Replace('\\', '/') + "/", PathComparison))
            {
                return item.Name;
            }
        }

        return null;
    }

    private static string? ResolveFileDependency(
        IFileSystem fs,
        string projectRoot,
        string packagesDir,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var raw = Uri.UnescapeDataString(value.Substring("file:".Length).Trim());
        if (raw.Length == 0)
            return null;

        var normalized = raw.Replace('/', Path.DirectorySeparatorChar);
        var projectRelative = Path.IsPathRooted(normalized)
            ? Path.GetFullPath(normalized)
            : Path.GetFullPath(Path.Combine(projectRoot, normalized));
        if (fs.DirectoryExists(projectRelative))
            return projectRelative;

        return Path.IsPathRooted(normalized)
            ? projectRelative
            : Path.GetFullPath(Path.Combine(packagesDir, normalized));
    }

    private static string? ReadJsonStringProperty(IFileSystem fs, string path, string propertyName)
    {
        try
        {
            using var doc = JsonDocument.Parse(fs.ReadAllText(path));
            return doc.RootElement.TryGetProperty(propertyName, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string[] TryFindFiles(IFileSystem fs, string directory, string pattern)
    {
        try
        {
            return fs.DirectoryExists(directory)
                ? fs.FindFiles(directory, pattern, recursive: true)
                : Array.Empty<string>();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static void CaptureTextInput(
        IFileSystem fs,
        string path,
        IDictionary<string, ContentFingerprint> inputs)
    {
        if (!fs.FileExists(path))
            return;

        try
        {
            inputs[Path.GetFullPath(path)] = ContentFingerprint.ComputeUtf8(
                "lifeblood.workspace-descriptor-content.v1",
                fs.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void AddPackage(
        Dictionary<string, MutablePackage> packages,
        string name,
        string rootPath,
        string source)
    {
        var root = Path.GetFullPath(rootPath);
        if (!packages.TryGetValue(root, out var package))
        {
            package = new MutablePackage(name, root);
            packages[root] = package;
        }

        if (!string.IsNullOrWhiteSpace(name))
            package.Name = name;
        package.DescriptorSources.Add(source);
    }

    private static string Relative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static StringComparer PathComparer { get; }
        = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison { get; }
        = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed class MutablePackage
    {
        public MutablePackage(string name, string rootPath)
        {
            Name = name;
            RootPath = rootPath;
        }

        public string Name { get; set; }
        public string RootPath { get; }
        public HashSet<string> DescriptorSources { get; } = new(StringComparer.Ordinal);
    }

    private sealed record AssemblyRoot(string Directory, string Name);
}

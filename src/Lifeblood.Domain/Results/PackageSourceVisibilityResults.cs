namespace Lifeblood.Domain.Results;

/// <summary>
/// Machine-readable source visibility for Unity packages under the current
/// analysis scope. The adapter owns how package descriptors map to compiled
/// source; the MCP layer only projects this immutable receipt onto the wire.
/// </summary>
public sealed class PackageSourceVisibilityReport
{
    public bool IsUnityWorkspace { get; init; }
    public string[] DescriptorPaths { get; init; } = Array.Empty<string>();
    public PackageSourceVisibilityPackage[] Packages { get; init; } = Array.Empty<PackageSourceVisibilityPackage>();

    public int PackageCount => Packages.Length;
    public int IncludedSourceFileCount => Packages.Sum(package => package.IncludedSourceFileCount);
    public int ExcludedSourceFileCount => Packages.Sum(package => package.ExcludedSourceFileCount);
    public int UnboundSourceFileCount => Packages.Sum(package => package.UnboundSourceFileCount);
}

public sealed class PackageSourceVisibilityPackage
{
    public required string Name { get; init; }
    public required string RootPath { get; init; }
    public string[] DescriptorSources { get; init; } = Array.Empty<string>();
    public PackageAssemblyDefinition[] AssemblyDefinitions { get; init; } = Array.Empty<PackageAssemblyDefinition>();
    public PackageSourceVisibilityFile[] Files { get; init; } = Array.Empty<PackageSourceVisibilityFile>();

    public int AssemblyDefinitionCount => AssemblyDefinitions.Length;
    public int SourceFileCount => Files.Length;
    public int IncludedSourceFileCount => Files.Count(file => file.Status == PackageSourceVisibilityStatus.Included);
    public int ExcludedSourceFileCount => Files.Count(file => file.Status == PackageSourceVisibilityStatus.Excluded);
    public int UnboundSourceFileCount => Files.Count(file => file.Status == PackageSourceVisibilityStatus.Unbound);
}

public sealed class PackageAssemblyDefinition
{
    public required string Name { get; init; }
    public required string Path { get; init; }
}

public sealed class PackageSourceVisibilityFile
{
    public required string PackageName { get; init; }
    public required string PackageRoot { get; init; }
    public required string Path { get; init; }
    public required string Status { get; init; }
    public required string Reason { get; init; }
    public string? ModuleName { get; init; }
    public string? ExpectedAssembly { get; init; }
}

public static class PackageSourceVisibilityStatus
{
    public const string Included = "included";
    public const string Excluded = "excluded";
    public const string Unbound = "unbound";
}

public static class PackageSourceVisibilityReason
{
    public const string CompilationMembership = "compilation-membership";
    public const string AnalysisExcludePath = "analysis-exclude-path";
    public const string NotInProjectDescriptors = "not-in-project-descriptors";
}

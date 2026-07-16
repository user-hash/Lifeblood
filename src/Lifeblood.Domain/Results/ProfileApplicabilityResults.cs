namespace Lifeblood.Domain.Results;

/// <summary>
/// Machine-readable provenance for define-profile module applicability. The
/// language adapter owns how descriptors map to profiles; server layers only
/// project this immutable receipt.
/// </summary>
public sealed class ProfileApplicabilityReport
{
    public bool IsUnityWorkspace { get; init; }
    public string[] Profiles { get; init; } = Array.Empty<string>();
    public ProfileApplicabilityModule[] Modules { get; init; } = Array.Empty<ProfileApplicabilityModule>();
    public Dictionary<string, int> IncludedModuleCountsByProfile { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ExcludedModuleCountsByProfile { get; init; } = new(StringComparer.Ordinal);

    public int ModuleCount => Modules.Length;
    public bool HasExcludedModules => Modules.Any(module => module.ExcludedProfiles.Length > 0);
}

public sealed class ProfileApplicabilityModule
{
    public required string Name { get; init; }
    public string? ProjectFile { get; init; }
    public string? UnityProjectType { get; init; }
    public bool IsEditorOnly { get; init; }
    public string[] IncludedProfiles { get; init; } = Array.Empty<string>();
    public string[] ExcludedProfiles { get; init; } = Array.Empty<string>();
    public ProfileApplicabilityExclusion[] Exclusions { get; init; } = Array.Empty<ProfileApplicabilityExclusion>();
}

public sealed class ProfileApplicabilityExclusion
{
    public required string Profile { get; init; }
    public required string Reason { get; init; }
}

public static class ProfileApplicabilityReason
{
    public const string EditorOnlyModuleExcludedByProfile = "editorOnlyModuleExcludedByProfile";
    public const string ModuleExcludedByProfile = "moduleExcludedByProfile";
}

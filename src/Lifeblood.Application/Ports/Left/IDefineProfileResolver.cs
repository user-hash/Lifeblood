namespace Lifeblood.Application.Ports.Left;

/// <summary>
/// INV-MULTI-DEFINE-RESOLVER-001. Resolves the set of preprocessor-symbol
/// profiles a project should be analyzed under. Wave 6.A entry point for
/// L-LIM-001 multi-define union analyze.
/// </summary>
public interface IDefineProfileResolver
{
    IReadOnlyList<DefineProfile> ResolveProfiles(string projectRoot);
}

/// <summary>
/// One named compile-profile relative to a module's baseline preprocessor-
/// symbol set (csproj DefineConstants). The active define set per profile
/// is computed as <c>(BASE - RemoveDefines) ∪ AddDefines</c>, ordinal-sorted
/// for byte-stable provenance. INV-MULTI-DEFINE-RESOLVER-001.
/// </summary>
public sealed class DefineProfile
{
    /// <summary>Canonical profile name. e.g. "Editor", "Player".</summary>
    public required string Name { get; init; }

    /// <summary>Symbols added relative to baseline.</summary>
    public required string[] AddDefines { get; init; }

    /// <summary>Symbols removed relative to baseline.</summary>
    public required string[] RemoveDefines { get; init; }
}

using Lifeblood.Application.Ports.Left;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// INV-MULTI-DEFINE-RESOLVER-001 default adapter. Returns a single
/// identity Editor profile — preserves single-profile back-compat for
/// every callsite that does not opt into multi-profile analyze.
/// </summary>
public sealed class DefaultDefineProfileResolver : IDefineProfileResolver
{
    /// <summary>Canonical name of the identity profile.</summary>
    public const string EditorProfileName = "Editor";

    private static readonly IReadOnlyList<DefineProfile> SingleEditorProfile = new[]
    {
        new DefineProfile
        {
            Name = EditorProfileName,
            AddDefines = Array.Empty<string>(),
            RemoveDefines = Array.Empty<string>(),
        },
    };

    public IReadOnlyList<DefineProfile> ResolveProfiles(string projectRoot)
        => SingleEditorProfile;
}

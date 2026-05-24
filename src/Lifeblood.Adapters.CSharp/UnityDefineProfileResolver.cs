using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Left;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// INV-MULTI-DEFINE-UNITY-RESOLVER-001. 2-profile MVP for Unity workspaces:
/// <c>Editor</c> identity + <c>Player</c> = baseline minus the Unity Editor
/// discriminator family. Closes L-LIM-001's load-bearing root cause —
/// <c>#if !UNITY_EDITOR</c>-gated callsites flip from inactive to active
/// under Player. Non-Unity workspaces (no <c>Library/</c> at root) fall
/// back to the identity Editor single-profile shape so the resolver is
/// safe to inject everywhere.
/// </summary>
public sealed class UnityDefineProfileResolver : IDefineProfileResolver
{
    public const string EditorProfileName = "Editor";
    public const string PlayerProfileName = "Player";

    /// <summary>
    /// INV-MULTI-DEFINE-UNITY-RESOLVER-001 canonical Editor-discriminator
    /// vocabulary. The Player profile removes EVERY symbol in this set
    /// from the baseline so <c>#if !UNITY_EDITOR</c> / <c>#if !UNITY_EDITOR_WIN</c>
    /// / etc. branches activate. Eternal: any new <c>UNITY_EDITOR*</c>
    /// symbol Unity ships must extend this set.
    /// </summary>
    internal static readonly string[] UnityEditorDiscriminators =
    {
        "UNITY_EDITOR",
        "UNITY_EDITOR_WIN",
        "UNITY_EDITOR_64",
        "UNITY_EDITOR_OSX",
        "UNITY_EDITOR_LINUX",
    };

    private static readonly IReadOnlyList<DefineProfile> NonUnityFallback = new[]
    {
        new DefineProfile
        {
            Name = EditorProfileName,
            AddDefines = Array.Empty<string>(),
            RemoveDefines = Array.Empty<string>(),
        },
    };

    private static readonly IReadOnlyList<DefineProfile> UnityProfiles = new[]
    {
        new DefineProfile
        {
            Name = EditorProfileName,
            AddDefines = Array.Empty<string>(),
            RemoveDefines = Array.Empty<string>(),
        },
        new DefineProfile
        {
            Name = PlayerProfileName,
            AddDefines = Array.Empty<string>(),
            RemoveDefines = UnityEditorDiscriminators,
        },
    };

    private readonly IFileSystem _fs;

    public UnityDefineProfileResolver(IFileSystem fs)
    {
        _fs = fs;
    }

    public IReadOnlyList<DefineProfile> ResolveProfiles(string projectRoot)
    {
        var libraryPath = Path.Combine(projectRoot, "Library");
        return _fs.DirectoryExists(libraryPath) ? UnityProfiles : NonUnityFallback;
    }
}

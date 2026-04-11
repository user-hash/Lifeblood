namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Single source of truth for parsing path-shaped values out of csproj /
/// sln XML attributes. Both production discovery code and architecture
/// ratchet tests must route through this helper instead of rolling their
/// own normalization, otherwise the two drift and Linux CI breaks every
/// time someone touches a ProjectReference.
///
/// Concretely: csproj <c>ProjectReference Include</c>, <c>Compile Include</c>,
/// and <c>Reference HintPath</c> values are authored in Visual Studio and
/// MSBuild conventions — Windows backslash separators (e.g.
/// <c>..\Lifeblood.Domain\Lifeblood.Domain.csproj</c>). MSBuild itself
/// normalizes these on every host so the build works everywhere, but the
/// raw XML still contains backslashes on disk regardless of OS.
///
/// .NET's <see cref="System.IO.Path"/> APIs treat backslash as a path
/// separator only on Windows. On Linux and macOS the backslash is a
/// literal character, so <c>Path.GetFileNameWithoutExtension(@"..\X\X.csproj")</c>
/// returns the entire string as one filename. <c>Path.Combine</c> has
/// the same problem. Every site that touches a raw csproj path on a
/// non-Windows host must normalize first.
///
/// History: commits c9606b9 and 562dc6a normalized csproj/sln raw-path
/// sites in production discovery code, but the architecture ratchet
/// test in <c>ArchitectureInvariantTests</c> was missed and silently
/// failed every CI build on Linux until this helper was extracted and
/// shared. The lesson: anywhere two code paths must agree about how a
/// csproj string is interpreted, a single helper is the only way to
/// keep them aligned forever.
/// </summary>
internal static class CsprojPaths
{
    /// <summary>
    /// Normalize Windows backslash separators in a raw csproj attribute
    /// value to the host OS path separator. Idempotent on already-normalized
    /// strings.
    /// </summary>
    public static string NormalizeSeparators(string rawCsprojPath)
    {
        if (string.IsNullOrEmpty(rawCsprojPath)) return rawCsprojPath;
        return rawCsprojPath
            .Replace('\\', System.IO.Path.DirectorySeparatorChar)
            .Replace('/', System.IO.Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Extract the referenced module's bare name (no extension, no path)
    /// from a <c>ProjectReference Include</c> attribute value such as
    /// <c>..\Lifeblood.Domain\Lifeblood.Domain.csproj</c>. Cross-platform:
    /// always returns <c>"Lifeblood.Domain"</c> for that input regardless
    /// of host OS.
    /// </summary>
    public static string GetReferencedModuleName(string projectReferenceInclude)
    {
        if (string.IsNullOrEmpty(projectReferenceInclude)) return string.Empty;
        var normalized = NormalizeSeparators(projectReferenceInclude);
        return System.IO.Path.GetFileNameWithoutExtension(normalized);
    }
}

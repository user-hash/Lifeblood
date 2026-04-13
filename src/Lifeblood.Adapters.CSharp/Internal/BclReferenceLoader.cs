using Microsoft.CodeAnalysis;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Loads .NET BCL assemblies as metadata references for Roslyn compilations.
/// Prefers <b>reference assemblies</b> from the SDK pack
/// (<c>dotnet/packs/Microsoft.NETCore.App.Ref/{ver}/ref/net{tfm}/</c>) because
/// Roslyn resolves types against reference assembly metadata, not implementation
/// metadata. Implementation assemblies (from <c>dotnet/shared/</c>) have different
/// type-forwarding that causes CS0246 ("type not found") for <c>List&lt;&gt;</c>,
/// <c>HashSet&lt;&gt;</c>, <c>StringComparer</c>, etc. — silently breaking
/// <c>GetSymbolInfo</c> for 42% of invocations in a typical workspace.
/// Falls back to implementation assemblies when the SDK pack is not installed
/// (standalone runtime without SDK).
/// Cached once via Lazy — same for all modules in the workspace.
/// </summary>
internal static class BclReferenceLoader
{
    public static readonly Lazy<MetadataReference[]> References = new(Load);

    private static MetadataReference[] Load()
    {
        // Prefer reference assemblies from the SDK pack — correct type metadata
        // for Roslyn semantic analysis. Implementation assemblies from the runtime
        // directory work but reference assemblies are the canonical input for
        // compiler-level analysis (smaller, no IL, only type signatures).
        var refAssemblies = TryLoadReferenceAssemblies();
        if (refAssemblies != null && refAssemblies.Length > 0)
            return refAssemblies;

        // Fallback: implementation assemblies from the runtime directory.
        // Works for standalone runtimes without SDK.
        return LoadImplementationAssemblies();
    }

    /// <summary>
    /// Discover reference assemblies from the .NET SDK pack directory.
    /// Path: dotnet/packs/Microsoft.NETCore.App.Ref/{version}/ref/net{major}.0/
    /// Discovery: walk up from the runtime directory to the dotnet root,
    /// then down into packs/ with the matching runtime version.
    /// </summary>
    private static MetadataReference[]? TryLoadReferenceAssemblies()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir == null) return null;

        // runtimeDir = dotnet/shared/Microsoft.NETCore.App/{version}/
        // dotnet root = 3 levels up
        var dotnetRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir)));
        if (dotnetRoot == null) return null;

        var runtimeVersion = Path.GetFileName(runtimeDir); // e.g. "8.0.25"
        var majorVersion = Environment.Version.Major;       // e.g. 8
        var tfm = $"net{majorVersion}.0";                   // e.g. "net8.0"

        // Try exact version match first, then scan for any version of the same major.
        var packsBase = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref");
        if (!Directory.Exists(packsBase)) return null;

        var refDir = Path.Combine(packsBase, runtimeVersion!, "ref", tfm);
        if (!Directory.Exists(refDir))
        {
            // Exact version not found — try latest matching major version.
            var candidates = Directory.GetDirectories(packsBase)
                .Where(d => Path.GetFileName(d)!.StartsWith($"{majorVersion}.", StringComparison.Ordinal))
                .OrderByDescending(d => d)
                .FirstOrDefault();
            if (candidates == null) return null;
            refDir = Path.Combine(candidates, "ref", tfm);
            if (!Directory.Exists(refDir)) return null;
        }

        return Directory.GetFiles(refDir, "*.dll")
            .Select(path =>
            {
                try { return (MetadataReference)MetadataReference.CreateFromFile(path); }
                catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException) { return null; }
            })
            .Where(r => r != null)
            .ToArray()!;
    }

    private static MetadataReference[] LoadImplementationAssemblies()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir == null) return Array.Empty<MetadataReference>();

        return Directory.GetFiles(runtimeDir, "*.dll")
            .Where(path => !IsNativeDll(path))
            .Select(path =>
            {
                try { return (MetadataReference)MetadataReference.CreateFromFile(path); }
                catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException) { return null; }
            })
            .Where(r => r != null)
            .ToArray()!;
    }

    /// <summary>
    /// Filter out native (non-.NET) DLLs that Roslyn cannot load as metadata.
    /// These cause CS0009 "Metadata file could not be opened" errors.
    /// Check by attempting to read PE metadata — native DLLs have no CLI header.
    /// </summary>
    internal static bool IsNativeDll(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var peReader = new System.Reflection.PortableExecutable.PEReader(stream);
            return !peReader.HasMetadata;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return true; // If we can't read it, treat as native
        }
    }
}

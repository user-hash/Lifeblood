using Microsoft.CodeAnalysis;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Loads all .NET runtime assemblies as metadata references.
/// Cached once via Lazy — same for all modules in the workspace.
/// </summary>
internal static class BclReferenceLoader
{
    public static readonly Lazy<MetadataReference[]> References = new(Load);

    private static MetadataReference[] Load()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir == null) return Array.Empty<MetadataReference>();

        return Directory.GetFiles(runtimeDir, "*.dll")
            .Where(path => !IsNativeDll(path))
            .Select(path =>
            {
                try { return (MetadataReference)MetadataReference.CreateFromFile(path); }
                catch { return null; }
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
        catch
        {
            return true; // If we can't read it, treat as native
        }
    }
}

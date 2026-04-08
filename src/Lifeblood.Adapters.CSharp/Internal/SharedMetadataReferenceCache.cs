using Microsoft.CodeAnalysis;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Deduplicates MetadataReference instances across modules.
/// Without this, 100 modules sharing Newtonsoft.Json create 100 independent
/// MetadataReference objects for the same DLL (Roslyn does not cache internally).
/// One cache per AnalyzeWorkspace call — shared across all modules in the workspace.
/// </summary>
internal sealed class SharedMetadataReferenceCache
{
    private readonly Dictionary<string, MetadataReference> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a cached MetadataReference for the given DLL path.
    /// First call for a path creates the reference; subsequent calls reuse it.
    /// </summary>
    public MetadataReference GetOrCreate(string dllPath)
    {
        if (_cache.TryGetValue(dllPath, out var existing))
            return existing;

        var reference = MetadataReference.CreateFromFile(dllPath);
        _cache[dllPath] = reference;
        return reference;
    }

    /// <summary>Number of unique DLLs loaded (for diagnostics).</summary>
    public int Count => _cache.Count;
}

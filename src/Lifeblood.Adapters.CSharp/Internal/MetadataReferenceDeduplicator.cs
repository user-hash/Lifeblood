using System.Reflection.Metadata;
using Microsoft.CodeAnalysis;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Collapses a compilation reference set down to one
/// <see cref="MetadataReference"/> per assembly simple-name, keeping the
/// highest <see cref="AssemblyIdentity.Version"/> when duplicates appear.
/// Mirrors MSBuild's <c>&lt;AutoUnify&gt;true&lt;/AutoUnify&gt;</c> default
/// for SDK-style projects so a Lifeblood compilation sees the same
/// resolved reference graph an MSBuild invocation would. Stops Roslyn
/// from emitting CS1701 / CS1702 / CS1705 once per type-ref when the
/// raw reference set carries multiple identities for the same simple
/// name (BCL ref pack + NuGet contract assembly is the canonical
/// collision; the empirical 7,537 × CS1701 measured against
/// <c>Lifeblood.Tests</c> originated from this class).
/// Refs whose metadata cannot be read as an assembly (modules, native
/// DLLs sneaking past the loader filter, in-memory compilation refs
/// without an emitted identity) pass through unchanged — dedup is a
/// best-effort normalization, not a gate. INV-DIAGNOSTIC-NUGET-BINDING-PARITY-001.
/// </summary>
internal static class MetadataReferenceDeduplicator
{
    /// <summary>
    /// Apply MSBuild-equivalent assembly identity unification to a
    /// reference collection. Order of refs whose identity can be read
    /// is determined by the surviving entry per simple name; refs
    /// without a readable identity are appended after, in their
    /// original relative order.
    /// </summary>
    internal static IReadOnlyList<MetadataReference> Deduplicate(
        IEnumerable<MetadataReference> references)
    {
        var byName = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
        var bestVersionByName = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
        var unkeyed = new List<MetadataReference>();

        foreach (var reference in references)
        {
            var identity = TryReadAssemblyIdentity(reference);
            if (identity == null)
            {
                unkeyed.Add(reference);
                continue;
            }

            var name = identity.Name;
            var version = identity.Version;
            if (bestVersionByName.TryGetValue(name, out var bestSoFar))
            {
                if (version > bestSoFar)
                {
                    byName[name] = reference;
                    bestVersionByName[name] = version;
                }
                continue;
            }

            byName[name] = reference;
            bestVersionByName[name] = version;
        }

        var result = new List<MetadataReference>(byName.Count + unkeyed.Count);
        result.AddRange(byName.Values);
        result.AddRange(unkeyed);
        return result;
    }

    /// <summary>
    /// Read the assembly simple-name and version off a metadata reference
    /// without forcing a compilation. Routes through Roslyn's public
    /// <see cref="AssemblyMetadata.GetModules"/> +
    /// <see cref="ModuleMetadata.GetMetadataReader"/> primitive so the
    /// identity comes directly from the PE's <c>AssemblyDef</c> row —
    /// the same data MSBuild reads when applying <c>&lt;AutoUnify&gt;</c>.
    /// Module-only references (<see cref="ModuleMetadata"/>) and references
    /// whose backing data fails to parse return null — they flow through
    /// dedup as-is.
    /// </summary>
    private static AssemblyIdentity? TryReadAssemblyIdentity(MetadataReference reference)
    {
        if (reference is not PortableExecutableReference peRef) return null;
        try
        {
            if (peRef.GetMetadata() is not AssemblyMetadata asm) return null;
            var modules = asm.GetModules();
            if (modules.IsDefaultOrEmpty) return null;
            var reader = modules[0].GetMetadataReader();
            var def = reader.GetAssemblyDefinition();
            var name = reader.GetString(def.Name);
            if (string.IsNullOrEmpty(name)) return null;
            return new AssemblyIdentity(name, def.Version);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Pass through — the loader will surface the underlying load failure as a regular diagnostic.
        }
        return null;
    }
}

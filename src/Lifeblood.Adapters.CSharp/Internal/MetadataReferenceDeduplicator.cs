using System.Reflection.Metadata;
using Microsoft.CodeAnalysis;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Collapses a compilation reference set down to one
/// <see cref="MetadataReference"/> per distinct assembly identity
/// (simple-name + culture + public-key), keeping the highest
/// <see cref="AssemblyIdentity.Version"/> within each identity
/// bucket. Mirrors MSBuild's <c>&lt;AutoUnify&gt;true&lt;/AutoUnify&gt;</c>
/// default for SDK-style projects so a Lifeblood compilation sees the
/// same resolved reference graph an MSBuild invocation would. Stops
/// Roslyn from emitting CS1701 / CS1702 / CS1705 once per type-ref
/// when the raw reference set carries multiple versions of the same
/// assembly identity (BCL ref pack + NuGet contract assembly is the
/// canonical collision; the empirical 7,537 × CS1701 measured against
/// <c>Lifeblood.Tests</c> originated from this class).
///
/// **Identity granularity** (post-W6 audit). Pre-fix the dedup keyed
/// on the simple name alone, which would collapse two legitimately
/// distinct identities sharing a simple name but disagreeing on
/// culture / public-key (e.g. an official Microsoft-signed assembly
/// + a third-party shim with the same simple name). The bucket key
/// is now <c>name|culture|publicKey</c> so distinct identities
/// survive; only true version collisions inside a bucket unify.
/// Public-key bytes are compared as a raw hex string — same blob =
/// same identity — which is strictly more conservative than the
/// truncated 8-byte token MSBuild typically prints, and avoids the
/// token-collision corner case entirely.
///
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
    /// is determined by the surviving entry per identity bucket; refs
    /// without a readable identity are appended after, in their
    /// original relative order.
    /// </summary>
    internal static IReadOnlyList<MetadataReference> Deduplicate(
        IEnumerable<MetadataReference> references)
    {
        var byBucket = new Dictionary<string, MetadataReference>(StringComparer.Ordinal);
        var bestVersionByBucket = new Dictionary<string, Version>(StringComparer.Ordinal);
        var unkeyed = new List<MetadataReference>();

        foreach (var reference in references)
        {
            var fingerprint = TryReadIdentityFingerprint(reference);
            if (fingerprint == null)
            {
                unkeyed.Add(reference);
                continue;
            }

            var (bucketKey, version) = fingerprint.Value;
            if (bestVersionByBucket.TryGetValue(bucketKey, out var bestSoFar))
            {
                if (version > bestSoFar)
                {
                    byBucket[bucketKey] = reference;
                    bestVersionByBucket[bucketKey] = version;
                }
                continue;
            }

            byBucket[bucketKey] = reference;
            bestVersionByBucket[bucketKey] = version;
        }

        var result = new List<MetadataReference>(byBucket.Count + unkeyed.Count);
        result.AddRange(byBucket.Values);
        result.AddRange(unkeyed);
        return result;
    }

    /// <summary>
    /// Read the strong-identity fingerprint off a metadata reference
    /// without forcing a compilation. The fingerprint is a
    /// version-stripped identity bucket key (<c>name|culture|publicKey</c>)
    /// plus the actual <see cref="Version"/>; the caller bucketizes
    /// references by key and unifies by version inside each bucket.
    /// Routes through Roslyn's public <see cref="AssemblyMetadata.GetModules"/>
    /// + <see cref="ModuleMetadata.GetMetadataReader"/> primitive so the
    /// identity comes directly from the PE's <c>AssemblyDef</c> row.
    /// Module-only references (<see cref="ModuleMetadata"/>) and references
    /// whose backing data fails to parse return null — they flow through
    /// dedup as-is.
    /// </summary>
    private static (string BucketKey, Version Version)? TryReadIdentityFingerprint(MetadataReference reference)
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

            var culture = def.Culture.IsNil ? string.Empty : reader.GetString(def.Culture);
            var publicKeyHex = def.PublicKey.IsNil
                ? string.Empty
                : Convert.ToHexString(reader.GetBlobBytes(def.PublicKey));

            // Bucket key is case-insensitive on name + culture (MSBuild
            // identity comparison rule), case-sensitive on the public-key
            // hex (hex digits round-trip identically through ToHexString).
            var bucketKey = string.Concat(
                name.ToLowerInvariant(), "|",
                culture.ToLowerInvariant(), "|",
                publicKeyHex);
            return (bucketKey, def.Version);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Pass through — the loader will surface the underlying load failure as a regular diagnostic.
        }
        return null;
    }
}

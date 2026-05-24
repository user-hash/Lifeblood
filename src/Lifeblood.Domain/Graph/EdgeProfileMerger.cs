namespace Lifeblood.Domain.Graph;

/// <summary>
/// INV-MULTI-DEFINE-EDGE-PROFILES-001. Merges the <see cref="Edge.Profiles"/>
/// sets when two edges with identical <see cref="EdgeIdentityKey"/> are
/// observed under different define profiles. First-write-wins for every
/// other field; Profiles is the only mergeable axis.
/// </summary>
public static class EdgeProfileMerger
{
    /// <summary>
    /// Returns the existing edge unchanged when no profile merge is needed,
    /// or a new edge carrying the union of <paramref name="existing"/> and
    /// <paramref name="incoming"/> profile sets. Union is ordinal-sorted +
    /// distinct for byte-stable provenance.
    /// </summary>
    public static Edge MergeProfiles(Edge existing, Edge incoming)
    {
        var existingProfiles = existing.Profiles;
        var incomingProfiles = incoming.Profiles;

        if (existingProfiles == null && incomingProfiles == null)
            return existing;

        if (incomingProfiles == null || incomingProfiles.Count == 0)
            return existing;

        if (existingProfiles == null || existingProfiles.Count == 0)
        {
            return CloneWithProfiles(existing, NormalizeProfiles(incomingProfiles));
        }

        var unioned = new HashSet<string>(existingProfiles, StringComparer.Ordinal);
        var changed = false;
        foreach (var p in incomingProfiles)
        {
            if (unioned.Add(p)) changed = true;
        }

        if (!changed) return existing;

        var merged = unioned.OrderBy(p => p, StringComparer.Ordinal).ToArray();
        return CloneWithProfiles(existing, merged);
    }

    private static IReadOnlyList<string> NormalizeProfiles(IReadOnlyList<string> profiles)
    {
        if (profiles.Count == 0) return profiles;
        return profiles
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
    }

    private static Edge CloneWithProfiles(Edge source, IReadOnlyList<string> profiles)
        => new()
        {
            SourceId = source.SourceId,
            TargetId = source.TargetId,
            Kind = source.Kind,
            Evidence = source.Evidence,
            Properties = source.Properties,
            CallSite = source.CallSite,
            Profiles = profiles,
        };
}

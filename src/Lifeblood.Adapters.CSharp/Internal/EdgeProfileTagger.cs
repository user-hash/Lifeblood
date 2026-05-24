using Lifeblood.Domain.Graph;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// INV-MULTI-DEFINE-EDGE-PROFILES-001 / INV-MULTI-DEFINE-ANALYZE-001.
/// Wraps each edge with <c>Profiles = [profileName]</c>; pure clone, no
/// mutation. Single-profile back-compat: caller passes <c>profileName = null</c>
/// → returns edges unchanged (Profiles stays null).
/// </summary>
internal static class EdgeProfileTagger
{
    public static List<Edge> Tag(IEnumerable<Edge> edges, string? profileName)
    {
        if (profileName == null)
            return edges as List<Edge> ?? edges.ToList();

        var tag = new[] { profileName };
        var result = new List<Edge>();
        foreach (var e in edges)
        {
            result.Add(new Edge
            {
                SourceId = e.SourceId,
                TargetId = e.TargetId,
                Kind = e.Kind,
                Evidence = e.Evidence,
                Properties = e.Properties,
                CallSite = e.CallSite,
                Profiles = tag,
            });
        }
        return result;
    }
}

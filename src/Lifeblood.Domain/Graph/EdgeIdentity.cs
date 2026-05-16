namespace Lifeblood.Domain.Graph;

internal readonly record struct EdgeIdentityKey(
    string SourceId,
    string TargetId,
    EdgeKind Kind,
    string Role);

internal static class EdgeIdentity
{
    public static EdgeIdentityKey KeyFor(Edge edge)
        => new(edge.SourceId, edge.TargetId, edge.Kind, SemanticRole(edge));

    private static string SemanticRole(Edge edge)
    {
        if (edge.Properties.TryGetValue("native.referenceKind", out var nativeReferenceKind))
            return "native.referenceKind=" + nativeReferenceKind;

        if (edge.Properties.TryGetValue("native.kind", out var nativeKind))
            return "native.kind=" + nativeKind;

        if (edge.Properties.TryGetValue("native.callKind", out var nativeCallKind))
            return "native.callKind=" + nativeCallKind;

        return string.Join(
            "\u001f",
            edge.Properties
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key + "=" + kv.Value));
    }
}

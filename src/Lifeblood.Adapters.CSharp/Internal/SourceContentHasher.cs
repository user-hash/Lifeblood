using Lifeblood.Domain.Workspaces;

namespace Lifeblood.Adapters.CSharp.Internal;

internal static class SourceContentHasher
{
    public static ContentFingerprint HashText(string text)
        => ContentFingerprint.ComputeUtf8("lifeblood.source-file-content.v1", text);
}

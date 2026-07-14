namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Maps Roslyn syntax-tree paths into stable graph paths without consulting the
/// process working directory. Physical source paths remain project-relative;
/// source-generator hint paths live in a reserved, module-qualified namespace.
/// </summary>
internal readonly record struct SyntaxTreePathIdentity(string GraphPath, bool IsGenerated)
{
    private const string GeneratedRoot = "generated";
    private const string PhysicalEscapeRoot = "source";

    public static SyntaxTreePathIdentity Resolve(
        string projectRoot,
        string moduleName,
        string treePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(treePath);

        if (Path.IsPathFullyQualified(treePath))
        {
            var relativePath = Path.GetRelativePath(
                    Path.GetFullPath(projectRoot),
                    Path.GetFullPath(treePath))
                .Replace('\\', '/');

            return new SyntaxTreePathIdentity(
                EscapeReservedPhysicalPath(relativePath),
                IsGenerated: false);
        }

        var moduleSegment = EncodeSegment(moduleName);
        var hintPath = NormalizeVirtualPath(treePath);
        return new SyntaxTreePathIdentity(
            $"{GeneratedRoot}/{moduleSegment}/{hintPath}",
            IsGenerated: true);
    }

    private static string EscapeReservedPhysicalPath(string relativePath)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var firstNonEscapeSegment = 0;
        while (firstNonEscapeSegment < segments.Length
               && string.Equals(
                   segments[firstNonEscapeSegment],
                   PhysicalEscapeRoot,
                   StringComparison.OrdinalIgnoreCase))
        {
            firstNonEscapeSegment++;
        }

        if (firstNonEscapeSegment < segments.Length
            && string.Equals(
                segments[firstNonEscapeSegment],
                GeneratedRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            return PhysicalEscapeRoot + "/" + relativePath;
        }

        return relativePath;
    }

    private static string NormalizeVirtualPath(string treePath)
    {
        var segments = treePath
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(EncodeSegment)
            .ToArray();

        return segments.Length == 0 ? "_" : string.Join('/', segments);
    }

    private static string EncodeSegment(string value)
    {
        return value switch
        {
            "." => "%2E",
            ".." => "%2E%2E",
            _ => Uri.EscapeDataString(value),
        };
    }
}

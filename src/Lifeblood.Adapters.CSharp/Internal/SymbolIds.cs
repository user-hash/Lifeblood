namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Canonical symbol ID generation for the Roslyn adapter.
/// ID format is adapter-specific — other adapters may use different strategies.
/// </summary>
internal static class SymbolIds
{
    public static string Module(string assemblyName) => $"mod:{assemblyName}";

    public static string File(string relativePath) => $"file:{Normalize(relativePath)}";

    public static string Namespace(string ns) => $"ns:{ns}";

    public static string Type(string fullyQualifiedName) => $"type:{fullyQualifiedName}";

    public static string Method(string containingType, string name, string paramSignature)
        => $"method:{containingType}.{name}({paramSignature})";

    public static string Field(string containingType, string name)
        => $"field:{containingType}.{name}";

    public static string Property(string containingType, string name)
        => $"prop:{containingType}.{name}";

    private static string Normalize(string path)
        => path.Replace('\\', '/');
}

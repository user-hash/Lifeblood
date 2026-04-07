namespace Lifeblood.Domain.Graph;

/// <summary>
/// A node in the semantic graph. Language-agnostic.
/// INV-GRAPH-001: No language-specific kinds. Only universals.
/// INV-GRAPH-002: Language-specific metadata goes in Properties.
/// </summary>
public sealed class Symbol
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string QualifiedName { get; init; } = "";
    public SymbolKind Kind { get; init; }
    public string FilePath { get; init; } = "";
    public int Line { get; init; }
    public string ParentId { get; init; } = "";
    public Visibility Visibility { get; init; } = Visibility.Internal;
    public bool IsAbstract { get; init; }
    public bool IsStatic { get; init; }
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

public enum SymbolKind
{
    Module,
    File,
    Namespace,
    Type,
    Method,
    Field,
    Parameter,
}

public enum Visibility
{
    Public,
    Internal,
    Protected,
    Private,
}

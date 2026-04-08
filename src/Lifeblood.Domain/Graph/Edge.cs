namespace Lifeblood.Domain.Graph;

/// <summary>
/// A directed relationship between two symbols.
/// INV-GRAPH-003: Every edge carries Evidence.
/// INV-GRAPH-004: Edges are read-only after graph construction. Analyzers do not modify them.
/// </summary>
public sealed class Edge
{
    public string SourceId { get; init; } = "";
    public string TargetId { get; init; } = "";
    public EdgeKind Kind { get; init; }
    public Evidence Evidence { get; init; } = Evidence.Default;
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Language-agnostic edge kinds. INV-GRAPH-001.
/// </summary>
public enum EdgeKind
{
    Contains,
    DependsOn,
    Implements,
    Inherits,
    Calls,
    References,
    Overrides,
}

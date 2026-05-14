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

    /// <summary>
    /// Optional source-occurrence provenance for this edge: the
    /// <c>(file, line, column)</c> where the authoring expression appears and
    /// the canonical id of the enclosing declaration. Null for edges with no
    /// single authoring location (module→module DependsOn, graph-derived
    /// type-level Inherits/Implements when the inheritance clause itself is
    /// not surfaced, etc.). See <see cref="CallSite"/>. INV-EDGE-CALLSITE-001.
    /// </summary>
    public CallSite? CallSite { get; init; }
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

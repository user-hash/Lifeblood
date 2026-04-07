namespace Lifeblood.Core.Graph;

/// <summary>
/// A directed relationship between two symbols.
/// INV-GRAPH-004: Source depends-on/references/calls Target.
/// </summary>
public sealed class Edge
{
    /// <summary>ID of the source symbol (the one that depends/references/calls).</summary>
    public string SourceId { get; init; } = "";

    /// <summary>ID of the target symbol (the one being depended on/referenced/called).</summary>
    public string TargetId { get; init; } = "";

    /// <summary>What kind of relationship this is.</summary>
    public EdgeKind Kind { get; init; }

    /// <summary>
    /// Confidence level from the adapter. 1.0 = certain (e.g., Roslyn resolved).
    /// Lower values for heuristic matches (e.g., text-based parser guessing).
    /// Core analysis can use this to weight results.
    /// </summary>
    public float Confidence { get; init; } = 1.0f;

    /// <summary>True if this edge violates an architecture rule. Set by RuleValidator.</summary>
    public bool IsViolation { get; set; }

    /// <summary>Provenance: how this edge was discovered, by whom, with what certainty.</summary>
    public Evidence? Evidence { get; init; }

    /// <summary>Optional metadata (e.g., which rule was violated).</summary>
    public Dictionary<string, string> Properties { get; init; } = new();
}

/// <summary>
/// Universal relationship kinds between code elements.
/// </summary>
public enum EdgeKind
{
    Contains,       // Parent holds child (file→type, type→method)
    DependsOn,      // Import/using dependency (file→file or module→module)
    Implements,     // Type implements interface/trait/protocol
    Inherits,       // Type extends base type
    Calls,          // Method invokes method
    References,     // Code uses a type (new, cast, generic arg, field type)
    Overrides,      // Method overrides base method
    TypeReference,  // Roslyn-level: actual type used in method body
}

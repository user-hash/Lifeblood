namespace Lifeblood.Core.Graph;

/// <summary>
/// Provenance of a graph edge. Every relationship carries proof of where it came from.
/// This is what separates a serious platform from a flashy demo.
///
/// Without evidence: "file A depends on file B" (trust me).
/// With evidence: "file A depends on file B, proven by Roslyn type resolution
///                at line 47, confidence: proven, adapter: csharp-roslyn v1.0."
/// </summary>
public sealed class Evidence
{
    /// <summary>How this relationship was discovered.</summary>
    public EvidenceKind Kind { get; init; }

    /// <summary>Which adapter produced this evidence.</summary>
    public string AdapterName { get; init; } = "";

    /// <summary>Source file and line where the relationship originates.</summary>
    public string SourceSpan { get; init; } = ""; // "src/Auth.cs:47"

    /// <summary>
    /// Confidence level. Proven = compiler-grade. BestEffort = heuristic.
    /// Consumers can filter or weight results by this.
    /// </summary>
    public float Confidence { get; init; } = 1.0f;
}

/// <summary>
/// How a relationship was discovered.
/// </summary>
public enum EvidenceKind
{
    /// <summary>Found by parsing syntax (import statement, class declaration). Any parser can do this.</summary>
    Syntax,

    /// <summary>Resolved by semantic analysis (type resolution, overload resolution). Requires compiler-grade adapter.</summary>
    Semantic,

    /// <summary>Inferred by heuristic (name matching, convention-based). May have false positives.</summary>
    Inferred,
}

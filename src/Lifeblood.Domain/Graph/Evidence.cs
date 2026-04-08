using Lifeblood.Domain.Capabilities;

namespace Lifeblood.Domain.Graph;

/// <summary>
/// Provenance of a graph edge. How was this relationship discovered and how much to trust it.
/// INV-GRAPH-003: Every edge carries Evidence.
/// </summary>
public sealed class Evidence
{
    /// <summary>
    /// Fallback for edges without explicit evidence (e.g., from external JSON adapters
    /// that omit the evidence field). Confidence is None — unknown provenance.
    /// </summary>
    public static readonly Evidence Default = new()
    {
        Kind = EvidenceKind.Inferred,
        AdapterName = "unknown",
        Confidence = ConfidenceLevel.None,
    };

    public required EvidenceKind Kind { get; init; }
    public string AdapterName { get; init; } = "";
    public string SourceSpan { get; init; } = "";
    public required ConfidenceLevel Confidence { get; init; }
}

public enum EvidenceKind
{
    Syntax,
    Semantic,
    Inferred,
}

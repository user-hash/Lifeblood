using Lifeblood.Domain.Capabilities;

namespace Lifeblood.Domain.Graph;

/// <summary>
/// Provenance of a graph edge. How was this relationship discovered and how much to trust it.
/// </summary>
public sealed class Evidence
{
    public static readonly Evidence Default = new();

    public EvidenceKind Kind { get; init; }
    public string AdapterName { get; init; } = "";
    public string SourceSpan { get; init; } = "";
    public ConfidenceLevel Confidence { get; init; } = ConfidenceLevel.Proven;
}

public enum EvidenceKind
{
    Syntax,
    Semantic,
    Inferred,
}

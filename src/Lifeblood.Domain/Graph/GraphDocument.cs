using Lifeblood.Domain.Capabilities;

namespace Lifeblood.Domain.Graph;

/// <summary>
/// A complete graph document: the semantic graph plus protocol metadata.
/// This is the full wire format. SemanticGraph is the pure graph data.
/// GraphDocument adds the envelope: version, language, adapter info.
/// </summary>
public sealed class GraphDocument
{
    public const string CurrentVersion = "1.0";

    public string Version { get; init; } = CurrentVersion;
    public string Language { get; init; } = "";
    public AdapterCapability? Adapter { get; init; }
    public SemanticGraph Graph { get; init; } = new();
}

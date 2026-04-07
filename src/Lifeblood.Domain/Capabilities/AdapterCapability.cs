namespace Lifeblood.Domain.Capabilities;

/// <summary>
/// What a language adapter can actually do. Declared honestly.
/// </summary>
public sealed class AdapterCapability
{
    public string Language { get; init; } = "";
    public string AdapterName { get; init; } = "";
    public string AdapterVersion { get; init; } = "";
    public bool CanDiscoverSymbols { get; init; }
    public ConfidenceLevel TypeResolution { get; init; }
    public ConfidenceLevel CallResolution { get; init; }
    public ConfidenceLevel ImplementationResolution { get; init; }
    public ConfidenceLevel CrossModuleReferences { get; init; }
    public ConfidenceLevel OverrideResolution { get; init; }
}

public enum ConfidenceLevel
{
    None,
    BestEffort,
    High,
    Proven,
}

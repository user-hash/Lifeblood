using Lifeblood.Domain.Capabilities;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Declares what the Roslyn adapter can do. Honest. Proven.
/// INV-ADAPT-001: Every adapter declares capabilities honestly.
/// INV-ADAPT-002: C# adapter is the reference. Most complete, best tested.
/// </summary>
public static class RoslynCapabilityDescriptor
{
    public static readonly AdapterCapability Capability = new()
    {
        Language = "csharp",
        AdapterName = "Roslyn",
        AdapterVersion = "1.0.0",
        CanDiscoverSymbols = true,
        TypeResolution = ConfidenceLevel.Proven,
        CallResolution = ConfidenceLevel.Proven,
        ImplementationResolution = ConfidenceLevel.Proven,
        CrossModuleReferences = ConfidenceLevel.Proven, // compilations built in dependency order with CompilationReferences
        OverrideResolution = ConfidenceLevel.None, // Overrides edges not yet extracted
    };
}

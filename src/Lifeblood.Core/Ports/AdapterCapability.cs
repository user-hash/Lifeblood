namespace Lifeblood.Core.Ports;

/// <summary>
/// Declares what a language adapter can actually do.
/// Every adapter reports its capabilities honestly.
/// Analysis passes check these before running.
///
/// This prevents fake authority. If a Python adapter cannot resolve types,
/// coupling analysis reports "best-effort" not "proven."
/// </summary>
public sealed class AdapterCapability
{
    /// <summary>Language this adapter handles.</summary>
    public string Language { get; init; } = "";

    /// <summary>Can parse files and discover symbols (types, methods, fields).</summary>
    public bool CanDiscoverSymbols { get; init; }

    /// <summary>Can resolve which type a reference actually points to.</summary>
    public ConfidenceLevel TypeResolution { get; init; }

    /// <summary>Can resolve which method a call expression actually invokes.</summary>
    public ConfidenceLevel CallResolution { get; init; }

    /// <summary>Can find interface implementations and trait impls.</summary>
    public ConfidenceLevel ImplementationResolution { get; init; }

    /// <summary>Can track references across module/package boundaries.</summary>
    public ConfidenceLevel CrossModuleReferences { get; init; }

    /// <summary>Can find method overrides.</summary>
    public ConfidenceLevel OverrideResolution { get; init; }

    /// <summary>Can expand macros / metaprogramming before analysis.</summary>
    public bool CanExpandMacros { get; init; }

    /// <summary>Supports incremental updates (re-parse only changed files).</summary>
    public bool SupportsIncremental { get; init; }
}

/// <summary>
/// How much to trust an adapter's output for a given capability.
/// Analysis results carry this so consumers know what they are getting.
/// </summary>
public enum ConfidenceLevel
{
    /// <summary>Not supported. Adapter cannot do this.</summary>
    None,

    /// <summary>Best-effort heuristic. May have false positives/negatives.</summary>
    BestEffort,

    /// <summary>High confidence but not compiler-grade. Most results correct.</summary>
    High,

    /// <summary>Compiler-grade resolution. Proven correct (e.g., Roslyn).</summary>
    Proven,
}

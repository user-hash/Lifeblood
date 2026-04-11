namespace Lifeblood.Domain.Results;

/// <summary>
/// Combined output of all analysis passes. Separate from the graph.
/// INV-GRAPH-004: The graph is not modified. Results live here.
/// </summary>
public sealed class AnalysisResult
{
    public CouplingMetrics[] Coupling { get; init; } = Array.Empty<CouplingMetrics>();
    public Violation[] Violations { get; init; } = Array.Empty<Violation>();
    public TierAssignment[] Tiers { get; init; } = Array.Empty<TierAssignment>();
    public string[][] Cycles { get; init; } = Array.Empty<string[]>();
    public BlastRadiusResult[] BlastRadii { get; init; } = Array.Empty<BlastRadiusResult>();
    public GraphMetrics Metrics { get; init; } = new();
}

public sealed class CouplingMetrics
{
    public string SymbolId { get; init; } = "";
    public int FanIn { get; init; }
    public int FanOut { get; init; }
    public float Instability { get; init; }
}

public sealed class TierAssignment
{
    public string SymbolId { get; init; } = "";
    public ArchitectureTier Tier { get; init; }
    public string Reason { get; init; } = "";
}

public enum ArchitectureTier
{
    Pure,
    Boundary,
    Runtime,
    Tooling,
}

public sealed class BlastRadiusResult
{
    public string TargetSymbolId { get; init; } = "";
    public string[] AffectedSymbolIds { get; init; } = Array.Empty<string>();
    public int AffectedCount { get; init; }

    /// <summary>
    /// Per-dependant break-kind classification. Each entry names one
    /// affected symbol and the kind of break it would experience if the
    /// target symbol were changed. Derived from the edge kind that
    /// connects the dependant to the target: e.g. a <c>Calls</c> edge
    /// into a removed method is <see cref="BreakKind.BindingRemoval"/>,
    /// an <c>Implements</c> edge into a type whose contract changes is
    /// <see cref="BreakKind.SignatureChange"/>, and so on. Empty when
    /// the caller did not request classification. Added 2026-04-11
    /// (Phase 6 / B7) to close DAWG R4.
    /// </summary>
    public BreakInfo[] Breaks { get; init; } = Array.Empty<BreakInfo>();
}

/// <summary>
/// One classified break in a <see cref="BlastRadiusResult"/>. See the
/// parent result doc for why this shape exists.
/// </summary>
public sealed class BreakInfo
{
    public required string SymbolId { get; init; }
    public required BreakKind Kind { get; init; }
    public string Reason { get; init; } = "";
}

/// <summary>
/// Canonical categories of how a blast-radius dependant can break if
/// the target symbol changes. Derived from the edge kind that connects
/// them in the semantic graph. The categories are deliberately coarse:
/// a caller asking "what happens if I delete this" does not need
/// per-compiler-error precision, just a rough bucket so they can
/// triage triage scope and risk.
/// </summary>
public enum BreakKind
{
    /// <summary>Binding breaks: the target was deleted or the dependant's reference no longer resolves.</summary>
    BindingRemoval,
    /// <summary>Signature breaks: the target's shape changed and the dependant won't compile against the new shape.</summary>
    SignatureChange,
    /// <summary>Type rename: the target was renamed; the dependant's name reference is stale.</summary>
    TypeRename,
    /// <summary>Behavioral breaks: the target compiles fine but does something different at runtime.</summary>
    Behavioral,
    /// <summary>Fallback for edges whose kind doesn't map cleanly to any of the above.</summary>
    Unknown,
}

public sealed class GraphMetrics
{
    public int TotalSymbols { get; init; }
    public int TotalEdges { get; init; }
    public int TotalFiles { get; init; }
    public int TotalTypes { get; init; }
    public int TotalModules { get; init; }
    public int ViolationCount { get; init; }
    public int CycleCount { get; init; }
}

/// <summary>
/// A rule violation. Result object, not a graph mutation.
/// INV-GRAPH-004: Analyzers do not modify the graph.
/// </summary>
public sealed class Violation
{
    public string SourceSymbolId { get; init; } = "";
    public string TargetSymbolId { get; init; } = "";
    public string SourceNamespace { get; init; } = "";
    public string TargetNamespace { get; init; } = "";
    public string RuleBroken { get; init; } = "";
    public int EdgeIndex { get; init; } = -1;
}

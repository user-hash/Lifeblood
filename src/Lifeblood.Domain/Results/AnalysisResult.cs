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
    /// (Phase 6 / B7) to close the R4 finding.
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
/// One detected cycle (strongly-connected component) classified by what
/// it most likely represents. Pairs the raw symbol-id member list from
/// the Tarjan SCC pass with a triage bucket so a caller can fold the
/// noise tail without re-walking the cycle members.
/// INV-CYCLE-TAXONOMY-001 / LB-TRACK-20260514-008.
/// </summary>
public sealed class CycleDescriptor
{
    /// <summary>Symbol ids participating in the cycle, in SCC order.</summary>
    public required string[] Symbols { get; init; }

    /// <summary>
    /// Triage bucket. <see cref="CycleBucket.LikelyRealLoop"/> by
    /// default; the analyzer downgrades to one of the noise buckets
    /// when the cycle matches the matching pattern.
    /// </summary>
    public required CycleBucket Bucket { get; init; }
}

/// <summary>
/// Cycle triage buckets. Precedence (most-authoritative wins):
///   1. <see cref="GeneratedOrStaticAnalysisArtifact"/> — any
///      participating symbol's file path matches a generated-code
///      pattern (<c>obj</c>/<c>bin</c>/<c>generated</c> segment,
///      <c>*.Generated.*</c> / <c>*.g.cs</c> filename). Build
///      artifacts and source-generator output are never an
///      architectural-refactor target.
///   2. <see cref="PartialClassCluster"/> — every participating
///      method/property/field walks (via <see cref="Lifeblood.Domain.Graph.EdgeKind.Contains"/>
///      reverse-chain) to the same enclosing Type. Intra-type
///      mutual recursion / method-pair cycles inside one host
///      (partial classes manifest the same way at the SCC level
///      because Roslyn surfaces them as one type with members
///      spread across files).
///   3. <see cref="LikelyRealLoop"/> — everything else. Cross-type
///      / cross-module loops; the actual architectural-cycle
///      backlog.
/// </summary>
public enum CycleBucket
{
    LikelyRealLoop,
    PartialClassCluster,
    GeneratedOrStaticAnalysisArtifact,
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

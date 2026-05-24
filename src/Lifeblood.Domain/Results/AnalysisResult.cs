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
    /// the caller did not request classification.
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
/// Result of <c>TestImpactAnalyzer.Analyze</c>: which test classes
/// transitively depend on a given symbol or file. Lets a caller
/// answer "which tests prove that my change to X is safe?" in one
/// call instead of pairing blast_radius with manual test discovery.
/// INV-TEST-IMPACT-001 / LB-TRACK-20260514-007.
/// </summary>
public sealed class TestImpactReport
{
    /// <summary>The resolved id or file path the analyzer ran against.</summary>
    public required string Target { get; init; }

    /// <summary>Whether <see cref="Target"/> was treated as a symbol or a file.</summary>
    public required TestImpactTargetKind TargetKind { get; init; }

    /// <summary>
    /// Per-class impact rows, sorted by ascending <see cref="TestClassImpact.MinDistance"/>
    /// then by qualified name. Each row aggregates one test class's
    /// affected test methods.
    /// </summary>
    public required TestClassImpact[] AffectedTestClasses { get; init; }

    /// <summary>Total number of affected test methods (sum of <see cref="TestClassImpact.TestMethodNames"/>.Length).</summary>
    public required int TotalTestMethodCount { get; init; }

    /// <summary>
    /// Count of test classes at <see cref="TestImpactConfidence.Direct"/>
    /// distance — caller-friendly "how many tests directly touch this
    /// symbol" headline.
    /// </summary>
    public required int DirectTestClassCount { get; init; }

    /// <summary>
    /// Recommended `dotnet test --filter` strings, one per affected
    /// class, in distance order. Caller pastes them into their test
    /// runner without composing the filter syntax themselves.
    /// </summary>
    public required string[] RecommendedFilters { get; init; }

    /// <summary>
    /// Count of <see cref="AffectedTestClasses"/> sourced via the BFS
    /// over Calls / References incoming edges (the existing v0.7.x
    /// behavior). Always populated. INV-TEST-IMPACT-REFLECTION-002.
    /// </summary>
    public int SemanticEdgeHits { get; init; }

    /// <summary>
    /// Count of <see cref="AffectedTestClasses"/> sourced via the
    /// Wave-3 source-text reflection heuristic (FQN-literal + bare
    /// short-name with namespace context). Zero when
    /// <see cref="TestImpactOptions.IncludeReflectionHeuristic"/> was
    /// false (the default). INV-TEST-IMPACT-REFLECTION-001.
    /// </summary>
    public int ReflectionHeuristicHits { get; init; }

    /// <summary>
    /// Per-tool limitations surfaced when reflection heuristic is
    /// active. Discloses the approximate nature of the source-text
    /// scan: tests using <c>Type.GetType(computedString)</c> remain
    /// invisible; comments and identifier names containing the FQN
    /// can produce false positives. Open extension set.
    /// INV-TEST-IMPACT-REFLECTION-003.
    /// </summary>
    public IReadOnlyList<string> Limitations { get; init; } = Array.Empty<string>();
}

/// <summary>
/// One test class transitively affected by a target change.
/// </summary>
public sealed class TestClassImpact
{
    public required string TypeId { get; init; }
    public required string Name { get; init; }
    public required string QualifiedName { get; init; }
    public required string FilePath { get; init; }

    /// <summary>
    /// Minimum hop count from the target to any of this class's test
    /// methods (or the class itself). 1 = direct reference, 2 = one
    /// hop through an intermediate symbol, etc.
    /// </summary>
    public required int MinDistance { get; init; }

    /// <summary>
    /// Confidence bucket derived from <see cref="MinDistance"/> —
    /// Direct (1) / OneHop (2) / Transitive (3+). Caller reads the
    /// label without computing thresholds.
    /// </summary>
    public required TestImpactConfidence Confidence { get; init; }

    /// <summary>Short names of the affected test methods in this class.</summary>
    public required string[] TestMethodNames { get; init; }

    /// <summary>
    /// Canonical hit-kind string. <see cref="TestImpactHitKind.Semantic"/>
    /// (default) when the row was surfaced via the BFS over
    /// Calls / References incoming edges; <see cref="TestImpactHitKind.ReflectionHeuristic"/>
    /// when surfaced via the Wave-3 source-text scan. Additive — back-compat
    /// callers reading only the existing fields keep working.
    /// INV-TEST-IMPACT-REFLECTION-002.
    /// </summary>
    public string Kind { get; init; } = TestImpactHitKind.Semantic;
}

/// <summary>
/// Canonical hit-kind string set for <see cref="TestClassImpact.Kind"/>.
/// Open for non-breaking extension; callers MUST tolerate unknown kinds.
/// </summary>
public static class TestImpactHitKind
{
    public const string Semantic = "Semantic";
    public const string ReflectionHeuristic = "ReflectionHeuristic";
}

/// <summary>
/// Per-call options for <c>TestImpactAnalyzer.AnalyzeSymbol</c>. The
/// existing parameterless / int-only overloads remain — this options
/// shape is the opt-in path for the Wave-3 reflection heuristic.
/// Default values preserve v0.7.8 wire behavior exactly. INV-TEST-IMPACT-REFLECTION-001.
/// </summary>
public sealed class TestImpactOptions
{
    /// <summary>BFS depth cap (existing semantic).</summary>
    public int MaxDepth { get; init; } = 12;

    /// <summary>
    /// Opt in to the source-text reflection heuristic. When true the
    /// analyzer scans each test method's containing file for the
    /// target's fully-qualified name as a source-text substring, AND
    /// for the bare short name only when the test file also contains
    /// the target's namespace as a substring (or the short name is
    /// unique across the workspace's symbol-name index). Default false
    /// — opt-in keeps the v0.7.8 wire shape byte-stable for callers
    /// that do not request the heuristic. INV-TEST-IMPACT-REFLECTION-001.
    /// </summary>
    public bool IncludeReflectionHeuristic { get; init; } = false;
}

/// <summary>
/// Confidence bucket on a <see cref="TestClassImpact"/>. Derived from
/// graph hop distance; coarse on purpose so a wire consumer can render
/// "high / medium / low" without computing thresholds.
/// </summary>
public enum TestImpactConfidence
{
    Direct,        // MinDistance == 1
    OneHop,        // MinDistance == 2
    Transitive,    // MinDistance >= 3
}

/// <summary>
/// Whether the analyzer interpreted its <c>target</c> argument as a
/// single symbol or as a file (every symbol declared in that file is
/// treated as a multi-source BFS start).
/// </summary>
public enum TestImpactTargetKind
{
    Symbol,
    File,
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

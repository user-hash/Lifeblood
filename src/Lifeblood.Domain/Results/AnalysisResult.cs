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
    public string[] DeadSymbols { get; init; } = Array.Empty<string>();
    public HubInfo[] Hubs { get; init; } = Array.Empty<HubInfo>();
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

public sealed class HubInfo
{
    public string SymbolId { get; init; } = "";
    public float BetweennessCentrality { get; init; }
    public int FanIn { get; init; }
    public int FanOut { get; init; }
}

public sealed class BlastRadiusResult
{
    public string TargetSymbolId { get; init; } = "";
    public string[] AffectedSymbolIds { get; init; } = Array.Empty<string>();
    public int AffectedCount { get; init; }
}

public sealed class GraphMetrics
{
    public int TotalSymbols { get; init; }
    public int TotalEdges { get; init; }
    public int TotalFiles { get; init; }
    public int TotalTypes { get; init; }
    public int TotalModules { get; init; }
    public int PureModules { get; init; }
    public int ViolationCount { get; init; }
    public int CycleCount { get; init; }
    public float AverageInstability { get; init; }
}

/// <summary>
/// AUDIT FIX (Hole 4): Violation is a result object, not a graph mutation.
/// RuleValidator returns these. Edge.IsViolation removed.
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

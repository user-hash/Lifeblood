using Lifeblood.Core.Rules;

namespace Lifeblood.Core.Analysis;

/// <summary>
/// Combined output of all analysis passes over a SemanticGraph.
/// </summary>
public sealed class AnalysisResult
{
    /// <summary>Coupling metrics per symbol.</summary>
    public CouplingMetrics[] Coupling { get; init; } = Array.Empty<CouplingMetrics>();

    /// <summary>Architecture rule violations.</summary>
    public Violation[] Violations { get; init; } = Array.Empty<Violation>();

    /// <summary>Architecture tier per file/module.</summary>
    public TierAssignment[] Tiers { get; init; } = Array.Empty<TierAssignment>();

    /// <summary>Circular dependency cycles.</summary>
    public string[][] Cycles { get; init; } = Array.Empty<string[]>();

    /// <summary>Dead symbols (unreachable from entry points).</summary>
    public string[] DeadSymbols { get; init; } = Array.Empty<string>();

    /// <summary>Hub nodes (high betweenness centrality).</summary>
    public HubInfo[] Hubs { get; init; } = Array.Empty<HubInfo>();

    /// <summary>Aggregate metrics.</summary>
    public GraphMetrics Metrics { get; init; } = new();
}

public sealed class CouplingMetrics
{
    public string SymbolId { get; init; } = "";
    public int FanIn { get; init; }     // Afferent coupling (Ca): how many depend on this
    public int FanOut { get; init; }    // Efferent coupling (Ce): how many this depends on
    public float Instability { get; init; } // Ce / (Ca + Ce). 0 = stable, 1 = unstable
}

public sealed class TierAssignment
{
    public string SymbolId { get; init; } = "";
    public ArchitectureTier Tier { get; init; }
    public string Reason { get; init; } = "";
}

public enum ArchitectureTier
{
    Pure,       // Zero platform dependencies. Leaf module.
    Boundary,   // Interface-only, port definitions.
    Runtime,    // Has platform dependencies but is production code.
    Tooling,    // Editor/test/build-only.
}

public sealed class HubInfo
{
    public string SymbolId { get; init; } = "";
    public float BetweennessCentrality { get; init; }
    public int FanIn { get; init; }
    public int FanOut { get; init; }
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
    public int DeadSymbolCount { get; init; }
    public float AverageInstability { get; init; }
    public string MostCoupledNode { get; init; } = "";
    public int MaxFanIn { get; init; }
    public int MaxFanOut { get; init; }
}

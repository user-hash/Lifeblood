using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Application.Ports.Right;

/// <summary>
/// THE KILLER FEATURE. Right side. Generates AI-consumable context packs.
/// </summary>
public interface IAgentContextGenerator
{
    AgentContextPack Generate(SemanticGraph graph, AnalysisResult analysis);
}

public sealed class AgentContextPack
{
    public GraphSummary Summary { get; init; } = new();
    public HighValueFile[] HighValueFiles { get; init; } = Array.Empty<HighValueFile>();
    public BoundaryInfo[] Boundaries { get; init; } = Array.Empty<BoundaryInfo>();
    public string[] Invariants { get; init; } = Array.Empty<string>();
    public string[] Hotspots { get; init; } = Array.Empty<string>();
    public string[] ReadingOrder { get; init; } = Array.Empty<string>();
    public ModuleDependency[] DependencyMatrix { get; init; } = Array.Empty<ModuleDependency>();
    public string[] ActiveViolations { get; init; } = Array.Empty<string>();
}

public sealed class GraphSummary
{
    public int TotalSymbols { get; init; }
    public int TotalEdges { get; init; }
    public int Modules { get; init; }
    public int Types { get; init; }
    public int Methods { get; init; }
    public int Files { get; init; }
    public int Cycles { get; init; }
    public int Violations { get; init; }
}

public sealed class HighValueFile
{
    public string FilePath { get; init; } = "";
    public int FanIn { get; init; }
    public float Instability { get; init; }
    public string Tier { get; init; } = "";
}

public sealed class BoundaryInfo
{
    public string ModuleName { get; init; } = "";
    public string Tier { get; init; } = "";
    public string[] DependsOn { get; init; } = Array.Empty<string>();
    public bool IsPure { get; init; }
}

public sealed class ModuleDependency
{
    public string Source { get; init; } = "";
    public string Target { get; init; } = "";
    public int EdgeCount { get; init; }
}

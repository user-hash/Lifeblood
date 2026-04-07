using Lifeblood.Core.Analysis;
using Lifeblood.Core.Graph;

namespace Lifeblood.Core.Ports;

/// <summary>
/// THE KILLER FEATURE.
///
/// Generates context packs for AI agents. This is what makes Lifeblood
/// the glue between AI and codebases.
///
/// Without this: AI agents grep, guess, break things they cannot see.
/// With this: AI agents know the architecture before they write code.
/// </summary>
public interface IAgentContextGenerator
{
    /// <summary>
    /// Generate a context pack from analysis results.
    /// Output is designed to be pasted into CLAUDE.md or similar AI instruction files.
    /// </summary>
    AgentContextPack Generate(SemanticGraph graph, AnalysisResult analysis);
}

/// <summary>
/// Everything an AI agent needs to understand a codebase before editing it.
/// </summary>
public sealed class AgentContextPack
{
    /// <summary>High-value files: highest coupling, most dependants, most risk.</summary>
    public HighValueFile[] HighValueFiles { get; init; } = Array.Empty<HighValueFile>();

    /// <summary>Architecture boundaries: which modules are pure, which are coupled.</summary>
    public BoundaryInfo[] Boundaries { get; init; } = Array.Empty<BoundaryInfo>();

    /// <summary>Active invariants extracted from rules.</summary>
    public string[] Invariants { get; init; } = Array.Empty<string>();

    /// <summary>Hotspots: high complexity + high coupling + high change risk.</summary>
    public string[] Hotspots { get; init; } = Array.Empty<string>();

    /// <summary>Recommended reading order: topological sort by importance.</summary>
    public string[] ReadingOrder { get; init; } = Array.Empty<string>();

    /// <summary>Dependency matrix summary: who depends on whom at module level.</summary>
    public ModuleDependency[] DependencyMatrix { get; init; } = Array.Empty<ModuleDependency>();

    /// <summary>Current violations: what is already broken.</summary>
    public string[] ActiveViolations { get; init; } = Array.Empty<string>();
}

public sealed class HighValueFile
{
    public string FilePath { get; init; } = "";
    public int FanIn { get; init; }
    public int FanOut { get; init; }
    public float Instability { get; init; }
    public string Tier { get; init; } = "";
    public string Reason { get; init; } = "";
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

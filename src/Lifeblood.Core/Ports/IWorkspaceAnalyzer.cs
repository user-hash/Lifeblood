using Lifeblood.Core.Graph;

namespace Lifeblood.Core.Ports;

/// <summary>
/// PRIMARY adapter port. Workspace-scoped, not file-scoped.
///
/// INV-PORT-001: The primary contract is workspace → graph, not file → symbols.
/// Roslyn needs the full compilation context to resolve types across files.
/// File-level parsing is an internal adapter concern, not a core contract.
///
/// In-process adapters (C#, TypeScript) implement this directly.
/// Out-of-process adapters (Python, Go, Rust) produce JSON graphs instead.
/// </summary>
public interface IWorkspaceAnalyzer
{
    /// <summary>
    /// Analyze an entire workspace/project and produce a semantic graph.
    /// </summary>
    /// <param name="projectRoot">Root directory of the project.</param>
    /// <param name="config">Analysis configuration (which files to include, depth, etc.).</param>
    /// <returns>Complete semantic graph with symbols, edges, and evidence.</returns>
    SemanticGraph AnalyzeWorkspace(string projectRoot, AnalysisConfig config);

    /// <summary>Adapter capability declaration.</summary>
    AdapterCapability Capability { get; }
}

/// <summary>
/// Configuration for workspace analysis.
/// </summary>
public sealed class AnalysisConfig
{
    /// <summary>File patterns to include (e.g., ["**/*.cs"]). Empty = all supported files.</summary>
    public string[] IncludePatterns { get; init; } = Array.Empty<string>();

    /// <summary>File patterns to exclude (e.g., ["**/bin/**", "**/obj/**"]).</summary>
    public string[] ExcludePatterns { get; init; } = Array.Empty<string>();

    /// <summary>Whether to use semantic analysis (slower, higher confidence) or syntax only (faster).</summary>
    public AnalysisDepth Depth { get; init; } = AnalysisDepth.Semantic;

    /// <summary>Maximum files to analyze. 0 = unlimited.</summary>
    public int MaxFiles { get; init; }
}

public enum AnalysisDepth
{
    /// <summary>Syntax only. Fast. Every adapter can do this.</summary>
    Syntax,

    /// <summary>Full semantic analysis. Slower. Requires compiler-grade adapter.</summary>
    Semantic,
}

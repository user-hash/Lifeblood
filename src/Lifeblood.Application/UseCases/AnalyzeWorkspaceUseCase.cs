using Lifeblood.Application.Ports.Left;
using Lifeblood.Application.Ports.Output;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Application.UseCases;

/// <summary>
/// The main pipeline. Left side adapter → graph → analysis → right side output.
/// INV-PIPE-001: Deterministic. Same input = same output.
/// </summary>
public sealed class AnalyzeWorkspaceUseCase
{
    private readonly IWorkspaceAnalyzer _adapter;
    private readonly IProgressSink? _progress;

    public AnalyzeWorkspaceUseCase(IWorkspaceAnalyzer adapter, IProgressSink? progress = null)
    {
        _adapter = adapter;
        _progress = progress;
    }

    public AnalyzeWorkspaceResult Execute(string projectRoot, AnalysisConfig config)
    {
        _progress?.Report("Analyzing workspace", 0, 3);

        // Step 1: Left side adapter produces the graph
        var graph = _adapter.AnalyzeWorkspace(projectRoot, config);
        _progress?.Report("Graph built", 1, 3);

        // Step 2: Graph is ready for right side consumption
        // Analysis passes are optional addons, not the core flow
        _progress?.Report("Complete", 3, 3);

        return new AnalyzeWorkspaceResult
        {
            Graph = graph,
            Capability = _adapter.Capability,
        };
    }
}

public sealed class AnalyzeWorkspaceResult
{
    public required SemanticGraph Graph { get; init; }
    public required Domain.Capabilities.AdapterCapability Capability { get; init; }
}

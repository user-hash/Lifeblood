using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Application.Ports.Output;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Application.UseCases;

/// <summary>
/// The main pipeline. Left side adapter → graph → validate → return.
/// INV-PIPE-001: Deterministic. Same input = same output.
///
/// Optionally captures runtime usage (wall time, CPU time, peak memory,
/// per-phase timings) via <see cref="IUsageProbe"/>. When a probe is
/// supplied, the result carries a populated <see cref="AnalyzeWorkspaceResult.Usage"/>
/// snapshot. When no probe is supplied, the result's <c>Usage</c> is null
/// and the use case runs exactly as before (no overhead).
/// </summary>
public sealed class AnalyzeWorkspaceUseCase
{
    private readonly IWorkspaceAnalyzer _adapter;
    private readonly IProgressSink? _progress;
    private readonly IUsageProbe? _usageProbe;

    public AnalyzeWorkspaceUseCase(
        IWorkspaceAnalyzer adapter,
        IProgressSink? progress = null,
        IUsageProbe? usageProbe = null)
    {
        _adapter = adapter;
        _progress = progress;
        _usageProbe = usageProbe;
    }

    public AnalyzeWorkspaceResult Execute(string projectRoot, AnalysisConfig config)
    {
        var capture = _usageProbe?.Start();
        try
        {
            _progress?.Report("Analyzing workspace", 0, 3);

            // Step 1: Left side adapter produces the graph
            var graph = _adapter.AnalyzeWorkspace(projectRoot, config);
            capture?.MarkPhase("analyze");
            _progress?.Report("Graph built", 1, 3);

            // Step 2: Validate graph integrity before returning.
            // GraphBuilder.Build() already drops dangling edges, so DANGLING_EDGE_* should not occur.
            // Validator still catches empty IDs, duplicates, dangling parents, self-references.
            var validationErrors = GraphValidator.Validate(graph);
            if (validationErrors.Length > 0)
            {
                var first = validationErrors[0];
                throw new InvalidOperationException(
                    $"Graph validation failed: {validationErrors.Length} errors. First: [{first.Code}] {first.Message}");
            }
            capture?.MarkPhase("validate");
            _progress?.Report("Validated", 2, 3);

            _progress?.Report("Complete", 3, 3);

            return new AnalyzeWorkspaceResult
            {
                Graph = graph,
                Capability = _adapter.Capability,
                Usage = capture?.Stop(),
            };
        }
        catch
        {
            // Dispose the capture on the error path so the background sample
            // timer does not outlive the failed run.
            capture?.Dispose();
            throw;
        }
    }
}

public sealed class AnalyzeWorkspaceResult
{
    public required SemanticGraph Graph { get; init; }
    public required Domain.Capabilities.AdapterCapability Capability { get; init; }

    /// <summary>
    /// Runtime usage snapshot for this run, populated when the use case was
    /// constructed with a non-null <see cref="IUsageProbe"/>. Null otherwise.
    /// </summary>
    public AnalysisUsage? Usage { get; init; }
}

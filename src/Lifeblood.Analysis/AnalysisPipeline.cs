using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Lifeblood.Domain.Rules;

namespace Lifeblood.Analysis;

/// <summary>
/// Runs all analysis passes in deterministic order.
/// Single source of truth — both CLI and MCP server use this.
/// INV-PIPE-001: Deterministic. Same input = same output.
/// </summary>
public static class AnalysisPipeline
{
    public static AnalysisResult Run(SemanticGraph graph, ArchitectureRule[]? rules = null)
    {
        var coupling = CouplingAnalyzer.Analyze(graph,
            new[] { SymbolKind.Type, SymbolKind.Module });

        var cycles = CircularDependencyDetector.Detect(graph);

        var tiers = TierClassifier.Classify(graph);

        var blastRadii = coupling
            .Where(c => c.FanIn >= 3)
            .Select(c => BlastRadiusAnalyzer.Analyze(graph, c.SymbolId))
            .ToArray();

        var violations = rules is { Length: > 0 }
            ? RuleValidator.Validate(graph, rules)
            : Array.Empty<Violation>();

        return new AnalysisResult
        {
            Coupling = coupling,
            Violations = violations,
            Tiers = tiers,
            Cycles = cycles,
            BlastRadii = blastRadii,
            Metrics = new GraphMetrics
            {
                TotalSymbols = graph.Symbols.Count,
                TotalEdges = graph.Edges.Count,
                TotalFiles = graph.Symbols.Count(s => s.Kind == SymbolKind.File),
                TotalTypes = graph.Symbols.Count(s => s.Kind == SymbolKind.Type),
                TotalModules = graph.Symbols.Count(s => s.Kind == SymbolKind.Module),
                ViolationCount = violations.Length,
                CycleCount = cycles.Length,
            },
        };
    }
}

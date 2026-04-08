// AnalysisPipeline moved to Lifeblood.Analysis.AnalysisPipeline (single source of truth).
// This file kept as a thin alias so CLI code doesn't need using changes.

using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Lifeblood.Domain.Rules;

namespace Lifeblood.CLI;

internal static class AnalysisPipeline
{
    public static AnalysisResult Run(SemanticGraph graph, ArchitectureRule[]? rules = null)
        => Analysis.AnalysisPipeline.Run(graph, rules);
}

using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Application.Ports.Analysis;

/// <summary>
/// Port for blast radius computation. Abstracts the analysis layer
/// so connectors never depend on Analysis directly.
/// INV-ANALYSIS-002: Read-only — does not modify the graph.
/// </summary>
public interface IBlastRadiusProvider
{
    BlastRadiusResult Analyze(SemanticGraph graph, string targetSymbolId, int maxDepth = 10);
}

using Lifeblood.Domain.Graph;

namespace Lifeblood.Application.Ports.Analysis;

/// <summary>
/// Generic analyzer interface. All analyzers are stateless.
/// INV-ANALYSIS-001: Input: graph + config. Output: typed result. No side effects.
/// </summary>
public interface IAnalyzer<TConfig, TResult>
{
    TResult Analyze(SemanticGraph graph, TConfig config);
}

using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Application.Ports.Right;

/// <summary>
/// Right side. Generates CLAUDE.md / AGENTS.md sections from the graph.
/// </summary>
public interface IInstructionFileGenerator
{
    string Generate(SemanticGraph graph, AnalysisResult analysis);
}

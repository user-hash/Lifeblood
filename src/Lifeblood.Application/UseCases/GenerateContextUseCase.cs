using Lifeblood.Application.Ports.Right;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Application.UseCases;

/// <summary>
/// The killer feature. Graph → AI-consumable context pack.
/// This is what makes Lifeblood the glue between code intelligence and AI.
/// </summary>
public sealed class GenerateContextUseCase
{
    private readonly IAgentContextGenerator _generator;

    public GenerateContextUseCase(IAgentContextGenerator generator)
    {
        _generator = generator;
    }

    public AgentContextPack Execute(SemanticGraph graph, AnalysisResult analysis)
    {
        return _generator.Generate(graph, analysis);
    }
}

using System.Text.Json;
using Lifeblood.Analysis;
using Lifeblood.Application.UseCases;
using Lifeblood.Domain.Results;

namespace Lifeblood.Server.Mcp;

/// <summary>Composes one leased graph candidate plan with one bounded operation scan.</summary>
internal sealed class MethodEffectToolHandler(GraphSession session)
{
    public MethodEffectReport Execute(JsonElement? arguments, CancellationToken cancellationToken)
    {
        var bound = ToolRequestBinder.BindMethodEffects(arguments);
        var request = new MethodEffectRequest
        {
            RootSymbolIds = bound.RootSymbolIds,
            ProfileScope = string.IsNullOrWhiteSpace(bound.ProfileScope) ? session.RetainedProfileName : bound.ProfileScope.Trim(),
            MaxDepth = bound.MaxDepth,
            MaxMethods = bound.MaxMethods,
            MaxFacts = bound.MaxFacts,
            MaxEvidencePerMethod = bound.MaxEvidencePerMethod,
        };
        var graph = session.Graph ?? throw new InvalidOperationException("Method effects require a loaded semantic graph.");
        var provider = session.OperationFactProvider ?? throw new InvalidOperationException("Method effects require the selected publication's operation-fact provider.");
        var plan = MethodEffectAnalyzer.Plan(graph, request);
        var owners = plan.Matches.Select(match => match.ContainingSymbolId).Distinct(StringComparer.Ordinal).ToArray();
        var files = owners.Select(owner => graph.GetSymbol(owner)?.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!)
            .Distinct(StringComparer.Ordinal).ToArray();
        var facts = new List<OperationFact>();
        var receipt = new ScanOperationFactsUseCase(provider).Execute(new OperationFactQuery
        {
            ProfileScope = request.ProfileScope,
            FilePaths = files,
            ContainingSymbolIds = owners,
            IncludeImplicit = true,
            IncludeCallableDeclarations = true,
            MaxFacts = request.MaxFacts,
        }, fact => { facts.Add(fact); return true; }, cancellationToken);
        return MethodEffectAnalyzer.Analyze(graph, request, plan, facts, receipt, cancellationToken);
    }
}

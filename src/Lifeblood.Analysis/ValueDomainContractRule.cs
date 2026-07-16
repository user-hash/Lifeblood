using Lifeblood.Domain.Results;

namespace Lifeblood.Analysis;

/// <summary>
/// Pure value-domain policy evaluation over one neutral operation fact. Domain
/// names and conversions remain consumer-owned manifest data.
/// </summary>
internal static class ValueDomainContractRule
{
    internal static ValueDomainAssessment? Evaluate(ValueDomainContract contract, OperationFact fact)
    {
        if (fact.TargetSymbolId == null
            || !contract.TargetSymbolIds.Contains(fact.TargetSymbolId, StringComparer.Ordinal)
            || !contract.OperationKinds.Contains(fact.Kind, StringComparer.Ordinal))
            return null;

        var input = fact.Inputs.FirstOrDefault(candidate =>
            string.Equals(candidate.Role, contract.InputRole, StringComparison.Ordinal)
            && (!contract.InputOrdinal.HasValue || candidate.Ordinal == contract.InputOrdinal));
        if (input == null)
            return new ValueDomainAssessment(null, Array.Empty<string>(), IsUnclassified: false);
        if (input.Value.IsCompileTimeConstant && contract.AllowCompileTimeConstants)
            return null;

        var observedDomains = contract.Bindings
            .Where(binding => binding.SourceSymbolIds.Any(sourceId =>
                input.Value.SourceSymbolIds.Contains(sourceId, StringComparer.Ordinal)))
            .Select(binding => binding.Domain)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(domain => domain, StringComparer.Ordinal)
            .ToArray();

        if (observedDomains.Length == 1
            && string.Equals(observedDomains[0], contract.TargetDomain, StringComparison.Ordinal))
            return null;

        if (observedDomains.Length > 0
            && contract.AllowedConversions.Any(conversion => ConversionMatches(
                conversion,
                observedDomains,
                input.Value)))
            return null;

        if (observedDomains.Length == 0 && !contract.ReportUnclassifiedValues)
            return null;

        return new ValueDomainAssessment(
            input,
            observedDomains,
            IsUnclassified: observedDomains.Length == 0);
    }

    private static bool ConversionMatches(
        ValueDomainConversion conversion,
        IReadOnlyList<string> observedDomains,
        OperationValueFact value)
    {
        var declaredDomains = conversion.SourceDomains
            .Distinct(StringComparer.Ordinal)
            .OrderBy(domain => domain, StringComparer.Ordinal)
            .ToArray();
        return declaredDomains.SequenceEqual(observedDomains, StringComparer.Ordinal)
            && conversion.RequiredSourceSymbolIds.All(id =>
                value.SourceSymbolIds.Contains(id, StringComparer.Ordinal))
            && conversion.RequiredOperators.All(operatorName =>
                value.Operators.Contains(operatorName, StringComparer.Ordinal));
    }
}

internal sealed record ValueDomainAssessment(
    OperationInputFact? Input,
    string[] ObservedDomains,
    bool IsUnclassified);

using Lifeblood.Domain.Results;

namespace Lifeblood.Analysis;

/// <summary>
/// Pure value-domain policy evaluation over one neutral operation fact. Domain
/// names, non-finite actions, constant exceptions, and conversion evidence
/// remain consumer-owned manifest data.
/// </summary>
internal static class ValueDomainContractRule
{
    internal static ValueDomainAssessment[] Evaluate(ValueDomainContract contract, OperationFact fact)
    {
        if (fact.TargetSymbolId == null
            || !contract.TargetSymbolIds.Contains(fact.TargetSymbolId, StringComparer.Ordinal)
            || !contract.OperationKinds.Contains(fact.Kind, StringComparer.Ordinal))
            return Array.Empty<ValueDomainAssessment>();

        var input = fact.Inputs.FirstOrDefault(candidate =>
            string.Equals(candidate.Role, contract.InputRole, StringComparison.Ordinal)
            && (!contract.InputOrdinal.HasValue || candidate.Ordinal == contract.InputOrdinal));
        if (input == null)
        {
            return new[]
            {
                new ValueDomainAssessment(
                    ContractFindingKind.ValueDomainMismatch,
                    null,
                    Array.Empty<string>(),
                    Array.Empty<OperationConstantFact>(),
                    null,
                    IsUnclassified: false,
                    ConfidenceBand.Proven),
            };
        }

        var findings = new List<ValueDomainAssessment>();
        var observedDomains = contract.Bindings
            .Where(binding => binding.SourceSymbolIds.Any(sourceId =>
                input.Value.SourceSymbolIds.Contains(sourceId, StringComparer.Ordinal)))
            .Select(binding => binding.Domain)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(domain => domain, StringComparer.Ordinal)
            .ToArray();

        EvaluateNonFinitePolicy(contract, input, observedDomains, findings);
        EvaluateConstantPolicy(contract, input, observedDomains, findings);

        var domainSatisfied = observedDomains.Length == 1
            && string.Equals(observedDomains[0], contract.TargetDomain, StringComparison.Ordinal);
        if (!domainSatisfied && observedDomains.Length > 0)
        {
            domainSatisfied = contract.AllowedConversions.Any(conversion => ConversionMatches(
                conversion,
                observedDomains,
                input.Value));
        }

        if (!domainSatisfied
            && !(input.Value.IsCompileTimeConstant && contract.AllowCompileTimeConstants)
            && (observedDomains.Length > 0 || contract.ReportUnclassifiedValues))
        {
            findings.Add(new ValueDomainAssessment(
                ContractFindingKind.ValueDomainMismatch,
                input,
                observedDomains,
                Array.Empty<OperationConstantFact>(),
                null,
                IsUnclassified: observedDomains.Length == 0,
                observedDomains.Length == 0 ? ConfidenceBand.Advisory : ConfidenceBand.Proven));
        }

        return findings.ToArray();
    }

    private static void EvaluateNonFinitePolicy(
        ValueDomainContract contract,
        OperationInputFact input,
        string[] observedDomains,
        List<ValueDomainAssessment> findings)
    {
        var policy = contract.NonFinitePolicy;
        if (policy == null
            || string.Equals(policy.Action, NonFinitePolicyAction.Allow, StringComparison.Ordinal))
            return;

        var hasEvidence = policy.EvidenceSymbolIds.Any(id =>
            input.Value.SourceSymbolIds.Contains(id, StringComparer.Ordinal));
        var explicitNonFinite = input.Value.Constants
            .Where(constant => constant.NumericClassification is
                OperationNumericClassification.NaN
                or OperationNumericClassification.PositiveInfinity
                or OperationNumericClassification.NegativeInfinity)
            .ToArray();

        if (explicitNonFinite.Length > 0 && !hasEvidence)
        {
            findings.Add(new ValueDomainAssessment(
                ContractFindingKind.NonFinitePolicyMismatch,
                input,
                observedDomains,
                explicitNonFinite,
                policy.Action,
                IsUnclassified: false,
                ConfidenceBand.Proven));
            return;
        }

        if (policy.RequireEvidenceForAllValues && !hasEvidence)
        {
            findings.Add(new ValueDomainAssessment(
                ContractFindingKind.NonFinitePolicyMismatch,
                input,
                observedDomains,
                Array.Empty<OperationConstantFact>(),
                policy.Action,
                IsUnclassified: false,
                ConfidenceBand.Advisory));
        }
    }

    private static void EvaluateConstantPolicy(
        ValueDomainContract contract,
        OperationInputFact input,
        string[] observedDomains,
        List<ValueDomainAssessment> findings)
    {
        var policy = contract.ConstantPolicy;
        if (policy is not { ReportRawNumericLiterals: true }) return;

        var disallowed = input.Value.Constants
            .Where(constant => string.Equals(
                constant.Origin,
                OperationConstantOrigin.Literal,
                StringComparison.Ordinal))
            .Where(constant => !string.Equals(
                constant.NumericClassification,
                OperationNumericClassification.NonNumeric,
                StringComparison.Ordinal))
            .Where(constant => constant.Value == null
                || !policy.AllowedLiteralValues.Contains(constant.Value, StringComparer.Ordinal))
            .ToArray();
        if (disallowed.Length == 0) return;

        findings.Add(new ValueDomainAssessment(
            ContractFindingKind.ConstantProvenanceMismatch,
            input,
            observedDomains,
            disallowed,
            null,
            IsUnclassified: false,
            ConfidenceBand.Proven));
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
    string FindingKind,
    OperationInputFact? Input,
    string[] ObservedDomains,
    OperationConstantFact[] Constants,
    string? NonFiniteAction,
    bool IsUnclassified,
    ConfidenceBand Confidence);

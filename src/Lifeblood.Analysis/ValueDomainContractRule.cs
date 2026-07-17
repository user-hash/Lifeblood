using System.Globalization;
using Lifeblood.Domain.Results;

namespace Lifeblood.Analysis;

/// <summary>
/// Pure value-domain policy evaluation over one neutral operation fact. Domain
/// names, non-finite actions, constant exceptions, and conversion evidence
/// remain consumer-owned manifest data.
/// </summary>
internal static class ValueDomainContractRule
{
    internal static bool Selects(ValueDomainContract contract, OperationFact fact)
        => fact.TargetSymbolId != null
            && contract.TargetSymbolIds.Contains(fact.TargetSymbolId, StringComparer.Ordinal)
            && contract.OperationKinds.Contains(fact.Kind, StringComparer.Ordinal);

    internal static ValueDomainAssessment[] Evaluate(ValueDomainContract contract, OperationFact fact)
    {
        if (!Selects(contract, fact))
            return Array.Empty<ValueDomainAssessment>();

        var input = SelectInput(contract, fact);
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
        EvaluateBoundaryPolicy(contract, fact, input, observedDomains, findings);

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

    internal static NearEqualConstantObservation[] CollectNearEqualObservations(
        ValueDomainContract contract,
        OperationFact fact)
    {
        if (contract.ConstantPolicy?.NearEqualPolicy == null || !Selects(contract, fact))
            return Array.Empty<NearEqualConstantObservation>();

        var input = SelectInput(contract, fact);
        if (input == null) return Array.Empty<NearEqualConstantObservation>();

        return input.Value.Constants
            .Where(constant => string.Equals(
                constant.Origin,
                OperationConstantOrigin.Literal,
                StringComparison.Ordinal))
            .Where(constant => string.Equals(
                constant.NumericClassification,
                OperationNumericClassification.Finite,
                StringComparison.Ordinal))
            .Select(constant => TryParseFinite(constant.Value, out var numeric)
                ? new NearEqualConstantObservation(fact, input, constant, numeric)
                : null)
            .Where(observation => observation != null)
            .Cast<NearEqualConstantObservation>()
            .ToArray();
    }

    internal static NearEqualConstantAssessment[] GroupNearEqualConstants(
        ValueDomainNearEqualPolicy policy,
        IReadOnlyList<NearEqualConstantObservation> observations)
    {
        var groups = observations
            .GroupBy(observation => new NearEqualValueKey(
                observation.Fact.Kind,
                observation.NumericValue))
            .Select(group => new NearEqualValueGroup(
                group.Key.OperationKind,
                group.Key.NumericValue,
                group.OrderBy(observation => observation.Constant.Source.FilePath, StringComparer.Ordinal)
                    .ThenBy(observation => observation.Constant.Source.Line)
                    .ThenBy(observation => observation.Constant.Source.Column)
                    .ThenBy(observation => observation.Fact.Id, StringComparer.Ordinal)
                    .ToArray()))
            .OrderBy(group => group.OperationKind, StringComparer.Ordinal)
            .ThenBy(group => group.NumericValue)
            .ToArray();

        var assessments = new List<NearEqualConstantAssessment>();
        foreach (var operationGroup in groups.GroupBy(group => group.OperationKind, StringComparer.Ordinal))
        {
            var values = operationGroup.ToArray();
            for (var index = 1; index < values.Length; index++)
            {
                var previous = values[index - 1];
                var current = values[index];
                if (!AreNear(previous.NumericValue, current.NumericValue, policy)
                    || previous.Observations.Length + current.Observations.Length < policy.MinimumOccurrences)
                    continue;

                assessments.Add(new NearEqualConstantAssessment(
                    operationGroup.Key,
                    previous.NumericValue,
                    current.NumericValue,
                    previous.Observations.Concat(current.Observations).ToArray()));
            }
        }

        return assessments.ToArray();
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

    private static void EvaluateBoundaryPolicy(
        ValueDomainContract contract,
        OperationFact fact,
        OperationInputFact input,
        string[] observedDomains,
        List<ValueDomainAssessment> findings)
    {
        var policy = contract.BoundaryPolicy;
        if (policy == null) return;

        var candidates = new List<BoundaryCandidate>();
        foreach (var context in fact.ControlContexts.Where(context =>
                     policy.ContextKinds.Contains(context.Kind, StringComparer.Ordinal)))
        {
            foreach (var predicate in context.Predicates)
            {
                if (!IsComparison(predicate.Operator)
                    || predicate.LeftValue == null
                    || predicate.RightValue == null)
                    continue;

                var leftHasBoundary = HasAnySource(
                    predicate.LeftValue,
                    policy.BoundarySourceSymbolIds);
                var rightHasBoundary = HasAnySource(
                    predicate.RightValue,
                    policy.BoundarySourceSymbolIds);
                if (leftHasBoundary == rightHasBoundary) continue;

                var side = leftHasBoundary ? BoundaryOperandSide.Left : BoundaryOperandSide.Right;
                var boundaryValue = leftHasBoundary ? predicate.LeftValue : predicate.RightValue;
                var cursorValue = leftHasBoundary ? predicate.RightValue : predicate.LeftValue;
                if (policy.RequireInputSourceInPredicate
                    && (input.Value.SourceSymbolIds.Length == 0
                        || !input.Value.SourceSymbolIds.Any(id =>
                            cursorValue.SourceSymbolIds.Contains(id, StringComparer.Ordinal))))
                    continue;

                candidates.Add(new BoundaryCandidate(context, predicate, boundaryValue, side));
            }
        }

        if (candidates.Count == 0)
        {
            if (policy.ReportMissingBoundary)
            {
                findings.Add(new ValueDomainAssessment(
                    ContractFindingKind.CadenceBoundaryMismatch,
                    input,
                    observedDomains,
                    Array.Empty<OperationConstantFact>(),
                    null,
                    IsUnclassified: false,
                    ConfidenceBand.Advisory,
                    BoundaryMissing: true));
            }
            return;
        }

        foreach (var candidate in candidates)
        {
            if (policy.AllowedShapes.Any(shape => ShapeMatches(shape, candidate)))
                continue;

            findings.Add(new ValueDomainAssessment(
                ContractFindingKind.CadenceBoundaryMismatch,
                input,
                observedDomains,
                candidate.BoundaryValue.Constants,
                null,
                IsUnclassified: false,
                ConfidenceBand.Proven,
                BoundaryContext: candidate.Context,
                BoundaryPredicate: candidate.Predicate,
                BoundaryValue: candidate.BoundaryValue,
                BoundarySide: candidate.Side));
        }
    }

    private static bool ShapeMatches(ValueDomainBoundaryShape shape, BoundaryCandidate candidate)
    {
        if (!string.Equals(shape.ComparisonOperator, candidate.Predicate.Operator, StringComparison.Ordinal)
            || (!string.Equals(shape.BoundarySide, BoundaryOperandSide.Either, StringComparison.Ordinal)
                && !string.Equals(shape.BoundarySide, candidate.Side, StringComparison.Ordinal)))
            return false;
        if (shape.BoundaryValueKinds.Length > 0
            && !shape.BoundaryValueKinds.Contains(candidate.BoundaryValue.Kind, StringComparer.Ordinal))
            return false;

        var actualOperators = Normalize(candidate.BoundaryValue.Operators);
        var expectedOperators = Normalize(shape.BoundaryOperators);
        if (!actualOperators.SequenceEqual(expectedOperators, StringComparer.Ordinal))
            return false;

        var actualConstants = candidate.BoundaryValue.Constants
            .Where(constant => constant.Value != null)
            .Select(constant => constant.Value!)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var expectedConstants = Normalize(shape.BoundaryConstantValues);
        return actualConstants.SequenceEqual(expectedConstants, StringComparer.Ordinal);
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

    private static OperationInputFact? SelectInput(ValueDomainContract contract, OperationFact fact)
        => fact.Inputs.FirstOrDefault(candidate =>
            string.Equals(candidate.Role, contract.InputRole, StringComparison.Ordinal)
            && (!contract.InputOrdinal.HasValue || candidate.Ordinal == contract.InputOrdinal));

    private static bool HasAnySource(OperationValueFact value, IReadOnlyList<string> sourceIds)
        => sourceIds.Any(id => value.SourceSymbolIds.Contains(id, StringComparer.Ordinal));

    private static bool IsComparison(string operatorName)
        => operatorName is "Equals" or "NotEquals" or "LessThan" or "LessThanOrEqual"
            or "GreaterThan" or "GreaterThanOrEqual";

    private static string[] Normalize(IEnumerable<string> values)
        => values.OrderBy(value => value, StringComparer.Ordinal).ToArray();

    private static bool TryParseFinite(string? value, out double numeric)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out numeric)
            && double.IsFinite(numeric);

    private static bool AreNear(
        double left,
        double right,
        ValueDomainNearEqualPolicy policy)
    {
        if (left.Equals(right)) return false;
        var difference = Math.Abs(left - right);
        var scale = Math.Max(Math.Abs(left), Math.Abs(right));
        var tolerance = Math.Max(
            policy.AbsoluteTolerance,
            policy.RelativeTolerance * scale);
        return difference <= tolerance;
    }
}

internal sealed record ValueDomainAssessment(
    string FindingKind,
    OperationInputFact? Input,
    string[] ObservedDomains,
    OperationConstantFact[] Constants,
    string? NonFiniteAction,
    bool IsUnclassified,
    ConfidenceBand Confidence,
    OperationControlContext? BoundaryContext = null,
    OperationControlPredicate? BoundaryPredicate = null,
    OperationValueFact? BoundaryValue = null,
    string? BoundarySide = null,
    bool BoundaryMissing = false);

internal sealed record NearEqualConstantObservation(
    OperationFact Fact,
    OperationInputFact Input,
    OperationConstantFact Constant,
    double NumericValue);

internal sealed record NearEqualConstantAssessment(
    string OperationKind,
    double LowerValue,
    double UpperValue,
    NearEqualConstantObservation[] Observations);

internal sealed record BoundaryCandidate(
    OperationControlContext Context,
    OperationControlPredicate Predicate,
    OperationValueFact BoundaryValue,
    string Side);

internal readonly record struct NearEqualValueKey(string OperationKind, double NumericValue);

internal sealed record NearEqualValueGroup(
    string OperationKind,
    double NumericValue,
    NearEqualConstantObservation[] Observations);

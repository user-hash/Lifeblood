using Lifeblood.Domain.Results;

namespace Lifeblood.Analysis;

/// <summary>
/// Stateless exact-shape evaluator over neutral occurrence facts. It assigns
/// no meaning to consumer categories or symbol names; the manifest owns every
/// accepted input, representation, and lexical control relationship.
/// </summary>
internal static class OperationShapeContractRule
{
    internal static OperationShapeAssessment? Evaluate(
        OperationShapeContract contract,
        OperationFact fact)
    {
        if (!Selects(contract, fact)) return null;

        var failures = new List<OperationAllowedShapeFailure>();
        foreach (var shape in contract.AllowedShapes)
        {
            var reasons = EvaluateShape(shape, fact);
            if (reasons.Length == 0) return null;
            failures.Add(new OperationAllowedShapeFailure(shape.Id, reasons));
        }

        return new OperationShapeAssessment(failures.ToArray());
    }

    internal static OperationShapeUniqueObservation[] CollectUniquenessObservations(
        OperationShapeContract contract,
        OperationFact fact)
    {
        var policy = contract.UniquenessPolicy;
        if (policy == null || !Selects(contract, fact))
            return Array.Empty<OperationShapeUniqueObservation>();

        var input = fact.Inputs.FirstOrDefault(candidate =>
            string.Equals(candidate.Role, policy.InputRole, StringComparison.Ordinal)
            && (!policy.InputOrdinal.HasValue || candidate.Ordinal == policy.InputOrdinal));
        if (input == null) return Array.Empty<OperationShapeUniqueObservation>();

        var keys = policy.KeyKind switch
        {
            OperationShapeKeyKind.ConstantValue => ConstantValues(input.Value),
            OperationShapeKeyKind.SourceSymbolId => input.Value.SourceSymbolIds,
            _ => Array.Empty<string>(),
        };
        return keys
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .Select(key => new OperationShapeUniqueObservation(fact, input, key))
            .ToArray();
    }

    internal static OperationShapeDuplicateAssessment[] GroupDuplicateKeys(
        OperationShapeUniquenessPolicy policy,
        IReadOnlyList<OperationShapeUniqueObservation> observations)
        => observations
            .GroupBy(observation => observation.Key, StringComparer.Ordinal)
            .Where(group => group.Count() >= policy.MinimumOccurrences)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new OperationShapeDuplicateAssessment(
                group.Key,
                group.OrderBy(observation => observation.Fact.Source.FilePath, StringComparer.Ordinal)
                    .ThenBy(observation => observation.Fact.Source.Line)
                    .ThenBy(observation => observation.Fact.Source.Column)
                    .ThenBy(observation => observation.Fact.Id, StringComparer.Ordinal)
                    .ToArray()))
            .ToArray();

    private static bool Selects(OperationShapeContract contract, OperationFact fact)
        => contract.OperationKinds.Contains(fact.Kind, StringComparer.Ordinal)
            && MatchesOptional(contract.TargetSymbolIds, fact.TargetSymbolId)
            && MatchesOptional(contract.ContainingSymbolIds, fact.ContainingSymbolId)
            && MatchesOptional(contract.Operators, fact.Operator);

    private static string[] EvaluateShape(OperationAllowedShape shape, OperationFact fact)
    {
        var reasons = new List<string>();
        if (shape.AllowedResultTypes.Length > 0
            && (fact.ResultType == null
                || !shape.AllowedResultTypes.Contains(fact.ResultType, StringComparer.Ordinal)))
        {
            reasons.Add(
                $"result type '{fact.ResultType ?? "<none>"}' is not one of " +
                $"[{string.Join(", ", shape.AllowedResultTypes)}]");
        }

        foreach (var expected in shape.Inputs)
        {
            var actual = fact.Inputs.FirstOrDefault(input =>
                string.Equals(input.Role, expected.Role, StringComparison.Ordinal)
                && (!expected.Ordinal.HasValue || input.Ordinal == expected.Ordinal));
            var label = expected.Ordinal.HasValue
                ? $"input {expected.Role}[{expected.Ordinal}]"
                : $"input {expected.Role}";
            if (actual == null)
            {
                reasons.Add(label + " is missing");
                continue;
            }

            EvaluateInput(expected, actual.Value, label, reasons);
        }

        foreach (var expected in shape.ControlContexts)
        {
            if (fact.ControlContexts.Any(actual => MatchesControl(expected, actual))) continue;
            reasons.Add($"control context '{expected.Kind}' lacks the declared operator/source shape");
        }

        return reasons.ToArray();
    }

    private static void EvaluateInput(
        OperationInputShape expected,
        OperationValueFact actual,
        string label,
        List<string> reasons)
    {
        if (expected.AllowedValueKinds.Length > 0
            && !expected.AllowedValueKinds.Contains(actual.Kind, StringComparer.Ordinal))
        {
            reasons.Add($"{label} value kind '{actual.Kind}' is not allowed");
        }
        if (expected.AllowedTypes.Length > 0
            && (actual.Type == null
                || !expected.AllowedTypes.Contains(actual.Type, StringComparer.Ordinal)))
        {
            reasons.Add($"{label} type '{actual.Type ?? "<none>"}' is not allowed");
        }
        if (expected.AnySourceSymbolIds.Length > 0
            && !expected.AnySourceSymbolIds.Any(id =>
                actual.SourceSymbolIds.Contains(id, StringComparer.Ordinal)))
        {
            reasons.Add($"{label} has none of the declared source symbols");
        }

        var missingSources = expected.RequiredSourceSymbolIds
            .Where(id => !actual.SourceSymbolIds.Contains(id, StringComparer.Ordinal))
            .ToArray();
        if (missingSources.Length > 0)
            reasons.Add($"{label} is missing source symbols [{string.Join(", ", missingSources)}]");

        var missingOperators = expected.RequiredOperators
            .Where(value => !actual.Operators.Contains(value, StringComparer.Ordinal))
            .ToArray();
        if (missingOperators.Length > 0)
            reasons.Add($"{label} is missing operators [{string.Join(", ", missingOperators)}]");

        var actualConstants = ConstantValues(actual);
        if (expected.AllowedConstantValues.Length > 0
            && (actualConstants.Length == 0
                || actualConstants.Any(value =>
                    !expected.AllowedConstantValues.Contains(value, StringComparer.Ordinal))))
        {
            reasons.Add(
                $"{label} constants [{string.Join(", ", actualConstants)}] are not within the allowed set");
        }
        var missingConstants = expected.RequiredConstantValues
            .Where(value => !actualConstants.Contains(value, StringComparer.Ordinal))
            .ToArray();
        if (missingConstants.Length > 0)
            reasons.Add($"{label} is missing constants [{string.Join(", ", missingConstants)}]");

        if (expected.CompileTimeConstant.HasValue
            && actual.IsCompileTimeConstant != expected.CompileTimeConstant.Value)
        {
            reasons.Add(
                $"{label} compile-time-constant state is {actual.IsCompileTimeConstant} " +
                $"instead of {expected.CompileTimeConstant.Value}");
        }
    }

    private static bool MatchesControl(
        OperationControlShape expected,
        OperationControlContext actual)
    {
        if (!string.Equals(expected.Kind, actual.Kind, StringComparison.Ordinal)) return false;
        if (!expected.RequiredOperators.All(value =>
                actual.Operators.Contains(value, StringComparer.Ordinal)))
            return false;

        var sources = actual.ConditionValue?.SourceSymbolIds ?? Array.Empty<string>();
        return (expected.AnySourceSymbolIds.Length == 0
                || expected.AnySourceSymbolIds.Any(id => sources.Contains(id, StringComparer.Ordinal)))
            && expected.RequiredSourceSymbolIds.All(id => sources.Contains(id, StringComparer.Ordinal));
    }

    private static bool MatchesOptional(string[] expected, string? actual)
        => expected.Length == 0
            || (actual != null && expected.Contains(actual, StringComparer.Ordinal));

    private static string[] ConstantValues(OperationValueFact value)
        => value.Constants
            .Where(constant => constant.Value != null)
            .Select(constant => constant.Value!)
            .Append(value.ConstantValue)
            .Where(constant => constant != null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(constant => constant, StringComparer.Ordinal)
            .ToArray();
}

internal sealed record OperationShapeAssessment(OperationAllowedShapeFailure[] Failures);

internal sealed record OperationAllowedShapeFailure(string ShapeId, string[] Reasons);

internal sealed record OperationShapeUniqueObservation(
    OperationFact Fact,
    OperationInputFact Input,
    string Key);

internal sealed record OperationShapeDuplicateAssessment(
    string Key,
    OperationShapeUniqueObservation[] Observations);

using System.Globalization;
using System.Text;
using Lifeblood.Domain.Results;

namespace Lifeblood.Analysis;

/// <summary>
/// Pure selection and signature projection for facts partitioned by call
/// routes. The evaluator owns policy; this rule never walks source or graph
/// edges and retains no state between requests.
/// </summary>
internal static class RouteFactContractRule
{
    private const int MaximumSummaryCharacters = 2_048;

    public static bool Selects(RouteFactContract contract, OperationFact fact)
        => contract.OperationKinds.Contains(fact.Kind, StringComparer.Ordinal)
            && (contract.Operators.Length == 0
                || (fact.Operator != null && contract.Operators.Contains(fact.Operator, StringComparer.Ordinal)))
            && (contract.MatchAnyTarget
                || (fact.TargetSymbolId != null
                    && contract.TargetSymbolIds.Contains(fact.TargetSymbolId, StringComparer.Ordinal)));

    public static RouteFactSignature Signature(RouteFactContract contract, OperationFact fact)
    {
        var components = RouteFactSignaturePart.All
            .Where(part => contract.SignatureParts.Contains(part, StringComparer.Ordinal))
            .Select(part => new SignatureComponent(part, Values(part, fact)))
            .ToArray();
        var key = string.Concat(components.Select(component =>
            Frame(component.Name) + FrameMany(component.Values)));
        var summary = string.Join(
            "; ",
            components.Select(component =>
                $"{component.Name}=[{string.Join(", ", component.Values)}]"));
        if (summary.Length > MaximumSummaryCharacters)
            summary = summary[..MaximumSummaryCharacters] + "...";
        return new RouteFactSignature(key, summary);
    }

    private static string[] Values(string part, OperationFact fact)
        => part switch
        {
            RouteFactSignaturePart.Kind => One(fact.Kind),
            RouteFactSignaturePart.TargetSymbol => One(fact.TargetSymbolId ?? "<targetless>"),
            RouteFactSignaturePart.Operator => One(fact.Operator ?? "<none>"),
            RouteFactSignaturePart.ResultType => One(fact.ResultType ?? "<none>"),
            RouteFactSignaturePart.InputValueKinds => Inputs(fact, input => input.Value.Kind),
            RouteFactSignaturePart.InputTypes => Inputs(fact, input => input.Value.Type ?? "<none>"),
            RouteFactSignaturePart.InputConstants => InputsMany(
                fact,
                input => input.Value.Constants.Select(constant =>
                    $"{constant.Origin}:{constant.SymbolId ?? "<literal>"}:{constant.Value ?? "<unknown>"}:" +
                    constant.NumericClassification)),
            RouteFactSignaturePart.InputSourceSymbols => InputsMany(
                fact,
                input => input.Value.SourceSymbolIds.OrderBy(id => id, StringComparer.Ordinal)),
            RouteFactSignaturePart.InputOperators => InputsMany(
                fact,
                input => input.Value.Operators.OrderBy(value => value, StringComparer.Ordinal)),
            RouteFactSignaturePart.ControlKinds => fact.ControlContexts
                .Select(context => context.BranchArm == null
                    ? context.Kind
                    : $"{context.Kind}[{context.BranchArm}]")
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            RouteFactSignaturePart.ControlSourceSymbols => fact.ControlContexts
                .SelectMany(context => (context.ConditionValue?.SourceSymbolIds ?? Array.Empty<string>())
                    .Select(id => $"{context.Kind}:{id}"))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            RouteFactSignaturePart.ControlOperators => fact.ControlContexts
                .SelectMany(context => context.Operators
                    .Concat(context.Predicates.Select(predicate => predicate.Operator))
                    .Select(value => $"{context.Kind}:{value}"))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            _ => Array.Empty<string>(),
        };

    private static string[] Inputs(OperationFact fact, Func<OperationInputFact, string> project)
        => OrderedInputs(fact)
            .Select(input => InputLabel(input) + "=" + project(input))
            .ToArray();

    private static string[] InputsMany(
        OperationFact fact,
        Func<OperationInputFact, IEnumerable<string>> project)
        => OrderedInputs(fact)
            .SelectMany(input => project(input).Select(value => InputLabel(input) + "=" + value))
            .ToArray();

    private static IOrderedEnumerable<OperationInputFact> OrderedInputs(OperationFact fact)
        => fact.Inputs
            .OrderBy(input => input.Role, StringComparer.Ordinal)
            .ThenBy(input => input.Ordinal ?? -1)
            .ThenBy(input => input.ParameterId ?? string.Empty, StringComparer.Ordinal);

    private static string InputLabel(OperationInputFact input)
        => $"{input.Role}[{input.Ordinal?.ToString(CultureInfo.InvariantCulture) ?? "any"}]";

    private static string[] One(string value) => new[] { value };

    private static string FrameMany(IEnumerable<string> values)
    {
        var retained = values.ToArray();
        var builder = new StringBuilder();
        builder.Append(Frame(retained.Length.ToString(CultureInfo.InvariantCulture)));
        foreach (var value in retained)
            builder.Append(Frame(value));
        return builder.ToString();
    }

    private static string Frame(string value)
        => value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;

    private sealed record SignatureComponent(string Name, string[] Values);
}

internal sealed record RouteFactSignature(string Key, string Summary);

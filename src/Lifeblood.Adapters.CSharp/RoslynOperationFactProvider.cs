using System.Globalization;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Operations;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// One-pass Roslyn-to-neutral-operation-fact adapter. It streams bounded facts
/// to the caller and retains no fact graph or compiler objects. Contract policy
/// belongs above this adapter. INV-OPERATION-FACTS-001.
/// </summary>
internal sealed class RoslynOperationFactProvider
{
    private const int MaxExpressionLength = 240;

    private readonly IReadOnlyDictionary<string, CSharpCompilation> _compilations;
    private readonly string _profileScope;
    private readonly string[] _availableProfiles;

    internal RoslynOperationFactProvider(
        IReadOnlyDictionary<string, CSharpCompilation> compilations,
        string profileScope,
        IReadOnlyList<string> availableProfiles)
    {
        _compilations = compilations;
        _profileScope = profileScope;
        _availableProfiles = availableProfiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal OperationFactScanReceipt Scan(
        OperationFactQuery query,
        Func<OperationFact, bool> consume,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(consume);
        if (query.MaxFacts <= 0)
            throw new ArgumentOutOfRangeException(nameof(query), "maxFacts must be greater than zero.");
        if (query.ProfileScope != null
            && !string.Equals(query.ProfileScope, _profileScope, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Requested profile '{query.ProfileScope}' is not the retained operation profile '{_profileScope}'. " +
                $"Available profiles: {string.Join(", ", _availableProfiles)}.",
                nameof(query));
        }

        var fileFilter = NormalizeSet(
            query.FilePaths,
            NormalizePath,
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var containingFilter = NormalizeSet(
            query.ContainingSymbolIds,
            value => value,
            StringComparer.Ordinal);
        var targetFilter = NormalizeSet(
            query.TargetSymbolIds,
            value => value,
            StringComparer.Ordinal);
        var kindFilter = NormalizeSet(
            query.IncludeKinds,
            value => value,
            StringComparer.Ordinal);
        var scannedModules = 0;
        var scannedFiles = 0;
        var observedOperations = 0;
        var emittedFacts = 0;
        var truncated = false;
        var stoppedByConsumer = false;
        var stop = false;

        foreach (var (moduleName, compilation) in _compilations.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (query.ModuleScope != null
                && !string.Equals(query.ModuleScope, moduleName, StringComparison.Ordinal))
                continue;

            scannedModules++;
            foreach (var tree in compilation.SyntaxTrees.OrderBy(tree => tree.FilePath, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var filePath = NormalizePath(tree.FilePath);
                if (fileFilter is { Count: > 0 } && !MatchesPath(fileFilter, filePath))
                    continue;

                scannedFiles++;
                var model = compilation.GetSemanticModel(tree);
                var roots = FindOperationRoots(model, tree.GetRoot(cancellationToken), cancellationToken);
                var visited = new HashSet<IOperation>(ReferenceEqualityComparer.Instance);
                var treeOperationOrdinal = 0;

                foreach (var root in roots)
                {
                    if (!Visit(
                            root,
                            model,
                            compilation,
                            moduleName,
                            query,
                            containingFilter,
                            targetFilter,
                            kindFilter,
                            visited,
                            consume,
                            cancellationToken,
                            ref observedOperations,
                            ref treeOperationOrdinal,
                            ref emittedFacts,
                            ref truncated,
                            ref stoppedByConsumer))
                    {
                        stop = true;
                        break;
                    }
                }

                if (stop) break;
            }

            if (stop) break;
        }

        return new OperationFactScanReceipt
        {
            Status = OperationFactScanStatus.Completed,
            ProfileScope = _profileScope,
            AvailableProfiles = _availableProfiles,
            ExecutionMode = OperationFactExecutionMode.RetainedCompilation,
            InputIdentityVerifiedAtStart = true,
            AdditionalSemanticBaseCount = 0,
            CompiledModuleCount = 0,
            ScannedModuleCount = scannedModules,
            ScannedFileCount = scannedFiles,
            ObservedOperationCount = observedOperations,
            EmittedFactCount = emittedFacts,
            Truncated = truncated,
            StoppedByConsumer = stoppedByConsumer,
            Limitations = _availableProfiles.Length > 1
                ? new[]
                {
                    $"Operation facts currently execute against retained profile '{_profileScope}'. " +
                    "Other available profiles require the profile-scoped execution lane.",
                }
                : Array.Empty<string>(),
        };
    }

    private bool Visit(
        IOperation operation,
        SemanticModel model,
        CSharpCompilation compilation,
        string moduleName,
        OperationFactQuery query,
        HashSet<string>? containingFilter,
        HashSet<string>? targetFilter,
        HashSet<string>? kindFilter,
        HashSet<IOperation> visited,
        Func<OperationFact, bool> consume,
        CancellationToken cancellationToken,
        ref int observedOperations,
        ref int treeOperationOrdinal,
        ref int emittedFacts,
        ref bool truncated,
        ref bool stoppedByConsumer)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!visited.Add(operation)) return true;
        observedOperations++;
        treeOperationOrdinal++;

        var fact = BuildFact(operation, model, compilation, moduleName, treeOperationOrdinal);
        if (fact != null
            && (query.IncludeImplicit || !fact.IsImplicit)
            && (containingFilter is not { Count: > 0 } || containingFilter.Contains(fact.ContainingSymbolId))
            && (targetFilter is not { Count: > 0 }
                || (fact.TargetSymbolId != null && targetFilter.Contains(fact.TargetSymbolId)))
            && (kindFilter is not { Count: > 0 } || kindFilter.Contains(fact.Kind)))
        {
            if (emittedFacts >= query.MaxFacts)
            {
                truncated = true;
                return false;
            }

            emittedFacts++;
            if (!consume(fact))
            {
                stoppedByConsumer = true;
                return false;
            }
        }

        foreach (var child in operation.ChildOperations)
        {
            if (!Visit(
                    child,
                    model,
                    compilation,
                    moduleName,
                    query,
                    containingFilter,
                    targetFilter,
                    kindFilter,
                    visited,
                    consume,
                    cancellationToken,
                    ref observedOperations,
                    ref treeOperationOrdinal,
                    ref emittedFacts,
                    ref truncated,
                    ref stoppedByConsumer))
                return false;
        }

        return true;
    }

    private OperationFact? BuildFact(
        IOperation operation,
        SemanticModel model,
        CSharpCompilation compilation,
        string moduleName,
        int operationOrdinal)
    {
        string? kind = null;
        string? targetId = null;
        string? operatorName = null;
        var inputs = new List<OperationInputFact>();

        switch (operation)
        {
            case IInvocationOperation invocation:
                kind = OperationFactKind.Call;
                targetId = SymbolId(invocation.TargetMethod);
                AddInput(inputs, OperationInputRole.Receiver, invocation.Instance);
                AddArguments(inputs, invocation.Arguments, compilation);
                break;

            case IObjectCreationOperation creation:
                kind = OperationFactKind.ObjectCreation;
                targetId = creation.Constructor == null ? null : SymbolId(creation.Constructor);
                AddArguments(inputs, creation.Arguments, compilation);
                break;

            case IArrayCreationOperation arrayCreation:
                kind = OperationFactKind.ArrayCreation;
                foreach (var dimension in arrayCreation.DimensionSizes)
                    AddInput(inputs, OperationInputRole.Argument, dimension, inputs.Count);
                break;

            case IDelegateCreationOperation delegateCreation:
                kind = OperationFactKind.DelegateCreation;
                AddInput(inputs, OperationInputRole.Value, delegateCreation.Target);
                break;

            case IAssignmentOperation assignment:
                kind = OperationFactKind.Assignment;
                targetId = ReferencedSymbolId(assignment.Target);
                operatorName = assignment switch
                {
                    ICompoundAssignmentOperation compound => compound.OperatorKind.ToString(),
                    ICoalesceAssignmentOperation => "Coalesce",
                    _ => "Simple",
                };
                AddInput(inputs, OperationInputRole.Target, assignment.Target);
                AddInput(inputs, OperationInputRole.Value, assignment.Value);
                break;

            case IIncrementOrDecrementOperation increment:
                kind = OperationFactKind.Assignment;
                targetId = ReferencedSymbolId(increment.Target);
                operatorName = increment.Kind.ToString();
                AddInput(inputs, OperationInputRole.Target, increment.Target);
                break;

            case IFieldReferenceOperation field:
                kind = RoslynOperationFacts.IsWriteContext(field)
                    ? OperationFactKind.MemberWrite
                    : OperationFactKind.MemberRead;
                targetId = SymbolId(field.Field);
                AddInput(inputs, OperationInputRole.Receiver, field.Instance);
                break;

            case IPropertyReferenceOperation property:
                kind = RoslynOperationFacts.IsWriteContext(property)
                    ? OperationFactKind.MemberWrite
                    : OperationFactKind.MemberRead;
                targetId = SymbolId(property.Property);
                AddInput(inputs, OperationInputRole.Receiver, property.Instance);
                break;

            case IEventReferenceOperation eventReference:
                kind = RoslynOperationFacts.IsWriteContext(eventReference)
                    ? OperationFactKind.MemberWrite
                    : OperationFactKind.MemberRead;
                targetId = SymbolId(eventReference.Event);
                AddInput(inputs, OperationInputRole.Receiver, eventReference.Instance);
                break;

            case IArrayElementReferenceOperation element:
                kind = OperationFactKind.ElementAccess;
                AddInput(inputs, OperationInputRole.Receiver, element.ArrayReference);
                for (var index = 0; index < element.Indices.Length; index++)
                    AddInput(inputs, OperationInputRole.Index, element.Indices[index], index);
                break;

            case IBinaryOperation binary:
                kind = OperationFactKind.Binary;
                targetId = binary.OperatorMethod == null ? null : SymbolId(binary.OperatorMethod);
                operatorName = binary.OperatorKind.ToString();
                AddInput(inputs, OperationInputRole.Left, binary.LeftOperand);
                AddInput(inputs, OperationInputRole.Right, binary.RightOperand);
                break;

            case IUnaryOperation unary:
                kind = OperationFactKind.Unary;
                targetId = unary.OperatorMethod == null ? null : SymbolId(unary.OperatorMethod);
                operatorName = unary.OperatorKind.ToString();
                AddInput(inputs, OperationInputRole.Operand, unary.Operand);
                break;

            case IConversionOperation conversion:
                kind = OperationFactKind.Conversion;
                targetId = conversion.OperatorMethod == null ? null : SymbolId(conversion.OperatorMethod);
                operatorName = conversion.IsChecked ? "Checked" : "Unchecked";
                AddInput(inputs, OperationInputRole.Value, conversion.Operand);
                break;

            case IConditionalOperation conditional:
                kind = OperationFactKind.Branch;
                AddInput(inputs, OperationInputRole.Condition, conditional.Condition);
                break;

            case ILoopOperation loop:
                kind = OperationFactKind.Loop;
                operatorName = loop.LoopKind.ToString();
                AddLoopInput(inputs, loop);
                break;

            case IReturnOperation returned:
                kind = OperationFactKind.Return;
                AddInput(inputs, OperationInputRole.ReturnedValue, returned.ReturnedValue);
                break;

            case IThrowOperation thrown:
                kind = OperationFactKind.Throw;
                AddInput(inputs, OperationInputRole.Exception, thrown.Exception);
                break;

            case IAwaitOperation awaited:
                kind = OperationFactKind.Await;
                AddInput(inputs, OperationInputRole.Value, awaited.Operation);
                break;

            case ILockOperation locked:
                kind = OperationFactKind.Lock;
                AddInput(inputs, OperationInputRole.LockedValue, locked.LockedValue);
                break;

            case IInterpolatedStringOperation:
                kind = OperationFactKind.InterpolatedString;
                break;
        }

        if (kind == null) return null;

        var containingSymbolId = ContainingSymbolId(model, operation.Syntax.SpanStart, moduleName);
        var source = SourceSpan(operation.Syntax);
        return new OperationFact
        {
            Id = BuildFactId(moduleName, _profileScope, operation.Syntax, kind, operationOrdinal),
            Kind = kind,
            ModuleName = moduleName,
            ProfileScope = _profileScope,
            ContainingSymbolId = containingSymbolId,
            TargetSymbolId = targetId,
            ResultType = operation.Type == null ? null : TypeDisplay(operation.Type),
            Operator = operatorName,
            Source = source,
            IsImplicit = operation.IsImplicit,
            Inputs = inputs.ToArray(),
            ControlContexts = BuildControlContexts(operation),
        };
    }

    private static IReadOnlyList<IOperation> FindOperationRoots(
        SemanticModel model,
        SyntaxNode syntaxRoot,
        CancellationToken cancellationToken)
    {
        var roots = new List<IOperation>();
        var seen = new HashSet<IOperation>(ReferenceEqualityComparer.Instance);
        foreach (var node in syntaxRoot.DescendantNodesAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = model.GetOperation(node, cancellationToken);
            if (operation?.Parent != null || operation == null || !seen.Add(operation)) continue;
            roots.Add(operation);
        }
        return roots;
    }

    private static void AddArguments(
        List<OperationInputFact> inputs,
        IEnumerable<IArgumentOperation> arguments,
        CSharpCompilation compilation)
    {
        foreach (var argument in arguments)
        {
            var (value, _) = RoslynArgumentBinding.Resolve(argument, compilation);
            inputs.Add(new OperationInputFact
            {
                Role = OperationInputRole.Argument,
                Ordinal = argument.Parameter?.Ordinal,
                ParameterId = argument.Parameter == null ? null : ParameterId(argument.Parameter),
                ParameterName = argument.Parameter?.Name,
                AuthorSupplied = argument.ArgumentKind != ArgumentKind.DefaultValue,
                Value = DescribeValue(value),
            });
        }
    }

    private static void AddLoopInput(List<OperationInputFact> inputs, ILoopOperation loop)
    {
        switch (loop)
        {
            case IForLoopOperation forLoop:
                AddInput(inputs, OperationInputRole.Condition, forLoop.Condition);
                break;
            case IWhileLoopOperation whileLoop:
                AddInput(inputs, OperationInputRole.Condition, whileLoop.Condition);
                break;
            case IForEachLoopOperation forEach:
                AddInput(inputs, OperationInputRole.Collection, forEach.Collection);
                break;
        }
    }

    private static void AddInput(
        List<OperationInputFact> inputs,
        string role,
        IOperation? value,
        int? ordinal = null)
    {
        if (value == null) return;
        inputs.Add(new OperationInputFact
        {
            Role = role,
            Ordinal = ordinal,
            AuthorSupplied = true,
            Value = DescribeValue(value),
        });
    }

    private static OperationValueFact DescribeValue(IOperation operation)
    {
        var conversionTypes = new List<string>();
        var unwrapped = operation;
        while (true)
        {
            switch (unwrapped)
            {
                case IConversionOperation conversion:
                    if (conversion.Type != null)
                        conversionTypes.Add(TypeDisplay(conversion.Type));
                    unwrapped = conversion.Operand;
                    continue;
                case IParenthesizedOperation parenthesized:
                    unwrapped = parenthesized.Operand;
                    continue;
                default:
                    break;
            }
            break;
        }

        var sourceSymbols = new List<string>();
        var seenSymbols = new HashSet<string>(StringComparer.Ordinal);
        CollectSourceSymbols(operation, sourceSymbols, seenSymbols);

        return new OperationValueFact
        {
            Kind = ValueKind(unwrapped),
            SymbolId = ReferencedSymbolId(unwrapped),
            Type = operation.Type == null ? null : TypeDisplay(operation.Type),
            ConstantValue = ConstantText(unwrapped.ConstantValue),
            Expression = Clip(operation.Syntax.ToString()),
            IsCompileTimeConstant = unwrapped.ConstantValue.HasValue,
            SourceSymbolIds = sourceSymbols.ToArray(),
            ConversionTypes = conversionTypes.ToArray(),
        };
    }

    private static void CollectSourceSymbols(
        IOperation operation,
        List<string> result,
        HashSet<string> seen)
    {
        var id = ReferencedSymbolId(operation);
        if (id != null && seen.Add(id)) result.Add(id);
        foreach (var child in operation.ChildOperations)
            CollectSourceSymbols(child, result, seen);
    }

    private static string ValueKind(IOperation operation) => operation switch
    {
        ILiteralOperation { ConstantValue: { HasValue: true, Value: null } } => OperationValueKind.Null,
        ILiteralOperation => OperationValueKind.Literal,
        IFieldReferenceOperation field when field.Field.IsConst => OperationValueKind.Constant,
        IFieldReferenceOperation => OperationValueKind.Field,
        IPropertyReferenceOperation => OperationValueKind.Property,
        IParameterReferenceOperation => OperationValueKind.Parameter,
        ILocalReferenceOperation => OperationValueKind.Local,
        IInvocationOperation => OperationValueKind.Invocation,
        IObjectCreationOperation => OperationValueKind.ObjectCreation,
        IArrayCreationOperation => OperationValueKind.ArrayCreation,
        IBinaryOperation => OperationValueKind.Binary,
        IUnaryOperation => OperationValueKind.Unary,
        IConditionalOperation => OperationValueKind.Conditional,
        IDefaultValueOperation => OperationValueKind.Default,
        IAnonymousFunctionOperation => OperationValueKind.Lambda,
        IMethodReferenceOperation => OperationValueKind.MethodGroup,
        IInterpolatedStringOperation => OperationValueKind.InterpolatedString,
        _ when operation.ConstantValue.HasValue => OperationValueKind.Constant,
        _ => OperationValueKind.Other,
    };

    private static string? ReferencedSymbolId(IOperation operation) => operation switch
    {
        IFieldReferenceOperation field => SymbolId(field.Field),
        IPropertyReferenceOperation property => SymbolId(property.Property),
        IEventReferenceOperation eventReference => SymbolId(eventReference.Event),
        IParameterReferenceOperation parameter => ParameterId(parameter.Parameter),
        ILocalReferenceOperation local => LocalId(local.Local),
        IInvocationOperation invocation => SymbolId(invocation.TargetMethod),
        IObjectCreationOperation creation when creation.Constructor != null => SymbolId(creation.Constructor),
        IMethodReferenceOperation method => SymbolId(method.Method),
        _ => null,
    };

    private static string SymbolId(ISymbol symbol)
        => CanonicalSymbolFormat.BuildSymbolId(symbol.OriginalDefinition);

    private static string ParameterId(IParameterSymbol parameter)
    {
        var owner = parameter.ContainingSymbol == null
            ? "(unknown)"
            : SymbolId(parameter.ContainingSymbol);
        return $"parameter:{owner}#{parameter.Ordinal}:{parameter.Name}";
    }

    private static string LocalId(ILocalSymbol local)
    {
        var owner = local.ContainingSymbol == null ? "(unknown)" : SymbolId(local.ContainingSymbol);
        var declarationStart = local.DeclaringSyntaxReferences.FirstOrDefault()?.Span.Start ?? -1;
        return $"local:{owner}@{declarationStart}:{local.Name}";
    }

    private static string ContainingSymbolId(SemanticModel model, int position, string moduleName)
    {
        ISymbol? symbol = model.GetEnclosingSymbol(position);
        while (symbol is IMethodSymbol method
               && method.MethodKind is MethodKind.AnonymousFunction or MethodKind.LocalFunction)
            symbol = symbol.ContainingSymbol;
        return symbol == null ? $"mod:{moduleName}" : SymbolId(symbol);
    }

    private static OperationControlContext[] BuildControlContexts(IOperation operation)
    {
        var contexts = new List<OperationControlContext>();
        for (var parent = operation.Parent; parent != null; parent = parent.Parent)
        {
            string? kind = null;
            IOperation? conditionOperation = null;
            switch (parent)
            {
                case IConditionalOperation conditional:
                    kind = OperationControlContextKind.Branch;
                    conditionOperation = conditional.Condition;
                    break;
                case ILoopOperation loop:
                    kind = OperationControlContextKind.Loop;
                    conditionOperation = LoopCondition(loop);
                    break;
                case ISwitchOperation switchOperation:
                    kind = OperationControlContextKind.Switch;
                    conditionOperation = switchOperation.Value;
                    break;
                case ITryOperation:
                    kind = OperationControlContextKind.Try;
                    break;
                case ICatchClauseOperation:
                    kind = OperationControlContextKind.Catch;
                    break;
                case IAnonymousFunctionOperation:
                    kind = OperationControlContextKind.AnonymousFunction;
                    break;
                case ILocalFunctionOperation:
                    kind = OperationControlContextKind.LocalFunction;
                    break;
            }

            if (kind != null)
            {
                contexts.Add(new OperationControlContext
                {
                    Kind = kind,
                    Condition = Clip(conditionOperation?.Syntax.ToString()),
                    ConditionValue = conditionOperation == null ? null : DescribeValue(conditionOperation),
                    Operators = conditionOperation == null
                        ? Array.Empty<string>()
                        : CollectOperators(conditionOperation),
                    Predicates = conditionOperation == null
                        ? Array.Empty<OperationControlPredicate>()
                        : CollectPredicates(conditionOperation),
                    Source = SourceSpan(parent.Syntax),
                });
            }
        }
        contexts.Reverse();
        return contexts.ToArray();
    }

    private static string[] CollectOperators(IOperation operation)
    {
        var result = new List<string>();
        CollectOperators(operation, result);
        return result.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void CollectOperators(IOperation operation, List<string> result)
    {
        switch (operation)
        {
            case IBinaryOperation binary:
                result.Add(binary.OperatorKind.ToString());
                break;
            case IUnaryOperation unary:
                result.Add(unary.OperatorKind.ToString());
                break;
        }

        foreach (var child in operation.ChildOperations)
            CollectOperators(child, result);
    }

    private static OperationControlPredicate[] CollectPredicates(IOperation operation)
    {
        var result = new List<OperationControlPredicate>();
        CollectPredicates(operation, result);
        return result.ToArray();
    }

    private static void CollectPredicates(IOperation operation, List<OperationControlPredicate> result)
    {
        var operatorName = operation switch
        {
            IBinaryOperation binary => binary.OperatorKind.ToString(),
            IUnaryOperation unary => unary.OperatorKind.ToString(),
            _ => null,
        };
        if (operatorName != null)
        {
            var symbols = new List<string>();
            CollectSourceSymbols(operation, symbols, new HashSet<string>(StringComparer.Ordinal));
            result.Add(new OperationControlPredicate
            {
                Operator = operatorName,
                Expression = Clip(operation.Syntax.ToString()),
                SourceSymbolIds = symbols.ToArray(),
            });
        }

        foreach (var child in operation.ChildOperations)
            CollectPredicates(child, result);
    }

    private static IOperation? LoopCondition(ILoopOperation loop) => loop switch
    {
        IForLoopOperation forLoop => forLoop.Condition,
        IWhileLoopOperation whileLoop => whileLoop.Condition,
        IForEachLoopOperation forEach => forEach.Collection,
        _ => null,
    };

    private static OperationSourceSpan SourceSpan(SyntaxNode syntax)
    {
        var span = syntax.GetLocation().GetLineSpan();
        return new OperationSourceSpan
        {
            FilePath = NormalizePath(span.Path),
            Line = span.StartLinePosition.Line + 1,
            Column = span.StartLinePosition.Character + 1,
            EndLine = span.EndLinePosition.Line + 1,
            EndColumn = span.EndLinePosition.Character + 1,
        };
    }

    private static string BuildFactId(
        string moduleName,
        string profileScope,
        SyntaxNode syntax,
        string kind,
        int operationOrdinal)
        => $"op:{profileScope}:{moduleName}:{NormalizePath(syntax.SyntaxTree.FilePath)}:" +
           $"{syntax.SpanStart}:{syntax.Span.Length}:{kind}:{operationOrdinal}";

    private static string? ConstantText(Optional<object?> constant)
    {
        if (!constant.HasValue) return null;
        if (constant.Value == null) return "null";
        return constant.Value switch
        {
            bool value => value ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => constant.Value.ToString(),
        };
    }

    private static string TypeDisplay(ITypeSymbol type)
        => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", string.Empty, StringComparison.Ordinal);

    private static string? Clip(string? text)
    {
        if (text == null) return null;
        var collapsed = string.Join(" ", text
            .Split('\n', '\r', '\t')
            .Where(part => part.Length > 0)
            .Select(part => part.Trim()));
        return collapsed.Length <= MaxExpressionLength
            ? collapsed
            : collapsed[..(MaxExpressionLength - 3)] + "...";
    }

    private static HashSet<string>? NormalizeSet(
        IReadOnlyList<string>? values,
        Func<string, string> normalize,
        StringComparer comparer)
    {
        if (values == null || values.Count == 0) return null;
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(normalize)
            .ToHashSet(comparer);
    }

    private static bool MatchesPath(HashSet<string> filter, string filePath)
        => filter.Contains(filePath)
           || filter.Any(candidate => filePath.EndsWith(
               '/' + candidate,
               OperatingSystem.IsWindows()
                   ? StringComparison.OrdinalIgnoreCase
                   : StringComparison.Ordinal));

    private static string NormalizePath(string? path)
        => (path ?? string.Empty).Replace('\\', '/');
}

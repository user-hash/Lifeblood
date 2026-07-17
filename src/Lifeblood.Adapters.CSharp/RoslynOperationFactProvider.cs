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
        var selectors = NormalizeSelectors(query.Selectors);
        var symbolIds = new ScanSymbolIds();
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
                            selectors,
                            symbolIds,
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
        ScanSelector[]? selectors,
        ScanSymbolIds symbolIds,
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

        var kind = OperationKind(operation);
        if (kind != null
            && (query.IncludeImplicit || !operation.IsImplicit)
            && (kindFilter is not { Count: > 0 } || kindFilter.Contains(kind)))
        {
            var operatorName = OperatorName(operation);
            var targetId = TargetSymbolId(operation, symbolIds);
            var targetMatches = targetFilter is not { Count: > 0 }
                || (targetId != null && targetFilter.Contains(targetId));
            var selectorCandidate = SelectorCanMatchBeforeContaining(
                selectors,
                kind,
                operatorName,
                targetId);
            var containingSymbolId = targetMatches && selectorCandidate
                ? ContainingSymbolId(model, operation.Syntax.SpanStart, moduleName, symbolIds)
                : null;
            var containingMatches = containingFilter is not { Count: > 0 }
                || (containingSymbolId != null && containingFilter.Contains(containingSymbolId));
            var selectorMatches = SelectorMatches(
                selectors,
                kind,
                operatorName,
                targetId,
                containingSymbolId);
            if (targetMatches && containingMatches && selectorMatches)
            {
                if (emittedFacts >= query.MaxFacts)
                {
                    truncated = true;
                    return false;
                }

                var fact = BuildFact(
                    operation,
                    compilation,
                    moduleName,
                    treeOperationOrdinal,
                    kind,
                    targetId,
                    containingSymbolId!,
                    operatorName,
                    symbolIds);
                emittedFacts++;
                if (!consume(fact))
                {
                    stoppedByConsumer = true;
                    return false;
                }
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
                    selectors,
                    symbolIds,
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

    private static string? OperationKind(IOperation operation) => operation switch
    {
        IInvocationOperation => OperationFactKind.Call,
        IObjectCreationOperation => OperationFactKind.ObjectCreation,
        IArrayCreationOperation => OperationFactKind.ArrayCreation,
        IDelegateCreationOperation => OperationFactKind.DelegateCreation,
        IAssignmentOperation or IIncrementOrDecrementOperation => OperationFactKind.Assignment,
        IFieldReferenceOperation field => RoslynOperationFacts.IsWriteContext(field)
            ? OperationFactKind.MemberWrite
            : OperationFactKind.MemberRead,
        IPropertyReferenceOperation { Property.IsIndexer: true } => OperationFactKind.ElementAccess,
        IPropertyReferenceOperation property => RoslynOperationFacts.IsWriteContext(property)
            ? OperationFactKind.MemberWrite
            : OperationFactKind.MemberRead,
        IEventReferenceOperation eventReference => RoslynOperationFacts.IsWriteContext(eventReference)
            ? OperationFactKind.MemberWrite
            : OperationFactKind.MemberRead,
        IArrayElementReferenceOperation => OperationFactKind.ElementAccess,
        _ when operation.Syntax.IsKind(SyntaxKind.ElementAccessExpression)
               && TryGetPointerElementOperands(operation, out _, out _)
            => OperationFactKind.ElementAccess,
        _ when operation.Syntax.IsKind(SyntaxKind.PointerIndirectionExpression)
            => OperationFactKind.PointerIndirection,
        IBinaryOperation => OperationFactKind.Binary,
        IUnaryOperation => OperationFactKind.Unary,
        IConversionOperation => OperationFactKind.Conversion,
        IConditionalOperation => OperationFactKind.Branch,
        ILoopOperation => OperationFactKind.Loop,
        IReturnOperation => OperationFactKind.Return,
        IThrowOperation => OperationFactKind.Throw,
        IAwaitOperation => OperationFactKind.Await,
        ILockOperation => OperationFactKind.Lock,
        IInterpolatedStringOperation => OperationFactKind.InterpolatedString,
        _ => null,
    };

    private static string? TargetSymbolId(IOperation operation, ScanSymbolIds symbolIds) => operation switch
    {
        IInvocationOperation invocation => symbolIds.Get(invocation.TargetMethod),
        IObjectCreationOperation { Constructor: { } constructor } => symbolIds.Get(constructor),
        IAssignmentOperation assignment => ReferencedSymbolId(assignment.Target, symbolIds),
        IIncrementOrDecrementOperation increment => ReferencedSymbolId(increment.Target, symbolIds),
        IFieldReferenceOperation field => symbolIds.Get(field.Field),
        IPropertyReferenceOperation property => symbolIds.Get(property.Property),
        IEventReferenceOperation eventReference => symbolIds.Get(eventReference.Event),
        IBinaryOperation { OperatorMethod: { } method } => symbolIds.Get(method),
        IUnaryOperation { OperatorMethod: { } method } => symbolIds.Get(method),
        IConversionOperation { OperatorMethod: { } method } => symbolIds.Get(method),
        _ => null,
    };

    private static string? OperatorName(IOperation operation) => operation switch
    {
        ICompoundAssignmentOperation compound => compound.OperatorKind.ToString(),
        ICoalesceAssignmentOperation => "Coalesce",
        IAssignmentOperation => "Simple",
        IIncrementOrDecrementOperation increment => increment.Kind.ToString(),
        IBinaryOperation binary => binary.OperatorKind.ToString(),
        IUnaryOperation unary => unary.OperatorKind.ToString(),
        IConversionOperation conversion => conversion.IsChecked ? "Checked" : "Unchecked",
        ILoopOperation loop => loop.LoopKind.ToString(),
        _ => null,
    };

    private OperationFact BuildFact(
        IOperation operation,
        CSharpCompilation compilation,
        string moduleName,
        int operationOrdinal,
        string kind,
        string? targetId,
        string containingSymbolId,
        string? operatorName,
        ScanSymbolIds symbolIds)
    {
        var inputs = new List<OperationInputFact>();

        switch (operation)
        {
            case IInvocationOperation invocation:
                AddInput(inputs, OperationInputRole.Receiver, invocation.Instance, symbolIds);
                AddArguments(inputs, invocation.Arguments, compilation, symbolIds);
                break;

            case IObjectCreationOperation creation:
                AddArguments(inputs, creation.Arguments, compilation, symbolIds);
                break;

            case IArrayCreationOperation arrayCreation:
                foreach (var dimension in arrayCreation.DimensionSizes)
                    AddInput(inputs, OperationInputRole.Argument, dimension, symbolIds, inputs.Count);
                break;

            case IDelegateCreationOperation delegateCreation:
                AddInput(inputs, OperationInputRole.Value, delegateCreation.Target, symbolIds);
                break;

            case IAssignmentOperation assignment:
                AddInput(inputs, OperationInputRole.Target, assignment.Target, symbolIds);
                AddInput(inputs, OperationInputRole.Value, assignment.Value, symbolIds);
                break;

            case IIncrementOrDecrementOperation increment:
                AddInput(inputs, OperationInputRole.Target, increment.Target, symbolIds);
                break;

            case IFieldReferenceOperation field:
                AddInput(inputs, OperationInputRole.Receiver, field.Instance, symbolIds);
                break;

            case IPropertyReferenceOperation property:
                AddInput(inputs, OperationInputRole.Receiver, property.Instance, symbolIds);
                if (property.Property.IsIndexer)
                    AddIndexArguments(inputs, property.Arguments, compilation, symbolIds);
                break;

            case IEventReferenceOperation eventReference:
                AddInput(inputs, OperationInputRole.Receiver, eventReference.Instance, symbolIds);
                break;

            case IArrayElementReferenceOperation element:
                AddInput(inputs, OperationInputRole.Receiver, element.ArrayReference, symbolIds);
                for (var index = 0; index < element.Indices.Length; index++)
                    AddInput(inputs, OperationInputRole.Index, element.Indices[index], symbolIds, index);
                break;

            case IBinaryOperation binary:
                AddInput(inputs, OperationInputRole.Left, binary.LeftOperand, symbolIds);
                AddInput(inputs, OperationInputRole.Right, binary.RightOperand, symbolIds);
                break;

            case IUnaryOperation unary:
                AddInput(inputs, OperationInputRole.Operand, unary.Operand, symbolIds);
                break;

            case IConversionOperation conversion:
                AddInput(inputs, OperationInputRole.Value, conversion.Operand, symbolIds);
                break;

            case IConditionalOperation conditional:
                AddInput(inputs, OperationInputRole.Condition, conditional.Condition, symbolIds);
                AddInput(inputs, OperationInputRole.WhenTrue, conditional.WhenTrue, symbolIds);
                AddInput(inputs, OperationInputRole.WhenFalse, conditional.WhenFalse, symbolIds);
                break;

            case ILoopOperation loop:
                AddLoopInput(inputs, loop, symbolIds);
                break;

            case IReturnOperation returned:
                AddInput(inputs, OperationInputRole.ReturnedValue, returned.ReturnedValue, symbolIds);
                break;

            case IThrowOperation thrown:
                AddInput(inputs, OperationInputRole.Exception, thrown.Exception, symbolIds);
                break;

            case IAwaitOperation awaited:
                AddInput(inputs, OperationInputRole.Value, awaited.Operation, symbolIds);
                break;

            case ILockOperation locked:
                AddInput(inputs, OperationInputRole.LockedValue, locked.LockedValue, symbolIds);
                break;

            default:
                if (kind == OperationFactKind.ElementAccess
                    && TryGetPointerElementOperands(operation, out var receiver, out var indexValue))
                {
                    AddInput(inputs, OperationInputRole.Receiver, receiver, symbolIds);
                    AddInput(inputs, OperationInputRole.Index, indexValue, symbolIds, 0);
                }
                else if (kind == OperationFactKind.PointerIndirection)
                {
                    AddInput(
                        inputs,
                        OperationInputRole.Receiver,
                        operation.ChildOperations.FirstOrDefault(),
                        symbolIds);
                }
                break;
        }

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
            ControlContexts = BuildControlContexts(operation, symbolIds),
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
        CSharpCompilation compilation,
        ScanSymbolIds symbolIds)
    {
        foreach (var argument in arguments)
        {
            var (value, _) = RoslynArgumentBinding.Resolve(argument, compilation);
            inputs.Add(new OperationInputFact
            {
                Role = OperationInputRole.Argument,
                Ordinal = argument.Parameter?.Ordinal,
                ParameterId = argument.Parameter == null ? null : symbolIds.Parameter(argument.Parameter),
                ParameterName = argument.Parameter?.Name,
                AuthorSupplied = argument.ArgumentKind != ArgumentKind.DefaultValue,
                Value = DescribeValue(value, symbolIds),
            });
        }
    }

    private static void AddIndexArguments(
        List<OperationInputFact> inputs,
        IEnumerable<IArgumentOperation> arguments,
        CSharpCompilation compilation,
        ScanSymbolIds symbolIds)
    {
        foreach (var argument in arguments)
        {
            var (value, _) = RoslynArgumentBinding.Resolve(argument, compilation);
            inputs.Add(new OperationInputFact
            {
                Role = OperationInputRole.Index,
                Ordinal = argument.Parameter?.Ordinal,
                ParameterId = argument.Parameter == null ? null : symbolIds.Parameter(argument.Parameter),
                ParameterName = argument.Parameter?.Name,
                AuthorSupplied = argument.ArgumentKind != ArgumentKind.DefaultValue,
                Value = DescribeValue(value, symbolIds),
            });
        }
    }

    private static void AddLoopInput(
        List<OperationInputFact> inputs,
        ILoopOperation loop,
        ScanSymbolIds symbolIds)
    {
        switch (loop)
        {
            case IForLoopOperation forLoop:
                AddInput(inputs, OperationInputRole.Condition, forLoop.Condition, symbolIds);
                break;
            case IWhileLoopOperation whileLoop:
                AddInput(inputs, OperationInputRole.Condition, whileLoop.Condition, symbolIds);
                break;
            case IForEachLoopOperation forEach:
                AddInput(inputs, OperationInputRole.Collection, forEach.Collection, symbolIds);
                break;
        }
    }

    private static void AddInput(
        List<OperationInputFact> inputs,
        string role,
        IOperation? value,
        ScanSymbolIds symbolIds,
        int? ordinal = null)
    {
        if (value == null) return;
        inputs.Add(new OperationInputFact
        {
            Role = role,
            Ordinal = ordinal,
            AuthorSupplied = true,
            Value = DescribeValue(value, symbolIds),
        });
    }

    private static OperationValueFact DescribeValue(IOperation operation, ScanSymbolIds symbolIds)
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
        CollectSourceSymbols(operation, sourceSymbols, seenSymbols, symbolIds);

        return new OperationValueFact
        {
            Kind = ValueKind(unwrapped),
            SymbolId = ReferencedSymbolId(unwrapped, symbolIds),
            Type = operation.Type == null ? null : TypeDisplay(operation.Type),
            ConstantValue = ConstantText(unwrapped.ConstantValue),
            Expression = Clip(operation.Syntax.ToString()),
            IsCompileTimeConstant = unwrapped.ConstantValue.HasValue,
            SourceSymbolIds = sourceSymbols.ToArray(),
            ConversionTypes = conversionTypes.ToArray(),
            Operators = CollectOperators(operation),
            Constants = CollectConstants(operation, symbolIds),
        };
    }

    private static void CollectSourceSymbols(
        IOperation operation,
        List<string> result,
        HashSet<string> seen,
        ScanSymbolIds symbolIds)
    {
        var id = ReferencedSymbolId(operation, symbolIds);
        if (id != null && seen.Add(id)) result.Add(id);
        foreach (var child in operation.ChildOperations)
            CollectSourceSymbols(child, result, seen, symbolIds);
    }

    private static OperationConstantFact[] CollectConstants(
        IOperation operation,
        ScanSymbolIds symbolIds)
    {
        var result = new List<OperationConstantFact>();
        CollectConstantLeaves(operation, result, symbolIds);

        var foldedClassification = NumericClassification(operation.ConstantValue);
        if (IsNonFinite(foldedClassification)
            && !result.Any(constant => IsNonFinite(constant.NumericClassification)))
        {
            result.Add(new OperationConstantFact
            {
                Origin = OperationConstantOrigin.FoldedExpression,
                Type = operation.Type == null ? null : TypeDisplay(operation.Type),
                Value = ConstantText(operation.ConstantValue),
                NumericClassification = foldedClassification,
                Expression = Clip(operation.Syntax.ToString()),
                Source = SourceSpan(operation.Syntax),
            });
        }

        return result.ToArray();
    }

    private static void CollectConstantLeaves(
        IOperation operation,
        List<OperationConstantFact> result,
        ScanSymbolIds symbolIds)
    {
        var origin = operation switch
        {
            ILiteralOperation => OperationConstantOrigin.Literal,
            IFieldReferenceOperation field when field.Field.IsConst => OperationConstantOrigin.NamedConstant,
            ILocalReferenceOperation local when local.Local.IsConst => OperationConstantOrigin.NamedConstant,
            IDefaultValueOperation => OperationConstantOrigin.DefaultValue,
            _ => null,
        };
        if (origin != null)
        {
            result.Add(new OperationConstantFact
            {
                Origin = origin,
                SymbolId = ReferencedSymbolId(operation, symbolIds),
                Type = operation.Type == null ? null : TypeDisplay(operation.Type),
                Value = ConstantText(operation.ConstantValue),
                NumericClassification = NumericClassification(operation.ConstantValue),
                Expression = Clip(operation.Syntax.ToString()),
                Source = SourceSpan(operation.Syntax),
            });
            return;
        }

        foreach (var child in operation.ChildOperations)
            CollectConstantLeaves(child, result, symbolIds);
    }

    private static string NumericClassification(Optional<object?> constant)
    {
        if (!constant.HasValue) return OperationNumericClassification.NonNumeric;
        return constant.Value switch
        {
            float value when float.IsNaN(value) => OperationNumericClassification.NaN,
            float value when float.IsPositiveInfinity(value) => OperationNumericClassification.PositiveInfinity,
            float value when float.IsNegativeInfinity(value) => OperationNumericClassification.NegativeInfinity,
            double value when double.IsNaN(value) => OperationNumericClassification.NaN,
            double value when double.IsPositiveInfinity(value) => OperationNumericClassification.PositiveInfinity,
            double value when double.IsNegativeInfinity(value) => OperationNumericClassification.NegativeInfinity,
            sbyte or byte or short or ushort or int or uint or long or ulong or decimal or float or double
                => OperationNumericClassification.Finite,
            _ => OperationNumericClassification.NonNumeric,
        };
    }

    private static bool IsNonFinite(string classification)
        => classification is OperationNumericClassification.NaN
            or OperationNumericClassification.PositiveInfinity
            or OperationNumericClassification.NegativeInfinity;

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

    private static string? ReferencedSymbolId(IOperation operation, ScanSymbolIds symbolIds) => operation switch
    {
        IFieldReferenceOperation field => symbolIds.Get(field.Field),
        IPropertyReferenceOperation property => symbolIds.Get(property.Property),
        IEventReferenceOperation eventReference => symbolIds.Get(eventReference.Event),
        IParameterReferenceOperation parameter => symbolIds.Parameter(parameter.Parameter),
        ILocalReferenceOperation local => symbolIds.Local(local.Local),
        IInvocationOperation invocation => symbolIds.Get(invocation.TargetMethod),
        IObjectCreationOperation creation when creation.Constructor != null => symbolIds.Get(creation.Constructor),
        IMethodReferenceOperation method => symbolIds.Get(method.Method),
        _ => null,
    };

    private static string ContainingSymbolId(
        SemanticModel model,
        int position,
        string moduleName,
        ScanSymbolIds symbolIds)
    {
        ISymbol? symbol = model.GetEnclosingSymbol(position);
        while (symbol is IMethodSymbol method
               && method.MethodKind is MethodKind.AnonymousFunction or MethodKind.LocalFunction)
            symbol = symbol.ContainingSymbol;
        return symbol == null ? $"mod:{moduleName}" : symbolIds.Get(symbol);
    }

    private static OperationControlContext[] BuildControlContexts(
        IOperation operation,
        ScanSymbolIds symbolIds)
    {
        var contexts = new List<OperationControlContext>();
        var child = operation;
        for (var parent = operation.Parent; parent != null; child = parent, parent = parent.Parent)
        {
            string? kind = null;
            string? branchArm = null;
            IOperation? conditionOperation = null;
            switch (parent)
            {
                case IConditionalOperation conditional:
                    kind = OperationControlContextKind.Branch;
                    branchArm = BranchArm(conditional, child);
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
                    BranchArm = branchArm,
                    Condition = Clip(conditionOperation?.Syntax.ToString()),
                    ConditionValue = conditionOperation == null
                        ? null
                        : DescribeValue(conditionOperation, symbolIds),
                    Operators = conditionOperation == null
                        ? Array.Empty<string>()
                        : CollectOperators(conditionOperation),
                    Predicates = conditionOperation == null
                        ? Array.Empty<OperationControlPredicate>()
                        : CollectPredicates(conditionOperation, symbolIds),
                    Source = SourceSpan(parent.Syntax),
                });
            }
        }
        contexts.Reverse();
        return contexts.ToArray();
    }

    private static string? BranchArm(IConditionalOperation conditional, IOperation directChild)
    {
        if (ReferenceEquals(directChild, conditional.Condition))
            return OperationBranchArm.Condition;
        if (ReferenceEquals(directChild, conditional.WhenTrue))
            return OperationBranchArm.WhenTrue;
        if (ReferenceEquals(directChild, conditional.WhenFalse))
            return OperationBranchArm.WhenFalse;
        return null;
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

    private static OperationControlPredicate[] CollectPredicates(
        IOperation operation,
        ScanSymbolIds symbolIds)
    {
        var result = new List<OperationControlPredicate>();
        CollectPredicates(operation, result, symbolIds);
        return result.ToArray();
    }

    private static void CollectPredicates(
        IOperation operation,
        List<OperationControlPredicate> result,
        ScanSymbolIds symbolIds)
    {
        var operatorName = operation switch
        {
            IBinaryOperation binary => binary.OperatorKind.ToString(),
            IUnaryOperation unary => unary.OperatorKind.ToString(),
            _ => null,
        };
        if (operatorName != null)
        {
            var binary = operation as IBinaryOperation;
            var symbols = new List<string>();
            CollectSourceSymbols(
                operation,
                symbols,
                new HashSet<string>(StringComparer.Ordinal),
                symbolIds);
            result.Add(new OperationControlPredicate
            {
                Operator = operatorName,
                Expression = Clip(operation.Syntax.ToString()),
                SourceSymbolIds = symbols.ToArray(),
                LeftValue = binary == null ? null : DescribeValue(binary.LeftOperand, symbolIds),
                RightValue = binary == null ? null : DescribeValue(binary.RightOperand, symbolIds),
                Source = SourceSpan(operation.Syntax),
            });
        }

        foreach (var child in operation.ChildOperations)
            CollectPredicates(child, result, symbolIds);
    }

    private static IOperation? LoopCondition(ILoopOperation loop) => loop switch
    {
        IForLoopOperation forLoop => forLoop.Condition,
        IWhileLoopOperation whileLoop => whileLoop.Condition,
        IForEachLoopOperation forEach => forEach.Collection,
        _ => null,
    };

    private static bool TryGetPointerElementOperands(
        IOperation operation,
        out IOperation receiver,
        out IOperation index)
    {
        if (operation.Syntax.IsKind(SyntaxKind.ElementAccessExpression))
        {
            var children = operation.ChildOperations.ToArray();
            var pointerChild = children.FirstOrDefault(child => child.Type is IPointerTypeSymbol);
            var indexChild = children.FirstOrDefault(child => child.Type is not IPointerTypeSymbol);
            if (pointerChild != null && indexChild != null)
            {
                receiver = pointerChild;
                index = indexChild;
                return true;
            }
        }

        if (operation is IBinaryOperation binary
            && binary.OperatorKind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract)
        {
            if (binary.LeftOperand.Type is IPointerTypeSymbol
                && binary.RightOperand.Type is not IPointerTypeSymbol)
            {
                receiver = binary.LeftOperand;
                index = binary.RightOperand;
                return true;
            }
            if (binary.OperatorKind == BinaryOperatorKind.Add
                && binary.RightOperand.Type is IPointerTypeSymbol
                && binary.LeftOperand.Type is not IPointerTypeSymbol)
            {
                receiver = binary.RightOperand;
                index = binary.LeftOperand;
                return true;
            }
        }

        foreach (var child in operation.ChildOperations)
        {
            if (TryGetPointerElementOperands(child, out receiver, out index)) return true;
        }

        receiver = null!;
        index = null!;
        return false;
    }

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

    private static ScanSelector[]? NormalizeSelectors(
        IReadOnlyList<OperationFactSelector>? selectors)
    {
        if (selectors is not { Count: > 0 }) return null;

        return selectors.Select((selector, index) =>
        {
            if (selector == null)
                throw new ArgumentException($"Operation fact selector at index {index} cannot be null.");
            return new ScanSelector(
                NormalizeSet(selector.IncludeKinds, value => value, StringComparer.Ordinal),
                NormalizeSet(selector.TargetSymbolIds, value => value, StringComparer.Ordinal),
                NormalizeSet(selector.ContainingSymbolIds, value => value, StringComparer.Ordinal),
                NormalizeSet(selector.Operators, value => value, StringComparer.Ordinal));
        }).ToArray();
    }

    private static bool SelectorCanMatchBeforeContaining(
        ScanSelector[]? selectors,
        string kind,
        string? operatorName,
        string? targetId)
        => selectors == null || selectors.Any(selector =>
            Matches(selector.Kinds, kind)
            && Matches(selector.Operators, operatorName)
            && Matches(selector.Targets, targetId));

    private static bool SelectorMatches(
        ScanSelector[]? selectors,
        string kind,
        string? operatorName,
        string? targetId,
        string? containingSymbolId)
        => selectors == null || selectors.Any(selector =>
            Matches(selector.Kinds, kind)
            && Matches(selector.Operators, operatorName)
            && Matches(selector.Targets, targetId)
            && Matches(selector.ContainingSymbols, containingSymbolId));

    private static bool Matches(HashSet<string>? values, string? candidate)
        => values is not { Count: > 0 }
            || (candidate != null && values.Contains(candidate));

    private static bool MatchesPath(HashSet<string> filter, string filePath)
        => filter.Contains(filePath)
           || filter.Any(candidate => filePath.EndsWith(
               '/' + candidate,
               OperatingSystem.IsWindows()
                   ? StringComparison.OrdinalIgnoreCase
                   : StringComparison.Ordinal));

    private static string NormalizePath(string? path)
        => (path ?? string.Empty).Replace('\\', '/');

    private sealed record ScanSelector(
        HashSet<string>? Kinds,
        HashSet<string>? Targets,
        HashSet<string>? ContainingSymbols,
        HashSet<string>? Operators);

    /// <summary>
    /// Request-scoped canonical-id memoization. The cache dies with the scan,
    /// so it never retains Roslyn symbols or creates another semantic base.
    /// </summary>
    private sealed class ScanSymbolIds
    {
        private readonly Dictionary<ISymbol, string> _canonical = new(SymbolEqualityComparer.Default);

        internal string Get(ISymbol symbol)
        {
            var canonical = symbol.OriginalDefinition;
            if (_canonical.TryGetValue(canonical, out var id)) return id;
            id = CanonicalSymbolFormat.BuildSymbolId(canonical);
            _canonical.Add(canonical, id);
            return id;
        }

        internal string Parameter(IParameterSymbol parameter)
        {
            var owner = parameter.ContainingSymbol == null
                ? "(unknown)"
                : Get(parameter.ContainingSymbol);
            return $"parameter:{owner}#{parameter.Ordinal}:{parameter.Name}";
        }

        internal string Local(ILocalSymbol local)
        {
            var owner = local.ContainingSymbol == null ? "(unknown)" : Get(local.ContainingSymbol);
            var declarationStart = local.DeclaringSyntaxReferences.FirstOrDefault()?.Span.Start ?? -1;
            return $"local:{owner}@{declarationStart}:{local.Name}";
        }
    }
}

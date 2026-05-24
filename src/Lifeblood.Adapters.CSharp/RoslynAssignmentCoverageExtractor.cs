using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Per-construction-site slot-coverage extractor. For each
/// <c>new TargetType { ... }</c> or <c>new TargetType()</c> +
/// statement-level assignment site, walks the containing method's
/// <see cref="IOperation"/> tree and emits per-slot
/// <see cref="AssignmentCoverageSlot"/> facts. Operation-tree only;
/// never regex, never syntax-text. INV-ASSIGNMENT-COVERAGE-001 /
/// INV-ASSIGNMENT-COVERAGE-002 / INV-ASSIGNMENT-COVERAGE-003 /
/// INV-ASSIGNMENT-COVERAGE-004.
/// </summary>
internal static class RoslynAssignmentCoverageExtractor
{
    internal const int DefaultMaxSites = 256;

    internal static AssignmentCoverageReport? Extract(
        IReadOnlyDictionary<string, CSharpCompilation> compilations,
        INamedTypeSymbol targetType,
        string targetTypeId,
        AssignmentCoverageOptions options,
        Func<ISymbol, string> buildSymbolId)
    {
        var maxSites = ClampPositive(options.MaxSites, DefaultMaxSites);

        var slotMembers = EnumerateSlotMembers(targetType, options).ToArray();
        if (slotMembers.Length == 0)
        {
            return new AssignmentCoverageReport
            {
                TargetTypeId = targetTypeId,
                AllSlots = Array.Empty<string>(),
                Sites = Array.Empty<AssignmentCoverageSite>(),
            };
        }

        var slotNames = slotMembers.Select(m => m.Name).ToArray();
        var slotNameSet = new HashSet<string>(slotNames, StringComparer.Ordinal);
        var slotFilter = options.SlotName;
        var targetFqn = targetType.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        var sites = new List<AssignmentCoverageSite>();

        foreach (var (moduleName, compilation) in compilations)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                var root = tree.GetRoot();
                var model = compilation.GetSemanticModel(tree);

                foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
                {
                    var op = model.GetOperation(creation) as IObjectCreationOperation;
                    if (op == null) continue;
                    if (op.Type is not INamedTypeSymbol opType) continue;
                    var opFqn = opType.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    if (!string.Equals(opFqn, targetFqn, StringComparison.Ordinal)) continue;

                    var site = AnalyzeSite(op, model, moduleName, slotMembers, slotNames, slotNameSet, slotFilter, buildSymbolId);
                    if (site == null) continue;
                    sites.Add(site);
                    if (sites.Count >= maxSites) break;
                }
                if (sites.Count >= maxSites) break;
            }
            if (sites.Count >= maxSites) break;
        }

        return new AssignmentCoverageReport
        {
            TargetTypeId = targetTypeId,
            AllSlots = slotFilter != null
                ? slotNames.Where(n => string.Equals(n, slotFilter, StringComparison.Ordinal)).ToArray()
                : slotNames,
            Sites = sites.ToArray(),
        };
    }

    private static IEnumerable<ISymbol> EnumerateSlotMembers(INamedTypeSymbol type, AssignmentCoverageOptions options)
    {
        foreach (var m in type.GetMembers())
        {
            if (m.IsStatic) continue;
            if (m.DeclaredAccessibility != Accessibility.Public) continue;

            switch (m)
            {
                case IFieldSymbol f when !f.IsReadOnly && !f.IsConst:
                    if (IsDelegateLike(f.Type))
                    {
                        if (options.IncludeDelegateFields) yield return f;
                    }
                    else if (options.IncludePublicMutableFields)
                    {
                        yield return f;
                    }
                    break;
                case IPropertySymbol p when p.SetMethod != null && p.SetMethod.DeclaredAccessibility == Accessibility.Public:
                    if (IsDelegateLike(p.Type))
                    {
                        if (options.IncludeDelegateProperties) yield return p;
                    }
                    else if (options.IncludePublicMutableProperties)
                    {
                        yield return p;
                    }
                    break;
            }
        }
    }

    private static bool IsDelegateLike(ITypeSymbol type)
    {
        return type.TypeKind == TypeKind.Delegate;
    }

    private static AssignmentCoverageSite? AnalyzeSite(
        IObjectCreationOperation creation,
        SemanticModel model,
        string moduleName,
        ISymbol[] slotMembers,
        string[] slotNames,
        HashSet<string> slotNameSet,
        string? slotFilter,
        Func<ISymbol, string> buildSymbolId)
    {
        var syntax = creation.Syntax;
        var location = syntax.GetLocation().GetLineSpan();
        var filePath = location.Path;
        var line = location.StartLinePosition.Line + 1;
        var column = location.StartLinePosition.Character + 1;

        var containingMethod = FindContainingMethodSymbol(creation, model);
        if (containingMethod == null) return null;
        var containingMethodId = buildSymbolId(containingMethod);

        // Per-slot state. Order matches slotMembers (== slotNames).
        var assignments = new SlotAssignment[slotMembers.Length];
        for (int i = 0; i < assignments.Length; i++) assignments[i] = SlotAssignment.Absent;

        var limitations = new HashSet<string>(StringComparer.Ordinal);

        // 1) Inline object-initializer writes.
        if (creation.Initializer != null)
        {
            CaptureInlineInitializer(creation.Initializer, slotNameSet, slotNames, assignments, limitations);
        }

        // 2) Statement-level writes on the constructed local before escape.
        var localBinding = TryFindLocalBinding(creation);
        if (localBinding != null)
        {
            ScanLocalAssignments(localBinding, creation, slotNameSet, slotNames, assignments, limitations);
        }

        var slots = BuildSlotArray(slotMembers, slotNames, assignments, slotFilter);
        var confidence = limitations.Count == 0
            ? AssignmentCoverageConfidence.Proven
            : AssignmentCoverageConfidence.Advisory;

        return new AssignmentCoverageSite
        {
            ContainingMethodId = containingMethodId,
            FilePath = filePath,
            Line = line,
            Column = column,
            ModuleName = moduleName,
            Slots = slots,
            Confidence = confidence,
            SiteLimitations = limitations.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
        };
    }

    private static IMethodSymbol? FindContainingMethodSymbol(IOperation op, SemanticModel model)
    {
        var node = op.Syntax;
        for (var cur = node; cur != null; cur = cur.Parent)
        {
            switch (cur)
            {
                case MethodDeclarationSyntax m:
                    return model.GetDeclaredSymbol(m);
                case ConstructorDeclarationSyntax c:
                    return model.GetDeclaredSymbol(c);
                case PropertyDeclarationSyntax pd:
                    return model.GetDeclaredSymbol(pd)?.GetMethod ?? model.GetDeclaredSymbol(pd)?.SetMethod;
                case AccessorDeclarationSyntax ad:
                    return model.GetDeclaredSymbol(ad);
                case LocalFunctionStatementSyntax lf:
                    return model.GetDeclaredSymbol(lf) as IMethodSymbol;
            }
        }
        return null;
    }

    private static void CaptureInlineInitializer(
        IObjectOrCollectionInitializerOperation initializer,
        HashSet<string> slotNameSet,
        string[] slotNames,
        SlotAssignment[] assignments,
        HashSet<string> limitations)
    {
        foreach (var element in initializer.Initializers)
        {
            if (element is not ISimpleAssignmentOperation assign) continue;
            if (assign.Target is not IMemberReferenceOperation memberRef) continue;
            var memberName = memberRef.Member.Name;
            if (!slotNameSet.Contains(memberName)) continue;
            var idx = Array.IndexOf(slotNames, memberName);
            if (idx < 0) continue;

            var loc = assign.Syntax.GetLocation().GetLineSpan();
            assignments[idx] = new SlotAssignment(
                Status: ClassifyStatusForExpression(assign.Value),
                ExpressionKind: ClassifyExpressionKind(assign.Value),
                Line: loc.StartLinePosition.Line + 1,
                Column: loc.StartLinePosition.Character + 1);
        }
    }

    /// <summary>
    /// Locates the local variable bound to a construction expression.
    /// Returns null when the construction is consumed inline (passed as
    /// argument, returned, assigned directly to a member of another type).
    /// </summary>
    private static ILocalSymbol? TryFindLocalBinding(IObjectCreationOperation creation)
    {
        // Walk up the operation tree looking for a variable declarator
        // whose initializer is this creation (possibly through implicit
        // conversion).
        for (var p = creation.Parent; p != null; p = p.Parent)
        {
            switch (p)
            {
                case IVariableInitializerOperation:
                    continue;
                case IConversionOperation:
                    continue;
                case IVariableDeclaratorOperation vd:
                    return vd.Symbol;
                default:
                    return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Forward-walks the containing method body looking for slot
    /// assignments on the bound local. Stops at the first escape signal
    /// (passed as argument, returned, assigned to a member of another
    /// type, or aliased to another local). Per
    /// INV-ASSIGNMENT-COVERAGE-003 post-escape writes are NOT counted.
    /// </summary>
    private static void ScanLocalAssignments(
        ILocalSymbol local,
        IObjectCreationOperation creation,
        HashSet<string> slotNameSet,
        string[] slotNames,
        SlotAssignment[] assignments,
        HashSet<string> limitations)
    {
        var methodBody = FindEnclosingExecutableOperation(creation);
        if (methodBody == null) return;

        var creationSpan = creation.Syntax.SpanStart;
        bool escaped = false;

        foreach (var descendant in methodBody.Descendants())
        {
            if (descendant.Syntax.SpanStart <= creationSpan) continue;
            if (escaped) break;

            switch (descendant)
            {
                case ISimpleAssignmentOperation assign:
                    HandleAssignment(assign, local, slotNameSet, slotNames, assignments, limitations, ref escaped);
                    break;
                case IArgumentOperation arg when ReferencesLocal(arg.Value, local):
                    limitations.Add(AssignmentCoverageSiteLimitation.PostEscapeAssignment);
                    escaped = true;
                    break;
                case IReturnOperation ret when ret.ReturnedValue != null && ReferencesLocal(ret.ReturnedValue, local):
                    limitations.Add(AssignmentCoverageSiteLimitation.PostEscapeAssignment);
                    escaped = true;
                    break;
                case IVariableDeclaratorOperation vd when vd.Initializer != null && ReferencesLocal(vd.Initializer.Value, local):
                    // var b2 = b; — alias created. v1 does not track b2's writes.
                    limitations.Add(AssignmentCoverageSiteLimitation.AliasedLocal);
                    break;
            }
        }
    }

    private static void HandleAssignment(
        ISimpleAssignmentOperation assign,
        ILocalSymbol local,
        HashSet<string> slotNameSet,
        string[] slotNames,
        SlotAssignment[] assignments,
        HashSet<string> limitations,
        ref bool escaped)
    {
        bool isLocalSlotWrite = assign.Target is IMemberReferenceOperation { Instance: ILocalReferenceOperation lrTarget } memberRefTry
            && SymbolEqualityComparer.Default.Equals(lrTarget.Local, local)
            && slotNameSet.Contains(memberRefTry.Member.Name);

        if (!isLocalSlotWrite)
        {
            // Any non-slot-write whose RHS reads our local escapes it
            // (e.g. _bindings = b; foo.Callback = b; etc.).
            if (ReferencesLocal(assign.Value, local))
            {
                limitations.Add(AssignmentCoverageSiteLimitation.PostEscapeAssignment);
                escaped = true;
            }
            return;
        }

        var memberRef = (IMemberReferenceOperation)assign.Target;
        var memberName = memberRef.Member.Name;
        var idx = Array.IndexOf(slotNames, memberName);
        if (idx < 0) return;

        // INV-ASSIGNMENT-COVERAGE: detect branched MAY-assign. If the
        // assignment lives inside an IConditionalOperation / ILoopOperation
        // beneath the method body, it is conditional. v1 conservative path:
        // mark the slot as MAY-assign, surface BranchedMayAssign limitation,
        // do NOT mark Assigned (so absent-count stays honest).
        if (IsInsideConditional(assign))
        {
            limitations.Add(AssignmentCoverageSiteLimitation.BranchedMayAssign);
            return;
        }

        var loc = assign.Syntax.GetLocation().GetLineSpan();
        assignments[idx] = new SlotAssignment(
            Status: ClassifyStatusForExpression(assign.Value),
            ExpressionKind: ClassifyExpressionKind(assign.Value),
            Line: loc.StartLinePosition.Line + 1,
            Column: loc.StartLinePosition.Character + 1);
    }

    private static bool ReferencesLocal(IOperation? op, ILocalSymbol local)
    {
        if (op == null) return false;
        if (op is ILocalReferenceOperation lr && SymbolEqualityComparer.Default.Equals(lr.Local, local)) return true;
        foreach (var child in op.ChildOperations)
        {
            if (ReferencesLocal(child, local)) return true;
        }
        return false;
    }

    private static bool IsInsideConditional(IOperation op)
    {
        for (var p = op.Parent; p != null; p = p.Parent)
        {
            switch (p)
            {
                case IConditionalOperation:
                case ILoopOperation:
                case ISwitchOperation:
                case ISwitchCaseOperation:
                case ITryOperation:
                case ICatchClauseOperation:
                    return true;
                case IMethodBodyOperation:
                case IConstructorBodyOperation:
                case IBlockOperation { Parent: IMethodBodyOperation }:
                case IBlockOperation { Parent: IConstructorBodyOperation }:
                case IAnonymousFunctionOperation:
                case ILocalFunctionOperation:
                    return false;
            }
        }
        return false;
    }

    private static IOperation? FindEnclosingExecutableOperation(IOperation op)
    {
        for (var p = op.Parent; p != null; p = p.Parent)
        {
            switch (p)
            {
                case IMethodBodyOperation mb:
                    return (IOperation?)mb.BlockBody ?? mb.ExpressionBody;
                case IConstructorBodyOperation cb:
                    return (IOperation?)cb.BlockBody ?? cb.ExpressionBody;
                case IAnonymousFunctionOperation af:
                    return af.Body;
                case ILocalFunctionOperation lf:
                    return lf.Body;
            }
        }
        return null;
    }

    private static string ClassifyStatusForExpression(IOperation value)
    {
        // Unwrap implicit conversions (e.g. method group -> delegate).
        var inner = value;
        while (inner is IConversionOperation conv) inner = conv.Operand;

        if (inner is ILiteralOperation lit && lit.ConstantValue.HasValue && lit.ConstantValue.Value == null)
        {
            return AssignmentCoverageStatus.AssignedNull;
        }
        return AssignmentCoverageStatus.Assigned;
    }

    private static string ClassifyExpressionKind(IOperation value)
    {
        var inner = value;
        while (inner is IConversionOperation conv) inner = conv.Operand;

        return inner switch
        {
            IAnonymousFunctionOperation => AssignmentExpressionKind.Lambda,
            IDelegateCreationOperation dc when dc.Target is IMethodReferenceOperation => AssignmentExpressionKind.MethodGroup,
            IDelegateCreationOperation dc when dc.Target is IAnonymousFunctionOperation => AssignmentExpressionKind.Lambda,
            IMethodReferenceOperation => AssignmentExpressionKind.MethodGroup,
            IFieldReferenceOperation => AssignmentExpressionKind.FieldReference,
            IPropertyReferenceOperation => AssignmentExpressionKind.PropertyAccess,
            ILiteralOperation lit when lit.ConstantValue.HasValue && lit.ConstantValue.Value == null => AssignmentExpressionKind.NullLiteral,
            _ => AssignmentExpressionKind.Other,
        };
    }

    private static AssignmentCoverageSlot[] BuildSlotArray(
        ISymbol[] slotMembers,
        string[] slotNames,
        SlotAssignment[] assignments,
        string? slotFilter)
    {
        var result = new List<AssignmentCoverageSlot>(slotMembers.Length);
        for (int i = 0; i < slotMembers.Length; i++)
        {
            if (slotFilter != null && !string.Equals(slotNames[i], slotFilter, StringComparison.Ordinal)) continue;
            var a = assignments[i];
            result.Add(new AssignmentCoverageSlot
            {
                SlotName = slotNames[i],
                Status = a.Status,
                ExpressionKind = a.ExpressionKind,
                Line = a.Line,
                Column = a.Column,
            });
        }
        return result.ToArray();
    }

    private static int ClampPositive(int? requested, int defaultValue)
    {
        if (requested is null) return defaultValue;
        return requested.Value <= 0 ? defaultValue : requested.Value;
    }

    private readonly record struct SlotAssignment(string Status, string? ExpressionKind, int? Line, int? Column)
    {
        public static SlotAssignment Absent => new(AssignmentCoverageStatus.Absent, null, null, null);
    }
}

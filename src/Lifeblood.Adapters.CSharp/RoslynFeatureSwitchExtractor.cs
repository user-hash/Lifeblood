using System;
using System.Collections.Generic;
using System.Linq;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Domain.PathClassification;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Dormant feature-switch extractor. Audits boolean fields / settable boolean
/// properties that gate branches, and decides — operation-exactly — whether any
/// reachable write flips the switch off its default. One pass over every loaded
/// compilation classifies each switch reference (write vs branch-gating read),
/// records assignment sites with their constant value + bucket + containing
/// member, and tallies call sites for every method / property so a flipping
/// write's containing member can be marked reachable. The verdict
/// (<see cref="FeatureSwitchVerdict"/>) falls out of the reachable-flipping-write
/// set. Operation-tree only — never regex. INV-FEATURE-SWITCH-001.
/// </summary>
internal static class RoslynFeatureSwitchExtractor
{
    internal const int DefaultMaxFindings = 200;

    private const string ValueTrue = "true";
    private const string ValueFalse = "false";
    private const string ValueUnknown = "Unknown";

    private sealed class SwitchInfo
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string MemberKind { get; init; }   // Field | Property
        public required string DeclaringTypeId { get; init; }
        public required string FilePath { get; init; }
        public required int Line { get; init; }
        public required string ModuleName { get; init; }
        public required bool IsStatic { get; init; }
        public required string DefaultValue { get; init; }
        public int BranchReads;
        public readonly List<RawAssignment> Assignments = new();
        public readonly Dictionary<string, FeatureSwitchGate> Gates = new(StringComparer.Ordinal);
    }

    private sealed record RawAssignment(
        string ContainingMemberId,
        string ContainingMemberName,
        string FilePath,
        int Line,
        string Bucket,
        string AssignedValue,
        bool FlipsDefault,
        ContainingMemberKind ContainingKind,
        string? PropertyAccessorOwnerId,
        IReadOnlyList<string> CallSiteAliasIds);

    private enum ContainingMemberKind { Method, Constructor, PropertySetAccessor, Initializer }

    internal static FeatureSwitchReport Extract(
        IReadOnlyDictionary<string, CSharpCompilation> compilations,
        FeatureSwitchAuditOptions options,
        Func<ISymbol, string> buildSymbolId)
    {
        var maxFindings = options.MaxFindings is { } m && m > 0 ? m : DefaultMaxFindings;

        // 1. Candidate boolean fields / settable boolean properties from SOURCE types.
        var switches = new Dictionary<string, SwitchInfo>(StringComparer.Ordinal);
        foreach (var (moduleName, compilation) in compilations)
        {
            foreach (var type in RoslynOperationFacts.EnumerateTypes(compilation.GlobalNamespace))
            {
                if (!type.Locations.Any(l => l.IsInSource)) continue;
                foreach (var member in type.GetMembers())
                {
                    if (member.IsImplicitlyDeclared) continue;
                    var info = TryMakeCandidate(member, type, moduleName, options, buildSymbolId);
                    if (info != null) switches.TryAdd(info.Id, info);
                }
            }
        }
        if (switches.Count == 0)
            return Empty(options);

        // 2. One operation-tree pass: classify switch references, and count call
        //    sites for every method (invocations) and property (write refs) so a
        //    flipping write's containing member can be resolved to reachable.
        var methodCallCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var propertyWriteCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, compilation) in compilations)
        {
            // A `using` statement / declaration synthesizes an IDisposable.Dispose
            // call that is NOT an IInvocationOperation; credit it so a Dispose()
            // mutator reached only through `using` is not misread as never-called.
            var disposeId = ResolveDisposeId(compilation, buildSymbolId);
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var node in tree.GetRoot().DescendantNodes())
                {
                    var op = model.GetOperation(node);
                    if (op == null) continue;

                    switch (op)
                    {
                        case IInvocationOperation inv:
                            Bump(methodCallCounts, buildSymbolId(inv.TargetMethod.OriginalDefinition));
                            break;
                        case IPropertyReferenceOperation pw when RoslynOperationFacts.IsWriteContext(pw):
                            Bump(propertyWriteCounts, buildSymbolId(pw.Property.OriginalDefinition));
                            break;
                        case IUsingOperation or IUsingDeclarationOperation when disposeId != null:
                            Bump(methodCallCounts, disposeId);
                            break;
                    }

                    ISymbol? referenced = op switch
                    {
                        IFieldReferenceOperation f => f.Field,
                        IPropertyReferenceOperation p => p.Property,
                        _ => null,
                    };
                    if (referenced == null) continue;
                    var id = buildSymbolId(referenced.OriginalDefinition);
                    if (!switches.TryGetValue(id, out var info)) continue;

                    if (RoslynOperationFacts.IsWriteContext(op))
                        RecordWrite(info, op, node, model, buildSymbolId);
                    else if (FlowsToBranchCondition(op))
                        RecordBranchRead(info, node, model, buildSymbolId);
                }
            }
        }

        // 3. Resolve reachability + verdict; scope filter.
        var results = new List<FeatureSwitch>();
        foreach (var info in switches.Values)
        {
            if (!InScope(info, options)) continue;
            if (options.RequireBranchCondition && info.BranchReads == 0) continue;

            results.Add(Resolve(info, methodCallCounts, propertyWriteCounts));
        }

        results = results
            .OrderBy(s => VerdictRank(s.Verdict))
            .ThenBy(s => s.FilePath, StringComparer.Ordinal)
            .ThenBy(s => s.Line)
            .ToList();

        var verdictBreakdown = results
            .GroupBy(s => s.Verdict, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var total = results.Count;
        var clamped = total > maxFindings ? results.Take(maxFindings).ToArray() : results.ToArray();

        return new FeatureSwitchReport
        {
            Scope = options.TypeId ?? options.ModuleScope ?? "(workspace)",
            SwitchCount = total,
            Truncated = total > clamped.Length,
            VerdictBreakdown = verdictBreakdown,
            Switches = clamped,
        };
    }

    private static SwitchInfo? TryMakeCandidate(
        ISymbol member, INamedTypeSymbol type, string moduleName,
        FeatureSwitchAuditOptions options, Func<ISymbol, string> buildSymbolId)
    {
        string memberKind;
        bool isStatic;
        string defaultValue;

        switch (member)
        {
            case IFieldSymbol f when !f.IsConst && f.Type.SpecialType == SpecialType.System_Boolean:
            {
                memberKind = "Field";
                isStatic = f.IsStatic;
                defaultValue = FieldDefault(f);
                break;
            }
            case IPropertySymbol p when options.IncludeProperties
                && p.Type.SpecialType == SpecialType.System_Boolean
                && p.SetMethod != null && !p.IsReadOnly && !p.IsIndexer:
            {
                memberKind = "Property";
                isStatic = p.IsStatic;
                defaultValue = PropertyDefault(p);
                break;
            }
            default:
                return null;
        }

        var loc = member.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan();
        return new SwitchInfo
        {
            Id = buildSymbolId(member.OriginalDefinition),
            Name = member.Name,
            MemberKind = memberKind,
            DeclaringTypeId = buildSymbolId(type.OriginalDefinition),
            FilePath = loc?.Path ?? "",
            Line = (loc?.StartLinePosition.Line ?? 0) + 1,
            ModuleName = moduleName,
            IsStatic = isStatic,
            DefaultValue = defaultValue,
        };
    }

    private static string FieldDefault(IFieldSymbol f)
    {
        var init = f.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault()?.Initializer?.Value;
        return LiteralBool(init) ?? (init == null ? ValueFalse : ValueUnknown);
    }

    private static string PropertyDefault(IPropertySymbol p)
    {
        var init = p.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault()?.Initializer?.Value;
        return LiteralBool(init) ?? (init == null ? ValueFalse : ValueUnknown);
    }

    private static string? LiteralBool(ExpressionSyntax? expr) => expr?.Kind() switch
    {
        SyntaxKind.TrueLiteralExpression => ValueTrue,
        SyntaxKind.FalseLiteralExpression => ValueFalse,
        _ => null,
    };

    private static void RecordWrite(
        SwitchInfo info, IOperation reference, SyntaxNode node, SemanticModel model,
        Func<ISymbol, string> buildSymbolId)
    {
        var assigned = ClassifyAssignedValue(reference);
        var flips = assigned == ValueUnknown || !string.Equals(assigned, info.DefaultValue, StringComparison.Ordinal);

        var (member, kind) = ResolveContainingMember(model, node);
        var memberId = member != null ? buildSymbolId(member.OriginalDefinition) : "(unknown)";
        var memberName = member?.Name ?? "(unknown)";

        // Call sites that reach this write's containing member dispatch through the
        // member's id PLUS the interface members it implements / base members it
        // overrides — a polymorphic call binds to the interface/base, not the
        // concrete implementer that holds the write. Property accessors alias their
        // owner property; counting then sums in the matching dictionary.
        string? accessorOwnerId = null;
        IReadOnlyList<string> aliasIds = Array.Empty<string>();
        if (member is IMethodSymbol method)
        {
            if (kind == ContainingMemberKind.PropertySetAccessor && method.AssociatedSymbol is IPropertySymbol ownerProp)
            {
                accessorOwnerId = buildSymbolId(ownerProp.OriginalDefinition);
                aliasIds = InterfacePropertyAliasIds(ownerProp, buildSymbolId);
            }
            else if (kind == ContainingMemberKind.Method)
            {
                aliasIds = DispatchAliasIds(method, buildSymbolId);
            }
        }

        var span = node.GetLocation().GetLineSpan();
        var path = span.Path ?? "";
        info.Assignments.Add(new RawAssignment(
            memberId, memberName, path, span.StartLinePosition.Line + 1,
            PathBucketClassifier.Classify(path).ToString(), assigned, flips, kind, accessorOwnerId, aliasIds));
    }

    private static void RecordBranchRead(
        SwitchInfo info, SyntaxNode node, SemanticModel model, Func<ISymbol, string> buildSymbolId)
    {
        info.BranchReads++;
        var (member, _) = ResolveContainingMember(model, node);
        var memberId = member != null ? buildSymbolId(member.OriginalDefinition) : "(unknown)";
        if (info.Gates.ContainsKey(memberId)) return;
        var span = node.GetLocation().GetLineSpan();
        var path = span.Path ?? "";
        info.Gates[memberId] = new FeatureSwitchGate
        {
            MemberId = memberId,
            MemberName = member?.Name ?? "(unknown)",
            FilePath = path,
            Line = span.StartLinePosition.Line + 1,
            Bucket = PathBucketClassifier.Classify(path).ToString(),
        };
    }

    /// <summary>
    /// Classify the value written to the switch: <c>"true"</c> / <c>"false"</c>
    /// for a constant boolean literal on a simple assignment, else
    /// <c>"Unknown"</c> (parameter / field / expression value, compound / coalesce
    /// assignment, ref/out, ++/--). Unknown is treated as "may flip".
    /// </summary>
    private static string ClassifyAssignedValue(IOperation reference)
    {
        if (reference.Parent is ISimpleAssignmentOperation a && ReferenceEquals(a.Target, reference))
        {
            var value = Unwrap(a.Value);
            if (value is ILiteralOperation { ConstantValue: { HasValue: true, Value: bool b } })
                return b ? ValueTrue : ValueFalse;
        }
        return ValueUnknown;
    }

    private static IOperation Unwrap(IOperation op)
    {
        while (true)
        {
            switch (op)
            {
                case IConversionOperation c when c.Operand != null:
                    op = c.Operand; continue;
                case IParenthesizedOperation p when p.Operand != null:
                    op = p.Operand; continue;
                default:
                    return op;
            }
        }
    }

    /// <summary>
    /// Resolve the nearest enclosing real member (method / accessor / constructor
    /// / field-or-property initializer) of a node, skipping lambda and
    /// local-function bodies so a write is attributed to the member it ships in.
    /// </summary>
    private static (ISymbol? Member, ContainingMemberKind Kind) ResolveContainingMember(
        SemanticModel model, SyntaxNode node)
    {
        var sym = model.GetEnclosingSymbol(node.SpanStart);
        while (sym != null)
        {
            switch (sym)
            {
                case IMethodSymbol ms when ms.MethodKind is MethodKind.AnonymousFunction or MethodKind.LocalFunction:
                    sym = sym.ContainingSymbol;
                    continue;
                case IMethodSymbol ms when ms.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor:
                    return (ms, ContainingMemberKind.Constructor);
                case IMethodSymbol ms when ms.MethodKind == MethodKind.PropertySet:
                    return (ms, ContainingMemberKind.PropertySetAccessor);
                case IMethodSymbol ms:
                    return (ms, ContainingMemberKind.Method);
                case IFieldSymbol fs:
                    return (fs, ContainingMemberKind.Initializer);
                case IPropertySymbol ps:
                    return (ps, ContainingMemberKind.Initializer);
                default:
                    sym = sym.ContainingSymbol;
                    continue;
            }
        }
        return (null, ContainingMemberKind.Method);
    }

    /// <summary>
    /// Ids a call dispatches to <paramref name="method"/> through: every interface
    /// member it implements and every base member it overrides. A call site bound
    /// to the interface/base member (the static <c>IInvocationOperation.TargetMethod</c>)
    /// is credited to the concrete implementer here, so a mutator invoked
    /// polymorphically (e.g. <c>capture.Stop()</c> on an interface-typed receiver)
    /// is not misread as never-called.
    /// </summary>
    private static IReadOnlyList<string> DispatchAliasIds(IMethodSymbol method, Func<ISymbol, string> buildSymbolId)
    {
        var aliases = new List<string>();
        for (var ov = method.OverriddenMethod; ov != null; ov = ov.OverriddenMethod)
            aliases.Add(buildSymbolId(ov.OriginalDefinition));

        var type = method.ContainingType;
        if (type != null)
            foreach (var iface in type.AllInterfaces)
                foreach (var im in iface.GetMembers().OfType<IMethodSymbol>())
                {
                    var impl = type.FindImplementationForInterfaceMember(im);
                    if (impl != null && SymbolEqualityComparer.Default.Equals(impl.OriginalDefinition, method.OriginalDefinition))
                        aliases.Add(buildSymbolId(im.OriginalDefinition));
                }
        return aliases;
    }

    /// <summary>Property-side mirror of <see cref="DispatchAliasIds"/>: interface / base properties whose write dispatches to <paramref name="property"/>.</summary>
    private static IReadOnlyList<string> InterfacePropertyAliasIds(IPropertySymbol property, Func<ISymbol, string> buildSymbolId)
    {
        var aliases = new List<string>();
        for (var ov = property.OverriddenProperty; ov != null; ov = ov.OverriddenProperty)
            aliases.Add(buildSymbolId(ov.OriginalDefinition));

        var type = property.ContainingType;
        if (type != null)
            foreach (var iface in type.AllInterfaces)
                foreach (var im in iface.GetMembers().OfType<IPropertySymbol>())
                {
                    var impl = type.FindImplementationForInterfaceMember(im);
                    if (impl != null && SymbolEqualityComparer.Default.Equals(impl.OriginalDefinition, property.OriginalDefinition))
                        aliases.Add(buildSymbolId(im.OriginalDefinition));
                }
        return aliases;
    }

    private static string? ResolveDisposeId(Compilation compilation, Func<ISymbol, string> buildSymbolId)
    {
        var dispose = compilation.GetSpecialType(SpecialType.System_IDisposable)
            .GetMembers("Dispose").OfType<IMethodSymbol>().FirstOrDefault();
        return dispose != null ? buildSymbolId(dispose.OriginalDefinition) : null;
    }

    private static FeatureSwitch Resolve(
        SwitchInfo info,
        IReadOnlyDictionary<string, int> methodCallCounts,
        IReadOnlyDictionary<string, int> propertyWriteCounts)
    {
        var assignments = new List<FeatureSwitchAssignment>();
        var mutators = new Dictionary<string, FeatureSwitchMutator>(StringComparer.Ordinal);

        foreach (var raw in info.Assignments)
        {
            var callerCount = CallSites(raw, methodCallCounts, propertyWriteCounts);
            // A write is active (runnable in-graph) when its containing member is a
            // constructor/initializer (always runs), has an in-graph call site, or
            // lives in Test/Editor/Generated code — those buckets are driven by
            // their own harness (test runner, editor menu, codegen bootstrap) so we
            // do not require an in-graph caller. A PRODUCTION method with zero
            // callers is the dormant signal and stays inactive.
            var active = raw.ContainingKind is ContainingMemberKind.Constructor or ContainingMemberKind.Initializer
                         || callerCount > 0
                         || !string.Equals(raw.Bucket, nameof(PathBucket.Production), StringComparison.Ordinal);

            assignments.Add(new FeatureSwitchAssignment
            {
                ContainingMemberId = raw.ContainingMemberId,
                ContainingMemberName = raw.ContainingMemberName,
                FilePath = raw.FilePath,
                Line = raw.Line,
                Bucket = raw.Bucket,
                AssignedValue = raw.AssignedValue,
                FlipsDefault = raw.FlipsDefault,
                Active = active,
            });

            // Mutators = non-constructor members that flip the default — the
            // activation-authority surface (the SetGrammarMode shape).
            if (raw.FlipsDefault && raw.ContainingKind != ContainingMemberKind.Constructor
                && !mutators.ContainsKey(raw.ContainingMemberId))
            {
                mutators[raw.ContainingMemberId] = new FeatureSwitchMutator
                {
                    MemberId = raw.ContainingMemberId,
                    MemberName = raw.ContainingMemberName,
                    Bucket = raw.Bucket,
                    CallerCount = callerCount,
                    IsExternallyReachable = raw.ContainingKind == ContainingMemberKind.PropertySetAccessor,
                };
            }
        }

        var bucketBreakdown = assignments
            .GroupBy(a => a.Bucket, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var (verdict, reason) = Verdict(info, assignments);

        return new FeatureSwitch
        {
            MemberId = info.Id,
            MemberName = info.Name,
            MemberKind = info.MemberKind,
            DeclaringTypeId = info.DeclaringTypeId,
            FilePath = info.FilePath,
            Line = info.Line,
            IsStatic = info.IsStatic,
            DefaultValue = info.DefaultValue,
            Verdict = verdict,
            Reason = reason,
            BranchConditionReadCount = info.BranchReads,
            Assignments = assignments.ToArray(),
            BranchGatedMembers = info.Gates.Values
                .OrderBy(g => g.FilePath, StringComparer.Ordinal).ThenBy(g => g.Line).ToArray(),
            Mutators = mutators.Values
                .OrderBy(mt => mt.MemberName, StringComparer.Ordinal).ToArray(),
            AssignmentBucketBreakdown = bucketBreakdown,
        };
    }

    private static int CallSites(
        RawAssignment raw,
        IReadOnlyDictionary<string, int> methodCallCounts,
        IReadOnlyDictionary<string, int> propertyWriteCounts)
    {
        // Sum the containing member's own call sites PLUS every dispatch alias
        // (interface members it implements / base members it overrides), in the
        // dictionary that matches the member kind. A polymorphic call counted
        // against the interface/base id then credits the concrete implementer.
        if (raw.ContainingKind == ContainingMemberKind.PropertySetAccessor && raw.PropertyAccessorOwnerId != null)
            return Sum(propertyWriteCounts, raw.PropertyAccessorOwnerId, raw.CallSiteAliasIds);
        return Sum(methodCallCounts, raw.ContainingMemberId, raw.CallSiteAliasIds);
    }

    private static int Sum(IReadOnlyDictionary<string, int> counts, string primaryId, IReadOnlyList<string> aliasIds)
    {
        var total = counts.TryGetValue(primaryId, out var p) ? p : 0;
        foreach (var alias in aliasIds)
            if (counts.TryGetValue(alias, out var c)) total += c;
        return total;
    }

    private static (string Verdict, string Reason) Verdict(SwitchInfo info, List<FeatureSwitchAssignment> assignments)
    {
        var activeFlips = assignments.Where(a => a.FlipsDefault && a.Active).ToList();
        if (activeFlips.Count == 0)
        {
            var flipsButDead = assignments.Any(a => a.FlipsDefault);
            var reason = flipsButDead
                ? $"every write that would flip '{info.Name}' off its default ({info.DefaultValue}) sits in a member with no in-graph call site — no reachable activation authority"
                : $"no write flips '{info.Name}' off its default ({info.DefaultValue}); the gated branch is pinned to the default path in-graph";
            return (FeatureSwitchVerdict.AlwaysDefaultInGraph, reason);
        }

        if (activeFlips.Any(a => string.Equals(a.Bucket, nameof(PathBucket.Production), StringComparison.Ordinal)))
            return (FeatureSwitchVerdict.RuntimeMutable,
                $"'{info.Name}' is flipped off its default ({info.DefaultValue}) by {activeFlips.Count(a => a.Bucket == nameof(PathBucket.Production))} reachable production write(s)");

        var buckets = string.Join("/", activeFlips.Select(a => a.Bucket).Distinct().OrderBy(b => b, StringComparer.Ordinal));
        return (FeatureSwitchVerdict.TestOnlyActivation,
            $"'{info.Name}' is flipped off its default ({info.DefaultValue}) only by reachable {buckets} writes; production sees the default");
    }

    private static int VerdictRank(string verdict) => verdict switch
    {
        FeatureSwitchVerdict.AlwaysDefaultInGraph => 0,
        FeatureSwitchVerdict.TestOnlyActivation => 1,
        FeatureSwitchVerdict.RuntimeMutable => 2,
        _ => 3,
    };

    /// <summary>
    /// True if the read reference flows into a branch condition: walk up through
    /// logical-not / and / or, parentheses, and implicit conversions, and return
    /// true when the chain terminates at the condition slot of an if / ternary,
    /// while, or for. Operation-tree only.
    /// </summary>
    private static bool FlowsToBranchCondition(IOperation reference)
    {
        var current = reference;
        for (var parent = current.Parent; parent != null; current = parent, parent = parent.Parent)
        {
            if (parent is IConditionalOperation cond && ReferenceEquals(cond.Condition, current)) return true;
            if (parent is IWhileLoopOperation wl && ReferenceEquals(wl.Condition, current)) return true;
            if (parent is IForLoopOperation fl && ReferenceEquals(fl.Condition, current)) return true;

            var isWrapper =
                (parent is IUnaryOperation u && u.OperatorKind == UnaryOperatorKind.Not)
                || (parent is IBinaryOperation b && b.OperatorKind is BinaryOperatorKind.ConditionalAnd
                    or BinaryOperatorKind.ConditionalOr or BinaryOperatorKind.And or BinaryOperatorKind.Or)
                || parent is IParenthesizedOperation
                || parent is IConversionOperation;
            if (!isWrapper) return false;
        }
        return false;
    }

    private static void Bump(Dictionary<string, int> counts, string key)
        => counts[key] = counts.TryGetValue(key, out var v) ? v + 1 : 1;

    private static bool InScope(SwitchInfo info, FeatureSwitchAuditOptions options)
    {
        if (options.TypeId != null && !string.Equals(info.DeclaringTypeId, options.TypeId, StringComparison.Ordinal))
            return false;
        if (options.ModuleScope != null && !string.Equals(info.ModuleName, options.ModuleScope, StringComparison.Ordinal))
            return false;
        return true;
    }

    private static FeatureSwitchReport Empty(FeatureSwitchAuditOptions options) => new()
    {
        Scope = options.TypeId ?? options.ModuleScope ?? "(workspace)",
        SwitchCount = 0,
        Truncated = false,
        VerdictBreakdown = new Dictionary<string, int>(StringComparer.Ordinal),
        Switches = Array.Empty<FeatureSwitch>(),
    };
}

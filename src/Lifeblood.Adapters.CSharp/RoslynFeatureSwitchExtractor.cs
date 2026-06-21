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
        string? PropertyAccessorOwnerId);

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

        var (memberId, memberName, kind, accessorOwnerId) = ResolveContainingMember(model, node, buildSymbolId);
        var span = node.GetLocation().GetLineSpan();
        var path = span.Path ?? "";
        info.Assignments.Add(new RawAssignment(
            memberId, memberName, path, span.StartLinePosition.Line + 1,
            PathBucketClassifier.Classify(path).ToString(), assigned, flips, kind, accessorOwnerId));
    }

    private static void RecordBranchRead(
        SwitchInfo info, SyntaxNode node, SemanticModel model, Func<ISymbol, string> buildSymbolId)
    {
        info.BranchReads++;
        var (memberId, memberName, _, _) = ResolveContainingMember(model, node, buildSymbolId);
        if (info.Gates.ContainsKey(memberId)) return;
        var span = node.GetLocation().GetLineSpan();
        var path = span.Path ?? "";
        info.Gates[memberId] = new FeatureSwitchGate
        {
            MemberId = memberId,
            MemberName = memberName,
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
    private static (string Id, string Name, ContainingMemberKind Kind, string? AccessorOwnerId) ResolveContainingMember(
        SemanticModel model, SyntaxNode node, Func<ISymbol, string> buildSymbolId)
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
                    return (buildSymbolId(ms.OriginalDefinition), ms.Name, ContainingMemberKind.Constructor, null);
                case IMethodSymbol ms when ms.MethodKind == MethodKind.PropertySet:
                    return (buildSymbolId(ms.OriginalDefinition), ms.Name, ContainingMemberKind.PropertySetAccessor,
                        ms.AssociatedSymbol is { } owner ? buildSymbolId(owner.OriginalDefinition) : null);
                case IMethodSymbol ms:
                    return (buildSymbolId(ms.OriginalDefinition), ms.Name, ContainingMemberKind.Method, null);
                case IFieldSymbol fs:
                    return (buildSymbolId(fs.OriginalDefinition), fs.Name, ContainingMemberKind.Initializer, null);
                case IPropertySymbol ps:
                    return (buildSymbolId(ps.OriginalDefinition), ps.Name, ContainingMemberKind.Initializer, null);
                default:
                    sym = sym.ContainingSymbol;
                    continue;
            }
        }
        return ("(unknown)", "(unknown)", ContainingMemberKind.Method, null);
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
        if (raw.ContainingKind == ContainingMemberKind.PropertySetAccessor && raw.PropertyAccessorOwnerId != null)
            return propertyWriteCounts.TryGetValue(raw.PropertyAccessorOwnerId, out var pw) ? pw : 0;
        return methodCallCounts.TryGetValue(raw.ContainingMemberId, out var mc) ? mc : 0;
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

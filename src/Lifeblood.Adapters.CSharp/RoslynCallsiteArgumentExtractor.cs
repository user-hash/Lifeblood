using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Lifeblood.Domain.PathClassification;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Operations;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Call-site argument extractor for a target method / constructor. Walks every
/// loaded compilation's <see cref="IInvocationOperation"/> and
/// <see cref="IObjectCreationOperation"/>, matches the bound callee against the
/// target by canonical id, and reports per-argument facts (bound parameter,
/// author-supplied vs default-filled, classified value, raw text) plus a
/// per-parameter supplied/omitted histogram across all sites. Operation-tree
/// only — never regex. Default-value re-sourcing is delegated to
/// <see cref="RoslynArgumentBinding"/> so it stays one implementation with the
/// static-table cell binder. INV-CALLSITE-ARGS-001.
/// </summary>
internal static class RoslynCallsiteArgumentExtractor
{
    internal const int DefaultMaxSites = 256;

    internal static CallsiteArgumentsReport? Extract(
        IReadOnlyDictionary<string, CSharpCompilation> compilations,
        IMethodSymbol target,
        string targetId,
        CallsiteArgumentsOptions options,
        Func<ISymbol, string> buildSymbolId)
    {
        var maxSites = ClampPositive(options.MaxSites, DefaultMaxSites);
        var matchKey = buildSymbolId(target.OriginalDefinition);
        var moduleScope = options.ModuleScope;

        var parameters = target.Parameters;
        var suppliedCounts = new int[parameters.Length];
        var omittedCounts = new int[parameters.Length];
        // Authored default text per parameter, sourced from the target's own
        // (source) declaration. Used as the authoritative rawText for omitted
        // args so a cross-module / metadata call site never shows the call
        // expression instead of the default. INV-CALLSITE-ARGS-001.
        var defaultTexts = new string?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++) defaultTexts[i] = ResolveDefaultText(parameters[i]);

        var sites = new List<CallsiteArgumentSite>();
        int total = 0;

        foreach (var (moduleName, compilation) in compilations)
        {
            if (moduleScope != null && !string.Equals(moduleScope, moduleName, StringComparison.Ordinal)) continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                var root = tree.GetRoot();
                var model = compilation.GetSemanticModel(tree);

                foreach (var node in root.DescendantNodes())
                {
                    IMethodSymbol? callee;
                    ImmutableArray<IArgumentOperation> args;
                    IOperation? instance;

                    switch (model.GetOperation(node))
                    {
                        case IInvocationOperation inv:
                            callee = inv.TargetMethod;
                            args = inv.Arguments;
                            instance = inv.Instance;
                            break;
                        case IObjectCreationOperation ctor when ctor.Constructor != null:
                            callee = ctor.Constructor;
                            args = ctor.Arguments;
                            instance = null;
                            break;
                        default:
                            continue;
                    }

                    if (callee == null) continue;
                    var effective = callee.ReducedFrom ?? callee;
                    if (!string.Equals(buildSymbolId(effective.OriginalDefinition), matchKey, StringComparison.Ordinal))
                        continue;

                    var enclosing = model.GetEnclosingSymbol(node.SpanStart);
                    if (options.ExcludeTests && enclosing != null
                        && PathBucketClassifier.IsTest(enclosing.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan().Path ?? ""))
                        continue;

                    total++;

                    var argFacts = BuildArguments(args, parameters, defaultTexts, compilation, suppliedCounts, omittedCounts);
                    if (sites.Count < maxSites)
                        sites.Add(BuildSite(node, moduleName, enclosing, instance, argFacts, buildSymbolId));
                }
            }
        }

        return new CallsiteArgumentsReport
        {
            TargetId = targetId,
            TargetDisplay = target.ToDisplayString(),
            CallSiteCount = total,
            SitesTruncated = total > sites.Count,
            ParameterSummaries = BuildSummaries(parameters, defaultTexts, suppliedCounts, omittedCounts),
            Sites = sites.ToArray(),
        };
    }

    private static CallsiteArgument[] BuildArguments(
        ImmutableArray<IArgumentOperation> args,
        ImmutableArray<IParameterSymbol> parameters,
        string?[] defaultTexts,
        CSharpCompilation compilation,
        int[] suppliedCounts,
        int[] omittedCounts)
    {
        if (args.IsDefaultOrEmpty) return Array.Empty<CallsiteArgument>();

        var facts = new List<CallsiteArgument>(args.Length);
        foreach (var arg in args)
        {
            var p = arg.Parameter;
            int ordinal = p?.Ordinal ?? facts.Count;
            bool inRange = ordinal >= 0 && ordinal < parameters.Length;
            bool supplied = arg.ArgumentKind != ArgumentKind.DefaultValue;

            if (p != null && inRange)
            {
                if (supplied) suppliedCounts[ordinal]++;
                else omittedCounts[ordinal]++;
            }

            var (classifyOp, _) = RoslynArgumentBinding.Resolve(arg, compilation);
            var unwrapped = Unwrap(classifyOp);

            // Omitted args: rawText is the parameter's authored default (source of
            // truth), never the call-site syntax — the call site never wrote the
            // value. Supplied args: rawText is the author-written value expression.
            string? rawText = supplied
                ? Clip(unwrapped.Syntax?.ToString())
                : (inRange ? defaultTexts[ordinal] : null);

            facts.Add(new CallsiteArgument
            {
                ParameterName = p?.Name ?? $"#{ordinal}",
                ParameterType = p?.Type.ToDisplayString() ?? "?",
                Ordinal = ordinal,
                Supplied = supplied,
                ArgumentKind = MapArgumentKind(arg.ArgumentKind),
                ValueKind = ClassifyKind(unwrapped),
                RawText = rawText,
                IsConstant = unwrapped.ConstantValue.HasValue,
            });
        }
        return facts.ToArray();
    }

    private static CallsiteArgumentSite BuildSite(
        Microsoft.CodeAnalysis.SyntaxNode node,
        string moduleName,
        ISymbol? enclosing,
        IOperation? instance,
        CallsiteArgument[] argFacts,
        Func<ISymbol, string> buildSymbolId)
    {
        var span = node.GetLocation().GetLineSpan();
        return new CallsiteArgumentSite
        {
            ContainingSymbolId = enclosing != null ? buildSymbolId(enclosing) : null,
            FilePath = span.Path ?? "",
            Line = span.StartLinePosition.Line + 1,
            Column = span.StartLinePosition.Character + 1,
            ModuleName = moduleName,
            Receiver = Clip(instance?.Syntax.ToString()),
            Arguments = argFacts,
        };
    }

    private static CallsiteParameterSummary[] BuildSummaries(
        ImmutableArray<IParameterSymbol> parameters, string?[] defaultTexts, int[] supplied, int[] omitted)
    {
        var summaries = new CallsiteParameterSummary[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            summaries[i] = new CallsiteParameterSummary
            {
                Name = p.Name,
                Type = p.Type.ToDisplayString(),
                Ordinal = p.Ordinal,
                IsOptional = p.IsOptional,
                DefaultValueText = defaultTexts[i],
                SuppliedCount = supplied[i],
                OmittedCount = omitted[i],
            };
        }
        return summaries;
    }

    private static string? ResolveDefaultText(IParameterSymbol p)
    {
        if (!p.IsOptional) return null;
        foreach (var reference in p.DeclaringSyntaxReferences)
            if (reference.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.ParameterSyntax ps && ps.Default != null)
                return Clip(ps.Default.Value.ToString());
        return p.HasExplicitDefaultValue ? (p.ExplicitDefaultValue?.ToString() ?? "null") : null;
    }

    /// <summary>Strip implicit conversions / parenthesization so the value kind
    /// reflects the authored expression, not Roslyn's lowering wrapper.</summary>
    private static IOperation Unwrap(IOperation op)
    {
        while (true)
        {
            switch (op)
            {
                case IConversionOperation conv when conv.Operand != null:
                    op = conv.Operand;
                    break;
                case IParenthesizedOperation paren when paren.Operand != null:
                    op = paren.Operand;
                    break;
                default:
                    return op;
            }
        }
    }

    private static string ClassifyKind(IOperation op) => op switch
    {
        ILiteralOperation when op.ConstantValue is { HasValue: true, Value: null } => CallsiteArgumentValueKind.NullLiteral,
        ILiteralOperation => CallsiteArgumentValueKind.Literal,
        IFieldReferenceOperation => CallsiteArgumentValueKind.FieldReference,
        IPropertyReferenceOperation => CallsiteArgumentValueKind.PropertyReference,
        ILocalReferenceOperation => CallsiteArgumentValueKind.LocalReference,
        IParameterReferenceOperation => CallsiteArgumentValueKind.ParameterReference,
        IAnonymousFunctionOperation => CallsiteArgumentValueKind.Lambda,
        IDelegateCreationOperation dc => dc.Target is IAnonymousFunctionOperation
            ? CallsiteArgumentValueKind.Lambda
            : CallsiteArgumentValueKind.MethodGroup,
        IMethodReferenceOperation => CallsiteArgumentValueKind.MethodGroup,
        IObjectCreationOperation => CallsiteArgumentValueKind.ObjectCreation,
        IInvocationOperation => CallsiteArgumentValueKind.Invocation,
        _ when op.ConstantValue is { HasValue: true, Value: null } => CallsiteArgumentValueKind.NullLiteral,
        _ when op.ConstantValue.HasValue => CallsiteArgumentValueKind.Constant,
        _ => CallsiteArgumentValueKind.Other,
    };

    private static string MapArgumentKind(ArgumentKind kind) => kind switch
    {
        ArgumentKind.Explicit => "Explicit",
        ArgumentKind.DefaultValue => "DefaultValue",
        ArgumentKind.ParamArray => "ParamArray",
        _ => kind.ToString(),
    };

    private static string? Clip(string? text)
    {
        if (text == null) return null;
        var collapsed = string.Join(" ", text.Split('\n', '\r', '\t').Where(s => s.Length > 0).Select(s => s.Trim()));
        return collapsed.Length > 160 ? collapsed.Substring(0, 157) + "..." : collapsed;
    }

    private static int ClampPositive(int? value, int fallback)
        => value is { } v && v > 0 ? v : fallback;
}

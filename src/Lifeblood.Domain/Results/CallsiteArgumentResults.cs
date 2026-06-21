namespace Lifeblood.Domain.Results;

/// <summary>
/// Per-call-site argument facts for a target method or constructor. Answers
/// the API-adoption question that "callee is referenced" checks miss: when a
/// richer overload or a new optional parameter exists, do the call sites
/// actually pass it? For each site, reports the bound parameter, whether the
/// argument was author-supplied or filled from the parameter default, the
/// classified value, and the raw source text. The per-parameter
/// <see cref="ParameterSummaries"/> histogram makes "parameter X omitted by
/// 7/7 call sites" a one-call answer. Operation-tree based — never regex.
/// INV-CALLSITE-ARGS-001.
/// </summary>
public sealed class CallsiteArgumentsReport
{
    /// <summary>Canonical id of the target method / constructor whose call sites are reported.</summary>
    public required string TargetId { get; init; }

    /// <summary>Human-readable signature of the resolved target (display string).</summary>
    public required string TargetDisplay { get; init; }

    /// <summary>Total number of discovered call sites (before any <c>maxSites</c> clamp on <see cref="Sites"/>).</summary>
    public required int CallSiteCount { get; init; }

    /// <summary>True when <see cref="Sites"/> was clamped by the caller's <c>maxSites</c>.</summary>
    public required bool SitesTruncated { get; init; }

    /// <summary>
    /// One summary per target parameter, in declaration order, with the
    /// supplied-vs-omitted histogram across ALL discovered call sites (the
    /// histogram is computed before <c>maxSites</c> truncation so the counts
    /// reflect the full population, not just the returned sample).
    /// </summary>
    public required CallsiteParameterSummary[] ParameterSummaries { get; init; }

    /// <summary>One entry per discovered call site (clamped by <c>maxSites</c>).</summary>
    public required CallsiteArgumentSite[] Sites { get; init; }
}

/// <summary>Per-parameter histogram across every discovered call site.</summary>
public sealed class CallsiteParameterSummary
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required int Ordinal { get; init; }

    /// <summary>True iff the parameter declares a default value (is optional).</summary>
    public required bool IsOptional { get; init; }

    /// <summary>Source text of the declared default value, or null for a required parameter.</summary>
    public string? DefaultValueText { get; init; }

    /// <summary>How many call sites passed this parameter explicitly.</summary>
    public required int SuppliedCount { get; init; }

    /// <summary>How many call sites left this (optional) parameter to its default.</summary>
    public required int OmittedCount { get; init; }
}

/// <summary>One discovered call site of the target method / constructor.</summary>
public sealed class CallsiteArgumentSite
{
    /// <summary>Canonical id of the method/property/field initializer containing the call (null when the containing member could not be resolved).</summary>
    public string? ContainingSymbolId { get; init; }

    /// <summary>Source file of the call expression.</summary>
    public required string FilePath { get; init; }

    /// <summary>1-based line of the call expression.</summary>
    public required int Line { get; init; }

    /// <summary>1-based column of the call expression.</summary>
    public required int Column { get; init; }

    /// <summary>Module that owns the call site.</summary>
    public required string ModuleName { get; init; }

    /// <summary>
    /// Receiver expression text (e.g. <c>owner</c> in <c>owner.Do()</c>),
    /// null for static calls, constructors, and implicit-this invocations.
    /// </summary>
    public string? Receiver { get; init; }

    /// <summary>Per-parameter argument facts at this site, in parameter order.</summary>
    public required CallsiteArgument[] Arguments { get; init; }
}

/// <summary>One bound argument at a single call site.</summary>
public sealed class CallsiteArgument
{
    public required string ParameterName { get; init; }
    public required string ParameterType { get; init; }
    public required int Ordinal { get; init; }

    /// <summary>
    /// True when the argument was author-supplied; false when Roslyn filled it
    /// from the parameter default (the parameter was omitted at this site).
    /// </summary>
    public required bool Supplied { get; init; }

    /// <summary>Roslyn <c>ArgumentKind</c> mirror: <c>Explicit</c> / <c>DefaultValue</c> / <c>ParamArray</c>.</summary>
    public required string ArgumentKind { get; init; }

    /// <summary>
    /// Classified value-expression kind. One of the
    /// <see cref="CallsiteArgumentValueKind"/> constants; open extension set,
    /// callers MUST tolerate unknown kinds.
    /// </summary>
    public required string ValueKind { get; init; }

    /// <summary>Raw source text of the bound value expression (the default expression when omitted).</summary>
    public string? RawText { get; init; }

    /// <summary>True when the value is a compile-time constant.</summary>
    public required bool IsConstant { get; init; }
}

/// <summary>
/// Canonical value-kind string set for <see cref="CallsiteArgument.ValueKind"/>.
/// Open for non-breaking extension.
/// </summary>
public static class CallsiteArgumentValueKind
{
    public const string Literal = "Literal";
    public const string NullLiteral = "NullLiteral";
    public const string Constant = "Constant";
    public const string FieldReference = "FieldReference";
    public const string PropertyReference = "PropertyReference";
    public const string LocalReference = "LocalReference";
    public const string ParameterReference = "ParameterReference";
    public const string MethodGroup = "MethodGroup";
    public const string Lambda = "Lambda";
    public const string ObjectCreation = "ObjectCreation";
    public const string Invocation = "Invocation";
    public const string Other = "Other";
}

/// <summary>Per-call options for <c>lifeblood_callsite_arguments</c>.</summary>
public sealed class CallsiteArgumentsOptions
{
    /// <summary>When set, only call sites in this module are reported.</summary>
    public string? ModuleScope { get; init; }

    /// <summary>Maximum sites returned. Adapter-side default applies when unset.</summary>
    public int? MaxSites { get; init; }

    /// <summary>When true, drop sites whose containing symbol is in a Test path bucket. Default false.</summary>
    public bool ExcludeTests { get; init; }
}

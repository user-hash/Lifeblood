namespace Lifeblood.Domain.Results;

/// <summary>
/// Per-construction-site assignment coverage for a target type. For each
/// <c>new TargetType { ... }</c> or <c>new TargetType()</c> + statement-level
/// assignment site, reports which of the target's public mutable slot
/// members are assigned at that site and which are absent. Sister tool to
/// <see cref="StaticTableReport"/>: where <c>static_tables</c> answers
/// "what's the static dispatch table?", this answers "did this consumer fill
/// every required slot before the local escaped scope?". Operation-tree
/// based — never regex, never syntax-text. INV-ASSIGNMENT-COVERAGE-001.
/// </summary>
public sealed class AssignmentCoverageReport
{
    /// <summary>Canonical id of the type whose construction sites are reported.</summary>
    public required string TargetTypeId { get; init; }

    /// <summary>
    /// Slot members considered for coverage, in canonical declaration order.
    /// Filtered by <see cref="AssignmentCoverageOptions"/>; for the default
    /// "delegate fields only" filter this is every public mutable Func/Action
    /// /delegate-typed field on the target type.
    /// </summary>
    public required string[] AllSlots { get; init; }

    /// <summary>One entry per discovered construction site.</summary>
    public required AssignmentCoverageSite[] Sites { get; init; }
}

/// <summary>
/// One construction site of the target type. Carries per-slot assignment
/// status plus a confidence tier reflecting the construction shape's
/// analysis rigor: direct same-method inline initializer or statement-level
/// assignment before escape is <c>Proven</c>; factory-constructed, aliased,
/// or branched MAY-assign sites are <c>Advisory</c> with the bumping shape
/// named in <see cref="SiteLimitations"/>.
/// </summary>
public sealed class AssignmentCoverageSite
{
    /// <summary>Canonical id of the method containing the construction expression.</summary>
    public required string ContainingMethodId { get; init; }

    /// <summary>Source file of the construction expression.</summary>
    public required string FilePath { get; init; }

    /// <summary>1-based line number of the construction expression.</summary>
    public required int Line { get; init; }

    /// <summary>1-based column number of the construction expression.</summary>
    public required int Column { get; init; }

    /// <summary>Name of the compilation module that owns the containing method.</summary>
    public required string ModuleName { get; init; }

    /// <summary>Per-slot assignment status. Length matches the parent report's <see cref="AssignmentCoverageReport.AllSlots"/> length and order.</summary>
    public required AssignmentCoverageSlot[] Slots { get; init; }

    /// <summary>
    /// Confidence tier for this site. <c>Proven</c> when the construction is
    /// an inline object-initializer OR a statement-level assignment chain on
    /// a non-aliased local in a single method's control flow, where the
    /// escape boundary (call argument, return, member assignment to a
    /// different type) is detectable. <c>Advisory</c> when the site involves
    /// a factory call, aliased local, or a branched MAY-assign whose
    /// conservative classification is reported as Absent.
    /// </summary>
    public required string Confidence { get; init; }

    /// <summary>
    /// Construction shapes that bumped this site to Advisory tier. Empty
    /// when <see cref="Confidence"/> is Proven. Open extension set — callers
    /// MUST tolerate unknown shape names.
    /// </summary>
    public required string[] SiteLimitations { get; init; }
}

/// <summary>
/// One slot's status at a single construction site. Order matches the
/// parent report's <c>AllSlots</c> array so a caller can pair them
/// without re-lookup.
/// </summary>
public sealed class AssignmentCoverageSlot
{
    /// <summary>Short slot member name (e.g. <c>GetCurrentTime</c>).</summary>
    public required string SlotName { get; init; }

    /// <summary>
    /// Coverage status string. One of
    /// <see cref="AssignmentCoverageStatus.Assigned"/>,
    /// <see cref="AssignmentCoverageStatus.Absent"/>,
    /// <see cref="AssignmentCoverageStatus.AssignedNull"/>.
    /// Null-literal assignment is distinct from Absent so a caller can tell
    /// "forgot to wire" from "deliberately wired null".
    /// INV-ASSIGNMENT-COVERAGE-004.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Classification of the assignment expression. Null when
    /// <see cref="Status"/> is Absent. Open extension set per
    /// <see cref="AssignmentExpressionKind"/>.
    /// </summary>
    public string? ExpressionKind { get; init; }

    /// <summary>1-based line number of the assignment. Null when Absent.</summary>
    public int? Line { get; init; }

    /// <summary>1-based column number of the assignment. Null when Absent.</summary>
    public int? Column { get; init; }
}

/// <summary>
/// Canonical status string set. Open for non-breaking extension.
/// </summary>
public static class AssignmentCoverageStatus
{
    public const string Assigned = "Assigned";
    public const string Absent = "Absent";
    public const string AssignedNull = "AssignedNull";
}

/// <summary>
/// Canonical assignment-expression-kind string set. Open for non-breaking
/// extension; callers MUST tolerate unknown kinds.
/// </summary>
public static class AssignmentExpressionKind
{
    public const string Lambda = "Lambda";
    public const string MethodGroup = "MethodGroup";
    public const string FieldReference = "FieldReference";
    public const string PropertyAccess = "PropertyAccess";
    public const string NullLiteral = "NullLiteral";
    public const string Other = "Other";
}

/// <summary>
/// Canonical per-site confidence-tier string set. Tied to construction
/// shape, NOT response envelope (the response-envelope tier is decorated
/// at the connector boundary by <c>LifebloodResponseDecorator</c>).
/// </summary>
public static class AssignmentCoverageConfidence
{
    /// <summary>Inline object-initializer OR single-method statement-level chain on a non-aliased local, escape boundary detectable.</summary>
    public const string Proven = "Proven";

    /// <summary>Factory-constructed, aliased local, branched MAY-assign, or other shape whose coverage is best-effort. See <c>SiteLimitations</c> for the specific shape.</summary>
    public const string Advisory = "Advisory";
}

/// <summary>
/// Canonical site-limitation string set. Names the construction shape that
/// bumped a site from Proven to Advisory. Open for non-breaking extension.
/// </summary>
public static class AssignmentCoverageSiteLimitation
{
    /// <summary>Construction call was a factory method, not <c>new T(...)</c>; inline-initializer slots invisible.</summary>
    public const string FactoryConstruction = "FactoryConstruction";

    /// <summary>The constructed local was assigned to another local before any slot writes; alias-tracking is not in scope.</summary>
    public const string AliasedLocal = "AliasedLocal";

    /// <summary>A slot write appeared inside a conditional branch — coverage reports MAY-assign as Absent conservatively.</summary>
    public const string BranchedMayAssign = "BranchedMayAssign";

    /// <summary>The local escaped scope (passed as argument, returned, assigned to another type's member) before all slot writes finished. Post-escape writes are not counted.</summary>
    public const string PostEscapeAssignment = "PostEscapeAssignment";
}

/// <summary>
/// Per-call extraction options for <c>lifeblood_assignment_coverage</c>.
/// Default filter is "delegate slots only" — the Bindings shape that
/// motivated the tool. Toggle the public-mutable flags when coverage on
/// non-delegate mutable surface is needed.
/// </summary>
public sealed class AssignmentCoverageOptions
{
    /// <summary>Include public mutable Func / Action / custom-delegate-typed fields as slots. Default true.</summary>
    public bool IncludeDelegateFields { get; init; } = true;

    /// <summary>Include public mutable Func / Action / custom-delegate-typed properties as slots. Default true.</summary>
    public bool IncludeDelegateProperties { get; init; } = true;

    /// <summary>Include public mutable non-delegate fields as slots. Default false (Bindings default).</summary>
    public bool IncludePublicMutableFields { get; init; }

    /// <summary>Include public mutable non-delegate properties (settable from outside) as slots. Default false (Bindings default).</summary>
    public bool IncludePublicMutableProperties { get; init; }

    /// <summary>Optional slot-name filter. When set, only the matching slot is reported in the per-site Slots array.</summary>
    public string? SlotName { get; init; }

    /// <summary>Maximum sites returned. Adapter-side default applies when unset.</summary>
    public int? MaxSites { get; init; }
}

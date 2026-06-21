namespace Lifeblood.Domain.Results;

/// <summary>
/// Dead-WIRE audit: members that compile green and are referenced, but are
/// structurally unplugged at runtime. Complements <c>dead_code</c> (which finds
/// UNreferenced symbols) by catching the opposite failure — a field that is READ
/// but never written, or a delegate/binding slot that is never assigned anywhere.
/// This is the recurring extraction-severed-wiring bug class (e.g. a private
/// field read by guards with zero assignment sites after a refactor). Every
/// finding is operation-tree evidence; advisory because reflection / Unity
/// serialized (YAML) / runtime-procedural assignment is invisible to static
/// analysis. INV-WIRE-AUDIT-001.
/// </summary>
public sealed class WireAuditReport
{
    /// <summary>Output scope: a canonical type id, a module name, or <c>"(workspace)"</c>.</summary>
    public required string Scope { get; init; }

    /// <summary>Total qualifying findings before any <c>maxFindings</c> clamp.</summary>
    public required int FindingCount { get; init; }

    /// <summary>True when <see cref="Findings"/> was clamped by <c>maxFindings</c>.</summary>
    public required bool Truncated { get; init; }

    /// <summary>Per-kind histogram (<see cref="WireAuditFindingKind"/> → count), full population.</summary>
    public required IReadOnlyDictionary<string, int> KindBreakdown { get; init; }

    /// <summary>The findings (clamped by <c>maxFindings</c>).</summary>
    public required WireAuditFinding[] Findings { get; init; }
}

/// <summary>One dead-wire finding.</summary>
public sealed class WireAuditFinding
{
    /// <summary>One of the <see cref="WireAuditFindingKind"/> constants.</summary>
    public required string Kind { get; init; }

    /// <summary>Canonical id of the unplugged member.</summary>
    public required string MemberId { get; init; }

    /// <summary>Short member name.</summary>
    public required string MemberName { get; init; }

    /// <summary>Member symbol kind: <c>Field</c> or <c>Property</c>.</summary>
    public required string MemberKind { get; init; }

    /// <summary>Display type of the member (e.g. <c>System.Action&lt;string&gt;</c>).</summary>
    public required string MemberType { get; init; }

    /// <summary>Canonical id of the declaring type.</summary>
    public required string DeclaringTypeId { get; init; }

    /// <summary>Declaration file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>1-based declaration line.</summary>
    public required int Line { get; init; }

    /// <summary>Count of read references observed across the analyzed compilations.</summary>
    public required int ReadCount { get; init; }

    /// <summary>Count of write references (assignments, ++/--, ref/out, initializer) observed.</summary>
    public required int WriteCount { get; init; }

    /// <summary>Human-readable reason this member was flagged.</summary>
    public required string Reason { get; init; }
}

/// <summary>Canonical wire-audit finding-kind strings. Open for extension.</summary>
public static class WireAuditFindingKind
{
    /// <summary>Private/internal field read at ≥1 site with zero write sites (incl. no initializer). Read-but-never-written.</summary>
    public const string FieldReadWithoutWrite = "FieldReadWithoutWrite";

    /// <summary>Delegate-typed (Func/Action/custom-delegate) mutable field or property with zero assignment sites. Never-wired slot.</summary>
    public const string DelegateSlotNeverAssigned = "DelegateSlotNeverAssigned";

    /// <summary>Event with ≥1 subscriber (<c>+=</c>) but zero raise sites — handlers attached, nothing ever fires it. Dead event.</summary>
    public const string EventSubscribedNeverRaised = "EventSubscribedNeverRaised";

    /// <summary>Event raised at ≥1 site but with zero subscribers (<c>+=</c>) anywhere — fired into the void. No-op signal.</summary>
    public const string EventRaisedNeverSubscribed = "EventRaisedNeverSubscribed";

    /// <summary>Private/internal method whose every call site passes only compile-time-degenerate arguments (constants / default / null) — a vestigial parameter or placeholder wire.</summary>
    public const string DegenerateConstantCallSites = "DegenerateConstantCallSites";
}

/// <summary>Per-call options for <c>lifeblood_wire_audit</c>.</summary>
public sealed class WireAuditOptions
{
    /// <summary>Optional. Restrict FINDINGS to members declared on this type (read/write counting still scans all compilations).</summary>
    public string? TypeId { get; init; }

    /// <summary>Optional. Restrict findings to members declared in this module.</summary>
    public string? ModuleScope { get; init; }

    /// <summary>Run the field-read-without-write pass. Default true.</summary>
    public bool IncludeFieldReadWithoutWrite { get; init; } = true;

    /// <summary>Run the delegate-slot-never-assigned pass. Default true.</summary>
    public bool IncludeDelegateSlots { get; init; } = true;

    /// <summary>Run the event subscribed-never-raised / raised-never-subscribed pass. Default true.</summary>
    public bool IncludeEvents { get; init; } = true;

    /// <summary>Run the degenerate-constant-call-sites pass (private/internal methods only ever called with constant/default args). Default true.</summary>
    public bool IncludeDegenerateConstantCallSites { get; init; } = true;

    /// <summary>Maximum findings returned. Adapter-side default applies when unset.</summary>
    public int? MaxFindings { get; init; }

    /// <summary>When true, force the compact <c>SummarizeMaxFindings</c> cap regardless of <see cref="MaxFindings"/> — smallest viable triage shape. Same field shape, fewer findings. INV-LIST-SHAPE-UNIFORM-001.</summary>
    public bool? Summarize { get; init; }
}

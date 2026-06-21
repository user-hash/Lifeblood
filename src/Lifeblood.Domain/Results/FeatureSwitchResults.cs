namespace Lifeblood.Domain.Results;

/// <summary>
/// Dormant feature-switch audit: boolean fields / properties that GATE branches
/// but are pinned to their default because nothing in the analyzed graph flips
/// them. Complements <c>wire_audit</c> (zero wiring) and <c>dead_code</c>
/// (unreferenced symbols) by catching a third failure — a feature that compiles,
/// is read by live branch conditions, and looks shipped, yet has no reachable
/// activation authority (its public mutator has zero callers, or every flipping
/// write lives in test code). One operation-tree pass classifies each switch
/// reference read-vs-write, locates the branch conditions it gates, and resolves
/// each flipping write's reachability to a verdict. Advisory: reflection / Unity
/// serialized (YAML) / runtime-procedural / config-driven assignment is invisible
/// to static analysis, so an <c>AlwaysDefaultInGraph</c> verdict is a candidate to
/// verify, not proof a feature is dead. INV-FEATURE-SWITCH-001.
/// </summary>
public sealed class FeatureSwitchReport
{
    /// <summary>Output scope: a canonical type id, a module name, or <c>"(workspace)"</c>.</summary>
    public required string Scope { get; init; }

    /// <summary>Total qualifying switches before any <c>maxFindings</c> clamp.</summary>
    public required int SwitchCount { get; init; }

    /// <summary>True when <see cref="Switches"/> was clamped by <c>maxFindings</c>.</summary>
    public required bool Truncated { get; init; }

    /// <summary>Per-verdict histogram (<see cref="FeatureSwitchVerdict"/> → count), full population.</summary>
    public required IReadOnlyDictionary<string, int> VerdictBreakdown { get; init; }

    /// <summary>The audited switches (clamped by <c>maxFindings</c>).</summary>
    public required FeatureSwitch[] Switches { get; init; }
}

/// <summary>One audited boolean feature switch.</summary>
public sealed class FeatureSwitch
{
    /// <summary>Canonical id of the switch member.</summary>
    public required string MemberId { get; init; }

    /// <summary>Short member name.</summary>
    public required string MemberName { get; init; }

    /// <summary>Member symbol kind: <c>Field</c> or <c>Property</c>.</summary>
    public required string MemberKind { get; init; }

    /// <summary>Canonical id of the declaring type.</summary>
    public required string DeclaringTypeId { get; init; }

    /// <summary>Declaration file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>1-based declaration line.</summary>
    public required int Line { get; init; }

    /// <summary>True when the switch is a <c>static</c> field / property.</summary>
    public required bool IsStatic { get; init; }

    /// <summary>Declared default: <c>"true"</c> / <c>"false"</c> (literal initializer or the boolean type default) or <c>"Unknown"</c> (non-constant initializer).</summary>
    public required string DefaultValue { get; init; }

    /// <summary>One of the <see cref="FeatureSwitchVerdict"/> constants.</summary>
    public required string Verdict { get; init; }

    /// <summary>Human-readable justification for the verdict.</summary>
    public required string Reason { get; init; }

    /// <summary>Count of reads where the switch flows into a branch condition (if / while / for / ternary, through logical-not/and/or wrappers).</summary>
    public required int BranchConditionReadCount { get; init; }

    /// <summary>Every assignment site observed (writes to the switch outside its own initializer).</summary>
    public required FeatureSwitchAssignment[] Assignments { get; init; }

    /// <summary>Distinct members whose branch conditions read the switch.</summary>
    public required FeatureSwitchGate[] BranchGatedMembers { get; init; }

    /// <summary>Distinct non-constructor members that flip the switch off its default, with their caller counts — the activation-authority surface.</summary>
    public required FeatureSwitchMutator[] Mutators { get; init; }

    /// <summary>Per-bucket histogram of assignment sites (<c>Production</c> / <c>Test</c> / <c>Editor</c> / <c>Generated</c> → count).</summary>
    public required IReadOnlyDictionary<string, int> AssignmentBucketBreakdown { get; init; }
}

/// <summary>One write to a feature switch.</summary>
public sealed class FeatureSwitchAssignment
{
    /// <summary>Canonical id of the member containing the write.</summary>
    public required string ContainingMemberId { get; init; }

    /// <summary>Short name of the containing member.</summary>
    public required string ContainingMemberName { get; init; }

    /// <summary>File path of the write.</summary>
    public required string FilePath { get; init; }

    /// <summary>1-based line of the write.</summary>
    public required int Line { get; init; }

    /// <summary>Path bucket of the write site: <c>Production</c> / <c>Test</c> / <c>Editor</c> / <c>Generated</c>.</summary>
    public required string Bucket { get; init; }

    /// <summary>Constant assigned (<c>"true"</c> / <c>"false"</c>) or <c>"Unknown"</c> when the value is non-constant (parameter / field / expression / compound assignment).</summary>
    public required string AssignedValue { get; init; }

    /// <summary>True when this write can set the switch to a value other than its default (a different constant, or any non-constant value).</summary>
    public required bool FlipsDefault { get; init; }

    /// <summary>True when the write is runnable in-graph: a constructor / initializer, a method/accessor with ≥1 call site, or any member in Test / Editor / Generated code (those buckets run under their own harness). A Production member with zero in-graph callers stays inactive — the dormant-feature signal. A flipping write that is not active cannot change the switch within the analyzed graph.</summary>
    public required bool Active { get; init; }
}

/// <summary>A member whose branch condition reads a feature switch.</summary>
public sealed class FeatureSwitchGate
{
    /// <summary>Canonical id of the gated member.</summary>
    public required string MemberId { get; init; }

    /// <summary>Short name of the gated member.</summary>
    public required string MemberName { get; init; }

    /// <summary>File path of the gating branch.</summary>
    public required string FilePath { get; init; }

    /// <summary>1-based line of the gating branch.</summary>
    public required int Line { get; init; }

    /// <summary>Path bucket of the gating member.</summary>
    public required string Bucket { get; init; }
}

/// <summary>A member that flips a feature switch off its default — the switch's activation authority.</summary>
public sealed class FeatureSwitchMutator
{
    /// <summary>Canonical id of the mutator member.</summary>
    public required string MemberId { get; init; }

    /// <summary>Short name of the mutator member.</summary>
    public required string MemberName { get; init; }

    /// <summary>Path bucket of the mutator.</summary>
    public required string Bucket { get; init; }

    /// <summary>Direct call sites of the mutator across the analyzed compilations (invocations for methods, property-write references for set-accessors). Zero ⇒ no in-graph activation path.</summary>
    public required int CallerCount { get; init; }

    /// <summary>True when the mutator is <c>public</c> / <c>internal</c> (externally reachable accessibility) even if its in-graph <see cref="CallerCount"/> is zero.</summary>
    public required bool IsExternallyReachable { get; init; }
}

/// <summary>Canonical feature-switch verdict strings. Open for extension.</summary>
public static class FeatureSwitchVerdict
{
    /// <summary>No reachable write flips the switch off its default: every flipping write sits in an uncalled member, or there are no flipping writes at all. The branch behind it is effectively pinned to the default path within the analyzed graph.</summary>
    public const string AlwaysDefaultInGraph = "AlwaysDefaultInGraph";

    /// <summary>The switch is flipped off its default only by reachable writes in Test / Editor / Generated code. Production sees the default.</summary>
    public const string TestOnlyActivation = "TestOnlyActivation";

    /// <summary>At least one reachable Production write flips the switch off its default — it is genuinely toggled at runtime.</summary>
    public const string RuntimeMutable = "RuntimeMutable";
}

/// <summary>Per-call options for <c>lifeblood_feature_switch_audit</c>.</summary>
public sealed class FeatureSwitchAuditOptions
{
    /// <summary>Optional. Restrict findings to switches declared on this type (read/write counting still scans all compilations).</summary>
    public string? TypeId { get; init; }

    /// <summary>Optional. Restrict findings to switches declared in this module.</summary>
    public string? ModuleScope { get; init; }

    /// <summary>Only audit boolean members read in ≥1 branch condition (the feature-switch shape). Default true; false widens to every boolean field / settable property.</summary>
    public bool RequireBranchCondition { get; init; } = true;

    /// <summary>Include boolean properties (with a setter) as candidates, not just fields. Default true.</summary>
    public bool IncludeProperties { get; init; } = true;

    /// <summary>Maximum switches returned. Adapter-side default applies when unset.</summary>
    public int? MaxFindings { get; init; }
}

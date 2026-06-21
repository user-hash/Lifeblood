namespace Lifeblood.Domain.Results;

/// <summary>
/// Declared-member count for a single type, in one of two semantics
/// (<see cref="MemberCountSemantics"/>). The motivating case is an offline
/// architecture-debt ratchet that pins <c>System.Reflection</c> DeclaredOnly
/// member count: when the live test runner is unavailable, the reflection lane is
/// blocked (workspace-load boundary) and the raw graph child count diverges
/// (nested types + accessor/backing-field accounting). <c>reflectionDeclared</c>
/// reproduces the reflection number bit-exactly; <c>sourceSymbols</c> matches the
/// Lifeblood graph's child-symbol count. INV-MEMBER-COUNT-001.
/// </summary>
public sealed class MemberCountReport
{
    /// <summary>Canonical id of the counted type.</summary>
    public required string TypeId { get; init; }

    /// <summary>One of the <see cref="MemberCountSemantics"/> constants.</summary>
    public required string Semantics { get; init; }

    /// <summary>The headline declared-member count under the chosen semantics.</summary>
    public required int Count { get; init; }

    /// <summary>Per-category breakdown of the count.</summary>
    public required MemberCountBreakdown Breakdown { get; init; }
}

/// <summary>Per-category member tallies behind a <see cref="MemberCountReport.Count"/>.</summary>
public sealed class MemberCountBreakdown
{
    /// <summary>Methods including property/event accessors and operators (reflection <c>GetMethods</c>), excluding constructors.</summary>
    public required int Methods { get; init; }

    /// <summary>Instance + static constructors (reflection <c>GetConstructors</c>). The compiler-emitted implicit default ctor counts (it is not <c>[CompilerGenerated]</c>).</summary>
    public required int Constructors { get; init; }

    /// <summary>Fields, excluding compiler-generated backing fields (auto-property / field-like event).</summary>
    public required int Fields { get; init; }

    /// <summary>Properties.</summary>
    public required int Properties { get; init; }

    /// <summary>Events.</summary>
    public required int Events { get; init; }

    /// <summary>Nested types. EXCLUDED from <c>reflectionDeclared</c> <see cref="MemberCountReport.Count"/> (reflection's five GetX families do not include nested types); INCLUDED in <c>sourceSymbols</c> Count.</summary>
    public required int NestedTypes { get; init; }

    /// <summary>Members dropped for the chosen semantics: for <c>reflectionDeclared</c>, <c>[CompilerGenerated]</c> / backing-field members; for <c>sourceSymbols</c>, implicitly-declared members + property/event accessors (which are not independent source declarations). The source of the reflection-vs-graph divergence.</summary>
    public required int Excluded { get; init; }
}

/// <summary>Canonical member-count semantics strings.</summary>
public static class MemberCountSemantics
{
    /// <summary>Bit-exact <c>System.Reflection</c> DeclaredOnly counting: methods (incl. accessors / operators) + constructors + fields + properties + events, with <c>[CompilerGenerated]</c> / backing-field members filtered and nested types excluded.</summary>
    public const string ReflectionDeclared = "reflectionDeclared";

    /// <summary>Lifeblood graph child-symbol count: every source-declared member symbol whose parent is the type, including nested types — the semantics raw <c>Graph.Symbols.Where(ParentId == type)</c> produces.</summary>
    public const string SourceSymbols = "sourceSymbols";
}

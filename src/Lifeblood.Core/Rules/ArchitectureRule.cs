namespace Lifeblood.Core.Rules;

/// <summary>
/// An architecture rule that constrains which symbols may reference which.
/// INV-RULES-001: Defined in lifeblood.rules.json.
/// </summary>
public sealed class ArchitectureRule
{
    /// <summary>Glob pattern for source symbols (e.g., "MyApp.Domain", "*.Tests").</summary>
    public string Source { get; init; } = "";

    /// <summary>If set, source must not reference targets matching this pattern.</summary>
    public string? MustNotReference { get; init; }

    /// <summary>If set, source may only reference targets matching this pattern.</summary>
    public string? MayOnlyReference { get; init; }
}

/// <summary>
/// A violation of an architecture rule.
/// INV-RULES-002: Machine-readable with exact source, target, and rule.
/// </summary>
public sealed class Violation
{
    public string SourceSymbolId { get; init; } = "";
    public string TargetSymbolId { get; init; } = "";
    public string SourceNamespace { get; init; } = "";
    public string TargetNamespace { get; init; } = "";
    public string RuleBroken { get; init; } = "";
}

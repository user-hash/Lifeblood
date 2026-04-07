namespace Lifeblood.Domain.Rules;

/// <summary>
/// An architecture rule. Data only. Validation logic lives in Lifeblood.Analysis.
/// </summary>
public sealed class ArchitectureRule
{
    public string Id { get; init; } = "";
    public string Description { get; init; } = "";
    public string Source { get; init; } = "";
    public string? MustNotReference { get; init; }
    public string? MayOnlyReference { get; init; }
}

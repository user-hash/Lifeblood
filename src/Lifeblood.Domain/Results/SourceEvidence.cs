namespace Lifeblood.Domain.Results;

/// <summary>
/// Bounded lexical-evidence request. Search terms are caller-authored policy;
/// adapters report exact occurrences without deciding whether prose is stale
/// or whether an invariant is enforced.
/// </summary>
public sealed class SourceEvidenceQuery
{
    public string? ModuleScope { get; init; }
    public string? ProfileScope { get; init; }
    public IReadOnlyList<string>? FilePaths { get; init; }
    public required IReadOnlyList<string> SearchTerms { get; init; }
    public IReadOnlyList<string>? IncludeKinds { get; init; }
    public int MaxFacts { get; init; } = 10_000;
}

/// <summary>
/// One exact lexical occurrence from an analyzed source tree. The fact is
/// request-local and contains no compiler object or inferred policy.
/// </summary>
public sealed class SourceEvidenceFact
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string ModuleName { get; init; }
    public required string ProfileScope { get; init; }
    public required string MatchedTerm { get; init; }
    public required string Text { get; init; }
    public string? ContainingSymbolId { get; init; }
    public required OperationSourceSpan Source { get; init; }
}

/// <summary>
/// Truthful receipt for one streaming lexical-evidence pass over the retained
/// source trees. Facts flow through the callback and are never cached.
/// </summary>
public sealed class SourceEvidenceScanReceipt
{
    public required string Status { get; init; }
    public required string ProfileScope { get; init; }
    public string[] AvailableProfiles { get; init; } = Array.Empty<string>();
    public required string ExecutionMode { get; init; }
    public required bool InputIdentityVerifiedAtStart { get; init; }
    public required int AdditionalSemanticBaseCount { get; init; }
    public required int ScannedModuleCount { get; init; }
    public required int ScannedFileCount { get; init; }
    public required int ObservedEvidenceCount { get; init; }
    public required int EmittedFactCount { get; init; }
    public required bool Truncated { get; init; }
    public required bool StoppedByConsumer { get; init; }
    public string[] Limitations { get; init; } = Array.Empty<string>();
}

public static class SourceEvidenceKind
{
    public const string Comment = "Comment";
    public const string XmlDocumentation = "XmlDocumentation";
    public const string StringLiteral = "StringLiteral";

    public static readonly string[] All =
    {
        Comment,
        XmlDocumentation,
        StringLiteral,
    };
}

public static class SourceEvidenceExecutionMode
{
    public const string RetainedSyntaxTrees = "RetainedSyntaxTrees";
}

public static class SourceEvidenceScanStatus
{
    public const string Completed = "Completed";
}

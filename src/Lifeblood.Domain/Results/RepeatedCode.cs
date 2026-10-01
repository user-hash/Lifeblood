namespace Lifeblood.Domain.Results;

/// <summary>Scope and bounded work for an exact executable-body comparison.</summary>
public sealed class RepeatedCodeQuery
{
    public string? ModuleScope { get; init; }
    public string? ProfileScope { get; init; }
    public IReadOnlyList<string>? FilePaths { get; init; }
    public bool ExcludeTests { get; init; } = true;
    public bool ExcludeGenerated { get; init; } = true;
    public int MinTokens { get; init; } = 40;
    public int MaxBodies { get; init; } = 20_000;
    public int MaxTokens { get; init; } = 1_000_000;
    public int MaxSourceCharacters { get; init; } = 20_000_000;
    public int MaxNodes { get; init; } = 500_000;
    public int MaxFiles { get; init; } = 10_000;
    public int MaxGroups { get; init; } = 100;
    public int MaxMembersPerGroup { get; init; } = 20;

    public void Validate()
    {
        Bound(MinTokens, 1, 10_000, nameof(MinTokens));
        Bound(MaxBodies, 1, 50_000, nameof(MaxBodies));
        Bound(MaxTokens, 1, 2_000_000, nameof(MaxTokens));
        Bound(MaxSourceCharacters, 1, 100_000_000, nameof(MaxSourceCharacters));
        Bound(MaxNodes, 1, 2_000_000, nameof(MaxNodes));
        Bound(MaxFiles, 1, 20_000, nameof(MaxFiles));
        Bound(MaxGroups, 1, 1_000, nameof(MaxGroups));
        Bound(MaxMembersPerGroup, 2, 100, nameof(MaxMembersPerGroup));
        if (FilePaths is { Count: > 1_024 }) throw new ArgumentException("At most 1024 file paths are supported.", nameof(FilePaths));
    }

    private static void Bound(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, value, $"Expected {minimum}..{maximum}.");
    }
}

public sealed record RepeatedCodeOccurrence(string ModuleName, string ContainingSymbolId, string BodyKind, OperationSourceSpan Source);

public sealed record RepeatedCodeGroup(string Id, int TokenCount, int OccurrenceCount, bool MembersTruncated, RepeatedCodeOccurrence[] Members);

/// <summary>Evidence and coverage limits; token equality does not establish semantic equivalence.</summary>
public sealed class RepeatedCodeReport
{
    public string MatchPolicy { get; init; } = "ExactTokenSpellingIgnoringTrivia";
    public string ExecutionMode { get; init; } = "RetainedSyntaxTrees";
    public required string ProfileScope { get; init; }
    public required string[] AvailableProfiles { get; init; }
    public required RepeatedCodeQuery Scope { get; init; }
    public int AdditionalSemanticBaseCount { get; init; }
    public required int ScannedFiles { get; init; }
    public required int ScannedNodes { get; init; }
    public required int ObservedBodies { get; init; }
    public required int ComparedBodies { get; init; }
    public required int ScannedTokens { get; init; }
    public required int SourceCharacters { get; init; }
    public required int ExcludedFiles { get; init; }
    public required int SkippedMalformedBodies { get; init; }
    public required int GroupCount { get; init; }
    public required bool Truncated { get; init; }
    public required string[] ReachedLimits { get; init; }
    public required RepeatedCodeGroup[] Groups { get; init; }
    public required string[] Limitations { get; init; }
}

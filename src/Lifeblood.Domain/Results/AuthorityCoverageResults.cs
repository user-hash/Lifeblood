namespace Lifeblood.Domain.Results;

/// <summary>
/// Negative-dependency matrix: for each subject, report whether outgoing graph
/// paths reach the intended authority symbols. This catches "compiles but reads
/// the wrong source of truth" defects without turning the evidence into an
/// architectural verdict. INV-AUTHORITY-COVERAGE-001.
/// </summary>
public sealed class AuthorityCoverageReport
{
    public required string[] SubjectInputs { get; init; }
    public required string[] RequiredAuthorityIds { get; init; }
    public required string[] AllowedAlternativeIds { get; init; }
    public required int MaxDepth { get; init; }
    public required bool ExcludeTests { get; init; }
    public required bool ExcludeGenerated { get; init; }
    public required string[] IncludeBuckets { get; init; }
    public required int SubjectCount { get; init; }
    public required int ExpandedSubjectCount { get; init; }
    public required int AnalyzedSubjectSeedCount { get; init; }
    public required AuthorityCoverageRow[] Rows { get; init; }
    public required string[] Limitations { get; init; }
}

/// <summary>One row in an authority-coverage matrix.</summary>
public sealed class AuthorityCoverageRow
{
    public required string SubjectInput { get; init; }
    public required string SubjectId { get; init; }
    public required string SubjectKind { get; init; }
    public required string SubjectName { get; init; }
    public required string FilePath { get; init; }
    public required string Bucket { get; init; }
    public required int ExpandedSubjectCount { get; init; }
    public required int AnalyzedSubjectSeedCount { get; init; }
    public required string[] SubjectSeedPreview { get; init; }
    public required string Status { get; init; }
    public required bool HasAllRequiredAuthority { get; init; }
    public required int ReachedRequiredCount { get; init; }
    public required int MissingRequiredCount { get; init; }
    public required AuthorityCoverageReach[] ReachedAuthorities { get; init; }
    public required string[] MissingAuthorities { get; init; }
    public required AuthorityCoverageReach[] ReachedAllowedAlternatives { get; init; }
    public AuthorityCoverageReach? FirstCompetingAuthority { get; init; }
}

/// <summary>Shortest observed path from a subject seed to one authority root.</summary>
public sealed class AuthorityCoverageReach
{
    public required string AuthorityId { get; init; }
    public required string MatchedSymbolId { get; init; }
    public required string SubjectSeedId { get; init; }
    public required int Distance { get; init; }
    public required AuthorityCoveragePathStep[] Path { get; init; }
}

/// <summary>One symbol in an authority reachability path.</summary>
public sealed class AuthorityCoveragePathStep
{
    public required string SymbolId { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }

    /// <summary>Edge kind used to enter this step; empty on the path root.</summary>
    public required string ViaEdgeKind { get; init; }
}

/// <summary>Canonical authority-coverage row status strings. Open set.</summary>
public static class AuthorityCoverageStatus
{
    public const string RequiredReached = "RequiredReached";
    public const string MissingRequired = "MissingRequired";
    public const string AllowedAlternativeReached = "AllowedAlternativeReached";
    public const string NoSubjectSeeds = "NoSubjectSeeds";
}

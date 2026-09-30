namespace Lifeblood.Domain.Results;

/// <summary>Ordered per-target occurrences and request-local scan work.</summary>
public sealed class ReferenceSearchReport
{
    public ReferenceSearchGroup[] Symbols { get; init; } = Array.Empty<ReferenceSearchGroup>();
    public ReferenceSearchWork Work { get; init; } = new();
}

public sealed class ReferenceSearchGroup
{
    public required string SymbolId { get; init; }
    public bool Resolved { get; init; }
    public ReferenceLocation[] Locations { get; init; } = Array.Empty<ReferenceLocation>();
}

/// <summary>
/// Syntax matching counters exclude target admission, containing-member lookup
/// and the separately owned conversion operation traversal.
/// </summary>
public sealed class ReferenceSearchWork
{
    public int SyntaxTreeVisits { get; init; }
    public long SyntaxNodeVisits { get; init; }
    public long SyntaxBindingCount { get; init; }
    public long MatchIdBuildCount { get; init; }
    public int ConversionScanCount { get; init; }
}

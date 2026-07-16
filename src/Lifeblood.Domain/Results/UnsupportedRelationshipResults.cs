namespace Lifeblood.Domain.Results;

/// <summary>
/// Advisory receipt for relationship families that are deliberately not graph
/// edges. Semantic graph consumers can keep proven edges precise while callers
/// still see known blind spots and opt-in heuristic evidence.
/// </summary>
public sealed class UnsupportedRelationshipReport
{
    public string Mode { get; init; } = UnsupportedRelationshipMode.Advisory;
    public bool SemanticGraphEdgesChanged => false;
    public UnsupportedRelationshipFamilyReceipt[] Families { get; init; } = Array.Empty<UnsupportedRelationshipFamilyReceipt>();
    public UnsupportedRelationshipHit[] Hits { get; init; } = Array.Empty<UnsupportedRelationshipHit>();
    public int TotalHitCount { get; init; }
    public int ReturnedHitCount => Hits.Length;
    public int OmittedHitCount => Math.Max(0, TotalHitCount - Hits.Length);
    public bool Truncated => TotalHitCount > Hits.Length;
    public string[] Limitations { get; init; } = Array.Empty<string>();
}

public sealed class UnsupportedRelationshipFamilyReceipt
{
    public required string Name { get; init; }
    public required string Status { get; init; }
    public required string Description { get; init; }
}

public sealed class UnsupportedRelationshipHit
{
    public required string Family { get; init; }
    public required string SourceFilePath { get; init; }
    public required string TargetFilePath { get; init; }
    public required string Api { get; init; }
    public required int Line { get; init; }
    public required string Confidence { get; init; }
    public required string Evidence { get; init; }
}

public static class UnsupportedRelationshipMode
{
    public const string Advisory = "advisory";
}

public static class UnsupportedRelationshipFamily
{
    public const string SourceFileIoLiteral = "sourceFileIoLiteral";
    public const string ReflectionString = "reflectionString";
    public const string UnityResourcesLoad = "unityResourcesLoad";
    public const string UnitySerializedAssetReference = "unitySerializedAssetReference";
}

public static class UnsupportedRelationshipStatus
{
    public const string Scanned = "scanned";
    public const string DocumentedLimitation = "documentedLimitation";
}

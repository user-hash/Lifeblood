namespace Lifeblood.Domain.Results;

public sealed class MethodEffectRequest
{
    public string[] RootSymbolIds { get; init; } = Array.Empty<string>();
    public string? ProfileScope { get; init; }
    public int MaxDepth { get; init; } = 4;
    public int MaxMethods { get; init; } = 128;
    public int MaxFacts { get; init; } = 10_000;
    public int MaxEvidencePerMethod { get; init; } = 16;
}

/// <summary>Bounded static observations, never a runtime-effects or purity verdict.</summary>
public sealed class MethodEffectReport
{
    public string Kind { get; init; } = "lifeblood.method_effects";
    public required OperationFactScanReceipt ScanReceipt { get; init; }
    public required ContractCallRouteReceipt[] CandidateScopes { get; init; }
    public required MethodEffectSummary[] Methods { get; init; }
    public string[] Limitations { get; init; } = Array.Empty<string>();
}

public sealed class MethodEffectSummary
{
    public required string RootSymbolId { get; init; }
    public string? RootCallableId { get; init; }
    public required string[] ReachedCallableIds { get; init; }
    public required MethodEffectCount[] ObservedOperations { get; init; }
    public required int ObservedFactCount { get; init; }
    public required int MemberAccessCount { get; init; }
    public required MethodEffectEvidence[] MemberAccesses { get; init; }
    public required int CallCount { get; init; }
    public required MethodEffectEvidence[] Calls { get; init; }
    public required int CallbackBindingCount { get; init; }
    public required MethodCallbackBinding[] CallbackBindings { get; init; }
    public required int DeferredCallableCount { get; init; }
    public required MethodCallableEvidence[] DeferredCallables { get; init; }
    public required MethodCallableEvidence[] CallableDeclarations { get; init; }
    public required MethodEffectCount[] UnknownReasons { get; init; }
    public required MethodEffectUnknown[] UnknownEvidence { get; init; }
    public required bool EvidenceTruncated { get; init; }
}

public sealed record MethodEffectCount(string Kind, int Count);

public sealed class MethodEffectEvidence
{
    public required string FactId { get; init; }
    public required string CallableId { get; init; }
    public required string Kind { get; init; }
    public string? TargetSymbolId { get; init; }
    public string? TargetCallableId { get; init; }
    public string? InvocationKind { get; init; }
    public string[] AccessModes { get; init; } = Array.Empty<string>();
    public string? TargetMemberKind { get; init; }
    /// <summary>Opaque value occurrence key; not a graph declaration address.</summary>
    public string? ReceiverValueId { get; init; }
    public required OperationSourceSpan Source { get; init; }
}

public sealed record MethodCallbackBinding(
    string FactId,
    string CallableId,
    string BoundCallableId,
    string Role,
    string? DestinationValueId,
    OperationSourceSpan Source);

public sealed record MethodCallableEvidence(
    string CallableId,
    string? SymbolId,
    string ModuleName,
    string ProfileScope,
    OperationSourceSpan Source,
    OperationCallableDeclaration Declaration);

public sealed record MethodEffectUnknown(
    string Reason,
    string? CallableId,
    string? FactId,
    OperationSourceSpan? Source);

public static class MethodEffectUnknownReason
{
    public const string ScanRejected = "ScanRejected";
    public const string ScanTruncated = "ScanTruncated";
    public const string ScanStopped = "ScanStopped";
    public const string CandidateScopeTruncated = "CandidateScopeTruncated";
    public const string RootDeclarationUnavailable = "RootDeclarationUnavailable";
    public const string RootDeclarationAmbiguous = "RootDeclarationAmbiguous";
    public const string SourceBodyUnavailable = "SourceBodyUnavailable";
    public const string BindingErrors = "BindingErrors";
    public const string IndirectDispatch = "IndirectDispatch";
    public const string ExternalImplementation = "ExternalImplementation";
    public const string TargetBodyNotSelected = "TargetBodyNotSelected";
    public const string DepthLimit = "DepthLimit";
    public const string MethodLimit = "MethodLimit";
    public const string PropertyOrEventAccessorEffects = "PropertyOrEventAccessorEffects";
    public const string BoundOperatorNotExpanded = "BoundOperatorNotExpanded";
}

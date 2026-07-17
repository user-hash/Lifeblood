namespace Lifeblood.Domain.Results;

/// <summary>
/// Consumer-owned, versioned semantic contracts. The manifest is inert data:
/// language adapters emit facts, analysis evaluates policy, and connectors only
/// bind or project this shape. INV-CONTRACT-AUDIT-001.
/// </summary>
public sealed class ContractManifest
{
    public const string CurrentSchemaVersion = "1";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string Id { get; init; }
    public required string Version { get; init; }
    public string? Description { get; init; }
    public ContractCallRoute[] CallRoutes { get; init; } = Array.Empty<ContractCallRoute>();
    public RouteFactContract[] RouteFacts { get; init; } = Array.Empty<RouteFactContract>();
    public OperationGuardContract[] OperationGuards { get; init; } = Array.Empty<OperationGuardContract>();
    public ExternalApiCostContract[] ExternalApiCosts { get; init; } = Array.Empty<ExternalApiCostContract>();
    public StateAccessContract[] StateAccesses { get; init; } = Array.Empty<StateAccessContract>();
    public ValueDomainContract[] ValueDomains { get; init; } = Array.Empty<ValueDomainContract>();
    public OperationShapeContract[] OperationShapes { get; init; } = Array.Empty<OperationShapeContract>();
    public SourceTextPolicyContract[] SourceTextPolicies { get; init; } = Array.Empty<SourceTextPolicyContract>();
    public InvariantEvidenceContract[] InvariantEvidence { get; init; } = Array.Empty<InvariantEvidenceContract>();
    public ContractSuppression[] Suppressions { get; init; } = Array.Empty<ContractSuppression>();
}

/// <summary>
/// Caller-authored advisory policy for exact phrases in source comments or XML
/// documentation. Lifeblood proves the lexical occurrence and nearby symbol;
/// the caller owns the retired-term decision and suggested action.
/// </summary>
public sealed class SourceTextPolicyContract
{
    public required string Id { get; init; }
    public string[] Terms { get; init; } = Array.Empty<string>();
    public string[] IncludeKinds { get; init; } = new[]
    {
        SourceEvidenceKind.Comment,
        SourceEvidenceKind.XmlDocumentation,
    };
    public required string SuggestedAction { get; init; }
    public string[] InvariantIds { get; init; } = Array.Empty<string>();
    public string[] CallRouteIds { get; init; } = Array.Empty<string>();
    public string[] Categories { get; init; } = Array.Empty<string>();
    public string Severity { get; init; } = ContractSeverity.Warning;
    public string? Message { get; init; }
    public string? Guidance { get; init; }
}

public static class SourceTextSuggestedAction
{
    public const string Delete = "Delete";
    public const string UpdateAuthority = "UpdateAuthority";
    public const string Keep = "Keep";

    public static readonly string[] All = { Delete, UpdateAuthority, Keep };
}

/// <summary>
/// Consumer-selected, named evidence requirements for one invariant family.
/// Results remain category receipts and concrete gaps; no global quality score
/// is computed.
/// </summary>
public sealed class InvariantEvidenceContract
{
    public const int DefaultMaxInvariants = 256;
    public const int MaximumInvariants = 2_048;
    public const int DefaultMaxTestDepth = 12;
    public const int MaximumTestDepth = 32;

    public required string Id { get; init; }
    public string[] InvariantIds { get; init; } = Array.Empty<string>();
    public string[] InvariantIdPrefixes { get; init; } = Array.Empty<string>();
    public InvariantReferenceAlias[] ReferenceAliases { get; init; } = Array.Empty<InvariantReferenceAlias>();
    public string[] CallRouteIds { get; init; } = Array.Empty<string>();
    public string[] RequiredEvidenceKinds { get; init; } = new[]
    {
        ContractEvidenceKind.InvariantDeclaration,
        ContractEvidenceKind.SourceReference,
        ContractEvidenceKind.TestReference,
    };
    public string[] OperationContractIds { get; init; } = Array.Empty<string>();
    public ContractExternalEvidence[] ExternalEvidence { get; init; } = Array.Empty<ContractExternalEvidence>();
    public int MaxInvariants { get; init; } = DefaultMaxInvariants;
    public int MaxTestDepth { get; init; } = DefaultMaxTestDepth;
    public string[] Categories { get; init; } = Array.Empty<string>();
}

/// <summary>Caller-declared alternate exact term for one invariant id.</summary>
public sealed class InvariantReferenceAlias
{
    public required string InvariantId { get; init; }
    public string[] Terms { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Explicit external receipt reference. Lifeblood reports it as caller-declared
/// evidence and never claims to have executed or verified the referenced run.
/// </summary>
public sealed class ContractExternalEvidence
{
    public required string Kind { get; init; }
    public required string Reference { get; init; }
}

/// <summary>
/// Consumer-owned bounded execution scope. Analysis expands exact method roots
/// through semantic call edges; operation extraction then scans only methods in
/// the derived route. The plan is request-local and never becomes another graph.
/// </summary>
public sealed class ContractCallRoute
{
    public const int DefaultMaxDepth = 8;
    public const int DefaultMaxMembers = 4_096;
    public const int MaximumDepth = 32;
    public const int MaximumMembers = 50_000;

    public required string Id { get; init; }
    public string[] RootSymbolIds { get; init; } = Array.Empty<string>();
    public int MaxDepth { get; init; } = DefaultMaxDepth;
    public int MaxMembers { get; init; } = DefaultMaxMembers;
}

/// <summary>
/// Consumer-owned policy over neutral operation facts partitioned by bounded
/// call routes. Required, parity, and owner-only policies share one evaluator;
/// product execution-lane or sibling vocabulary remains in the manifest.
/// </summary>
public sealed class RouteFactContract
{
    public required string Id { get; init; }
    public required string Policy { get; init; }
    public string[] CallRouteIds { get; init; } = Array.Empty<string>();
    public string[] OperationKinds { get; init; } = Array.Empty<string>();
    public string[] TargetSymbolIds { get; init; } = Array.Empty<string>();
    public bool MatchAnyTarget { get; init; }
    public string[] Operators { get; init; } = Array.Empty<string>();
    public string[] SignatureParts { get; init; } = Array.Empty<string>();
    public string[] Categories { get; init; } = Array.Empty<string>();
    public string Severity { get; init; } = ContractSeverity.Warning;
    public string? Message { get; init; }
    public string? Guidance { get; init; }
}

public static class RouteFactPolicy
{
    public const string RequiredOnEveryRoute = "RequiredOnEveryRoute";
    public const string EquivalentAcrossRoutes = "EquivalentAcrossRoutes";
    public const string AllowedRoutesOnly = "AllowedRoutesOnly";

    public static readonly string[] All =
    {
        RequiredOnEveryRoute,
        EquivalentAcrossRoutes,
        AllowedRoutesOnly,
    };
}

/// <summary>
/// Open, consumer-selected dimensions used to compare neutral facts between
/// sibling routes. Source locations and expressions are deliberately absent.
/// </summary>
public static class RouteFactSignaturePart
{
    public const string Kind = "Kind";
    public const string TargetSymbol = "TargetSymbol";
    public const string Operator = "Operator";
    public const string ResultType = "ResultType";
    public const string InputValueKinds = "InputValueKinds";
    public const string InputTypes = "InputTypes";
    public const string InputConstants = "InputConstants";
    public const string InputSourceSymbols = "InputSourceSymbols";
    public const string InputOperators = "InputOperators";
    public const string ControlKinds = "ControlKinds";
    public const string ControlSourceSymbols = "ControlSourceSymbols";
    public const string ControlOperators = "ControlOperators";

    public static readonly string[] All =
    {
        Kind,
        TargetSymbol,
        Operator,
        ResultType,
        InputValueKinds,
        InputTypes,
        InputConstants,
        InputSourceSymbols,
        InputOperators,
        ControlKinds,
        ControlSourceSymbols,
        ControlOperators,
    };
}

/// <summary>
/// Requires one argument of a bound call or constructor to carry at least one
/// manifest-declared local safety signal.
/// </summary>
public sealed class OperationGuardContract
{
    public required string Id { get; init; }
    public string[] TargetSymbolIds { get; init; } = Array.Empty<string>();
    public int ArgumentOrdinal { get; init; }
    public string[] AllowedValueKinds { get; init; } = Array.Empty<string>();
    public string[] AllowedSourceSymbolIds { get; init; } = Array.Empty<string>();
    public string[] AllowedControlContextKinds { get; init; } = Array.Empty<string>();
    public string[] AllowedControlOperators { get; init; } = Array.Empty<string>();
    public bool RequireArgumentSourceInControlContext { get; init; } = true;
    public string Severity { get; init; } = ContractSeverity.Warning;
    public string? Message { get; init; }
    public string? Guidance { get; init; }
}

/// <summary>
/// Consumer-authored cost annotation for a bound external API. Lifeblood proves
/// occurrences and context; the consumer remains the authority for cost policy.
/// </summary>
public sealed class ExternalApiCostContract
{
    public required string Id { get; init; }
    public string[] TargetSymbolIds { get; init; } = Array.Empty<string>();
    public bool MatchAnyTarget { get; init; }
    public string[] OperationKinds { get; init; } = new[]
    {
        OperationFactKind.Call,
        OperationFactKind.ObjectCreation,
        OperationFactKind.MemberRead,
        OperationFactKind.MemberWrite,
    };
    public string[] Categories { get; init; } = Array.Empty<string>();
    public bool ReportEveryOccurrence { get; init; }
    public string[] ControlContextKinds { get; init; } = Array.Empty<string>();
    public string[] ContainingSymbolIds { get; init; } = Array.Empty<string>();
    public string[] CallRouteIds { get; init; } = Array.Empty<string>();
    public required string AnnotationSource { get; init; }
    public string? AppliesToVersion { get; init; }
    public string Severity { get; init; } = ContractSeverity.Warning;
    public string? Message { get; init; }
    public string? Guidance { get; init; }
}

/// <summary>
/// Consumer-owned policy for state members touched from bounded call routes.
/// Lifeblood classifies declaration and semantic access evidence; the caller
/// chooses which risk buckets are acceptable for the selected execution path.
/// </summary>
public sealed class StateAccessContract
{
    public const int DefaultMaxMembers = 4_096;
    public const int MaximumMembers = 50_000;

    public required string Id { get; init; }
    public string[] TargetSymbolIds { get; init; } = Array.Empty<string>();
    public bool MatchAnyMember { get; init; }
    public string MemberScope { get; init; } = StateMemberScope.Static;
    public string[] CallRouteIds { get; init; } = Array.Empty<string>();
    public string[] AllowedRiskBuckets { get; init; } = new[]
    {
        StateRiskBucket.ReadonlyTable,
        StateRiskBucket.InitializedOnceCache,
    };
    public string[] Categories { get; init; } = Array.Empty<string>();
    public int MaxMembers { get; init; } = DefaultMaxMembers;
    public string Severity { get; init; } = ContractSeverity.Warning;
    public string? Message { get; init; }
    public string? Guidance { get; init; }
}

public static class StateMemberScope
{
    public const string Static = "Static";
    public const string Instance = "Instance";
    public const string Any = "Any";
    public static readonly string[] All = { Static, Instance, Any };
}

public static class StateRiskBucket
{
    public const string ReadonlyTable = "ReadonlyTable";
    public const string InitializedOnceCache = "InitializedOnceCache";
    public const string RuntimeMutable = "RuntimeMutable";
    public const string SharedScratch = "SharedScratch";
    public const string Unknown = "Unknown";
    public static readonly string[] All =
    {
        ReadonlyTable,
        InitializedOnceCache,
        RuntimeMutable,
        SharedScratch,
        Unknown,
    };
}

/// <summary>
/// Requires a selected input to carry one declared value domain or an exact
/// manifest-declared conversion into that domain. Domain names are open
/// consumer vocabulary; Lifeblood binds only symbols and operators.
/// </summary>
public sealed class ValueDomainContract
{
    public required string Id { get; init; }
    public string[] TargetSymbolIds { get; init; } = Array.Empty<string>();
    public string[] OperationKinds { get; init; } = new[]
    {
        OperationFactKind.Call,
        OperationFactKind.ObjectCreation,
        OperationFactKind.Assignment,
    };
    public string InputRole { get; init; } = OperationInputRole.Argument;
    public int? InputOrdinal { get; init; } = 0;
    public required string TargetDomain { get; init; }
    public ValueDomainBinding[] Bindings { get; init; } = Array.Empty<ValueDomainBinding>();
    public ValueDomainConversion[] AllowedConversions { get; init; } = Array.Empty<ValueDomainConversion>();
    public ValueDomainNonFinitePolicy? NonFinitePolicy { get; init; }
    public ValueDomainConstantPolicy? ConstantPolicy { get; init; }
    public ValueDomainBoundaryPolicy? BoundaryPolicy { get; init; }
    public bool AllowCompileTimeConstants { get; init; }
    public bool ReportUnclassifiedValues { get; init; }
    public string Severity { get; init; } = ContractSeverity.Warning;
    public string? Message { get; init; }
    public string? Guidance { get; init; }
}

/// <summary>
/// Consumer-owned handling contract for non-finite values at one value-domain
/// boundary. Evidence symbols name the sanitizer/validator authority; the
/// action describes policy and never comes from product-name inference.
/// </summary>
public sealed class ValueDomainNonFinitePolicy
{
    public required string Action { get; init; }
    public string[] EvidenceSymbolIds { get; init; } = Array.Empty<string>();
    public bool RequireEvidenceForAllValues { get; init; }
}

/// <summary>
/// Consumer-owned raw-literal policy for a value-domain boundary. Named
/// constants stay distinguishable through <see cref="OperationConstantFact"/>;
/// exact literal exceptions cover universal identities such as zero or one.
/// </summary>
public sealed class ValueDomainConstantPolicy
{
    public bool ReportRawNumericLiterals { get; init; }
    public string[] AllowedLiteralValues { get; init; } = Array.Empty<string>();
    public ValueDomainNearEqualPolicy? NearEqualPolicy { get; init; }
}

/// <summary>
/// Consumer-owned tolerance for grouping distinct raw numeric literals inside
/// one value domain and operation kind. The rule reports deterministic adjacent
/// value pairs; it never invents a unit or tolerance from source names.
/// </summary>
public sealed class ValueDomainNearEqualPolicy
{
    public double AbsoluteTolerance { get; init; }
    public double RelativeTolerance { get; init; }
    public int MinimumOccurrences { get; init; } = 2;
}

/// <summary>
/// Consumer-owned lexical cadence boundary. Source symbols select the boundary
/// value; allowed shapes state the exact comparison side, operator, value kind,
/// nested operators, and constants accepted at the selected operation.
/// </summary>
public sealed class ValueDomainBoundaryPolicy
{
    public string[] ContextKinds { get; init; } = new[] { OperationControlContextKind.Loop };
    public string[] BoundarySourceSymbolIds { get; init; } = Array.Empty<string>();
    public ValueDomainBoundaryShape[] AllowedShapes { get; init; } = Array.Empty<ValueDomainBoundaryShape>();
    public bool RequireInputSourceInPredicate { get; init; } = true;
    public bool ReportMissingBoundary { get; init; } = true;
}

public sealed class ValueDomainBoundaryShape
{
    public required string ComparisonOperator { get; init; }
    public string BoundarySide { get; init; } = BoundaryOperandSide.Either;
    public string[] BoundaryValueKinds { get; init; } = Array.Empty<string>();
    public string[] BoundaryOperators { get; init; } = Array.Empty<string>();
    public string[] BoundaryConstantValues { get; init; } = Array.Empty<string>();
}

public static class BoundaryOperandSide
{
    public const string Left = "Left";
    public const string Right = "Right";
    public const string Either = "Either";

    public static readonly string[] All = { Left, Right, Either };
}

/// <summary>Exact source symbols that identify one consumer-named domain.</summary>
public sealed class ValueDomainBinding
{
    public required string Domain { get; init; }
    public string[] SourceSymbolIds { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Exact evidence required to accept a conversion from a set of source
/// domains into the parent contract's target domain.
/// </summary>
public sealed class ValueDomainConversion
{
    public required string Id { get; init; }
    public string[] SourceDomains { get; init; } = Array.Empty<string>();
    public string[] RequiredSourceSymbolIds { get; init; } = Array.Empty<string>();
    public string[] RequiredOperators { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Requires each selected occurrence to match one consumer-declared lexical
/// shape. This family covers exact multi-input relationships such as buffer
/// dimensions, sidecar indices, strides, and mask representation without
/// inferring product vocabulary or creating architecture edges.
/// </summary>
public sealed class OperationShapeContract
{
    public required string Id { get; init; }
    public string[] OperationKinds { get; init; } = Array.Empty<string>();
    public string[] TargetSymbolIds { get; init; } = Array.Empty<string>();
    public string[] ContainingSymbolIds { get; init; } = Array.Empty<string>();
    public string[] Operators { get; init; } = Array.Empty<string>();
    public OperationAllowedShape[] AllowedShapes { get; init; } = Array.Empty<OperationAllowedShape>();
    public OperationShapeUniquenessPolicy? UniquenessPolicy { get; init; }
    public string[] Categories { get; init; } = Array.Empty<string>();
    public string Severity { get; init; } = ContractSeverity.Warning;
    public string? Message { get; init; }
    public string? Guidance { get; init; }
}

/// <summary>One exact alternative accepted by an operation-shape contract.</summary>
public sealed class OperationAllowedShape
{
    public required string Id { get; init; }
    public OperationInputShape[] Inputs { get; init; } = Array.Empty<OperationInputShape>();
    public string[] AllowedResultTypes { get; init; } = Array.Empty<string>();
    public OperationControlShape[] ControlContexts { get; init; } = Array.Empty<OperationControlShape>();
}

/// <summary>
/// Exact lexical requirements for one input slot. Populated allowed dimensions
/// accept any declared value; populated required dimensions must all occur.
/// </summary>
public sealed class OperationInputShape
{
    public required string Role { get; init; }
    public int? Ordinal { get; init; }
    public string[] AllowedValueKinds { get; init; } = Array.Empty<string>();
    public string[] AllowedTypes { get; init; } = Array.Empty<string>();
    public string[] AnySourceSymbolIds { get; init; } = Array.Empty<string>();
    public string[] RequiredSourceSymbolIds { get; init; } = Array.Empty<string>();
    public string[] RequiredOperators { get; init; } = Array.Empty<string>();
    public string[] AllowedConstantValues { get; init; } = Array.Empty<string>();
    public string[] RequiredConstantValues { get; init; } = Array.Empty<string>();
    public string[] ForbiddenConstantValues { get; init; } = Array.Empty<string>();
    public bool? CompileTimeConstant { get; init; }
}

/// <summary>
/// Consumer-owned duplicate policy over one selected input. Observations are
/// retained only for the bounded request and released with the audit report.
/// </summary>
public sealed class OperationShapeUniquenessPolicy
{
    public required string InputRole { get; init; }
    public int? InputOrdinal { get; init; }
    public required string KeyKind { get; init; }
    public int MinimumOccurrences { get; init; } = 2;
}

public static class OperationShapeKeyKind
{
    public const string ConstantValue = "ConstantValue";
    public const string SourceSymbolId = "SourceSymbolId";

    public static readonly string[] All = { ConstantValue, SourceSymbolId };
}

/// <summary>Lexical control-context requirements for an allowed shape.</summary>
public sealed class OperationControlShape
{
    public required string Kind { get; init; }
    public string[] AllowedBranchArms { get; init; } = Array.Empty<string>();
    public string[] AnySourceSymbolIds { get; init; } = Array.Empty<string>();
    public string[] RequiredSourceSymbolIds { get; init; } = Array.Empty<string>();
    public string[] RequiredOperators { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Exact, reviewable suppression selectors. Values within a selector are ORed;
/// populated selector dimensions are ANDed. Empty suppressions are invalid.
/// </summary>
public sealed class ContractSuppression
{
    public required string Id { get; init; }
    public string[] RuleIds { get; init; } = Array.Empty<string>();
    public string[] ContractIds { get; init; } = Array.Empty<string>();
    public string[] FactIds { get; init; } = Array.Empty<string>();
    public string[] ContainingSymbolIds { get; init; } = Array.Empty<string>();
    public string[] FilePaths { get; init; } = Array.Empty<string>();
    public required string Reason { get; init; }
}

/// <summary>Bounded, adapter-neutral request for a contract audit.</summary>
public sealed class ContractAuditRequest
{
    public required ContractManifest Manifest { get; init; }
    public string? ProfileScope { get; init; }
    public string? ModuleScope { get; init; }
    public string[]? FilePaths { get; init; }
    public string[]? ContainingSymbolIds { get; init; }
    public string[]? IncludeRuleIds { get; init; }
    public ContractCallRoutePlan? CallRoutePlan { get; init; }
    public ContractStatePlan? StatePlan { get; init; }
    public int MaxFacts { get; init; } = 50_000;
    public int MaxFindings { get; init; } = 200;
    public int MaxEvidencePerFinding { get; init; } = 8;
    public bool Summarize { get; init; }
}

/// <summary>Complete receipt for one manifest evaluation over one fact stream.</summary>
public sealed class ContractAuditReport
{
    public required string Status { get; init; }
    public required string ManifestId { get; init; }
    public required string ManifestVersion { get; init; }
    public required string ManifestSchemaVersion { get; init; }
    public string[] SelectedRuleIds { get; init; } = Array.Empty<string>();
    public string[] SelectedContractIds { get; init; } = Array.Empty<string>();
    public required OperationFactScanReceipt ScanReceipt { get; init; }
    public required int FindingCount { get; init; }
    public required int ReturnedFindingCount { get; init; }
    public required int SuppressedFindingCount { get; init; }
    public required bool Truncated { get; init; }
    public ContractRuleBreakdown[] RuleBreakdown { get; init; } = Array.Empty<ContractRuleBreakdown>();
    public ContractCallRouteReceipt[] CallRoutes { get; init; } = Array.Empty<ContractCallRouteReceipt>();
    public ContractRouteFactReceipt[] RouteFacts { get; init; } = Array.Empty<ContractRouteFactReceipt>();
    public ContractStateAccessReceipt[] StateAccesses { get; init; } = Array.Empty<ContractStateAccessReceipt>();
    public SourceEvidenceScanReceipt? SourceEvidenceScan { get; init; }
    public ContractTextPolicyReceipt[] SourceTextPolicies { get; init; } = Array.Empty<ContractTextPolicyReceipt>();
    public ContractTextMatch[] SourceTextMatches { get; init; } = Array.Empty<ContractTextMatch>();
    public ContractInvariantEvidencePolicyReceipt[] InvariantEvidencePolicies { get; init; } = Array.Empty<ContractInvariantEvidencePolicyReceipt>();
    public ContractInvariantEvidenceReceipt[] InvariantEvidence { get; init; } = Array.Empty<ContractInvariantEvidenceReceipt>();
    public ContractFinding[] Findings { get; init; } = Array.Empty<ContractFinding>();
    public string[] Limitations { get; init; } = Array.Empty<string>();
}

public sealed class ContractTextPolicyReceipt
{
    public required string ContractId { get; init; }
    public required int MatchCount { get; init; }
    public required int ReturnedMatchCount { get; init; }
    public required int SuppressedMatchCount { get; init; }
    public required bool Truncated { get; init; }
}

public sealed class ContractTextMatch
{
    public required string ContractId { get; init; }
    public required string FactId { get; init; }
    public required string MatchedTerm { get; init; }
    public required string SuggestedAction { get; init; }
    public required ConfidenceBand Confidence { get; init; }
    public required string Authority { get; init; }
    public string[] InvariantIds { get; init; } = Array.Empty<string>();
    public string[] Categories { get; init; } = Array.Empty<string>();
    public required string Message { get; init; }
    public string? Guidance { get; init; }
    public required string Text { get; init; }
    public string? ContainingSymbolId { get; init; }
    public required OperationSourceSpan Source { get; init; }
}

public sealed class ContractInvariantEvidenceReceipt
{
    public required string ContractId { get; init; }
    public required string InvariantId { get; init; }
    public string[] States { get; init; } = Array.Empty<string>();
    public required bool RequirementsSatisfied { get; init; }
    public required bool Truncated { get; init; }
    public ContractEvidenceCategoryReceipt[] Evidence { get; init; } = Array.Empty<ContractEvidenceCategoryReceipt>();
    public ContractEvidenceGap[] Gaps { get; init; } = Array.Empty<ContractEvidenceGap>();
}

/// <summary>
/// Selection receipt for one invariant-evidence policy. It keeps an empty
/// exact/prefix selection distinguishable from an omitted policy.
/// </summary>
public sealed class ContractInvariantEvidencePolicyReceipt
{
    public required string ContractId { get; init; }
    public required int SelectedInvariantCount { get; init; }
    public required int ReturnedInvariantCount { get; init; }
    public required bool Truncated { get; init; }
}

public sealed class ContractEvidenceCategoryReceipt
{
    public required string Kind { get; init; }
    public required bool Required { get; init; }
    public required string Status { get; init; }
    public required int EvidenceCount { get; init; }
    public required int ReturnedEvidenceCount { get; init; }
    public ContractEvidence[] Evidence { get; init; } = Array.Empty<ContractEvidence>();
}

public sealed class ContractEvidenceGap
{
    public required string Kind { get; init; }
    public required string Subject { get; init; }
    public required string Reason { get; init; }
}

/// <summary>Neutral declaration record passed from invariant authority to Analysis.</summary>
public sealed class InvariantDeclarationEvidence
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    public required OperationSourceSpan Source { get; init; }
}

public static class ContractEvidenceKind
{
    public const string InvariantDeclaration = "InvariantDeclaration";
    public const string SourceReference = "SourceReference";
    public const string TestReference = "TestReference";
    public const string ReachableTest = "ReachableTest";
    public const string OperationContract = "OperationContract";

    public static readonly string[] BuiltIn =
    {
        InvariantDeclaration,
        SourceReference,
        TestReference,
        ReachableTest,
        OperationContract,
    };
}

public static class ContractEvidenceStatus
{
    public const string Present = "Present";
    public const string Missing = "Missing";
    public const string Failing = "Failing";
    public const string CallerDeclared = "CallerDeclared";
}

public static class InvariantEvidenceState
{
    public const string Covered = "Covered";
    public const string ProseOnly = "ProseOnly";
    public const string SourceOnly = "SourceOnly";
    public const string TestOnly = "TestOnly";
    public const string StaleReference = "StaleReference";
    public const string Orphan = "Orphan";
}

public sealed class ContractRuleBreakdown
{
    public required string RuleId { get; init; }
    public required int FindingCount { get; init; }
    public required int SuppressedFindingCount { get; init; }
    public ContractEvaluationBreakdown[] Contracts { get; init; } = Array.Empty<ContractEvaluationBreakdown>();
}

/// <summary>
/// Per-contract coverage derived from the same evaluation counters as the
/// enclosing rule row. Zero evaluated occurrences is an explicit evidence gap,
/// never an implicit pass.
/// </summary>
public sealed class ContractEvaluationBreakdown
{
    public required string ContractId { get; init; }
    public required int EvaluatedOccurrenceCount { get; init; }
    public required int FindingFreeOccurrenceCount { get; init; }
    public required int FindingCount { get; init; }
    public required int SuppressedFindingCount { get; init; }
}

/// <summary>One deterministic occurrence-level contract result.</summary>
public sealed class ContractFinding
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string RuleId { get; init; }
    public required string ContractId { get; init; }
    public required string Severity { get; init; }
    public required ConfidenceBand Confidence { get; init; }
    public string[] Categories { get; init; } = Array.Empty<string>();
    public required string Message { get; init; }
    public string? Guidance { get; init; }
    public required string FactId { get; init; }
    public required string ContainingSymbolId { get; init; }
    public string? TargetSymbolId { get; init; }
    public string? StateRiskBucket { get; init; }
    public required OperationSourceSpan Source { get; init; }
    public int CallRouteMatchCount { get; init; }
    public bool CallRouteMatchesTruncated { get; init; }
    public ContractCallRouteMatch[] CallRouteMatches { get; init; } = Array.Empty<ContractCallRouteMatch>();
    public ContractEvidence[] Evidence { get; init; } = Array.Empty<ContractEvidence>();
}

/// <summary>Request-local call-route expansion consumed by contract rules.</summary>
public sealed class ContractCallRoutePlan
{
    public static ContractCallRoutePlan Empty { get; } = new();

    public ContractCallRouteReceipt[] Routes { get; init; } = Array.Empty<ContractCallRouteReceipt>();
    public ContractCallRouteMatch[] Matches { get; init; } = Array.Empty<ContractCallRouteMatch>();
}

/// <summary>Bounded route expansion receipt; member identities stay in the plan.</summary>
public sealed class ContractCallRouteReceipt
{
    public required string RouteId { get; init; }
    public string[] RootSymbolIds { get; init; } = Array.Empty<string>();
    public ContractCallRouteRootReceipt[] Roots { get; init; } = Array.Empty<ContractCallRouteRootReceipt>();
    public required int MaxDepth { get; init; }
    public required int MaxMembers { get; init; }
    public required int ReachableMemberCount { get; init; }
    public required int MembershipCount { get; init; }
    public required bool Truncated { get; init; }
}

/// <summary>Declaration evidence for one route root, used by missing-route findings.</summary>
public sealed class ContractCallRouteRootReceipt
{
    public required string SymbolId { get; init; }
    public required OperationSourceSpan Source { get; init; }
}

/// <summary>
/// Exact shortest call route from one declared root to a containing method.
/// Distance zero means the selected operation occurs directly in the root;
/// positive distance means it occurs in a transitive callee.
/// </summary>
public sealed class ContractCallRouteMatch
{
    public required string RouteId { get; init; }
    public required string RootSymbolId { get; init; }
    public required string ContainingSymbolId { get; init; }
    public required int Distance { get; init; }
    public required string Placement { get; init; }
    public string[] PathSymbolIds { get; init; } = Array.Empty<string>();
}

public static class ContractCallRoutePlacement
{
    public const string Direct = "Direct";
    public const string Transitive = "Transitive";
}

public sealed class ContractRouteFactReceipt
{
    public required string ContractId { get; init; }
    public required string Policy { get; init; }
    public required int SelectedFactCount { get; init; }
    public required int SignatureCount { get; init; }
    public required bool Incomplete { get; init; }
    public ContractRouteFactRouteReceipt[] Routes { get; init; } = Array.Empty<ContractRouteFactRouteReceipt>();
}

public sealed class ContractRouteFactRouteReceipt
{
    public required string RouteId { get; init; }
    public required int FactCount { get; init; }
    public required int SignatureCount { get; init; }
    public required bool RequirementSatisfied { get; init; }
}

/// <summary>Bounded request-local projection of graph member declarations.</summary>
public sealed class ContractStatePlan
{
    public static ContractStatePlan Empty { get; } = new();
    public ContractStatePlanReceipt[] Contracts { get; init; } = Array.Empty<ContractStatePlanReceipt>();
    public ContractStateMember[] Members { get; init; } = Array.Empty<ContractStateMember>();
}

public sealed class ContractStatePlanReceipt
{
    public required string ContractId { get; init; }
    public required int CandidateMemberCount { get; init; }
    public required int RetainedMemberCount { get; init; }
    public required int MaxMembers { get; init; }
    public required bool Truncated { get; init; }
}

public sealed class ContractStateMember
{
    public required string ContractId { get; init; }
    public required string SymbolId { get; init; }
    public required string MemberKind { get; init; }
    public required string ValueType { get; init; }
    public required bool IsStatic { get; init; }
    public required bool IsReadOnly { get; init; }
    public required bool IsConst { get; init; }
    public required bool HasSetter { get; init; }
    public required bool HasInitializer { get; init; }
    public required OperationSourceSpan DeclarationSource { get; init; }
}

public sealed class ContractStateAccessReceipt
{
    public required string ContractId { get; init; }
    public required int CandidateMemberCount { get; init; }
    public required int RetainedMemberCount { get; init; }
    public required int AccessedMemberCount { get; init; }
    public required bool Truncated { get; init; }
    public ContractStateRiskBucketCount[] RiskBuckets { get; init; } = Array.Empty<ContractStateRiskBucketCount>();
}

public sealed class ContractStateRiskBucketCount
{
    public required string RiskBucket { get; init; }
    public required int MemberCount { get; init; }
}

public sealed class ContractEvidence
{
    public required string Kind { get; init; }
    public required string Summary { get; init; }
    public string[] SymbolIds { get; init; } = Array.Empty<string>();
    public OperationSourceSpan? Source { get; init; }
}

public static class ContractRuleId
{
    public const string RouteFact = "route-fact";
    public const string OperationGuard = "operation-guard";
    public const string ExternalApiCost = "external-api-cost";
    public const string StateAccess = "state-access";
    public const string ValueDomain = "value-domain";
    public const string OperationShape = "operation-shape";
    public const string SourceTextPolicy = "source-text-policy";
    public const string InvariantEvidence = "invariant-evidence";
}

public static class ContractFindingKind
{
    public const string MissingRequiredRouteFact = "MissingRequiredRouteFact";
    public const string RouteFactParityMismatch = "RouteFactParityMismatch";
    public const string RouteFactOutsideOwner = "RouteFactOutsideOwner";
    public const string MissingOperationGuard = "MissingOperationGuard";
    public const string ExternalApiCostExposure = "ExternalApiCostExposure";
    public const string StateAccessRisk = "StateAccessRisk";
    public const string ValueDomainMismatch = "ValueDomainMismatch";
    public const string NonFinitePolicyMismatch = "NonFinitePolicyMismatch";
    public const string ConstantProvenanceMismatch = "ConstantProvenanceMismatch";
    public const string NearEqualConstantGroup = "NearEqualConstantGroup";
    public const string CadenceBoundaryMismatch = "CadenceBoundaryMismatch";
    public const string OperationShapeMismatch = "OperationShapeMismatch";
    public const string DuplicateOperationShapeKey = "DuplicateOperationShapeKey";
}

public static class NonFinitePolicyAction
{
    public const string Allow = "Allow";
    public const string Reject = "Reject";
    public const string ClampToMinimum = "ClampToMinimum";
    public const string ClampToMaximum = "ClampToMaximum";
    public const string UseNeutral = "UseNeutral";
    public const string CallerOwned = "CallerOwned";

    public static readonly string[] All =
    {
        Allow,
        Reject,
        ClampToMinimum,
        ClampToMaximum,
        UseNeutral,
        CallerOwned,
    };
}

public static class ContractSeverity
{
    public const string Info = "Info";
    public const string Warning = "Warning";
    public const string Error = "Error";
}

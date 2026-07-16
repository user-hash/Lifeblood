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
    public OperationGuardContract[] OperationGuards { get; init; } = Array.Empty<OperationGuardContract>();
    public ExternalApiCostContract[] ExternalApiCosts { get; init; } = Array.Empty<ExternalApiCostContract>();
    public ValueDomainContract[] ValueDomains { get; init; } = Array.Empty<ValueDomainContract>();
    public ContractSuppression[] Suppressions { get; init; } = Array.Empty<ContractSuppression>();
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
    public required string AnnotationSource { get; init; }
    public string? AppliesToVersion { get; init; }
    public string Severity { get; init; } = ContractSeverity.Warning;
    public string? Message { get; init; }
    public string? Guidance { get; init; }
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
    public ContractFinding[] Findings { get; init; } = Array.Empty<ContractFinding>();
    public string[] Limitations { get; init; } = Array.Empty<string>();
}

public sealed class ContractRuleBreakdown
{
    public required string RuleId { get; init; }
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
    public required OperationSourceSpan Source { get; init; }
    public ContractEvidence[] Evidence { get; init; } = Array.Empty<ContractEvidence>();
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
    public const string OperationGuard = "operation-guard";
    public const string ExternalApiCost = "external-api-cost";
    public const string ValueDomain = "value-domain";
}

public static class ContractFindingKind
{
    public const string MissingOperationGuard = "MissingOperationGuard";
    public const string ExternalApiCostExposure = "ExternalApiCostExposure";
    public const string ValueDomainMismatch = "ValueDomainMismatch";
    public const string NonFinitePolicyMismatch = "NonFinitePolicyMismatch";
    public const string ConstantProvenanceMismatch = "ConstantProvenanceMismatch";
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

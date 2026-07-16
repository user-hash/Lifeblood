using System.Security.Cryptography;
using System.Text;
using Lifeblood.Domain.Results;

namespace Lifeblood.Analysis;

/// <summary>
/// Per-request, stateless-between-requests semantic contract evaluator. One
/// operation stream fans out to every selected rule family; only a bounded,
/// deterministically ordered finding set is retained. INV-CONTRACT-AUDIT-001.
/// </summary>
public sealed class ContractAuditEngine
{
    public const int MaximumFacts = 250_000;
    public const int MaximumFindings = 1_000;
    public const int MaximumEvidencePerFinding = 32;
    private const int SummaryFindingLimit = 25;

    private readonly ContractManifest _manifest;
    private readonly OperationGuardContract[] _guardContracts;
    private readonly ExternalApiCostContract[] _costContracts;
    private readonly ContractSuppression[] _suppressions;
    private readonly SortedSet<ContractFinding> _findings = new(FindingComparer.Instance);
    private readonly Dictionary<string, RuleCounts> _counts = new(StringComparer.Ordinal);
    private readonly int _findingLimit;
    private readonly int _evidenceLimit;
    private bool _completed;
    private int _findingCount;
    private int _suppressedCount;

    public ContractAuditEngine(ContractAuditRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _manifest = request.Manifest ?? throw new ArgumentException("A contract manifest is required.", nameof(request));
        ValidateManifest(_manifest);

        (_guardContracts, _costContracts) = SelectContracts(_manifest, request.IncludeRuleIds);
        if (_guardContracts.Length == 0 && _costContracts.Length == 0)
            throw new ArgumentException("The contract audit request did not select any manifest contracts.", nameof(request));
        _suppressions = _manifest.Suppressions ?? Array.Empty<ContractSuppression>();
        _findingLimit = request.Summarize
            ? Math.Min(SummaryFindingLimit, Clamp(request.MaxFindings, 1, MaximumFindings))
            : Clamp(request.MaxFindings, 1, MaximumFindings);
        _evidenceLimit = request.Summarize
            ? 0
            : Clamp(request.MaxEvidencePerFinding, 1, MaximumEvidencePerFinding);

        var targets = _guardContracts.SelectMany(contract => contract.TargetSymbolIds)
            .Concat(_costContracts.SelectMany(contract => contract.TargetSymbolIds))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var kinds = _guardContracts.Length > 0
            ? new[] { OperationFactKind.Call, OperationFactKind.ObjectCreation }
                .Concat(_costContracts.SelectMany(contract => contract.OperationKinds))
            : _costContracts.SelectMany(contract => contract.OperationKinds);

        Query = new OperationFactQuery
        {
            ProfileScope = NullIfWhiteSpace(request.ProfileScope),
            ModuleScope = NullIfWhiteSpace(request.ModuleScope),
            FilePaths = NormalizeOptional(request.FilePaths),
            ContainingSymbolIds = NormalizeOptional(request.ContainingSymbolIds),
            TargetSymbolIds = targets,
            IncludeKinds = kinds
                .Distinct(StringComparer.Ordinal)
                .OrderBy(kind => kind, StringComparer.Ordinal)
                .ToArray(),
            MaxFacts = Clamp(request.MaxFacts, 1, MaximumFacts),
        };
    }

    /// <summary>The exact bounded fact request derived from the selected contracts.</summary>
    public OperationFactQuery Query { get; }

    /// <summary>Consumes one neutral fact. Returns true so the provider owns the fact cap.</summary>
    public bool Observe(OperationFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (_completed)
            throw new InvalidOperationException("A completed contract audit cannot consume more facts.");

        EvaluateGuards(fact);
        EvaluateExternalCosts(fact);
        return true;
    }

    public ContractAuditReport Complete(OperationFactScanReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (_completed)
            throw new InvalidOperationException("A contract audit can be completed only once.");
        _completed = true;

        var selectedRules = _guardContracts.Select(_ => ContractRuleId.OperationGuard)
            .Concat(_costContracts.Select(_ => ContractRuleId.ExternalApiCost))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var selectedContracts = _guardContracts.Select(contract => contract.Id)
            .Concat(_costContracts.Select(contract => contract.Id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        return new ContractAuditReport
        {
            Status = receipt.Status,
            ManifestId = _manifest.Id,
            ManifestVersion = _manifest.Version,
            ManifestSchemaVersion = _manifest.SchemaVersion,
            SelectedRuleIds = selectedRules,
            SelectedContractIds = selectedContracts,
            ScanReceipt = receipt,
            FindingCount = _findingCount,
            ReturnedFindingCount = _findings.Count,
            SuppressedFindingCount = _suppressedCount,
            Truncated = receipt.Truncated || _findingCount > _findings.Count,
            RuleBreakdown = selectedRules.Select(ruleId =>
            {
                _counts.TryGetValue(ruleId, out var count);
                return new ContractRuleBreakdown
                {
                    RuleId = ruleId,
                    FindingCount = count.Findings,
                    SuppressedFindingCount = count.Suppressed,
                };
            }).ToArray(),
            Findings = _findings.ToArray(),
            Limitations = BuildLimitations(receipt),
        };
    }

    private void EvaluateGuards(OperationFact fact)
    {
        if (fact.TargetSymbolId == null) return;

        foreach (var contract in _guardContracts)
        {
            if (!Contains(contract.TargetSymbolIds, fact.TargetSymbolId)) continue;

            var argument = fact.Inputs.FirstOrDefault(input =>
                input.Role == OperationInputRole.Argument
                && input.Ordinal == contract.ArgumentOrdinal);
            if (argument != null && IsAllowed(contract, argument, fact.ControlContexts))
                continue;

            var evidence = new List<ContractEvidence>
            {
                new()
                {
                    Kind = "BoundTarget",
                    Summary = fact.TargetSymbolId,
                    SymbolIds = new[] { fact.TargetSymbolId },
                    Source = fact.Source,
                },
            };
            if (argument == null)
            {
                evidence.Add(new ContractEvidence
                {
                    Kind = "MissingArgument",
                    Summary = $"No bound argument exists at ordinal {contract.ArgumentOrdinal}.",
                    Source = fact.Source,
                });
            }
            else
            {
                evidence.Add(new ContractEvidence
                {
                    Kind = "ArgumentValue",
                    Summary = DescribeValue(argument.Value),
                    SymbolIds = argument.Value.SourceSymbolIds,
                    Source = fact.Source,
                });
            }
            evidence.AddRange(fact.ControlContexts.Select(context => new ContractEvidence
            {
                Kind = "ControlContext",
                Summary = context.Condition == null ? context.Kind : $"{context.Kind}: {context.Condition}",
                Source = context.Source,
            }));

            AddFinding(new ContractFinding
            {
                Id = FindingId(ContractRuleId.OperationGuard, contract.Id, fact.Id),
                Kind = ContractFindingKind.MissingOperationGuard,
                RuleId = ContractRuleId.OperationGuard,
                ContractId = contract.Id,
                Severity = contract.Severity,
                Confidence = ConfidenceBand.Advisory,
                Message = contract.Message
                    ?? $"Argument {contract.ArgumentOrdinal} passed to '{fact.TargetSymbolId}' lacks a manifest-declared safety signal.",
                Guidance = contract.Guidance,
                FactId = fact.Id,
                ContainingSymbolId = fact.ContainingSymbolId,
                TargetSymbolId = fact.TargetSymbolId,
                Source = fact.Source,
                Evidence = BoundEvidence(evidence),
            });
        }
    }

    private void EvaluateExternalCosts(OperationFact fact)
    {
        if (fact.TargetSymbolId == null) return;

        foreach (var contract in _costContracts)
        {
            if (!Contains(contract.TargetSymbolIds, fact.TargetSymbolId)
                || !Contains(contract.OperationKinds, fact.Kind)
                || !CostContextMatches(contract, fact))
                continue;

            var evidence = new List<ContractEvidence>
            {
                new()
                {
                    Kind = "ConsumerAnnotation",
                    Summary = contract.AppliesToVersion == null
                        ? contract.AnnotationSource
                        : $"{contract.AnnotationSource}; applies to {contract.AppliesToVersion}",
                },
                new()
                {
                    Kind = "BoundOccurrence",
                    Summary = $"{fact.Kind} {fact.TargetSymbolId}",
                    SymbolIds = new[] { fact.TargetSymbolId },
                    Source = fact.Source,
                },
            };
            evidence.AddRange(fact.ControlContexts.Select(context => new ContractEvidence
            {
                Kind = "ControlContext",
                Summary = context.Condition == null ? context.Kind : $"{context.Kind}: {context.Condition}",
                Source = context.Source,
            }));

            AddFinding(new ContractFinding
            {
                Id = FindingId(ContractRuleId.ExternalApiCost, contract.Id, fact.Id),
                Kind = ContractFindingKind.ExternalApiCostExposure,
                RuleId = ContractRuleId.ExternalApiCost,
                ContractId = contract.Id,
                Severity = contract.Severity,
                Confidence = ConfidenceBand.Proven,
                Categories = contract.Categories.OrderBy(category => category, StringComparer.Ordinal).ToArray(),
                Message = contract.Message
                    ?? $"Cost-annotated API '{fact.TargetSymbolId}' occurs in a manifest-selected execution context.",
                Guidance = contract.Guidance,
                FactId = fact.Id,
                ContainingSymbolId = fact.ContainingSymbolId,
                TargetSymbolId = fact.TargetSymbolId,
                Source = fact.Source,
                Evidence = BoundEvidence(evidence),
            });
        }
    }

    private static bool IsAllowed(
        OperationGuardContract contract,
        OperationInputFact argument,
        IReadOnlyList<OperationControlContext> contexts)
        => Contains(contract.AllowedValueKinds, argument.Value.Kind)
            || argument.Value.SourceSymbolIds.Any(id => Contains(contract.AllowedSourceSymbolIds, id))
            || contexts.Any(context => ControlContextAllows(contract, argument.Value, context));

    private static bool ControlContextAllows(
        OperationGuardContract contract,
        OperationValueFact argument,
        OperationControlContext context)
    {
        if (!Contains(contract.AllowedControlContextKinds, context.Kind)) return false;
        if (contract.AllowedControlOperators.Length > 0)
        {
            return context.Predicates.Any(predicate =>
                Contains(contract.AllowedControlOperators, predicate.Operator)
                && (!contract.RequireArgumentSourceInControlContext
                    || argument.SourceSymbolIds.Any(id =>
                        predicate.SourceSymbolIds.Contains(id, StringComparer.Ordinal))));
        }

        if (!contract.RequireArgumentSourceInControlContext) return true;
        if (argument.SourceSymbolIds.Length == 0 || context.ConditionValue == null) return false;
        return argument.SourceSymbolIds.Any(id =>
            context.ConditionValue.SourceSymbolIds.Contains(id, StringComparer.Ordinal));
    }

    private static bool CostContextMatches(ExternalApiCostContract contract, OperationFact fact)
        => contract.ReportEveryOccurrence
            || Contains(contract.ContainingSymbolIds, fact.ContainingSymbolId)
            || fact.ControlContexts.Any(context => Contains(contract.ControlContextKinds, context.Kind));

    private void AddFinding(ContractFinding finding)
    {
        if (IsSuppressed(finding))
        {
            _suppressedCount++;
            Increment(finding.RuleId, suppressed: true);
            return;
        }

        _findingCount++;
        Increment(finding.RuleId, suppressed: false);
        _findings.Add(finding);
        if (_findings.Count > _findingLimit && _findings.Max is { } last)
            _findings.Remove(last);
    }

    private bool IsSuppressed(ContractFinding finding)
        => _suppressions.Any(suppression =>
            SelectorMatches(suppression.RuleIds, finding.RuleId)
            && SelectorMatches(suppression.ContractIds, finding.ContractId)
            && SelectorMatches(suppression.FactIds, finding.FactId)
            && SelectorMatches(suppression.ContainingSymbolIds, finding.ContainingSymbolId)
            && PathSelectorMatches(suppression.FilePaths, finding.Source.FilePath));

    private static bool SelectorMatches(string[] selector, string value)
        => selector.Length == 0 || Contains(selector, value);

    private static bool PathSelectorMatches(string[] selector, string path)
    {
        if (selector.Length == 0) return true;
        var candidate = NormalizePath(path);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return selector.Any(raw =>
        {
            var expected = NormalizePath(raw);
            return string.Equals(candidate, expected, comparison)
                || candidate.EndsWith("/" + expected.TrimStart('/'), comparison);
        });
    }

    private void Increment(string ruleId, bool suppressed)
    {
        _counts.TryGetValue(ruleId, out var counts);
        _counts[ruleId] = suppressed
            ? counts with { Suppressed = counts.Suppressed + 1 }
            : counts with { Findings = counts.Findings + 1 };
    }

    private ContractEvidence[] BoundEvidence(IEnumerable<ContractEvidence> evidence)
        => _evidenceLimit == 0
            ? Array.Empty<ContractEvidence>()
            : evidence.Take(_evidenceLimit).ToArray();

    private string[] BuildLimitations(OperationFactScanReceipt receipt)
    {
        var limitations = new List<string>(receipt.Limitations);
        if (_guardContracts.Length > 0)
        {
            limitations.Add(
                "Operation guards recognize only manifest-declared local value origins and lexical control contexts. " +
                "Interprocedural range proofs and custom validation semantics are not inferred.");
        }
        if (_costContracts.Length > 0)
        {
            limitations.Add(
                "External API cost categories are consumer-authored policy annotations, not runtime measurements; " +
                "Lifeblood proves the bound occurrence and selected lexical context.");
        }
        limitations.Add(
            "Reflection, dynamic dispatch, and string-named invocation are outside the bound operation stream.");
        return limitations.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static (OperationGuardContract[] Guards, ExternalApiCostContract[] Costs) SelectContracts(
        ContractManifest manifest,
        string[]? includeRuleIds)
    {
        var requested = NormalizeOptional(includeRuleIds);
        if (requested is not { Length: > 0 })
            return (manifest.OperationGuards, manifest.ExternalApiCosts);

        var known = manifest.OperationGuards.Select(contract => contract.Id)
            .Concat(manifest.ExternalApiCosts.Select(contract => contract.Id))
            .Append(ContractRuleId.OperationGuard)
            .Append(ContractRuleId.ExternalApiCost)
            .ToHashSet(StringComparer.Ordinal);
        var unknown = requested.Where(id => !known.Contains(id)).ToArray();
        if (unknown.Length > 0)
            throw new ArgumentException("Unknown contract audit selectors: " + string.Join(", ", unknown));

        return (
            manifest.OperationGuards.Where(contract =>
                requested.Contains(ContractRuleId.OperationGuard, StringComparer.Ordinal)
                || requested.Contains(contract.Id, StringComparer.Ordinal)).ToArray(),
            manifest.ExternalApiCosts.Where(contract =>
                requested.Contains(ContractRuleId.ExternalApiCost, StringComparer.Ordinal)
                || requested.Contains(contract.Id, StringComparer.Ordinal)).ToArray());
    }

    private static void ValidateManifest(ContractManifest manifest)
    {
        if (!string.Equals(manifest.SchemaVersion, ContractManifest.CurrentSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Unsupported contract manifest schema '{manifest.SchemaVersion}'. Expected '{ContractManifest.CurrentSchemaVersion}'.");
        RequireText(manifest.Id, "Manifest id");
        RequireText(manifest.Version, "Manifest version");

        var guards = manifest.OperationGuards ?? throw new ArgumentException("operationGuards cannot be null.");
        var costs = manifest.ExternalApiCosts ?? throw new ArgumentException("externalApiCosts cannot be null.");
        var suppressions = manifest.Suppressions ?? throw new ArgumentException("suppressions cannot be null.");
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var contract in guards)
        {
            ValidateContractIdentity(contract.Id, ids);
            RequireValues(contract.TargetSymbolIds, $"Operation guard '{contract.Id}' targetSymbolIds");
            RequireNonNull(contract.AllowedValueKinds, $"Operation guard '{contract.Id}' allowedValueKinds");
            RequireNonNull(contract.AllowedSourceSymbolIds, $"Operation guard '{contract.Id}' allowedSourceSymbolIds");
            RequireNonNull(contract.AllowedControlContextKinds, $"Operation guard '{contract.Id}' allowedControlContextKinds");
            RequireNonNull(contract.AllowedControlOperators, $"Operation guard '{contract.Id}' allowedControlOperators");
            if (contract.ArgumentOrdinal < 0)
                throw new ArgumentException($"Operation guard '{contract.Id}' argumentOrdinal cannot be negative.");
            RequireText(contract.Severity, $"Operation guard '{contract.Id}' severity");
        }

        foreach (var contract in costs)
        {
            ValidateContractIdentity(contract.Id, ids);
            RequireValues(contract.TargetSymbolIds, $"External API cost '{contract.Id}' targetSymbolIds");
            RequireValues(contract.OperationKinds, $"External API cost '{contract.Id}' operationKinds");
            RequireValues(contract.Categories, $"External API cost '{contract.Id}' categories");
            RequireNonNull(contract.ControlContextKinds, $"External API cost '{contract.Id}' controlContextKinds");
            RequireNonNull(contract.ContainingSymbolIds, $"External API cost '{contract.Id}' containingSymbolIds");
            RequireText(contract.AnnotationSource, $"External API cost '{contract.Id}' annotationSource");
            RequireText(contract.Severity, $"External API cost '{contract.Id}' severity");
            if (!contract.ReportEveryOccurrence
                && contract.ControlContextKinds.Length == 0
                && contract.ContainingSymbolIds.Length == 0)
            {
                throw new ArgumentException(
                    $"External API cost '{contract.Id}' must report every occurrence or select a control/containing-symbol context.");
            }
        }

        foreach (var suppression in suppressions)
        {
            RequireText(suppression.Id, "Suppression id");
            RequireText(suppression.Reason, $"Suppression '{suppression.Id}' reason");
            RequireNonNull(suppression.RuleIds, $"Suppression '{suppression.Id}' ruleIds");
            RequireNonNull(suppression.ContractIds, $"Suppression '{suppression.Id}' contractIds");
            RequireNonNull(suppression.FactIds, $"Suppression '{suppression.Id}' factIds");
            RequireNonNull(suppression.ContainingSymbolIds, $"Suppression '{suppression.Id}' containingSymbolIds");
            RequireNonNull(suppression.FilePaths, $"Suppression '{suppression.Id}' filePaths");
            if (suppression.RuleIds.Length == 0
                && suppression.ContractIds.Length == 0
                && suppression.FactIds.Length == 0
                && suppression.ContainingSymbolIds.Length == 0
                && suppression.FilePaths.Length == 0)
            {
                throw new ArgumentException($"Suppression '{suppression.Id}' must declare at least one selector.");
            }
        }
    }

    private static void ValidateContractIdentity(string id, HashSet<string> ids)
    {
        RequireText(id, "Contract id");
        if (!ids.Add(id))
            throw new ArgumentException($"Duplicate contract id '{id}'.");
    }

    private static void RequireText(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException(label + " is required.");
    }

    private static void RequireValues(string[] values, string label)
    {
        if (values == null || values.Length == 0 || values.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException(label + " must contain non-empty values.");
    }

    private static void RequireNonNull(string[] values, string label)
    {
        if (values == null)
            throw new ArgumentException(label + " cannot be null.");
    }

    private static string DescribeValue(OperationValueFact value)
    {
        var expression = string.IsNullOrWhiteSpace(value.Expression) ? "<no source expression>" : value.Expression;
        return $"{value.Kind}: {expression}";
    }

    private static string FindingId(string ruleId, string contractId, string factId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ruleId + "\n" + contractId + "\n" + factId));
        return "finding_" + Convert.ToHexString(bytes).ToLowerInvariant()[..24];
    }

    private static int Clamp(int value, int minimum, int maximum)
        => Math.Clamp(value, minimum, maximum);

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string[]? NormalizeOptional(string[]? values)
    {
        if (values is not { Length: > 0 }) return null;
        var normalized = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        return normalized.Length == 0 ? null : normalized;
    }

    private static string NormalizePath(string value) => value.Replace('\\', '/').Trim();

    private static bool Contains(string[] values, string candidate)
        => values.Contains(candidate, StringComparer.Ordinal);

    private readonly record struct RuleCounts(int Findings, int Suppressed);

    private sealed class FindingComparer : IComparer<ContractFinding>
    {
        public static FindingComparer Instance { get; } = new();

        public int Compare(ContractFinding? left, ContractFinding? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;

            var result = string.CompareOrdinal(NormalizePath(left.Source.FilePath), NormalizePath(right.Source.FilePath));
            if (result != 0) return result;
            result = left.Source.Line.CompareTo(right.Source.Line);
            if (result != 0) return result;
            result = left.Source.Column.CompareTo(right.Source.Column);
            if (result != 0) return result;
            result = string.CompareOrdinal(left.RuleId, right.RuleId);
            if (result != 0) return result;
            result = string.CompareOrdinal(left.ContractId, right.ContractId);
            if (result != 0) return result;
            result = string.CompareOrdinal(left.FactId, right.FactId);
            return result != 0 ? result : string.CompareOrdinal(left.Id, right.Id);
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Globalization;
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
    private const int MaximumRouteMatchesPerFinding = 32;

    private readonly ContractManifest _manifest;
    private readonly RouteFactContract[] _routeFactContracts;
    private readonly OperationGuardContract[] _guardContracts;
    private readonly ExternalApiCostContract[] _costContracts;
    private readonly StateAccessContract[] _stateContracts;
    private readonly IReadOnlyDictionary<string, StateAccessContract> _stateContractsById;
    private readonly ValueDomainContract[] _domainContracts;
    private readonly OperationShapeContract[] _shapeContracts;
    private readonly ContractSuppression[] _suppressions;
    private readonly ContractCallRouteReceipt[] _callRouteReceipts;
    private readonly ContractCallRouteMatch[] _callRouteMatches;
    private readonly IReadOnlyDictionary<string, ContractCallRouteMatch[]> _callRouteMatchesByContaining;
    private readonly ContractStatePlanReceipt[] _statePlanReceipts;
    private readonly ContractStateMember[] _stateMembers;
    private readonly IReadOnlyDictionary<string, ContractStateMember[]> _stateMembersBySymbol;
    private readonly Dictionary<(string ContractId, string SymbolId), StateObservation> _stateObservations = new();
    private ContractStateAccessReceipt[] _stateAccessReceipts = Array.Empty<ContractStateAccessReceipt>();
    private readonly SortedSet<ContractFinding> _findings = new(FindingComparer.Instance);
    private readonly Dictionary<string, RuleCounts> _counts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContractCounts> _contractCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<NearEqualConstantObservation>> _nearEqualObservations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<OperationShapeUniqueObservation>> _shapeUniqueObservations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<RouteFactObservation>> _routeFactObservations =
        new(StringComparer.Ordinal);
    private ContractRouteFactReceipt[] _routeFactReceipts = Array.Empty<ContractRouteFactReceipt>();
    private readonly int _findingLimit;
    private readonly int _evidenceLimit;
    private bool _completed;
    private int _findingCount;
    private int _suppressedCount;

    public ContractAuditEngine(ContractAuditRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _manifest = request.Manifest ?? throw new ArgumentException("A contract manifest is required.", nameof(request));
        ContractManifestValidator.Validate(_manifest);

        (_routeFactContracts, _guardContracts, _costContracts, _stateContracts, _domainContracts, _shapeContracts) =
            SelectContracts(_manifest, request.IncludeRuleIds);
        if (_routeFactContracts.Length == 0
            && _guardContracts.Length == 0
            && _costContracts.Length == 0
            && _stateContracts.Length == 0
            && _domainContracts.Length == 0
            && _shapeContracts.Length == 0)
            throw new ArgumentException("The contract audit request did not select any manifest contracts.", nameof(request));
        _suppressions = _manifest.Suppressions ?? Array.Empty<ContractSuppression>();
        (_callRouteReceipts, _callRouteMatches) = SelectCallRoutePlan(
            request.CallRoutePlan,
            _routeFactContracts.SelectMany(contract => contract.CallRouteIds)
                .Concat(_costContracts.SelectMany(contract => contract.CallRouteIds))
                .Concat(_stateContracts.SelectMany(contract => contract.CallRouteIds)));
        _callRouteMatchesByContaining = _callRouteMatches
            .GroupBy(match => match.ContainingSymbolId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.Ordinal);
        (_statePlanReceipts, _stateMembers) = SelectStatePlan(
            request.StatePlan,
            _stateContracts.Select(contract => contract.Id));
        _stateContractsById = _stateContracts.ToDictionary(contract => contract.Id, StringComparer.Ordinal);
        _stateMembersBySymbol = _stateMembers
            .GroupBy(member => member.SymbolId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.Ordinal);
        _findingLimit = request.Summarize
            ? Math.Min(SummaryFindingLimit, Clamp(request.MaxFindings, 1, MaximumFindings))
            : Clamp(request.MaxFindings, 1, MaximumFindings);
        _evidenceLimit = request.Summarize
            ? 0
            : Clamp(request.MaxEvidencePerFinding, 1, MaximumEvidencePerFinding);

        var targets = _guardContracts.SelectMany(contract => contract.TargetSymbolIds)
            .Concat(_routeFactContracts.SelectMany(contract => contract.TargetSymbolIds))
            .Concat(_costContracts.SelectMany(contract => contract.TargetSymbolIds))
            .Concat(_domainContracts.SelectMany(contract => contract.TargetSymbolIds))
            .Concat(_shapeContracts.SelectMany(contract => contract.TargetSymbolIds))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var kinds = _routeFactContracts.SelectMany(contract => contract.OperationKinds)
            .Concat(_guardContracts.Length > 0
                ? new[] { OperationFactKind.Call, OperationFactKind.ObjectCreation }
                : Array.Empty<string>())
            .Concat(_costContracts.SelectMany(contract => contract.OperationKinds))
            .Concat(_stateContracts.SelectMany(_ => new[]
            {
                OperationFactKind.MemberRead,
                OperationFactKind.MemberWrite,
                OperationFactKind.ElementAccess,
            }))
            .Concat(_domainContracts.SelectMany(contract => contract.OperationKinds))
            .Concat(_shapeContracts.SelectMany(contract => contract.OperationKinds));
        var everyContractHasBoundTargets = _stateContracts.Length == 0
            && _routeFactContracts.All(contract => !contract.MatchAnyTarget)
            && _costContracts.All(contract => !contract.MatchAnyTarget)
            && _shapeContracts.All(contract => contract.TargetSymbolIds.Length > 0);

        Query = new OperationFactQuery
        {
            ProfileScope = NullIfWhiteSpace(request.ProfileScope),
            ModuleScope = NullIfWhiteSpace(request.ModuleScope),
            FilePaths = NormalizeOptional(request.FilePaths),
            ContainingSymbolIds = NormalizeOptional(request.ContainingSymbolIds),
            TargetSymbolIds = everyContractHasBoundTargets ? targets : null,
            IncludeKinds = kinds
                .Distinct(StringComparer.Ordinal)
                .OrderBy(kind => kind, StringComparer.Ordinal)
                .ToArray(),
            Selectors = BuildFactSelectors(),
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
        EvaluateRouteFacts(fact);
        EvaluateExternalCosts(fact);
        EvaluateStateAccess(fact);
        EvaluateValueDomains(fact);
        EvaluateOperationShapes(fact);
        return true;
    }

    public ContractAuditReport Complete(OperationFactScanReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (_completed)
            throw new InvalidOperationException("A contract audit can be completed only once.");
        _completed = true;
        EvaluateRouteFactResults(receipt.Truncated);
        EvaluateStateAccessResults(receipt.Truncated);
        EvaluateNearEqualConstants();
        EvaluateOperationShapeUniqueness();

        var selectedRules = _routeFactContracts.Select(_ => ContractRuleId.RouteFact)
            .Concat(_guardContracts.Select(_ => ContractRuleId.OperationGuard))
            .Concat(_costContracts.Select(_ => ContractRuleId.ExternalApiCost))
            .Concat(_stateContracts.Select(_ => ContractRuleId.StateAccess))
            .Concat(_domainContracts.Select(_ => ContractRuleId.ValueDomain))
            .Concat(_shapeContracts.Select(_ => ContractRuleId.OperationShape))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var selectedContracts = _routeFactContracts.Select(contract => contract.Id)
            .Concat(_guardContracts.Select(contract => contract.Id))
            .Concat(_costContracts.Select(contract => contract.Id))
            .Concat(_stateContracts.Select(contract => contract.Id))
            .Concat(_domainContracts.Select(contract => contract.Id))
            .Concat(_shapeContracts.Select(contract => contract.Id))
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
            Truncated = receipt.Truncated
                || _findingCount > _findings.Count
                || _callRouteReceipts.Any(route => route.Truncated)
                || _statePlanReceipts.Any(state => state.Truncated),
            RuleBreakdown = selectedRules.Select(ruleId =>
            {
                _counts.TryGetValue(ruleId, out var count);
                return new ContractRuleBreakdown
                {
                    RuleId = ruleId,
                    FindingCount = count.Findings,
                    SuppressedFindingCount = count.Suppressed,
                    Contracts = SelectedContractIds(ruleId).Select(contractId =>
                    {
                        _contractCounts.TryGetValue(contractId, out var contractCount);
                        return new ContractEvaluationBreakdown
                        {
                            ContractId = contractId,
                            EvaluatedOccurrenceCount = contractCount.EvaluatedOccurrences,
                            FindingFreeOccurrenceCount = contractCount.FindingFreeOccurrences,
                            FindingCount = contractCount.Findings,
                            SuppressedFindingCount = contractCount.Suppressed,
                        };
                    }).ToArray(),
                };
            }).ToArray(),
            CallRoutes = _callRouteReceipts,
            RouteFacts = _routeFactReceipts,
            StateAccesses = _stateAccessReceipts,
            Findings = _findings.ToArray(),
            Limitations = BuildLimitations(receipt),
        };
    }

    private void EvaluateRouteFacts(OperationFact fact)
    {
        foreach (var contract in _routeFactContracts)
        {
            if (!RouteFactContractRule.Selects(contract, fact)) continue;
            var routeMatches = CallRouteMatches(contract.CallRouteIds, fact.ContainingSymbolId);
            if (!string.Equals(contract.Policy, RouteFactPolicy.AllowedRoutesOnly, StringComparison.Ordinal)
                && routeMatches.Length == 0)
                continue;

            if (!_routeFactObservations.TryGetValue(contract.Id, out var observations))
            {
                observations = new List<RouteFactObservation>();
                _routeFactObservations.Add(contract.Id, observations);
            }
            observations.Add(new RouteFactObservation(
                fact,
                routeMatches,
                string.Equals(contract.Policy, RouteFactPolicy.EquivalentAcrossRoutes, StringComparison.Ordinal)
                    ? RouteFactContractRule.Signature(contract, fact)
                    : null));
        }
    }

    private void EvaluateRouteFactResults(bool scanTruncated)
    {
        var receipts = new List<ContractRouteFactReceipt>(_routeFactContracts.Length);
        foreach (var contract in _routeFactContracts.OrderBy(candidate => candidate.Id, StringComparer.Ordinal))
        {
            var observations = (_routeFactObservations.TryGetValue(contract.Id, out var retained)
                    ? retained
                    : new List<RouteFactObservation>())
                .OrderBy(observation => NormalizePath(observation.Fact.Source.FilePath), StringComparer.Ordinal)
                .ThenBy(observation => observation.Fact.Source.Line)
                .ThenBy(observation => observation.Fact.Source.Column)
                .ThenBy(observation => observation.Fact.Id, StringComparer.Ordinal)
                .ToArray();
            var routeReceipts = contract.CallRouteIds
                .Select(RouteReceipt)
                .ToArray();
            var incomplete = scanTruncated || routeReceipts.Any(route => route.Truncated);
            var signatures = observations
                .Where(observation => observation.Signature != null)
                .GroupBy(observation => observation.Signature!.Key, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToArray();

            if (string.Equals(contract.Policy, RouteFactPolicy.RequiredOnEveryRoute, StringComparison.Ordinal))
                EvaluateRequiredRouteFacts(contract, observations, incomplete);
            else if (string.Equals(contract.Policy, RouteFactPolicy.EquivalentAcrossRoutes, StringComparison.Ordinal))
                EvaluateEquivalentRouteFacts(contract, signatures, incomplete);
            else
                EvaluateAllowedRouteFacts(contract, observations, incomplete);

            var outsideCount = observations.Count(observation => observation.RouteMatches.Length == 0);
            receipts.Add(new ContractRouteFactReceipt
            {
                ContractId = contract.Id,
                Policy = contract.Policy,
                SelectedFactCount = observations.Length,
                SignatureCount = signatures.Length,
                Incomplete = incomplete,
                Routes = contract.CallRouteIds.Select(routeId =>
                {
                    var routeObservations = observations
                        .Where(observation => HasRoute(observation, routeId))
                        .ToArray();
                    var routeSignatureCount = routeObservations
                        .Where(observation => observation.Signature != null)
                        .Select(observation => observation.Signature!.Key)
                        .Distinct(StringComparer.Ordinal)
                        .Count();
                    var satisfied = contract.Policy switch
                    {
                        RouteFactPolicy.RequiredOnEveryRoute => routeObservations.Length > 0,
                        RouteFactPolicy.EquivalentAcrossRoutes => signatures.Length > 0
                            && routeSignatureCount == signatures.Length,
                        RouteFactPolicy.AllowedRoutesOnly => outsideCount == 0,
                        _ => false,
                    };
                    return new ContractRouteFactRouteReceipt
                    {
                        RouteId = routeId,
                        FactCount = routeObservations.Length,
                        SignatureCount = routeSignatureCount,
                        RequirementSatisfied = satisfied,
                    };
                }).ToArray(),
            });
        }
        _routeFactReceipts = receipts.ToArray();
    }

    private void EvaluateRequiredRouteFacts(
        RouteFactContract contract,
        RouteFactObservation[] observations,
        bool incomplete)
    {
        foreach (var routeId in contract.CallRouteIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            var present = observations.Any(observation => HasRoute(observation, routeId));
            RecordEvaluation(contract.Id, present);
            if (present) continue;

            var root = RouteRoot(routeId);
            var routeMatches = CallRouteMatches(new[] { routeId }, root.SymbolId);
            AddFinding(new ContractFinding
            {
                Id = FindingId(ContractRuleId.RouteFact, contract.Id, $"route:{routeId}:missing"),
                Kind = ContractFindingKind.MissingRequiredRouteFact,
                RuleId = ContractRuleId.RouteFact,
                ContractId = contract.Id,
                Severity = contract.Severity,
                Confidence = incomplete ? ConfidenceBand.Advisory : ConfidenceBand.Proven,
                Categories = RouteFactCategories(contract, "Missing"),
                Message = contract.Message
                    ?? $"Route '{routeId}' has no operation matching required route-fact contract '{contract.Id}'.",
                Guidance = contract.Guidance,
                FactId = $"route:{routeId}:missing",
                ContainingSymbolId = root.SymbolId,
                TargetSymbolId = contract.TargetSymbolIds.Length == 1 ? contract.TargetSymbolIds[0] : null,
                Source = root.Source,
                CallRouteMatchCount = routeMatches.Length,
                CallRouteMatchesTruncated = routeMatches.Length > MaximumRouteMatchesPerFinding,
                CallRouteMatches = BoundRouteMatches(routeMatches),
                Evidence = BoundEvidence(new[]
                {
                    new ContractEvidence
                    {
                        Kind = "RouteFactRequirement",
                        Summary = $"policy={contract.Policy}; kinds=[{string.Join(", ", contract.OperationKinds)}]; " +
                                  $"targets=[{string.Join(", ", contract.TargetSymbolIds)}]",
                    },
                    new ContractEvidence
                    {
                        Kind = "CallRouteRoot",
                        Summary = routeId,
                        SymbolIds = new[] { root.SymbolId },
                        Source = root.Source,
                    },
                }),
            });
        }
    }

    private void EvaluateEquivalentRouteFacts(
        RouteFactContract contract,
        IGrouping<string, RouteFactObservation>[] signatures,
        bool incomplete)
    {
        foreach (var signatureGroup in signatures)
        {
            var signatureObservations = signatureGroup.ToArray();
            var signature = signatureObservations[0].Signature!;
            foreach (var routeId in contract.CallRouteIds.OrderBy(id => id, StringComparer.Ordinal))
            {
                var present = signatureObservations.Any(observation => HasRoute(observation, routeId));
                RecordEvaluation(contract.Id, present);
                if (present) continue;

                var root = RouteRoot(routeId);
                var observedRoutes = contract.CallRouteIds
                    .Where(candidate => signatureObservations.Any(observation => HasRoute(observation, candidate)))
                    .OrderBy(candidate => candidate, StringComparer.Ordinal)
                    .ToArray();
                var routeMatches = CallRouteMatches(new[] { routeId }, root.SymbolId);
                var anchor = signatureObservations[0].Fact;
                var evidence = new List<ContractEvidence>
                {
                    new()
                    {
                        Kind = "RouteFactSignature",
                        Summary = signature.Summary,
                    },
                    new()
                    {
                        Kind = "MissingRoute",
                        Summary = routeId,
                        SymbolIds = new[] { root.SymbolId },
                        Source = root.Source,
                    },
                    new()
                    {
                        Kind = "ObservedRoutes",
                        Summary = string.Join(", ", observedRoutes),
                        SymbolIds = signatureObservations
                            .Select(observation => observation.Fact.ContainingSymbolId)
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(id => id, StringComparer.Ordinal)
                            .ToArray(),
                    },
                };
                evidence.AddRange(signatureObservations.Select(observation => new ContractEvidence
                {
                    Kind = "ObservedRouteFact",
                    Summary = observation.Fact.Kind,
                    SymbolIds = observation.Fact.TargetSymbolId == null
                        ? new[] { observation.Fact.ContainingSymbolId }
                        : new[] { observation.Fact.ContainingSymbolId, observation.Fact.TargetSymbolId },
                    Source = observation.Fact.Source,
                }));
                AddFinding(new ContractFinding
                {
                    Id = FindingId(
                        ContractRuleId.RouteFact,
                        contract.Id,
                        anchor.Id,
                        routeId + ":" + signature.Key),
                    Kind = ContractFindingKind.RouteFactParityMismatch,
                    RuleId = ContractRuleId.RouteFact,
                    ContractId = contract.Id,
                    Severity = contract.Severity,
                    Confidence = incomplete ? ConfidenceBand.Advisory : ConfidenceBand.Proven,
                    Categories = RouteFactCategories(contract, "Parity"),
                    Message = contract.Message
                        ?? $"Route '{routeId}' is missing one operation signature required for parity contract '{contract.Id}'.",
                    Guidance = contract.Guidance,
                    FactId = $"route:{routeId}:signature:{SignatureId(signature.Key)}",
                    ContainingSymbolId = root.SymbolId,
                    TargetSymbolId = anchor.TargetSymbolId,
                    Source = root.Source,
                    CallRouteMatchCount = routeMatches.Length,
                    CallRouteMatchesTruncated = routeMatches.Length > MaximumRouteMatchesPerFinding,
                    CallRouteMatches = BoundRouteMatches(routeMatches),
                    Evidence = BoundEvidence(evidence),
                });
            }
        }
    }

    private void EvaluateAllowedRouteFacts(
        RouteFactContract contract,
        RouteFactObservation[] observations,
        bool incomplete)
    {
        foreach (var observation in observations)
        {
            var allowed = observation.RouteMatches.Length > 0;
            RecordEvaluation(contract.Id, allowed);
            if (allowed) continue;

            var fact = observation.Fact;
            AddFinding(new ContractFinding
            {
                Id = FindingId(ContractRuleId.RouteFact, contract.Id, fact.Id),
                Kind = ContractFindingKind.RouteFactOutsideOwner,
                RuleId = ContractRuleId.RouteFact,
                ContractId = contract.Id,
                Severity = contract.Severity,
                Confidence = incomplete ? ConfidenceBand.Advisory : ConfidenceBand.Proven,
                Categories = RouteFactCategories(contract, "Ownership"),
                Message = contract.Message
                    ?? $"Operation '{fact.TargetSymbolId ?? fact.Kind}' occurs outside every allowed owner route for contract '{contract.Id}'.",
                Guidance = contract.Guidance,
                FactId = fact.Id,
                ContainingSymbolId = fact.ContainingSymbolId,
                TargetSymbolId = fact.TargetSymbolId,
                Source = fact.Source,
                Evidence = BoundEvidence(new[]
                {
                    new ContractEvidence
                    {
                        Kind = "OutsideOwnerRoute",
                        Summary = $"allowedRoutes=[{string.Join(", ", contract.CallRouteIds)}]",
                        SymbolIds = fact.TargetSymbolId == null
                            ? new[] { fact.ContainingSymbolId }
                            : new[] { fact.ContainingSymbolId, fact.TargetSymbolId },
                        Source = fact.Source,
                    },
                }),
            });
        }
    }

    private void EvaluateOperationShapes(OperationFact fact)
    {
        foreach (var contract in _shapeContracts)
        {
            if (!OperationShapeContractRule.Selects(contract, fact)) continue;

            var uniqueObservations = OperationShapeContractRule.CollectUniquenessObservations(contract, fact);
            if (uniqueObservations.Length > 0)
            {
                if (!_shapeUniqueObservations.TryGetValue(contract.Id, out var retained))
                {
                    retained = new List<OperationShapeUniqueObservation>();
                    _shapeUniqueObservations.Add(contract.Id, retained);
                }
                retained.AddRange(uniqueObservations);
            }

            var assessment = OperationShapeContractRule.Evaluate(contract, fact);
            RecordEvaluation(contract.Id, findingFree: assessment == null);
            if (assessment == null) continue;

            var evidence = new List<ContractEvidence>
            {
                new()
                {
                    Kind = "BoundOccurrence",
                    Summary = $"{fact.Kind}; operator={fact.Operator ?? "<none>"}; " +
                              $"resultType={fact.ResultType ?? "<none>"}",
                    SymbolIds = fact.TargetSymbolId == null
                        ? new[] { fact.ContainingSymbolId }
                        : new[] { fact.ContainingSymbolId, fact.TargetSymbolId },
                    Source = fact.Source,
                },
            };
            evidence.AddRange(fact.Inputs.Select(input => new ContractEvidence
            {
                Kind = "OperationInput",
                Summary = $"{input.Role}[{input.Ordinal?.ToString(CultureInfo.InvariantCulture) ?? "any"}]: " +
                          $"{DescribeValue(input.Value)}; type={input.Value.Type ?? "<none>"}; " +
                          $"operators=[{string.Join(", ", input.Value.Operators)}]",
                SymbolIds = input.Value.SourceSymbolIds,
                Source = fact.Source,
            }));
            evidence.AddRange(fact.ControlContexts.Select(context => new ContractEvidence
            {
                Kind = "ControlContext",
                Summary = DescribeControl(context),
                SymbolIds = context.ConditionValue?.SourceSymbolIds ?? Array.Empty<string>(),
                Source = context.Source,
            }));
            evidence.AddRange(assessment.Failures.Select(failure => new ContractEvidence
            {
                Kind = "AllowedShapeMismatch",
                Summary = $"{failure.ShapeId}: {string.Join("; ", failure.Reasons)}",
            }));

            AddFinding(new ContractFinding
            {
                Id = FindingId(ContractRuleId.OperationShape, contract.Id, fact.Id),
                Kind = ContractFindingKind.OperationShapeMismatch,
                RuleId = ContractRuleId.OperationShape,
                ContractId = contract.Id,
                Severity = contract.Severity,
                Confidence = ConfidenceBand.Proven,
                Categories = contract.Categories
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(category => category, StringComparer.Ordinal)
                    .ToArray(),
                Message = contract.Message
                    ?? $"{fact.Kind} occurrence in '{fact.ContainingSymbolId}' does not match any " +
                       $"allowed shape for contract '{contract.Id}'.",
                Guidance = contract.Guidance,
                FactId = fact.Id,
                ContainingSymbolId = fact.ContainingSymbolId,
                TargetSymbolId = fact.TargetSymbolId,
                Source = fact.Source,
                Evidence = BoundEvidence(evidence),
            });
        }
    }

    private void EvaluateOperationShapeUniqueness()
    {
        foreach (var contract in _shapeContracts)
        {
            var policy = contract.UniquenessPolicy;
            if (policy == null
                || !_shapeUniqueObservations.TryGetValue(contract.Id, out var observations))
                continue;

            foreach (var assessment in OperationShapeContractRule.GroupDuplicateKeys(policy, observations))
            {
                var anchor = assessment.Observations[0];
                var evidence = new List<ContractEvidence>
                {
                    new()
                    {
                        Kind = "UniquenessPolicy",
                        Summary = $"{policy.KeyKind} from {policy.InputRole}" +
                                  (policy.InputOrdinal.HasValue ? $"[{policy.InputOrdinal}]" : string.Empty),
                    },
                };
                evidence.AddRange(assessment.Observations.Select(observation => new ContractEvidence
                {
                    Kind = "DuplicateOperationShapeKey",
                    Summary = $"key={assessment.Key}; {observation.Fact.Kind}",
                    SymbolIds = observation.Input.Value.SourceSymbolIds,
                    Source = observation.Fact.Source,
                }));

                AddFinding(new ContractFinding
                {
                    Id = FindingId(
                        ContractRuleId.OperationShape,
                        contract.Id,
                        anchor.Fact.Id,
                        $"duplicate:{policy.KeyKind}:{assessment.Key}"),
                    Kind = ContractFindingKind.DuplicateOperationShapeKey,
                    RuleId = ContractRuleId.OperationShape,
                    ContractId = contract.Id,
                    Severity = contract.Severity,
                    Confidence = ConfidenceBand.Proven,
                    Categories = contract.Categories
                        .Append("Duplicate")
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(category => category, StringComparer.Ordinal)
                        .ToArray(),
                    Message = contract.Message
                        ?? $"Operation-shape key '{assessment.Key}' occurs {assessment.Observations.Length} times " +
                           $"under uniqueness contract '{contract.Id}'.",
                    Guidance = contract.Guidance,
                    FactId = anchor.Fact.Id,
                    ContainingSymbolId = anchor.Fact.ContainingSymbolId,
                    TargetSymbolId = anchor.Fact.TargetSymbolId,
                    Source = anchor.Fact.Source,
                    Evidence = BoundEvidence(evidence),
                });
            }
        }
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
            {
                RecordEvaluation(contract.Id, findingFree: true);
                continue;
            }
            RecordEvaluation(contract.Id, findingFree: false);

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
                Summary = DescribeControl(context),
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
        foreach (var contract in _costContracts)
        {
            var routeMatches = CallRouteMatches(contract.CallRouteIds, fact.ContainingSymbolId);
            var targetMatches = contract.MatchAnyTarget
                || (fact.TargetSymbolId != null && Contains(contract.TargetSymbolIds, fact.TargetSymbolId));
            if (!targetMatches
                || !Contains(contract.OperationKinds, fact.Kind)
                || !CostContextMatches(contract, fact, routeMatches.Length > 0))
                continue;
            RecordEvaluation(contract.Id, findingFree: false);

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
                    Summary = fact.TargetSymbolId == null
                        ? fact.Kind
                        : $"{fact.Kind} {fact.TargetSymbolId}",
                    SymbolIds = fact.TargetSymbolId == null
                        ? new[] { fact.ContainingSymbolId }
                        : new[] { fact.TargetSymbolId },
                    Source = fact.Source,
                },
            };
            evidence.AddRange(fact.ControlContexts.Select(context => new ContractEvidence
            {
                Kind = "ControlContext",
                Summary = DescribeControl(context),
                Source = context.Source,
            }));
            evidence.AddRange(routeMatches.Select(match => new ContractEvidence
            {
                Kind = "CallRoute",
                Summary = $"{match.RouteId}; {match.Placement}; distance={match.Distance}",
                SymbolIds = match.PathSymbolIds,
                Source = fact.Source,
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
                    ?? $"Cost-annotated operation '{fact.TargetSymbolId ?? fact.Kind}' occurs in a manifest-selected execution context.",
                Guidance = contract.Guidance,
                FactId = fact.Id,
                ContainingSymbolId = fact.ContainingSymbolId,
                TargetSymbolId = fact.TargetSymbolId,
                Source = fact.Source,
                CallRouteMatchCount = routeMatches.Length,
                CallRouteMatchesTruncated = routeMatches.Length > MaximumRouteMatchesPerFinding,
                CallRouteMatches = BoundRouteMatches(routeMatches),
                Evidence = BoundEvidence(evidence),
            });
        }
    }

    private void EvaluateStateAccess(OperationFact fact)
    {
        if (_stateContracts.Length == 0
            || fact.Kind is not (OperationFactKind.MemberRead
                or OperationFactKind.MemberWrite
                or OperationFactKind.ElementAccess))
            return;

        var candidates = new Dictionary<(string ContractId, string SymbolId), ContractStateMember>();
        if (fact.TargetSymbolId != null
            && _stateMembersBySymbol.TryGetValue(fact.TargetSymbolId, out var directMembers))
        {
            foreach (var member in directMembers)
                candidates[(member.ContractId, member.SymbolId)] = member;
        }
        if (string.Equals(fact.Kind, OperationFactKind.ElementAccess, StringComparison.Ordinal))
        {
            foreach (var symbolId in fact.Inputs
                         .Where(input => string.Equals(input.Role, OperationInputRole.Receiver, StringComparison.Ordinal))
                         .SelectMany(input => input.Value.SourceSymbolIds)
                         .Distinct(StringComparer.Ordinal))
            {
                if (!_stateMembersBySymbol.TryGetValue(symbolId, out var receiverMembers)) continue;
                foreach (var member in receiverMembers)
                    candidates[(member.ContractId, member.SymbolId)] = member;
            }
        }

        foreach (var (key, member) in candidates)
        {
            var contract = _stateContractsById[key.ContractId];
            var routeMatches = CallRouteMatches(contract.CallRouteIds, fact.ContainingSymbolId);
            var routeAccess = routeMatches.Length > 0;
            var directWrite = string.Equals(fact.Kind, OperationFactKind.MemberWrite, StringComparison.Ordinal)
                && string.Equals(fact.TargetSymbolId, member.SymbolId, StringComparison.Ordinal);
            if (!routeAccess && !directWrite) continue;

            if (!_stateObservations.TryGetValue(key, out var observation))
            {
                observation = new StateObservation(member);
                _stateObservations.Add(key, observation);
            }

            if (directWrite)
            {
                if (IsInitializationOwner(fact.ContainingSymbolId, member.SymbolId))
                    observation.InitializerWrite = Earlier(observation.InitializerWrite, fact);
                else
                    observation.RuntimeWrite = Earlier(observation.RuntimeWrite, fact);
            }
            if (!routeAccess) continue;

            observation.RouteAccessCount++;
            observation.Anchor = Earlier(observation.Anchor, fact);
            foreach (var routeMatch in routeMatches)
                observation.RouteMatches[RouteMatchIdentity(routeMatch)] = routeMatch;
            if (string.Equals(fact.Kind, OperationFactKind.ElementAccess, StringComparison.Ordinal)
                && string.Equals(fact.Operator, OperationAccessMode.Write, StringComparison.Ordinal))
            {
                observation.ElementWrite = Earlier(observation.ElementWrite, fact);
            }
        }
    }

    private void EvaluateStateAccessResults(bool scanTruncated)
    {
        var receipts = new List<ContractStateAccessReceipt>(_stateContracts.Length);
        foreach (var contract in _stateContracts.OrderBy(candidate => candidate.Id, StringComparer.Ordinal))
        {
            var planReceipt = _statePlanReceipts.Single(receipt =>
                string.Equals(receipt.ContractId, contract.Id, StringComparison.Ordinal));
            var observations = _stateObservations.Values
                .Where(observation => observation.RouteAccessCount > 0
                    && string.Equals(observation.Member.ContractId, contract.Id, StringComparison.Ordinal))
                .OrderBy(observation => observation.Member.SymbolId, StringComparer.Ordinal)
                .ToArray();
            var bucketCounts = StateRiskBucket.All.ToDictionary(bucket => bucket, _ => 0, StringComparer.Ordinal);

            foreach (var observation in observations)
            {
                var bucket = ClassifyState(observation, scanTruncated);
                bucketCounts[bucket]++;
                var findingFree = Contains(contract.AllowedRiskBuckets, bucket);
                RecordEvaluation(contract.Id, findingFree);
                if (findingFree) continue;

                var anchor = observation.Anchor!;
                var routeMatches = observation.RouteMatches.Values
                    .OrderBy(match => match.RouteId, StringComparer.Ordinal)
                    .ThenBy(match => match.RootSymbolId, StringComparer.Ordinal)
                    .ThenBy(match => match.Distance)
                    .ThenBy(match => match.ContainingSymbolId, StringComparer.Ordinal)
                    .ToArray();
                AddFinding(new ContractFinding
                {
                    Id = FindingId(
                        ContractRuleId.StateAccess,
                        contract.Id,
                        anchor.Id,
                        observation.Member.SymbolId + ":" + bucket),
                    Kind = ContractFindingKind.StateAccessRisk,
                    RuleId = ContractRuleId.StateAccess,
                    ContractId = contract.Id,
                    Severity = contract.Severity,
                    Confidence = bucket == StateRiskBucket.Unknown || !observation.Member.IsStatic
                        ? ConfidenceBand.Advisory
                        : ConfidenceBand.Proven,
                    Categories = contract.Categories
                        .Append(bucket)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(category => category, StringComparer.Ordinal)
                        .ToArray(),
                    Message = contract.Message
                        ?? $"State member '{observation.Member.SymbolId}' is classified '{bucket}' on the selected call route.",
                    Guidance = contract.Guidance,
                    FactId = anchor.Id,
                    ContainingSymbolId = anchor.ContainingSymbolId,
                    TargetSymbolId = observation.Member.SymbolId,
                    StateRiskBucket = bucket,
                    Source = anchor.Source,
                    CallRouteMatchCount = routeMatches.Length,
                    CallRouteMatchesTruncated = routeMatches.Length > MaximumRouteMatchesPerFinding,
                    CallRouteMatches = BoundRouteMatches(routeMatches),
                    Evidence = BuildStateEvidence(observation, bucket),
                });
            }

            receipts.Add(new ContractStateAccessReceipt
            {
                ContractId = contract.Id,
                CandidateMemberCount = planReceipt.CandidateMemberCount,
                RetainedMemberCount = planReceipt.RetainedMemberCount,
                AccessedMemberCount = observations.Length,
                Truncated = planReceipt.Truncated,
                RiskBuckets = StateRiskBucket.All.Select(bucket => new ContractStateRiskBucketCount
                {
                    RiskBucket = bucket,
                    MemberCount = bucketCounts[bucket],
                }).ToArray(),
            });
        }
        _stateAccessReceipts = receipts.ToArray();
    }

    private ContractEvidence[] BuildStateEvidence(StateObservation observation, string bucket)
    {
        var evidence = new List<ContractEvidence>
        {
            new()
            {
                Kind = "StateDeclaration",
                Summary = $"{observation.Member.MemberKind}; type={observation.Member.ValueType}; " +
                          $"static={observation.Member.IsStatic}; readonly={observation.Member.IsReadOnly}; " +
                          $"const={observation.Member.IsConst}; setter={observation.Member.HasSetter}; " +
                          $"initializer={observation.Member.HasInitializer}",
                SymbolIds = new[] { observation.Member.SymbolId },
                Source = observation.Member.DeclarationSource,
            },
            new()
            {
                Kind = "RouteStateAccess",
                Summary = $"accesses={observation.RouteAccessCount}; " +
                          $"roots={observation.RouteMatches.Values.Select(match => match.RootSymbolId).Distinct(StringComparer.Ordinal).Count()}",
                SymbolIds = observation.RouteMatches.Values
                    .Select(match => match.RootSymbolId)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray(),
                Source = observation.Anchor!.Source,
            },
            new()
            {
                Kind = "StateRiskClassification",
                Summary = bucket,
                SymbolIds = new[] { observation.Member.SymbolId },
            },
        };
        AddWriteEvidence(evidence, "RuntimeWrite", observation.RuntimeWrite);
        AddWriteEvidence(evidence, "InitializerWrite", observation.InitializerWrite);
        AddWriteEvidence(evidence, "ElementWrite", observation.ElementWrite);
        return BoundEvidence(evidence);
    }

    private static void AddWriteEvidence(
        ICollection<ContractEvidence> evidence,
        string kind,
        OperationFact? fact)
    {
        if (fact == null) return;
        evidence.Add(new ContractEvidence
        {
            Kind = kind,
            Summary = $"{fact.Kind}; containing={fact.ContainingSymbolId}",
            SymbolIds = fact.TargetSymbolId == null
                ? new[] { fact.ContainingSymbolId }
                : new[] { fact.TargetSymbolId, fact.ContainingSymbolId },
            Source = fact.Source,
        });
    }

    private static string ClassifyState(StateObservation observation, bool scanTruncated)
    {
        var member = observation.Member;
        if (member.IsStatic && observation.ElementWrite != null)
            return StateRiskBucket.SharedScratch;
        if (observation.RuntimeWrite != null)
            return StateRiskBucket.RuntimeMutable;
        if (scanTruncated)
            return StateRiskBucket.Unknown;
        if (member.IsStatic && (member.IsConst || member.IsReadOnly))
            return StateRiskBucket.ReadonlyTable;
        if (member.IsStatic
            && !member.HasSetter
            && (member.HasInitializer || observation.InitializerWrite != null))
            return StateRiskBucket.InitializedOnceCache;
        return StateRiskBucket.Unknown;
    }

    private void EvaluateValueDomains(OperationFact fact)
    {
        foreach (var contract in _domainContracts)
        {
            if (!ValueDomainContractRule.Selects(contract, fact)) continue;

            var observations = ValueDomainContractRule.CollectNearEqualObservations(contract, fact);
            if (observations.Length > 0)
            {
                if (!_nearEqualObservations.TryGetValue(contract.Id, out var retained))
                {
                    retained = new List<NearEqualConstantObservation>();
                    _nearEqualObservations.Add(contract.Id, retained);
                }
                retained.AddRange(observations);
            }

            var assessments = ValueDomainContractRule.Evaluate(contract, fact);
            RecordEvaluation(contract.Id, findingFree: assessments.Length == 0);
            foreach (var assessment in assessments)
            {
                var evidence = BuildValueDomainEvidence(contract, fact, assessment);
                AddFinding(new ContractFinding
                {
                    Id = FindingId(
                        ContractRuleId.ValueDomain,
                        contract.Id,
                        fact.Id,
                        assessment.FindingKind == ContractFindingKind.ValueDomainMismatch
                            ? null
                            : BoundaryDiscriminator(assessment)),
                    Kind = assessment.FindingKind,
                    RuleId = ContractRuleId.ValueDomain,
                    ContractId = contract.Id,
                    Severity = contract.Severity,
                    Confidence = assessment.Confidence,
                    Categories = ValueDomainCategories(assessment),
                    Message = contract.Message ?? ValueDomainMessage(contract, fact, assessment),
                    Guidance = contract.Guidance,
                    FactId = fact.Id,
                    ContainingSymbolId = fact.ContainingSymbolId,
                    TargetSymbolId = fact.TargetSymbolId,
                    Source = fact.Source,
                    Evidence = BoundEvidence(evidence),
                });
            }
        }
    }

    private void EvaluateNearEqualConstants()
    {
        foreach (var contract in _domainContracts)
        {
            var policy = contract.ConstantPolicy?.NearEqualPolicy;
            if (policy == null
                || !_nearEqualObservations.TryGetValue(contract.Id, out var observations))
                continue;

            foreach (var assessment in ValueDomainContractRule.GroupNearEqualConstants(policy, observations))
            {
                var ordered = assessment.Observations
                    .OrderBy(observation => NormalizePath(observation.Constant.Source.FilePath), StringComparer.Ordinal)
                    .ThenBy(observation => observation.Constant.Source.Line)
                    .ThenBy(observation => observation.Constant.Source.Column)
                    .ThenBy(observation => observation.Fact.Id, StringComparer.Ordinal)
                    .ToArray();
                var anchor = ordered[0];
                var lower = assessment.LowerValue.ToString("G17", CultureInfo.InvariantCulture);
                var upper = assessment.UpperValue.ToString("G17", CultureInfo.InvariantCulture);
                var evidence = new List<ContractEvidence>
                {
                    new()
                    {
                        Kind = "NearEqualPolicy",
                        Summary = $"absoluteTolerance={policy.AbsoluteTolerance.ToString("G17", CultureInfo.InvariantCulture)}, " +
                                  $"relativeTolerance={policy.RelativeTolerance.ToString("G17", CultureInfo.InvariantCulture)}",
                    },
                };
                evidence.AddRange(ordered.Select(observation => new ContractEvidence
                {
                    Kind = "NearEqualConstant",
                    Summary = $"{observation.Constant.Value} in {observation.Fact.Kind}",
                    SymbolIds = observation.Fact.TargetSymbolId == null
                        ? Array.Empty<string>()
                        : new[] { observation.Fact.TargetSymbolId },
                    Source = observation.Constant.Source,
                }));

                AddFinding(new ContractFinding
                {
                    Id = FindingId(
                        ContractRuleId.ValueDomain,
                        contract.Id,
                        anchor.Fact.Id,
                        $"near-equal:{assessment.OperationKind}:{lower}:{upper}"),
                    Kind = ContractFindingKind.NearEqualConstantGroup,
                    RuleId = ContractRuleId.ValueDomain,
                    ContractId = contract.Id,
                    Severity = contract.Severity,
                    Confidence = ConfidenceBand.Proven,
                    Categories = new[] { "ConstantProvenance", "NearEqual" },
                    Message = contract.Message
                        ?? $"Raw numeric literals {lower} and {upper} are within the manifest tolerance for " +
                           $"domain '{contract.TargetDomain}' and operation kind '{assessment.OperationKind}' " +
                           $"across {ordered.Length} occurrences.",
                    Guidance = contract.Guidance,
                    FactId = anchor.Fact.Id,
                    ContainingSymbolId = anchor.Fact.ContainingSymbolId,
                    TargetSymbolId = anchor.Fact.TargetSymbolId,
                    Source = anchor.Constant.Source,
                    Evidence = BoundEvidence(evidence),
                });
            }
        }
    }

    private static List<ContractEvidence> BuildValueDomainEvidence(
        ValueDomainContract contract,
        OperationFact fact,
        ValueDomainAssessment assessment)
    {
        var evidence = new List<ContractEvidence>();
        if (fact.TargetSymbolId != null)
        {
            evidence.Add(new ContractEvidence
            {
                Kind = "BoundTarget",
                Summary = fact.TargetSymbolId,
                SymbolIds = new[] { fact.TargetSymbolId },
                Source = fact.Source,
            });
        }

        if (assessment.Input == null)
        {
            evidence.Add(new ContractEvidence
            {
                Kind = "MissingInput",
                Summary = contract.InputOrdinal.HasValue
                    ? $"No '{contract.InputRole}' input exists at ordinal {contract.InputOrdinal}."
                    : $"No '{contract.InputRole}' input exists.",
                Source = fact.Source,
            });
            return evidence;
        }

        evidence.Add(new ContractEvidence
        {
            Kind = "ValueExpression",
            Summary = DescribeValue(assessment.Input.Value),
            SymbolIds = assessment.Input.Value.SourceSymbolIds,
            Source = fact.Source,
        });
        evidence.Add(new ContractEvidence
        {
            Kind = "ObservedDomains",
            Summary = assessment.IsUnclassified
                ? "No manifest binding classified the value."
                : assessment.ObservedDomains.Length == 0
                    ? "No domain classification was required for this policy finding."
                    : string.Join(", ", assessment.ObservedDomains),
            SymbolIds = assessment.Input.Value.SourceSymbolIds,
            Source = fact.Source,
        });
        if (assessment.Input.Value.Operators.Length > 0)
        {
            evidence.Add(new ContractEvidence
            {
                Kind = "ValueOperators",
                Summary = string.Join(", ", assessment.Input.Value.Operators),
                Source = fact.Source,
            });
        }
        if (assessment.NonFiniteAction != null)
        {
            evidence.Add(new ContractEvidence
            {
                Kind = "NonFinitePolicy",
                Summary = $"Action: {assessment.NonFiniteAction}",
                SymbolIds = contract.NonFinitePolicy?.EvidenceSymbolIds ?? Array.Empty<string>(),
            });
        }
        if (assessment.BoundaryMissing)
        {
            evidence.Add(new ContractEvidence
            {
                Kind = "MissingCadenceBoundary",
                Summary = "No manifest-selected lexical comparison tied the selected input source to the boundary source.",
                SymbolIds = contract.BoundaryPolicy?.BoundarySourceSymbolIds ?? Array.Empty<string>(),
                Source = fact.Source,
            });
        }
        else if (assessment.BoundaryPredicate != null && assessment.BoundaryValue != null)
        {
            evidence.Add(new ContractEvidence
            {
                Kind = "CadenceBoundary",
                Summary = $"{assessment.BoundaryPredicate.Operator}; boundarySide={assessment.BoundarySide}; " +
                          $"boundaryValue={DescribeValue(assessment.BoundaryValue)}",
                SymbolIds = assessment.BoundaryValue.SourceSymbolIds,
                Source = assessment.BoundaryPredicate.Source,
            });
        }
        foreach (var constant in assessment.Constants)
        {
            evidence.Add(new ContractEvidence
            {
                Kind = "ConstantProvenance",
                Summary = $"{constant.Origin}: {constant.Value ?? "<unknown>"} ({constant.NumericClassification})",
                SymbolIds = constant.SymbolId == null ? Array.Empty<string>() : new[] { constant.SymbolId },
                Source = constant.Source,
            });
        }
        return evidence;
    }

    private static string[] ValueDomainCategories(ValueDomainAssessment assessment)
        => assessment.FindingKind switch
        {
            ContractFindingKind.NonFinitePolicyMismatch => new[]
            {
                "NonFinite",
                assessment.NonFiniteAction ?? NonFinitePolicyAction.CallerOwned,
            },
            ContractFindingKind.ConstantProvenanceMismatch => new[] { "ConstantProvenance", "RawNumericLiteral" },
            ContractFindingKind.CadenceBoundaryMismatch => assessment.BoundaryMissing
                ? new[] { "Cadence", "Boundary", "Missing" }
                : new[] { "Cadence", "Boundary", "Mismatch" },
            _ => Array.Empty<string>(),
        };

    private static string ValueDomainMessage(
        ValueDomainContract contract,
        OperationFact fact,
        ValueDomainAssessment assessment)
    {
        if (assessment.FindingKind == ContractFindingKind.NonFinitePolicyMismatch)
        {
            return assessment.Constants.Length > 0
                ? $"Value passed to '{fact.TargetSymbolId}' contains explicit non-finite input but domain " +
                  $"'{contract.TargetDomain}' requires policy action '{assessment.NonFiniteAction}' with declared evidence."
                : $"Value passed to '{fact.TargetSymbolId}' lacks required local evidence for non-finite policy " +
                  $"'{assessment.NonFiniteAction}' in domain '{contract.TargetDomain}'.";
        }
        if (assessment.FindingKind == ContractFindingKind.ConstantProvenanceMismatch)
        {
            return $"Value passed to '{fact.TargetSymbolId}' contains raw numeric literal(s) not allowed by " +
                   $"domain '{contract.TargetDomain}' constant policy.";
        }
        if (assessment.FindingKind == ContractFindingKind.CadenceBoundaryMismatch)
        {
            return assessment.BoundaryMissing
                ? $"Value passed to '{fact.TargetSymbolId}' has no manifest-selected lexical cadence boundary " +
                  $"for domain '{contract.TargetDomain}'."
                : $"Cadence boundary '{assessment.BoundaryPredicate?.Expression}' for '{fact.TargetSymbolId}' " +
                  $"does not match any allowed boundary shape for domain '{contract.TargetDomain}'.";
        }
        if (assessment.IsUnclassified)
            return $"Value passed to '{fact.TargetSymbolId}' is unclassified for required domain '{contract.TargetDomain}'.";
        if (assessment.Input == null)
            return $"Operation '{fact.TargetSymbolId}' has no selected input for required domain '{contract.TargetDomain}'.";
        return $"Value domains [{string.Join(", ", assessment.ObservedDomains)}] passed to '{fact.TargetSymbolId}' " +
               $"do not satisfy required domain '{contract.TargetDomain}' or an allowed conversion.";
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

    private static bool CostContextMatches(
        ExternalApiCostContract contract,
        OperationFact fact,
        bool hasCallRouteMatch)
    {
        if (contract.CallRouteIds.Length > 0 && !hasCallRouteMatch) return false;

        var hasLocalSelector = contract.ReportEveryOccurrence
            || contract.ContainingSymbolIds.Length > 0
            || contract.ControlContextKinds.Length > 0;
        if (!hasLocalSelector) return true;
        return contract.ReportEveryOccurrence
            || Contains(contract.ContainingSymbolIds, fact.ContainingSymbolId)
            || fact.ControlContexts.Any(context => Contains(contract.ControlContextKinds, context.Kind));
    }

    private ContractCallRouteMatch[] CallRouteMatches(string[] routeIds, string containingSymbolId)
    {
        if (routeIds.Length == 0) return Array.Empty<ContractCallRouteMatch>();
        if (!_callRouteMatchesByContaining.TryGetValue(containingSymbolId, out var matches))
            return Array.Empty<ContractCallRouteMatch>();
        return matches
            .Where(match => Contains(routeIds, match.RouteId))
            .ToArray();
    }

    private void AddFinding(ContractFinding finding)
    {
        if (IsSuppressed(finding))
        {
            _suppressedCount++;
            Increment(finding.RuleId, suppressed: true);
            IncrementContract(finding.ContractId, suppressed: true);
            return;
        }

        _findingCount++;
        Increment(finding.RuleId, suppressed: false);
        IncrementContract(finding.ContractId, suppressed: false);
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

    private void RecordEvaluation(string contractId, bool findingFree)
    {
        _contractCounts.TryGetValue(contractId, out var counts);
        _contractCounts[contractId] = counts with
        {
            EvaluatedOccurrences = counts.EvaluatedOccurrences + 1,
            FindingFreeOccurrences = counts.FindingFreeOccurrences + (findingFree ? 1 : 0),
        };
    }

    private void IncrementContract(string contractId, bool suppressed)
    {
        _contractCounts.TryGetValue(contractId, out var counts);
        _contractCounts[contractId] = suppressed
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
        if (_callRouteReceipts.Length > 0)
        {
            limitations.Add(
                "Call routes follow profile-applicable semantic Calls edges to a manifest-owned depth/member bound. " +
                "Reflection, dynamic dispatch, delegates, and string-named invocation do not extend route membership.");
            if (_callRouteReceipts.Any(route => route.Truncated))
            {
                limitations.Add(
                    "At least one call route reached its member bound; findings cover only retained route membership.");
            }
        }
        if (_routeFactContracts.Length > 0)
        {
            limitations.Add(
                "Route-fact policies compare consumer-selected operation-fact dimensions within manifest-declared call routes. " +
                "They do not infer behavioral equivalence beyond those selected dimensions or outside the bounded call graph.");
        }
        if (_stateContracts.Length > 0)
        {
            limitations.Add(
                "State-access risk is a source-semantic classification. Readonly is declaration-level, initialized-once " +
                "covers visible direct writes/initializers, and reflection, native mutation, aliasing, and element writes " +
                "outside selected call routes can make mutable reference contents unknown.");
            if (_statePlanReceipts.Any(state => state.Truncated))
            {
                limitations.Add(
                    "At least one state-access member catalog reached its manifest bound; classification covers only retained members.");
            }
        }
        if (_domainContracts.Length > 0)
        {
            limitations.Add(
                "Value domains are consumer-authored symbol bindings. Lifeblood proves expression-local origins and " +
                "operators; it does not infer domains through local assignments, returns, or interprocedural flow.");
        }
        if (_shapeContracts.Length > 0)
        {
            limitations.Add(
                "Operation shapes prove manifest-selected lexical inputs, result types, branch arms, and control contexts. " +
                "They do not infer aliasing, interprocedural array lengths, runtime buffer contents, or enum/table coverage.");
        }
        limitations.Add(
            "Reflection, dynamic dispatch, and string-named invocation are outside the bound operation stream.");
        return limitations.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static (
        RouteFactContract[] RouteFacts,
        OperationGuardContract[] Guards,
        ExternalApiCostContract[] Costs,
        StateAccessContract[] StateAccesses,
        ValueDomainContract[] Domains,
        OperationShapeContract[] Shapes) SelectContracts(
        ContractManifest manifest,
        string[]? includeRuleIds)
    {
        var requested = NormalizeOptional(includeRuleIds);
        if (requested is not { Length: > 0 })
            return (
                manifest.RouteFacts,
                manifest.OperationGuards,
                manifest.ExternalApiCosts,
                manifest.StateAccesses,
                manifest.ValueDomains,
                manifest.OperationShapes);

        var known = manifest.RouteFacts.Select(contract => contract.Id)
            .Concat(manifest.OperationGuards.Select(contract => contract.Id))
            .Concat(manifest.ExternalApiCosts.Select(contract => contract.Id))
            .Concat(manifest.StateAccesses.Select(contract => contract.Id))
            .Concat(manifest.ValueDomains.Select(contract => contract.Id))
            .Concat(manifest.OperationShapes.Select(contract => contract.Id))
            .Append(ContractRuleId.RouteFact)
            .Append(ContractRuleId.OperationGuard)
            .Append(ContractRuleId.ExternalApiCost)
            .Append(ContractRuleId.StateAccess)
            .Append(ContractRuleId.ValueDomain)
            .Append(ContractRuleId.OperationShape)
            .ToHashSet(StringComparer.Ordinal);
        var unknown = requested.Where(id => !known.Contains(id)).ToArray();
        if (unknown.Length > 0)
            throw new ArgumentException("Unknown contract audit selectors: " + string.Join(", ", unknown));

        return (
            manifest.RouteFacts.Where(contract =>
                requested.Contains(ContractRuleId.RouteFact, StringComparer.Ordinal)
                || requested.Contains(contract.Id, StringComparer.Ordinal)).ToArray(),
            manifest.OperationGuards.Where(contract =>
                requested.Contains(ContractRuleId.OperationGuard, StringComparer.Ordinal)
                || requested.Contains(contract.Id, StringComparer.Ordinal)).ToArray(),
            manifest.ExternalApiCosts.Where(contract =>
                requested.Contains(ContractRuleId.ExternalApiCost, StringComparer.Ordinal)
                || requested.Contains(contract.Id, StringComparer.Ordinal)).ToArray(),
            manifest.StateAccesses.Where(contract =>
                requested.Contains(ContractRuleId.StateAccess, StringComparer.Ordinal)
                || requested.Contains(contract.Id, StringComparer.Ordinal)).ToArray(),
            manifest.ValueDomains.Where(contract =>
                requested.Contains(ContractRuleId.ValueDomain, StringComparer.Ordinal)
                || requested.Contains(contract.Id, StringComparer.Ordinal)).ToArray(),
            manifest.OperationShapes.Where(contract =>
                requested.Contains(ContractRuleId.OperationShape, StringComparer.Ordinal)
                || requested.Contains(contract.Id, StringComparer.Ordinal)).ToArray());
    }

    private OperationFactSelector[] BuildFactSelectors()
        => _routeFactContracts.Select(contract => new OperationFactSelector
        {
            IncludeKinds = contract.OperationKinds,
            TargetSymbolIds = contract.MatchAnyTarget
                ? Array.Empty<string>()
                : contract.TargetSymbolIds,
            ContainingSymbolIds = string.Equals(
                contract.Policy,
                RouteFactPolicy.AllowedRoutesOnly,
                StringComparison.Ordinal)
                    ? Array.Empty<string>()
                    : RouteContainingSymbolIds(contract.CallRouteIds),
            Operators = contract.Operators,
        })
            .Concat(_guardContracts.Select(contract => new OperationFactSelector
        {
            IncludeKinds = new[] { OperationFactKind.Call, OperationFactKind.ObjectCreation },
            TargetSymbolIds = contract.TargetSymbolIds,
        }))
            .Concat(_costContracts.Select(contract => new OperationFactSelector
            {
                IncludeKinds = contract.OperationKinds,
                TargetSymbolIds = contract.MatchAnyTarget
                    ? Array.Empty<string>()
                    : contract.TargetSymbolIds,
                ContainingSymbolIds = RouteContainingSymbolIds(contract.CallRouteIds),
            }))
            .Concat(BuildStateFactSelectors())
            .Concat(_domainContracts.Select(contract => new OperationFactSelector
            {
                IncludeKinds = contract.OperationKinds,
                TargetSymbolIds = contract.TargetSymbolIds,
            }))
            .Concat(_shapeContracts.Select(contract => new OperationFactSelector
            {
                IncludeKinds = contract.OperationKinds,
                TargetSymbolIds = contract.TargetSymbolIds,
                ContainingSymbolIds = contract.ContainingSymbolIds,
                Operators = contract.Operators,
            }))
            .ToArray();

    private string[] RouteContainingSymbolIds(string[] routeIds)
        => routeIds.Length == 0
            ? Array.Empty<string>()
            : _callRouteMatches
                .Where(match => Contains(routeIds, match.RouteId))
                .Select(match => match.ContainingSymbolId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

    private IEnumerable<OperationFactSelector> BuildStateFactSelectors()
    {
        foreach (var contract in _stateContracts)
        {
            var memberIds = _stateMembers
                .Where(member => string.Equals(member.ContractId, contract.Id, StringComparison.Ordinal))
                .Select(member => member.SymbolId)
                .ToArray();
            if (memberIds.Length == 0)
            {
                yield return new OperationFactSelector
                {
                    IncludeKinds = new[] { OperationFactKind.MemberRead },
                    TargetSymbolIds = new[] { "(no-retained-state-member)" },
                };
                continue;
            }

            var containingIds = RouteContainingSymbolIds(contract.CallRouteIds);
            yield return new OperationFactSelector
            {
                IncludeKinds = new[] { OperationFactKind.MemberRead, OperationFactKind.MemberWrite },
                TargetSymbolIds = memberIds,
                ContainingSymbolIds = containingIds,
            };
            yield return new OperationFactSelector
            {
                IncludeKinds = new[] { OperationFactKind.ElementAccess },
                ContainingSymbolIds = containingIds,
            };
            yield return new OperationFactSelector
            {
                IncludeKinds = new[] { OperationFactKind.MemberWrite },
                TargetSymbolIds = memberIds,
            };
        }
    }

    private static (ContractCallRouteReceipt[] Receipts, ContractCallRouteMatch[] Matches) SelectCallRoutePlan(
        ContractCallRoutePlan? plan,
        IEnumerable<string> referencedRouteIds)
    {
        var required = referencedRouteIds
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (required.Length == 0)
            return (Array.Empty<ContractCallRouteReceipt>(), Array.Empty<ContractCallRouteMatch>());
        if (plan == null)
        {
            throw new ArgumentException(
                "A call-route plan is required when selected contracts reference call routes.");
        }

        var duplicateReceipts = plan.Routes
            .GroupBy(receipt => receipt.RouteId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateReceipts != null)
            throw new ArgumentException($"Call-route plan contains duplicate receipt '{duplicateReceipts.Key}'.");

        var receipts = plan.Routes
            .Where(receipt => required.Contains(receipt.RouteId, StringComparer.Ordinal))
            .OrderBy(receipt => receipt.RouteId, StringComparer.Ordinal)
            .ToArray();
        var missing = required
            .Where(id => receipts.All(receipt => !string.Equals(receipt.RouteId, id, StringComparison.Ordinal)))
            .ToArray();
        if (missing.Length > 0)
            throw new ArgumentException("Call-route plan is missing routes: " + string.Join(", ", missing));

        foreach (var receipt in receipts)
        {
            var rootIds = receipt.RootSymbolIds
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            var evidenceIds = receipt.Roots
                .Select(root => root.SymbolId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            if (!rootIds.SequenceEqual(evidenceIds, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    $"Call-route plan receipt '{receipt.RouteId}' must carry declaration evidence for every root symbol.");
            }
        }

        var matches = plan.Matches
            .Where(match => required.Contains(match.RouteId, StringComparer.Ordinal))
            .OrderBy(match => match.RouteId, StringComparer.Ordinal)
            .ThenBy(match => match.RootSymbolId, StringComparer.Ordinal)
            .ThenBy(match => match.Distance)
            .ThenBy(match => match.ContainingSymbolId, StringComparer.Ordinal)
            .ToArray();
        return (receipts, matches);
    }

    private static (ContractStatePlanReceipt[] Receipts, ContractStateMember[] Members) SelectStatePlan(
        ContractStatePlan? plan,
        IEnumerable<string> referencedContractIds)
    {
        var required = referencedContractIds
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (required.Length == 0)
            return (Array.Empty<ContractStatePlanReceipt>(), Array.Empty<ContractStateMember>());
        if (plan == null)
            throw new ArgumentException("A state plan is required when selected state-access contracts exist.");

        var duplicate = plan.Contracts
            .GroupBy(receipt => receipt.ContractId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
            throw new ArgumentException($"State plan contains duplicate receipt '{duplicate.Key}'.");
        var receipts = plan.Contracts
            .Where(receipt => required.Contains(receipt.ContractId, StringComparer.Ordinal))
            .OrderBy(receipt => receipt.ContractId, StringComparer.Ordinal)
            .ToArray();
        var missing = required
            .Where(id => receipts.All(receipt => !string.Equals(receipt.ContractId, id, StringComparison.Ordinal)))
            .ToArray();
        if (missing.Length > 0)
            throw new ArgumentException("State plan is missing contracts: " + string.Join(", ", missing));

        var members = plan.Members
            .Where(member => required.Contains(member.ContractId, StringComparer.Ordinal))
            .OrderBy(member => member.ContractId, StringComparer.Ordinal)
            .ThenBy(member => member.SymbolId, StringComparer.Ordinal)
            .ToArray();
        var duplicateMember = members
            .GroupBy(member => (member.ContractId, member.SymbolId))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateMember != null)
        {
            throw new ArgumentException(
                $"State plan repeats member '{duplicateMember.Key.SymbolId}' for contract '{duplicateMember.Key.ContractId}'.");
        }
        foreach (var receipt in receipts)
        {
            var actual = members.Count(member =>
                string.Equals(member.ContractId, receipt.ContractId, StringComparison.Ordinal));
            if (actual != receipt.RetainedMemberCount)
            {
                throw new ArgumentException(
                    $"State plan receipt '{receipt.ContractId}' reports {receipt.RetainedMemberCount} retained members but carries {actual}.");
            }
        }
        return (receipts, members);
    }

    private static string DescribeValue(OperationValueFact value)
    {
        var expression = string.IsNullOrWhiteSpace(value.Expression) ? "<no source expression>" : value.Expression;
        return $"{value.Kind}: {expression}";
    }

    private static string DescribeControl(OperationControlContext context)
    {
        var label = context.BranchArm == null
            ? context.Kind
            : $"{context.Kind}[{context.BranchArm}]";
        return context.Condition == null ? label : $"{label}: {context.Condition}";
    }

    private IEnumerable<string> SelectedContractIds(string ruleId)
        => (ruleId switch
            {
                ContractRuleId.RouteFact => _routeFactContracts.Select(contract => contract.Id),
                ContractRuleId.OperationGuard => _guardContracts.Select(contract => contract.Id),
                ContractRuleId.ExternalApiCost => _costContracts.Select(contract => contract.Id),
                ContractRuleId.StateAccess => _stateContracts.Select(contract => contract.Id),
                ContractRuleId.ValueDomain => _domainContracts.Select(contract => contract.Id),
                ContractRuleId.OperationShape => _shapeContracts.Select(contract => contract.Id),
                _ => Array.Empty<string>(),
            })
            .OrderBy(contractId => contractId, StringComparer.Ordinal);

    private ContractCallRouteReceipt RouteReceipt(string routeId)
        => _callRouteReceipts.Single(receipt =>
            string.Equals(receipt.RouteId, routeId, StringComparison.Ordinal));

    private ContractCallRouteRootReceipt RouteRoot(string routeId)
        => RouteReceipt(routeId).Roots
            .OrderBy(root => root.SymbolId, StringComparer.Ordinal)
            .First();

    private static bool HasRoute(RouteFactObservation observation, string routeId)
        => observation.RouteMatches.Any(match =>
            string.Equals(match.RouteId, routeId, StringComparison.Ordinal));

    private static string[] RouteFactCategories(RouteFactContract contract, string category)
        => contract.Categories
            .Append("RouteFact" + category)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    private static string SignatureId(string signature)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(signature));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..24];
    }

    private static string FindingId(
        string ruleId,
        string contractId,
        string factId,
        string? discriminator = null)
    {
        var identity = ruleId + "\n" + contractId + "\n" + factId;
        if (discriminator != null) identity += "\n" + discriminator;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return "finding_" + Convert.ToHexString(bytes).ToLowerInvariant()[..24];
    }

    private static string BoundaryDiscriminator(ValueDomainAssessment assessment)
    {
        if (assessment.FindingKind != ContractFindingKind.CadenceBoundaryMismatch)
            return assessment.FindingKind;
        if (assessment.BoundaryMissing)
            return assessment.FindingKind + ":missing";
        var source = assessment.BoundaryPredicate?.Source;
        return $"{assessment.FindingKind}:{source?.FilePath}:{source?.Line}:{source?.Column}:" +
               assessment.BoundaryPredicate?.Expression;
    }

    private static ContractCallRouteMatch[] BoundRouteMatches(IEnumerable<ContractCallRouteMatch> matches)
        => matches.Take(MaximumRouteMatchesPerFinding).ToArray();

    private static bool IsInitializationOwner(string containingSymbolId, string memberSymbolId)
    {
        if (string.Equals(containingSymbolId, memberSymbolId, StringComparison.Ordinal))
            return true;

        var prefixSeparator = memberSymbolId.IndexOf(':');
        var memberSeparator = memberSymbolId.LastIndexOf('.');
        if (prefixSeparator < 0 || memberSeparator <= prefixSeparator + 1)
            return false;

        var ownerType = memberSymbolId[(prefixSeparator + 1)..memberSeparator];
        return string.Equals(
            containingSymbolId,
            $"method:{ownerType}..cctor()",
            StringComparison.Ordinal);
    }

    private static string RouteMatchIdentity(ContractCallRouteMatch match)
        => match.RouteId + "\n" + match.RootSymbolId + "\n" + match.ContainingSymbolId;

    private static OperationFact Earlier(OperationFact? current, OperationFact candidate)
    {
        if (current == null) return candidate;
        var comparison = string.CompareOrdinal(
            NormalizePath(candidate.Source.FilePath),
            NormalizePath(current.Source.FilePath));
        if (comparison < 0) return candidate;
        if (comparison > 0) return current;
        comparison = candidate.Source.Line.CompareTo(current.Source.Line);
        if (comparison < 0) return candidate;
        if (comparison > 0) return current;
        comparison = candidate.Source.Column.CompareTo(current.Source.Column);
        if (comparison < 0) return candidate;
        if (comparison > 0) return current;
        return string.CompareOrdinal(candidate.Id, current.Id) < 0 ? candidate : current;
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
    private readonly record struct ContractCounts(
        int EvaluatedOccurrences,
        int FindingFreeOccurrences,
        int Findings,
        int Suppressed);

    private sealed record RouteFactObservation(
        OperationFact Fact,
        ContractCallRouteMatch[] RouteMatches,
        RouteFactSignature? Signature);

    private sealed class StateObservation
    {
        public StateObservation(ContractStateMember member) => Member = member;

        public ContractStateMember Member { get; }
        public int RouteAccessCount { get; set; }
        public OperationFact? Anchor { get; set; }
        public OperationFact? RuntimeWrite { get; set; }
        public OperationFact? InitializerWrite { get; set; }
        public OperationFact? ElementWrite { get; set; }
        public Dictionary<string, ContractCallRouteMatch> RouteMatches { get; } = new(StringComparer.Ordinal);
    }

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

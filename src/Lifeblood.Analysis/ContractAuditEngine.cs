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

    private readonly ContractManifest _manifest;
    private readonly OperationGuardContract[] _guardContracts;
    private readonly ExternalApiCostContract[] _costContracts;
    private readonly ValueDomainContract[] _domainContracts;
    private readonly OperationShapeContract[] _shapeContracts;
    private readonly ContractSuppression[] _suppressions;
    private readonly SortedSet<ContractFinding> _findings = new(FindingComparer.Instance);
    private readonly Dictionary<string, RuleCounts> _counts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContractCounts> _contractCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<NearEqualConstantObservation>> _nearEqualObservations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<OperationShapeUniqueObservation>> _shapeUniqueObservations =
        new(StringComparer.Ordinal);
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

        (_guardContracts, _costContracts, _domainContracts, _shapeContracts) =
            SelectContracts(_manifest, request.IncludeRuleIds);
        if (_guardContracts.Length == 0
            && _costContracts.Length == 0
            && _domainContracts.Length == 0
            && _shapeContracts.Length == 0)
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
            .Concat(_domainContracts.SelectMany(contract => contract.TargetSymbolIds))
            .Concat(_shapeContracts.SelectMany(contract => contract.TargetSymbolIds))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var kinds = (_guardContracts.Length > 0
                ? new[] { OperationFactKind.Call, OperationFactKind.ObjectCreation }
                : Array.Empty<string>())
            .Concat(_costContracts.SelectMany(contract => contract.OperationKinds))
            .Concat(_domainContracts.SelectMany(contract => contract.OperationKinds))
            .Concat(_shapeContracts.SelectMany(contract => contract.OperationKinds));
        var everyContractHasBoundTargets = _shapeContracts.All(contract => contract.TargetSymbolIds.Length > 0);

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
        EvaluateExternalCosts(fact);
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
        EvaluateNearEqualConstants();
        EvaluateOperationShapeUniqueness();

        var selectedRules = _guardContracts.Select(_ => ContractRuleId.OperationGuard)
            .Concat(_costContracts.Select(_ => ContractRuleId.ExternalApiCost))
            .Concat(_domainContracts.Select(_ => ContractRuleId.ValueDomain))
            .Concat(_shapeContracts.Select(_ => ContractRuleId.OperationShape))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var selectedContracts = _guardContracts.Select(contract => contract.Id)
            .Concat(_costContracts.Select(contract => contract.Id))
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
            Truncated = receipt.Truncated || _findingCount > _findings.Count,
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
            Findings = _findings.ToArray(),
            Limitations = BuildLimitations(receipt),
        };
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
        if (fact.TargetSymbolId == null) return;

        foreach (var contract in _costContracts)
        {
            if (!Contains(contract.TargetSymbolIds, fact.TargetSymbolId)
                || !Contains(contract.OperationKinds, fact.Kind)
                || !CostContextMatches(contract, fact))
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
                    Summary = $"{fact.Kind} {fact.TargetSymbolId}",
                    SymbolIds = new[] { fact.TargetSymbolId },
                    Source = fact.Source,
                },
            };
            evidence.AddRange(fact.ControlContexts.Select(context => new ContractEvidence
            {
                Kind = "ControlContext",
                Summary = DescribeControl(context),
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
        OperationGuardContract[] Guards,
        ExternalApiCostContract[] Costs,
        ValueDomainContract[] Domains,
        OperationShapeContract[] Shapes) SelectContracts(
        ContractManifest manifest,
        string[]? includeRuleIds)
    {
        var requested = NormalizeOptional(includeRuleIds);
        if (requested is not { Length: > 0 })
            return (
                manifest.OperationGuards,
                manifest.ExternalApiCosts,
                manifest.ValueDomains,
                manifest.OperationShapes);

        var known = manifest.OperationGuards.Select(contract => contract.Id)
            .Concat(manifest.ExternalApiCosts.Select(contract => contract.Id))
            .Concat(manifest.ValueDomains.Select(contract => contract.Id))
            .Concat(manifest.OperationShapes.Select(contract => contract.Id))
            .Append(ContractRuleId.OperationGuard)
            .Append(ContractRuleId.ExternalApiCost)
            .Append(ContractRuleId.ValueDomain)
            .Append(ContractRuleId.OperationShape)
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
                || requested.Contains(contract.Id, StringComparer.Ordinal)).ToArray(),
            manifest.ValueDomains.Where(contract =>
                requested.Contains(ContractRuleId.ValueDomain, StringComparer.Ordinal)
                || requested.Contains(contract.Id, StringComparer.Ordinal)).ToArray(),
            manifest.OperationShapes.Where(contract =>
                requested.Contains(ContractRuleId.OperationShape, StringComparer.Ordinal)
                || requested.Contains(contract.Id, StringComparer.Ordinal)).ToArray());
    }

    private OperationFactSelector[] BuildFactSelectors()
        => _guardContracts.Select(contract => new OperationFactSelector
        {
            IncludeKinds = new[] { OperationFactKind.Call, OperationFactKind.ObjectCreation },
            TargetSymbolIds = contract.TargetSymbolIds,
        })
            .Concat(_costContracts.Select(contract => new OperationFactSelector
            {
                IncludeKinds = contract.OperationKinds,
                TargetSymbolIds = contract.TargetSymbolIds,
            }))
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
                ContractRuleId.OperationGuard => _guardContracts.Select(contract => contract.Id),
                ContractRuleId.ExternalApiCost => _costContracts.Select(contract => contract.Id),
                ContractRuleId.ValueDomain => _domainContracts.Select(contract => contract.Id),
                ContractRuleId.OperationShape => _shapeContracts.Select(contract => contract.Id),
                _ => Array.Empty<string>(),
            })
            .OrderBy(contractId => contractId, StringComparer.Ordinal);

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

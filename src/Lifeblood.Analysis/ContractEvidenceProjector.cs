using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Analysis;

/// <summary>
/// Stateless projection that joins caller policy, parsed invariant authority,
/// one lexical-evidence stream, and the existing semantic graph. It creates no
/// graph edges, scores, caches, or retained semantic state.
/// </summary>
public static class ContractEvidenceProjector
{
    public static ContractAuditReport Project(
        SemanticGraph graph,
        ContractManifest manifest,
        ContractAuditReport report,
        ContractCallRoutePlan callRoutePlan,
        IReadOnlyList<InvariantDeclarationEvidence> declarations,
        IReadOnlyList<SourceEvidenceFact> sourceFacts,
        SourceEvidenceScanReceipt sourceReceipt,
        int maxTextMatches,
        int maxEvidencePerCategory)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(callRoutePlan);
        ArgumentNullException.ThrowIfNull(declarations);
        ArgumentNullException.ThrowIfNull(sourceFacts);
        ArgumentNullException.ThrowIfNull(sourceReceipt);

        var selected = report.SelectedContractIds.ToHashSet(StringComparer.Ordinal);
        var textContracts = manifest.SourceTextPolicies
            .Where(contract => selected.Contains(contract.Id))
            .OrderBy(contract => contract.Id, StringComparer.Ordinal)
            .ToArray();
        var evidenceContracts = manifest.InvariantEvidence
            .Where(contract => selected.Contains(contract.Id))
            .OrderBy(contract => contract.Id, StringComparer.Ordinal)
            .ToArray();
        var boundedTextLimit = Math.Max(0, maxTextMatches);
        var boundedEvidenceLimit = Math.Max(0, maxEvidencePerCategory);
        var text = ProjectTextPolicies(
            textContracts,
            manifest.Suppressions,
            callRoutePlan,
            sourceFacts,
            sourceReceipt.Truncated,
            boundedTextLimit);
        var invariantEvidence = ProjectInvariantEvidence(
            graph,
            evidenceContracts,
            callRoutePlan,
            declarations,
            sourceFacts,
            report,
            sourceReceipt,
            boundedEvidenceLimit);

        var limitations = report.Limitations
            .Concat(sourceReceipt.Limitations)
            .Concat(textContracts.Length == 0
                ? Array.Empty<string>()
                : new[]
                {
                    "Source-text matches are advisory results of caller-authored retired-term policy. Lifeblood proves the occurrence and nearby symbol but does not infer that prose is wrong.",
                })
            .Concat(evidenceContracts.Length == 0
                ? Array.Empty<string>()
                : new[]
                {
                    "Invariant evidence is reported as named category receipts and concrete gaps, never as a global correctness or quality score.",
                    "Reachable-test evidence follows bounded semantic incoming edges. Reflection, generated dispatch, string-named invocation, and runtime-only execution remain outside that proof.",
                    "External compile, probe, and runtime receipts are caller-declared references; Lifeblood does not claim it executed or verified them.",
                })
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return CopyReport(
            report,
            sourceReceipt,
            text.Receipts,
            text.Matches,
            invariantEvidence.Policies,
            invariantEvidence.Rows,
            limitations);
    }

    private static (ContractTextPolicyReceipt[] Receipts, ContractTextMatch[] Matches) ProjectTextPolicies(
        SourceTextPolicyContract[] contracts,
        ContractSuppression[] suppressions,
        ContractCallRoutePlan callRoutePlan,
        IReadOnlyList<SourceEvidenceFact> sourceFacts,
        bool sourceTruncated,
        int matchLimit)
    {
        var candidates = new List<ContractTextMatch>();
        var suppressed = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var contract in contracts)
        {
            var routeMembers = RouteMembers(callRoutePlan, contract.CallRouteIds);
            foreach (var fact in sourceFacts)
            {
                if (!contract.IncludeKinds.Contains(fact.Kind, StringComparer.Ordinal)
                    || !contract.Terms.Contains(fact.MatchedTerm, StringComparer.OrdinalIgnoreCase)
                    || (contract.CallRouteIds.Length > 0
                        && (fact.ContainingSymbolId == null || !routeMembers.Contains(fact.ContainingSymbolId))))
                    continue;

                if (IsSuppressed(suppressions, ContractRuleId.SourceTextPolicy, contract.Id, fact))
                {
                    suppressed.TryGetValue(contract.Id, out var count);
                    suppressed[contract.Id] = count + 1;
                    continue;
                }

                candidates.Add(new ContractTextMatch
                {
                    ContractId = contract.Id,
                    FactId = fact.Id,
                    MatchedTerm = fact.MatchedTerm,
                    SuggestedAction = contract.SuggestedAction,
                    Confidence = ConfidenceBand.Advisory,
                    Authority = "CallerPolicy",
                    InvariantIds = contract.InvariantIds,
                    Categories = contract.Categories,
                    Message = contract.Message
                        ?? $"Source {fact.Kind} contains caller-selected phrase '{fact.MatchedTerm}'.",
                    Guidance = contract.Guidance,
                    Text = fact.Text,
                    ContainingSymbolId = fact.ContainingSymbolId,
                    Source = fact.Source,
                });
            }
        }

        var ordered = candidates
            .OrderBy(match => NormalizePath(match.Source.FilePath), StringComparer.Ordinal)
            .ThenBy(match => match.Source.Line)
            .ThenBy(match => match.Source.Column)
            .ThenBy(match => match.ContractId, StringComparer.Ordinal)
            .ThenBy(match => match.MatchedTerm, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var returned = ordered.Take(matchLimit).ToArray();
        var returnedByContract = returned
            .GroupBy(match => match.ContractId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var receipts = contracts.Select(contract =>
        {
            var count = ordered.Count(match => string.Equals(match.ContractId, contract.Id, StringComparison.Ordinal));
            suppressed.TryGetValue(contract.Id, out var suppressedCount);
            returnedByContract.TryGetValue(contract.Id, out var returnedCount);
            return new ContractTextPolicyReceipt
            {
                ContractId = contract.Id,
                MatchCount = count,
                ReturnedMatchCount = returnedCount,
                SuppressedMatchCount = suppressedCount,
                Truncated = sourceTruncated || returnedCount < count,
            };
        }).ToArray();
        return (receipts, returned);
    }

    private static (
        ContractInvariantEvidencePolicyReceipt[] Policies,
        ContractInvariantEvidenceReceipt[] Rows) ProjectInvariantEvidence(
        SemanticGraph graph,
        InvariantEvidenceContract[] contracts,
        ContractCallRoutePlan callRoutePlan,
        IReadOnlyList<InvariantDeclarationEvidence> declarations,
        IReadOnlyList<SourceEvidenceFact> sourceFacts,
        ContractAuditReport operationReport,
        SourceEvidenceScanReceipt sourceReceipt,
        int evidenceLimit)
    {
        var declarationsById = declarations
            .GroupBy(declaration => declaration.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var rows = new List<ContractInvariantEvidenceReceipt>();
        var policies = new List<ContractInvariantEvidencePolicyReceipt>();

        foreach (var contract in contracts)
        {
            var selectedIds = contract.InvariantIds
                .Concat(declarations
                    .Where(declaration => contract.InvariantIdPrefixes.Any(prefix =>
                        declaration.Id.StartsWith(prefix, StringComparison.Ordinal)))
                    .Select(declaration => declaration.Id))
                .Concat(contract.ReferenceAliases.Select(alias => alias.InvariantId))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            var boundedIds = selectedIds.Take(contract.MaxInvariants).ToArray();
            var contractTruncated = boundedIds.Length < selectedIds.Length;
            policies.Add(new ContractInvariantEvidencePolicyReceipt
            {
                ContractId = contract.Id,
                SelectedInvariantCount = selectedIds.Length,
                ReturnedInvariantCount = boundedIds.Length,
                Truncated = contractTruncated,
            });
            var reachableTests = FindReachableTests(
                graph,
                callRoutePlan,
                contract.CallRouteIds,
                sourceReceipt.ProfileScope,
                contract.MaxTestDepth);

            foreach (var invariantId in boundedIds)
            {
                declarationsById.TryGetValue(invariantId, out var declaration);
                var aliases = contract.ReferenceAliases
                    .Where(alias => string.Equals(alias.InvariantId, invariantId, StringComparison.Ordinal))
                    .SelectMany(alias => alias.Terms)
                    .Prepend(invariantId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var lexical = sourceFacts
                    .Where(fact => aliases.Contains(fact.MatchedTerm, StringComparer.OrdinalIgnoreCase))
                    .ToArray();
                var sourceReferences = new List<ContractEvidence>();
                var testReferences = new List<ContractEvidence>();
                foreach (var fact in lexical)
                {
                    var evidence = new ContractEvidence
                    {
                        Kind = fact.Kind,
                        Summary = $"matchedTerm={fact.MatchedTerm}; text={fact.Text}",
                        SymbolIds = fact.ContainingSymbolId == null
                            ? Array.Empty<string>()
                            : new[] { fact.ContainingSymbolId },
                        Source = fact.Source,
                    };
                    if (TestSymbolClassifier.IsTestSymbol(graph, fact.ContainingSymbolId))
                        testReferences.Add(evidence);
                    else if (fact.ContainingSymbolId != null && graph.GetSymbol(fact.ContainingSymbolId) != null)
                        sourceReferences.Add(evidence);
                }
                AddSymbolNameReferences(graph, aliases, sourceReferences, testReferences);

                var categoryRows = new List<ContractEvidenceCategoryReceipt>();
                var gaps = new List<ContractEvidenceGap>();
                categoryRows.Add(BuildSimpleCategory(
                    ContractEvidenceKind.InvariantDeclaration,
                    contract,
                    declaration == null
                        ? Array.Empty<ContractEvidence>()
                        : new[]
                        {
                            new ContractEvidence
                            {
                                Kind = ContractEvidenceKind.InvariantDeclaration,
                                Summary = declaration.Title,
                                Source = declaration.Source,
                            },
                        },
                    evidenceLimit,
                    gaps,
                    invariantId));
                categoryRows.Add(BuildSimpleCategory(
                    ContractEvidenceKind.SourceReference,
                    contract,
                    sourceReferences,
                    evidenceLimit,
                    gaps,
                    invariantId));
                categoryRows.Add(BuildSimpleCategory(
                    ContractEvidenceKind.TestReference,
                    contract,
                    testReferences,
                    evidenceLimit,
                    gaps,
                    invariantId));
                categoryRows.Add(BuildSimpleCategory(
                    ContractEvidenceKind.ReachableTest,
                    contract,
                    reachableTests,
                    evidenceLimit,
                    gaps,
                    invariantId));
                categoryRows.Add(BuildOperationCategory(
                    contract,
                    operationReport,
                    evidenceLimit,
                    gaps,
                    invariantId));

                foreach (var kind in contract.ExternalEvidence.Select(item => item.Kind)
                             .Concat(contract.RequiredEvidenceKinds.Where(kind =>
                                 !ContractEvidenceKind.BuiltIn.Contains(kind, StringComparer.Ordinal)))
                             .Distinct(StringComparer.Ordinal)
                             .OrderBy(kind => kind, StringComparer.Ordinal))
                {
                    var external = contract.ExternalEvidence
                        .Where(item => string.Equals(item.Kind, kind, StringComparison.Ordinal))
                        .Select(item => new ContractEvidence
                        {
                            Kind = kind,
                            Summary = "callerDeclaredReference=" + item.Reference,
                        })
                        .ToArray();
                    var required = contract.RequiredEvidenceKinds.Contains(kind, StringComparer.Ordinal);
                    var status = external.Length > 0
                        ? ContractEvidenceStatus.CallerDeclared
                        : ContractEvidenceStatus.Missing;
                    if (required && external.Length == 0)
                    {
                        gaps.Add(new ContractEvidenceGap
                        {
                            Kind = kind,
                            Subject = invariantId,
                            Reason = $"Required caller-owned {kind} receipt reference is absent.",
                        });
                    }
                    categoryRows.Add(new ContractEvidenceCategoryReceipt
                    {
                        Kind = kind,
                        Required = required,
                        Status = status,
                        EvidenceCount = external.Length,
                        ReturnedEvidenceCount = Math.Min(external.Length, evidenceLimit),
                        Evidence = external.Take(evidenceLimit).ToArray(),
                    });
                }

                var requirementsSatisfied = categoryRows
                    .Where(category => category.Required)
                    .All(category => category.Status is ContractEvidenceStatus.Present
                        or ContractEvidenceStatus.CallerDeclared);
                var hasTest = testReferences.Count > 0 || reachableTests.Count > 0;
                var hasSource = sourceReferences.Count > 0;
                var hasOperation = categoryRows.Any(category =>
                    category.Kind == ContractEvidenceKind.OperationContract
                    && category.EvidenceCount > 0);
                var hasExternal = contract.ExternalEvidence.Length > 0;
                var states = new List<string>();
                if (requirementsSatisfied) states.Add(InvariantEvidenceState.Covered);
                if (declaration != null && !hasSource && !hasTest)
                    states.Add(InvariantEvidenceState.ProseOnly);
                if (hasSource && !hasTest) states.Add(InvariantEvidenceState.SourceOnly);
                if (hasTest && !hasSource) states.Add(InvariantEvidenceState.TestOnly);
                if (declaration == null && (hasSource || hasTest))
                    states.Add(InvariantEvidenceState.StaleReference);
                if (declaration != null
                    && !hasSource
                    && !hasTest
                    && contract.CallRouteIds.Length == 0
                    && !hasOperation
                    && !hasExternal)
                    states.Add(InvariantEvidenceState.Orphan);

                rows.Add(new ContractInvariantEvidenceReceipt
                {
                    ContractId = contract.Id,
                    InvariantId = invariantId,
                    States = states.Distinct(StringComparer.Ordinal).ToArray(),
                    RequirementsSatisfied = requirementsSatisfied,
                    Truncated = contractTruncated
                        || sourceReceipt.Truncated
                        || categoryRows.Any(category => category.ReturnedEvidenceCount < category.EvidenceCount),
                    Evidence = categoryRows.ToArray(),
                    Gaps = gaps.ToArray(),
                });
            }
        }

        return (policies.ToArray(), rows.ToArray());
    }

    private static ContractEvidenceCategoryReceipt BuildSimpleCategory(
        string kind,
        InvariantEvidenceContract contract,
        IReadOnlyCollection<ContractEvidence> evidence,
        int limit,
        List<ContractEvidenceGap> gaps,
        string invariantId)
    {
        var required = contract.RequiredEvidenceKinds.Contains(kind, StringComparer.Ordinal);
        if (required && evidence.Count == 0)
        {
            gaps.Add(new ContractEvidenceGap
            {
                Kind = kind,
                Subject = invariantId,
                Reason = $"Required {kind} evidence is absent.",
            });
        }
        return new ContractEvidenceCategoryReceipt
        {
            Kind = kind,
            Required = required,
            Status = evidence.Count > 0 ? ContractEvidenceStatus.Present : ContractEvidenceStatus.Missing,
            EvidenceCount = evidence.Count,
            ReturnedEvidenceCount = Math.Min(evidence.Count, limit),
            Evidence = evidence.Take(limit).ToArray(),
        };
    }

    private static ContractEvidenceCategoryReceipt BuildOperationCategory(
        InvariantEvidenceContract contract,
        ContractAuditReport report,
        int limit,
        List<ContractEvidenceGap> gaps,
        string invariantId)
    {
        var required = contract.RequiredEvidenceKinds.Contains(
            ContractEvidenceKind.OperationContract,
            StringComparer.Ordinal);
        var rows = report.RuleBreakdown
            .SelectMany(rule => rule.Contracts)
            .Where(row => contract.OperationContractIds.Contains(row.ContractId, StringComparer.Ordinal))
            .ToDictionary(row => row.ContractId, StringComparer.Ordinal);
        var evidence = new List<ContractEvidence>();
        var missing = false;
        var failing = false;
        foreach (var contractId in contract.OperationContractIds)
        {
            if (!rows.TryGetValue(contractId, out var row) || row.EvaluatedOccurrenceCount == 0)
            {
                missing = true;
                gaps.Add(new ContractEvidenceGap
                {
                    Kind = ContractEvidenceKind.OperationContract,
                    Subject = contractId,
                    Reason = "The selected operation contract evaluated zero occurrences.",
                });
                continue;
            }
            if (row.FindingCount > 0)
            {
                failing = true;
                gaps.Add(new ContractEvidenceGap
                {
                    Kind = ContractEvidenceKind.OperationContract,
                    Subject = contractId,
                    Reason = $"The operation contract has {row.FindingCount} unsuppressed finding(s).",
                });
            }
            evidence.Add(new ContractEvidence
            {
                Kind = ContractEvidenceKind.OperationContract,
                Summary = $"contractId={contractId}; evaluated={row.EvaluatedOccurrenceCount}; findings={row.FindingCount}; suppressed={row.SuppressedFindingCount}",
            });
        }
        var status = failing
            ? ContractEvidenceStatus.Failing
            : missing || evidence.Count == 0
                ? ContractEvidenceStatus.Missing
                : ContractEvidenceStatus.Present;
        if (required && contract.OperationContractIds.Length == 0)
        {
            gaps.Add(new ContractEvidenceGap
            {
                Kind = ContractEvidenceKind.OperationContract,
                Subject = invariantId,
                Reason = "OperationContract evidence is required but no operation contracts were selected.",
            });
        }
        return new ContractEvidenceCategoryReceipt
        {
            Kind = ContractEvidenceKind.OperationContract,
            Required = required,
            Status = status,
            EvidenceCount = evidence.Count,
            ReturnedEvidenceCount = Math.Min(evidence.Count, limit),
            Evidence = evidence.Take(limit).ToArray(),
        };
    }

    private static void AddSymbolNameReferences(
        SemanticGraph graph,
        string[] aliases,
        List<ContractEvidence> sourceReferences,
        List<ContractEvidence> testReferences)
    {
        foreach (var symbol in graph.Symbols)
        {
            var matched = aliases.FirstOrDefault(alias =>
                string.Equals(symbol.Name, alias, StringComparison.OrdinalIgnoreCase)
                || string.Equals(symbol.QualifiedName, alias, StringComparison.OrdinalIgnoreCase));
            if (matched == null) continue;
            var evidence = new ContractEvidence
            {
                Kind = "SymbolName",
                Summary = $"symbol name matches caller term '{matched}'",
                SymbolIds = new[] { symbol.Id },
                Source = SymbolSource(symbol),
            };
            if (TestSymbolClassifier.IsTestSymbol(graph, symbol.Id))
                testReferences.Add(evidence);
            else
                sourceReferences.Add(evidence);
        }
    }

    private static List<ContractEvidence> FindReachableTests(
        SemanticGraph graph,
        ContractCallRoutePlan callRoutePlan,
        string[] routeIds,
        string profileScope,
        int maxDepth)
    {
        if (routeIds.Length == 0 || maxDepth == 0) return new List<ContractEvidence>();
        var selectedRoutes = routeIds.ToHashSet(StringComparer.Ordinal);
        var seeds = callRoutePlan.Matches
            .Where(match => selectedRoutes.Contains(match.RouteId))
            .Select(match => match.ContainingSymbolId)
            .Concat(callRoutePlan.Routes
                .Where(route => selectedRoutes.Contains(route.RouteId))
                .SelectMany(route => route.RootSymbolIds))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var distance = new Dictionary<string, int>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var seed in seeds)
        {
            distance[seed] = 0;
            queue.Enqueue(seed);
        }
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var currentDepth = distance[current];
            if (currentDepth >= maxDepth) continue;
            foreach (var edgeIndex in graph.GetIncomingEdgeIndexes(current))
            {
                var edge = graph.Edges[edgeIndex];
                if (edge.Kind == EdgeKind.Contains || !AppliesToProfile(edge, profileScope)) continue;
                var next = edge.SourceId;
                var nextDepth = currentDepth + 1;
                if (distance.TryGetValue(next, out var previous) && previous <= nextDepth) continue;
                distance[next] = nextDepth;
                queue.Enqueue(next);
            }
        }
        return distance
            .Where(pair => pair.Value > 0)
            .Select(pair => (Symbol: graph.GetSymbol(pair.Key), Distance: pair.Value))
            .Where(pair => pair.Symbol != null && TestSymbolClassifier.IsTestMethod(pair.Symbol))
            .OrderBy(pair => pair.Distance)
            .ThenBy(pair => pair.Symbol!.QualifiedName, StringComparer.Ordinal)
            .Select(pair => new ContractEvidence
            {
                Kind = ContractEvidenceKind.ReachableTest,
                Summary = $"semanticDistance={pair.Distance}",
                SymbolIds = new[] { pair.Symbol!.Id },
                Source = SymbolSource(pair.Symbol),
            })
            .ToList();
    }

    private static bool AppliesToProfile(Edge edge, string profileScope)
        => edge.Profiles is not { Count: > 0 }
            || edge.Profiles.Contains(profileScope, StringComparer.Ordinal);

    private static HashSet<string> RouteMembers(ContractCallRoutePlan plan, string[] routeIds)
    {
        if (routeIds.Length == 0) return new HashSet<string>(StringComparer.Ordinal);
        var selected = routeIds.ToHashSet(StringComparer.Ordinal);
        return plan.Matches
            .Where(match => selected.Contains(match.RouteId))
            .Select(match => match.ContainingSymbolId)
            .Concat(plan.Routes
                .Where(route => selected.Contains(route.RouteId))
                .SelectMany(route => route.RootSymbolIds))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool IsSuppressed(
        ContractSuppression[] suppressions,
        string ruleId,
        string contractId,
        SourceEvidenceFact fact)
        => suppressions.Any(suppression =>
            SelectorMatches(suppression.RuleIds, ruleId)
            && SelectorMatches(suppression.ContractIds, contractId)
            && SelectorMatches(suppression.FactIds, fact.Id)
            && SelectorMatches(suppression.ContainingSymbolIds, fact.ContainingSymbolId ?? string.Empty)
            && PathSelectorMatches(suppression.FilePaths, fact.Source.FilePath));

    private static bool SelectorMatches(string[] selector, string value)
        => selector.Length == 0 || selector.Contains(value, StringComparer.Ordinal);

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
                || candidate.EndsWith('/' + expected.TrimStart('/'), comparison);
        });
    }

    private static OperationSourceSpan SymbolSource(Symbol symbol) => new()
    {
        FilePath = symbol.FilePath,
        Line = symbol.Line,
        Column = 1,
        EndLine = symbol.Line,
        EndColumn = 1,
    };

    private static ContractAuditReport CopyReport(
        ContractAuditReport source,
        SourceEvidenceScanReceipt sourceReceipt,
        ContractTextPolicyReceipt[] textReceipts,
        ContractTextMatch[] textMatches,
        ContractInvariantEvidencePolicyReceipt[] invariantEvidencePolicies,
        ContractInvariantEvidenceReceipt[] invariantEvidence,
        string[] limitations)
        => new()
        {
            Status = source.Status,
            ManifestId = source.ManifestId,
            ManifestVersion = source.ManifestVersion,
            ManifestSchemaVersion = source.ManifestSchemaVersion,
            SelectedRuleIds = source.SelectedRuleIds,
            SelectedContractIds = source.SelectedContractIds,
            ScanReceipt = source.ScanReceipt,
            FindingCount = source.FindingCount,
            ReturnedFindingCount = source.ReturnedFindingCount,
            SuppressedFindingCount = source.SuppressedFindingCount,
            Truncated = source.Truncated
                || sourceReceipt.Truncated
                || textReceipts.Any(receipt => receipt.Truncated)
                || invariantEvidencePolicies.Any(receipt => receipt.Truncated)
                || invariantEvidence.Any(receipt => receipt.Truncated),
            RuleBreakdown = source.RuleBreakdown,
            CallRoutes = source.CallRoutes,
            RouteFacts = source.RouteFacts,
            StateAccesses = source.StateAccesses,
            SourceEvidenceScan = sourceReceipt,
            SourceTextPolicies = textReceipts,
            SourceTextMatches = textMatches,
            InvariantEvidencePolicies = invariantEvidencePolicies,
            InvariantEvidence = invariantEvidence,
            Findings = source.Findings,
            Limitations = limitations,
        };

    private static string NormalizePath(string value) => value.Replace('\\', '/').Trim();
}

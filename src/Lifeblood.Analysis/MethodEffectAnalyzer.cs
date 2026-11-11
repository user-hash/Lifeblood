using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Analysis;

/// <summary>
/// Request-local aggregation over one bounded occurrence stream. Graph routes
/// bound candidate owners; only bound direct/local call occurrences select
/// transitive callable bodies. No execution, points-to or purity proof is made.
/// </summary>
public static class MethodEffectAnalyzer
{
    public static ContractCallRoutePlan Plan(SemanticGraph graph, MethodEffectRequest request)
    {
        Validate(request);
        return ContractCallRoutePlanner.Plan(graph, new[]
        {
            new ContractCallRoute
            {
                Id = "method-effects",
                RootSymbolIds = request.RootSymbolIds,
                MaxDepth = request.MaxDepth,
                MaxMembers = request.MaxMethods,
            },
        }, request.ProfileScope);
    }

    public static MethodEffectReport Analyze(
        SemanticGraph graph,
        MethodEffectRequest request,
        ContractCallRoutePlan plan,
        IReadOnlyList<OperationFact> facts,
        OperationFactScanReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(receipt);
        Validate(request);
        if (facts.Count > request.MaxFacts)
            throw new ArgumentException("The observed stream exceeds the requested fact budget.", nameof(facts));
        var declarations = facts.Where(fact => fact.CallableDeclaration != null && fact.ContainingCallableId != null).ToArray();
        var bodies = declarations.GroupBy(fact => fact.ContainingCallableId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var byCallable = facts.Where(fact => fact.CallableDeclaration == null && fact.ContainingCallableId != null)
            .GroupBy(fact => fact.ContainingCallableId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var methods = new List<MethodEffectSummary>();
        foreach (var root in request.RootSymbolIds.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            methods.Add(Summarize(graph, request, plan, declarations, bodies, byCallable, receipt, root, cancellationToken));
        }
        return new MethodEffectReport
        {
            ScanReceipt = receipt,
            CandidateScopes = plan.Routes,
            Methods = methods.ToArray(),
            Limitations = new[]
            {
                "Observed lexical operations and supported bound call paths are static evidence, not execution, points-to completeness, purity, Burst output or timing proof.",
                "Graph call routes select candidate owners only. Deferred bodies are excluded until a supported direct/local call reaches their opaque callable key.",
                "Access modes describe observed placement, including both read/write for compound, increment and ref operations. They do not establish an exhaustive alias/accessor read or write set. Element writes do not establish writes to their receiver field or parameter.",
                "Implicit constructors, type initialization, property/event accessors, operators, disposal, alias effects and compiler lowering are not fully expanded. Absence of reported operations cannot prove absence of effects.",
                "Source checksums describe retained compiler text with its stated algorithm/encoding; they neither hash current raw files nor verify a native artifact or PDB identity.",
                "Evidence limits bound returned samples independently of scan, candidate-owner, depth and callable limits. Counts describe only the observed stream.",
            },
        };
    }

    public static void Validate(MethodEffectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RootSymbolIds is not { Length: >= 1 and <= 16 }
            || request.RootSymbolIds.Any(string.IsNullOrWhiteSpace)
            || request.RootSymbolIds.Distinct(StringComparer.Ordinal).Count() != request.RootSymbolIds.Length)
            throw new ArgumentException("RootSymbolIds must contain 1..16 distinct nonempty method addresses.");
        if (request.MaxDepth is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(request.MaxDepth));
        if (request.MaxMethods < request.RootSymbolIds.Length || request.MaxMethods > 256)
            throw new ArgumentOutOfRangeException(nameof(request.MaxMethods));
        if (request.MaxFacts is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(request.MaxFacts));
        if (request.MaxEvidencePerMethod is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(request.MaxEvidencePerMethod));
    }

    private static MethodEffectSummary Summarize(
        SemanticGraph graph,
        MethodEffectRequest request,
        ContractCallRoutePlan plan,
        OperationFact[] declarations,
        IReadOnlyDictionary<string, OperationFact[]> bodies,
        IReadOnlyDictionary<string, OperationFact[]> byCallable,
        OperationFactScanReceipt receipt,
        string root,
        CancellationToken cancellationToken)
    {
        var unknowns = new List<MethodEffectUnknown>();
        void Unknown(string reason, string? callable = null, OperationFact? fact = null)
            => unknowns.Add(new MethodEffectUnknown(reason, callable, fact?.Id, fact?.Source));
        if (receipt.Status != OperationFactScanStatus.Completed) Unknown(MethodEffectUnknownReason.ScanRejected);
        if (receipt.Truncated) Unknown(MethodEffectUnknownReason.ScanTruncated);
        if (receipt.StoppedByConsumer) Unknown(MethodEffectUnknownReason.ScanStopped);
        if (plan.Routes.Any(route => route.Truncated)) Unknown(MethodEffectUnknownReason.CandidateScopeTruncated);
        var roots = declarations.Where(fact => fact.TargetSymbolId == root)
            .Select(fact => fact.ContainingCallableId!).Distinct(StringComparer.Ordinal).ToArray();
        var rootCallable = roots.Length == 1 ? roots[0] : null;
        if (roots.Length == 0) Unknown(MethodEffectUnknownReason.RootDeclarationUnavailable);
        if (roots.Length > 1) Unknown(MethodEffectUnknownReason.RootDeclarationAmbiguous);
        var reached = new Dictionary<string, int>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        if (rootCallable != null) { reached.Add(rootCallable, 0); queue.Enqueue(rootCallable); }
        var observations = new List<OperationFact>();
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var callable = queue.Dequeue();
            var callableDeclarations = bodies[callable];
            if (callableDeclarations.Any(fact => fact.CallableDeclaration!.HasBindingErrors))
                Unknown(MethodEffectUnknownReason.BindingErrors, callable, callableDeclarations[0]);
            if (!callableDeclarations.Any(fact => fact.CallableDeclaration!.BodyKind == OperationCallableBodyKind.SourceBody))
                Unknown(MethodEffectUnknownReason.SourceBodyUnavailable, callable, callableDeclarations[0]);
            if (!byCallable.TryGetValue(callable, out var occurrences)) continue;
            observations.AddRange(occurrences);
            foreach (var fact in occurrences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (fact.TargetMemberKind is "Property" or "Event")
                    Unknown(MethodEffectUnknownReason.PropertyOrEventAccessorEffects, callable, fact);
                if (fact.Kind is OperationFactKind.Binary or OperationFactKind.Unary or OperationFactKind.Conversion
                    && fact.TargetSymbolId != null)
                    Unknown(MethodEffectUnknownReason.BoundOperatorNotExpanded, callable, fact);
                if (fact.Kind != OperationFactKind.Call) continue;
                if (fact.InvocationKind is not (OperationInvocationKind.Direct or OperationInvocationKind.LocalFunction))
                {
                    Unknown(MethodEffectUnknownReason.IndirectDispatch, callable, fact);
                    continue;
                }
                if (fact.TargetIsExternal == true)
                {
                    Unknown(MethodEffectUnknownReason.ExternalImplementation, callable, fact);
                    continue;
                }
                var target = fact.TargetCallableId;
                if (target != null && reached.ContainsKey(target)) continue;
                if (reached[callable] >= request.MaxDepth)
                {
                    Unknown(MethodEffectUnknownReason.DepthLimit, callable, fact);
                    continue;
                }
                if (reached.Count >= request.MaxMethods)
                {
                    Unknown(MethodEffectUnknownReason.MethodLimit, callable, fact);
                    continue;
                }
                if (target == null || !bodies.ContainsKey(target))
                {
                    Unknown(MethodEffectUnknownReason.TargetBodyNotSelected, callable, fact);
                    continue;
                }
                reached.Add(target, reached[callable] + 1);
                queue.Enqueue(target);
            }
        }

        var ordered = observations.OrderBy(fact => fact.Id, StringComparer.Ordinal).ToArray();
        var accesses = ordered.Where(fact => fact.Kind is OperationFactKind.MemberRead or OperationFactKind.MemberWrite
            or OperationFactKind.ElementAccess or OperationFactKind.PointerIndirection).ToArray();
        var calls = ordered.Where(fact => fact.Kind == OperationFactKind.Call).ToArray();
        var bindings = ordered.SelectMany(fact => fact.Inputs
            .Where(input => input.Value.CallableId != null)
            .Select(input => new MethodCallbackBinding(
                fact.Id, fact.ContainingCallableId!, input.Value.CallableId!, input.Role,
                fact.Kind == OperationFactKind.Assignment ? fact.TargetSymbolId : null, fact.Source))).ToArray();
        var ownerIds = declarations.Where(fact => reached.ContainsKey(fact.ContainingCallableId!))
            .Select(fact => fact.ContainingSymbolId).ToHashSet(StringComparer.Ordinal);
        var deferred = declarations.Where(fact => fact.CallableDeclaration!.IsDeferred
                && ownerIds.Contains(fact.ContainingSymbolId) && !reached.ContainsKey(fact.ContainingCallableId!))
            .OrderBy(fact => fact.Id, StringComparer.Ordinal).ToArray();
        var selectedDeclarations = declarations.Where(fact => reached.ContainsKey(fact.ContainingCallableId!))
            .OrderBy(fact => fact.Id, StringComparer.Ordinal).ToArray();
        var limit = request.MaxEvidencePerMethod;
        return new MethodEffectSummary
        {
            RootSymbolId = root,
            RootCallableId = rootCallable,
            ReachedCallableIds = reached.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            ObservedFactCount = ordered.Length,
            ObservedOperations = Counts(ordered.Select(fact => fact.Kind)),
            MemberAccessCount = accesses.Length,
            MemberAccesses = accesses.Take(limit).Select(fact => Evidence(graph, fact)).ToArray(),
            CallCount = calls.Length,
            Calls = calls.Take(limit).Select(fact => Evidence(graph, fact)).ToArray(),
            CallbackBindingCount = bindings.Length,
            CallbackBindings = bindings.Take(limit).ToArray(),
            DeferredCallableCount = deferred.Length,
            DeferredCallables = deferred.Take(limit).Select(fact => CallableEvidence(graph, fact)).ToArray(),
            CallableDeclarations = selectedDeclarations.Take(limit).Select(fact => CallableEvidence(graph, fact)).ToArray(),
            UnknownReasons = Counts(unknowns.Select(unknown => unknown.Reason)),
            UnknownEvidence = unknowns.Take(limit).ToArray(),
            EvidenceTruncated = accesses.Length > limit || calls.Length > limit || bindings.Length > limit
                || deferred.Length > limit || selectedDeclarations.Length > limit || unknowns.Count > limit,
        };
    }

    private static MethodEffectCount[] Counts(IEnumerable<string> kinds)
        => kinds.GroupBy(kind => kind, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new MethodEffectCount(group.Key, group.Count())).ToArray();

    private static MethodEffectEvidence Evidence(SemanticGraph graph, OperationFact fact) => new()
    {
        FactId = fact.Id,
        CallableId = fact.ContainingCallableId!,
        Kind = fact.Kind,
        TargetSymbolId = fact.TargetSymbolId != null && graph.GetSymbol(fact.TargetSymbolId) != null ? fact.TargetSymbolId : null,
        TargetCallableId = fact.TargetCallableId,
        InvocationKind = fact.InvocationKind,
        AccessModes = fact.AccessModes,
        TargetMemberKind = fact.TargetMemberKind,
        ReceiverValueId = fact.Inputs.FirstOrDefault(input => input.Role == OperationInputRole.Receiver)?.Value.SymbolId,
        Source = fact.Source,
    };

    private static MethodCallableEvidence CallableEvidence(SemanticGraph graph, OperationFact fact)
        => new(fact.ContainingCallableId!, fact.TargetSymbolId != null && graph.GetSymbol(fact.TargetSymbolId) != null ? fact.TargetSymbolId : null,
            fact.ModuleName, fact.ProfileScope, fact.Source, fact.CallableDeclaration!);
}

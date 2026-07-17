using System.Globalization;
using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Application.UseCases;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-CONTRACT-AUDIT-001. Versioned consumer policy evaluates one bounded
/// operation stream without a second graph or rule-specific compiler walk.
/// </summary>
public sealed class ContractAuditEngineTests
{
    private const string GuardTarget = "method:Acme.Dsp.SetRate(float)";
    private const string CostTarget = "method:Vendor.Api.Allocate(int)";
    private const string DomainTarget = "method:Acme.Clock.SetSeconds(float)";
    private const string Frames = "parameter:method:Acme.Clock.Run(float,float,float)#0:frames";
    private const string SampleRate = "parameter:method:Acme.Clock.Run(float,float,float)#1:sampleRate";
    private const string Seconds = "parameter:method:Acme.Clock.Run(float,float,float)#2:seconds";

    [Fact]
    public void OneStream_EvaluatesSelectedRuleFamiliesWithBoundedDeterministicFindings()
    {
        var engine = new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = Manifest(),
            MaxFindings = 1,
        });

        Assert.Equal(new[] { GuardTarget, CostTarget }, engine.Query.TargetSymbolIds);
        Assert.Contains(OperationFactKind.Call, engine.Query.IncludeKinds!);

        engine.Observe(Call("unsafe", GuardTarget, "Z.cs", 20, Argument(OperationValueKind.Parameter)));
        engine.Observe(Call(
            "guarded",
            GuardTarget,
            "A.cs",
            3,
            Argument(OperationValueKind.Invocation, "method:Acme.Math.Clamp(float,float,float)")));
        engine.Observe(Call(
            "cost-outside",
            CostTarget,
            "C.cs",
            4,
            Argument(OperationValueKind.Literal)));
        engine.Observe(Call(
            "cost-loop",
            CostTarget,
            "B.cs",
            8,
            Argument(OperationValueKind.Parameter),
            OperationControlContextKind.Loop));

        var report = engine.Complete(Receipt(emitted: 4));

        Assert.Equal(2, report.FindingCount);
        Assert.Equal(1, report.ReturnedFindingCount);
        Assert.True(report.Truncated);
        var retained = Assert.Single(report.Findings);
        Assert.Equal("B.cs", retained.Source.FilePath);
        Assert.Equal(ContractFindingKind.ExternalApiCostExposure, retained.Kind);
        Assert.Equal(new[] { "Allocation", "CacheRequired" }, retained.Categories);
        Assert.Equal(2, report.RuleBreakdown.Length);
        var guardRule = Assert.Single(report.RuleBreakdown, row => row.RuleId == ContractRuleId.OperationGuard);
        Assert.Equal(1, guardRule.FindingCount);
        var guardCoverage = Assert.Single(guardRule.Contracts);
        Assert.Equal(2, guardCoverage.EvaluatedOccurrenceCount);
        Assert.Equal(1, guardCoverage.FindingFreeOccurrenceCount);
        Assert.Equal(1, guardCoverage.FindingCount);
        var costRule = Assert.Single(report.RuleBreakdown, row => row.RuleId == ContractRuleId.ExternalApiCost);
        Assert.Equal(1, costRule.FindingCount);
        var costCoverage = Assert.Single(costRule.Contracts);
        Assert.Equal(1, costCoverage.EvaluatedOccurrenceCount);
        Assert.Equal(0, costCoverage.FindingFreeOccurrenceCount);
    }

    [Fact]
    public void SummaryAndSuppression_BoundPayloadWithoutHidingCounts()
    {
        var manifest = Manifest(suppressions: new[]
        {
            new ContractSuppression
            {
                Id = "generated-cost",
                ContractIds = new[] { "vendor-allocation" },
                FilePaths = new[] { "Generated.cs" },
                Reason = "Generated warm-up is reviewed separately.",
            },
        });
        var engine = new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = manifest,
            IncludeRuleIds = new[] { ContractRuleId.ExternalApiCost },
            Summarize = true,
        });

        engine.Observe(Call(
            "suppressed",
            CostTarget,
            "C:/Repo/Generated.cs",
            1,
            Argument(OperationValueKind.Literal),
            OperationControlContextKind.Loop));
        engine.Observe(Call(
            "reported",
            CostTarget,
            "Runtime.cs",
            2,
            Argument(OperationValueKind.Literal),
            OperationControlContextKind.Loop));

        var report = engine.Complete(Receipt(emitted: 2));

        Assert.Equal(1, report.FindingCount);
        Assert.Equal(1, report.SuppressedFindingCount);
        Assert.Empty(Assert.Single(report.Findings).Evidence);
        Assert.Equal(new[] { ContractRuleId.ExternalApiCost }, report.SelectedRuleIds);
        Assert.Equal(new[] { "vendor-allocation" }, report.SelectedContractIds);
        var coverage = Assert.Single(Assert.Single(report.RuleBreakdown).Contracts);
        Assert.Equal(2, coverage.EvaluatedOccurrenceCount);
        Assert.Equal(0, coverage.FindingFreeOccurrenceCount);
        Assert.Equal(1, coverage.FindingCount);
        Assert.Equal(1, coverage.SuppressedFindingCount);
    }

    [Fact]
    public void ContractBreakdown_ReportsZeroEvaluatedOccurrencesAsAnExplicitGap()
    {
        var engine = new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = Manifest(),
            IncludeRuleIds = new[] { ContractRuleId.OperationGuard },
        });

        var report = engine.Complete(Receipt(emitted: 0));

        Assert.Equal(0, report.FindingCount);
        var coverage = Assert.Single(Assert.Single(report.RuleBreakdown).Contracts);
        Assert.Equal("rate-is-normalized", coverage.ContractId);
        Assert.Equal(0, coverage.EvaluatedOccurrenceCount);
        Assert.Equal(0, coverage.FindingFreeOccurrenceCount);
        Assert.Equal(0, coverage.FindingCount);
        Assert.Equal(0, coverage.SuppressedFindingCount);
    }

    [Fact]
    public void CallRoute_CarriesDirectAndTransitiveProvenanceWithoutAnotherGraph()
    {
        const string root = "method:Acme.Audio.Process()";
        const string helper = "method:Acme.Audio.ProcessVoice()";
        const string outside = "method:Acme.Tools.Warmup()";
        var graph = new GraphBuilder()
            .AddSymbols(new[] { root, helper, outside }.Select(id => new Symbol
            {
                Id = id,
                Name = id,
                Kind = SymbolKind.Method,
            }))
            .AddEdge(new Edge { SourceId = root, TargetId = helper, Kind = EdgeKind.Calls })
            .Build();
        var manifest = new ContractManifest
        {
            Id = "audio-policy",
            Version = "1",
            CallRoutes = new[]
            {
                new ContractCallRoute
                {
                    Id = "production-audio",
                    RootSymbolIds = new[] { root },
                    MaxDepth = 4,
                    MaxMembers = 16,
                },
            },
            ExternalApiCosts = new[]
            {
                new ExternalApiCostContract
                {
                    Id = "allocation-on-audio-route",
                    TargetSymbolIds = new[] { CostTarget },
                    CallRouteIds = new[] { "production-audio" },
                    Categories = new[] { "Allocation" },
                    AnnotationSource = "Acme realtime policy",
                },
            },
        };
        var plan = ContractCallRoutePlanner.Plan(graph, manifest.CallRoutes, "Player");
        var engine = new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = manifest,
            ProfileScope = "Player",
            CallRoutePlan = plan,
        });

        var selector = Assert.Single(engine.Query.Selectors!);
        Assert.Equal(new[] { root, helper }, selector.ContainingSymbolIds);
        engine.Observe(Call("outside", CostTarget, "Tools.cs", 3, Argument(OperationValueKind.Literal), containingSymbolId: outside));
        engine.Observe(Call("direct", CostTarget, "Audio.cs", 5, Argument(OperationValueKind.Literal), containingSymbolId: root));
        engine.Observe(Call("transitive", CostTarget, "Voice.cs", 7, Argument(OperationValueKind.Literal), containingSymbolId: helper));

        var report = engine.Complete(Receipt(emitted: 3));

        Assert.Equal(2, report.FindingCount);
        Assert.Equal(0, report.ScanReceipt.AdditionalSemanticBaseCount);
        Assert.False(Assert.Single(report.CallRoutes).Truncated);
        Assert.Equal(
            new[] { ContractCallRoutePlacement.Direct, ContractCallRoutePlacement.Transitive },
            report.Findings.Select(finding => Assert.Single(finding.CallRouteMatches).Placement));
        Assert.Equal(new[] { root, helper }, report.Findings[1].CallRouteMatches[0].PathSymbolIds);
        Assert.All(report.Findings, finding => Assert.Contains(finding.Evidence, evidence => evidence.Kind == "CallRoute"));
    }

    [Fact]
    public void ExternalCost_MatchAnyTargetSelectsTargetlessOperationsOnlyInsideTheRoute()
    {
        const string root = "method:Acme.Audio.Process()";
        const string helper = "method:Acme.Audio.ProcessVoice()";
        const string outside = "method:Acme.Tools.Warmup()";
        var graph = new GraphBuilder()
            .AddSymbols(new[] { root, helper, outside }.Select(id => new Symbol
            {
                Id = id,
                Name = id,
                Kind = SymbolKind.Method,
            }))
            .AddEdge(new Edge { SourceId = root, TargetId = helper, Kind = EdgeKind.Calls })
            .Build();
        var manifest = new ContractManifest
        {
            Id = "realtime-policy",
            Version = "1",
            CallRoutes = new[]
            {
                new ContractCallRoute { Id = "audio", RootSymbolIds = new[] { root } },
            },
            ExternalApiCosts = new[]
            {
                new ExternalApiCostContract
                {
                    Id = "targetless-realtime-operations",
                    MatchAnyTarget = true,
                    OperationKinds = new[] { OperationFactKind.ArrayCreation, OperationFactKind.Throw },
                    Categories = new[] { "RealtimeForbidden" },
                    CallRouteIds = new[] { "audio" },
                    AnnotationSource = "Acme realtime policy",
                },
            },
        };
        var engine = new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = manifest,
            CallRoutePlan = ContractCallRoutePlanner.Plan(graph, manifest.CallRoutes, "Player"),
        });

        Assert.Null(engine.Query.TargetSymbolIds);
        Assert.Empty(Assert.Single(engine.Query.Selectors!).TargetSymbolIds);
        engine.Observe(ShapeFact("outside", OperationFactKind.ArrayCreation, outside));
        engine.Observe(ShapeFact("array", OperationFactKind.ArrayCreation, helper));
        engine.Observe(ShapeFact("throw", OperationFactKind.Throw, helper));

        var report = engine.Complete(Receipt(emitted: 3));

        Assert.Equal(2, report.FindingCount);
        Assert.All(report.Findings, finding =>
        {
            Assert.Null(finding.TargetSymbolId);
            Assert.Equal(ContractCallRoutePlacement.Transitive, Assert.Single(finding.CallRouteMatches).Placement);
            Assert.Contains(finding.Evidence, evidence => evidence.Kind == "BoundOccurrence");
        });
    }

    [Fact]
    public void StateAccess_TruncatedFactScanFailsSafeToUnknown()
    {
        const string root = "method:Acme.Audio.Process()";
        const string table = "field:Acme.Audio.Table";
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = root, Name = "Process", Kind = SymbolKind.Method })
            .AddSymbol(new Symbol
            {
                Id = table,
                Name = "Table",
                Kind = SymbolKind.Field,
                IsStatic = true,
                FilePath = "Audio.cs",
                Line = 3,
                Properties = new Dictionary<string, string>
                {
                    [SymbolPropertyKeys.FieldType] = "float[]",
                    [SymbolPropertyKeys.IsReadOnly] = "true",
                    [SymbolPropertyKeys.HasInitializer] = "true",
                },
            })
            .Build();
        var manifest = new ContractManifest
        {
            Id = "state-policy",
            Version = "1",
            CallRoutes = new[]
            {
                new ContractCallRoute { Id = "audio", RootSymbolIds = new[] { root } },
            },
            StateAccesses = new[]
            {
                new StateAccessContract
                {
                    Id = "state",
                    TargetSymbolIds = new[] { table },
                    CallRouteIds = new[] { "audio" },
                    Categories = new[] { "SharedState" },
                },
            },
        };
        var callRoutePlan = ContractCallRoutePlanner.Plan(graph, manifest.CallRoutes, "Player");
        var engine = new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = manifest,
            CallRoutePlan = callRoutePlan,
            StatePlan = ContractStatePlanner.Plan(
                graph,
                manifest.StateAccesses,
                callRoutePlan,
                "Player"),
        });
        engine.Observe(ShapeFact(
            "table-read",
            OperationFactKind.MemberRead,
            root,
            targetSymbolId: table));

        var report = engine.Complete(Receipt(emitted: 1, truncated: true));

        Assert.True(report.Truncated);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(StateRiskBucket.Unknown, finding.StateRiskBucket);
        Assert.Equal(1, Assert.Single(report.StateAccesses).RiskBuckets
            .Single(bucket => bucket.RiskBucket == StateRiskBucket.Unknown).MemberCount);
    }

    [Fact]
    public void StateAccess_ForeignStaticConstructorWriteIsRuntimeMutation()
    {
        const string root = "method:Acme.Audio.Process()";
        const string cache = "field:Acme.Audio.Cache";
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = root, Name = "Process", Kind = SymbolKind.Method })
            .AddSymbol(new Symbol
            {
                Id = cache,
                Name = "Cache",
                Kind = SymbolKind.Field,
                IsStatic = true,
                FilePath = "Audio.cs",
                Line = 3,
                Properties = new Dictionary<string, string>
                {
                    [SymbolPropertyKeys.FieldType] = "int",
                },
            })
            .Build();
        var manifest = new ContractManifest
        {
            Id = "state-policy",
            Version = "1",
            CallRoutes = new[]
            {
                new ContractCallRoute { Id = "audio", RootSymbolIds = new[] { root } },
            },
            StateAccesses = new[]
            {
                new StateAccessContract
                {
                    Id = "state",
                    TargetSymbolIds = new[] { cache },
                    CallRouteIds = new[] { "audio" },
                    Categories = new[] { "SharedState" },
                },
            },
        };
        var callRoutePlan = ContractCallRoutePlanner.Plan(graph, manifest.CallRoutes, "Player");
        var engine = new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = manifest,
            CallRoutePlan = callRoutePlan,
            StatePlan = ContractStatePlanner.Plan(
                graph,
                manifest.StateAccesses,
                callRoutePlan,
                "Player"),
        });
        engine.Observe(ShapeFact(
            "cache-read",
            OperationFactKind.MemberRead,
            root,
            targetSymbolId: cache));
        engine.Observe(ShapeFact(
            "cache-write",
            OperationFactKind.MemberWrite,
            "method:Acme.Other..cctor()",
            targetSymbolId: cache));

        var report = engine.Complete(Receipt(emitted: 2));

        Assert.Equal(StateRiskBucket.RuntimeMutable, Assert.Single(report.Findings).StateRiskBucket);
    }

    [Fact]
    public void Finding_CallRouteMatchesHaveAVisibleHardBound()
    {
        var roots = Enumerable.Range(0, 40).Select(index => $"method:Acme.Root{index}.Run()").ToArray();
        var manifest = new ContractManifest
        {
            Id = "route-bound-policy",
            Version = "1",
            CallRoutes = new[]
            {
                new ContractCallRoute { Id = "a", RootSymbolIds = roots[..20], MaxMembers = 20 },
                new ContractCallRoute { Id = "b", RootSymbolIds = roots[20..], MaxMembers = 20 },
            },
            ExternalApiCosts = new[]
            {
                new ExternalApiCostContract
                {
                    Id = "cost",
                    TargetSymbolIds = new[] { CostTarget },
                    CallRouteIds = new[] { "a", "b" },
                    Categories = new[] { "Allocation" },
                    AnnotationSource = "policy",
                },
            },
        };
        var matches = roots.Select((root, index) => new ContractCallRouteMatch
        {
            RouteId = index < 20 ? "a" : "b",
            RootSymbolId = root,
            ContainingSymbolId = "method:Acme.Dsp.Run()",
            Distance = 1,
            Placement = ContractCallRoutePlacement.Transitive,
            PathSymbolIds = new[] { root, "method:Acme.Dsp.Run()" },
        }).ToArray();
        var engine = new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = manifest,
            CallRoutePlan = new ContractCallRoutePlan
            {
                Routes = new[]
                {
                    new ContractCallRouteReceipt
                    {
                        RouteId = "a", RootSymbolIds = roots[..20], MaxDepth = 8, MaxMembers = 20,
                        Roots = roots[..20].Select(RootEvidence).ToArray(),
                        ReachableMemberCount = 1, MembershipCount = 20, Truncated = false,
                    },
                    new ContractCallRouteReceipt
                    {
                        RouteId = "b", RootSymbolIds = roots[20..], MaxDepth = 8, MaxMembers = 20,
                        Roots = roots[20..].Select(RootEvidence).ToArray(),
                        ReachableMemberCount = 1, MembershipCount = 20, Truncated = false,
                    },
                },
                Matches = matches,
            },
        });
        engine.Observe(Call("cost", CostTarget, "Dsp.cs", 4, Argument(OperationValueKind.Literal)));

        var finding = Assert.Single(engine.Complete(Receipt(emitted: 1)).Findings);

        Assert.Equal(40, finding.CallRouteMatchCount);
        Assert.Equal(32, finding.CallRouteMatches.Length);
        Assert.True(finding.CallRouteMatchesTruncated);
    }

    [Fact]
    public void RouteFact_RequiredOnEveryRoute_ReportsTheMissingRouteAtItsRoot()
    {
        const string target = "method:Acme.Transport.Publish()";
        const string rootA = "method:Acme.Editor.Refresh()";
        const string rootB = "method:Acme.Player.Refresh()";
        const string workA = "method:Acme.Editor.Publish()";
        const string workB = "method:Acme.Player.Publish()";
        var engine = RouteFactEngine(
            new RouteFactContract
            {
                Id = "publish-everywhere",
                Policy = RouteFactPolicy.RequiredOnEveryRoute,
                CallRouteIds = new[] { "editor", "player" },
                OperationKinds = new[] { OperationFactKind.Call },
                TargetSymbolIds = new[] { target },
                Categories = new[] { "Publication" },
            },
            RoutePlan(("editor", rootA, new[] { workA }), ("player", rootB, new[] { workB })));

        engine.Observe(Call(
            "editor-publish",
            target,
            "EditorPublisher.cs",
            12,
            Argument(OperationValueKind.Parameter),
            containingSymbolId: workA));

        var report = engine.Complete(Receipt(emitted: 1));

        var finding = Assert.Single(report.Findings);
        Assert.Equal(ContractFindingKind.MissingRequiredRouteFact, finding.Kind);
        Assert.Equal(rootB, finding.ContainingSymbolId);
        Assert.Equal("method_Acme.Player.Refresh__.cs", finding.Source.FilePath);
        Assert.Equal(ConfidenceBand.Proven, finding.Confidence);
        var coverage = Assert.Single(Assert.Single(report.RuleBreakdown).Contracts);
        Assert.Equal(2, coverage.EvaluatedOccurrenceCount);
        Assert.Equal(1, coverage.FindingFreeOccurrenceCount);
        var receipt = Assert.Single(report.RouteFacts);
        Assert.Equal(1, receipt.SelectedFactCount);
        Assert.True(Assert.Single(receipt.Routes, route => route.RouteId == "editor").RequirementSatisfied);
        Assert.False(Assert.Single(receipt.Routes, route => route.RouteId == "player").RequirementSatisfied);
    }

    [Fact]
    public void RouteFact_EquivalentAcrossRoutes_ComparesOnlyDeclaredSemanticDimensions()
    {
        const string target = "method:Acme.Kernel.Configure(int)";
        const string rootA = "method:Acme.Editor.Configure()";
        const string rootB = "method:Acme.Player.Configure()";
        const string workA = "method:Acme.Editor.ConfigureCore()";
        const string workB = "method:Acme.Player.ConfigureCore()";
        var engine = RouteFactEngine(
            new RouteFactContract
            {
                Id = "configuration-parity",
                Policy = RouteFactPolicy.EquivalentAcrossRoutes,
                CallRouteIds = new[] { "editor", "player" },
                OperationKinds = new[] { OperationFactKind.Call },
                TargetSymbolIds = new[] { target },
                SignatureParts = new[]
                {
                    RouteFactSignaturePart.Kind,
                    RouteFactSignaturePart.TargetSymbol,
                    RouteFactSignaturePart.InputConstants,
                },
                Categories = new[] { "Determinism" },
            },
            RoutePlan(("editor", rootA, new[] { workA }), ("player", rootB, new[] { workB })));

        engine.Observe(Call(
            "editor-shared",
            target,
            "EditorConfig.cs",
            10,
            ArgumentWithConstants(
                OperationValueKind.Literal,
                Array.Empty<string>(),
                Constant(OperationConstantOrigin.Literal, "1", OperationNumericClassification.Finite)),
            containingSymbolId: workA));
        engine.Observe(Call(
            "player-shared",
            target,
            "PlayerConfig.cs",
            10,
            ArgumentWithConstants(
                OperationValueKind.Literal,
                Array.Empty<string>(),
                Constant(OperationConstantOrigin.Literal, "1", OperationNumericClassification.Finite)),
            containingSymbolId: workB));
        engine.Observe(Call(
            "editor-extra",
            target,
            "EditorConfig.cs",
            20,
            ArgumentWithConstants(
                OperationValueKind.Literal,
                Array.Empty<string>(),
                Constant(OperationConstantOrigin.Literal, "2", OperationNumericClassification.Finite)),
            containingSymbolId: workA));

        var report = engine.Complete(Receipt(emitted: 3));

        var finding = Assert.Single(report.Findings);
        Assert.Equal(ContractFindingKind.RouteFactParityMismatch, finding.Kind);
        Assert.Equal(rootB, finding.ContainingSymbolId);
        Assert.Contains("InputConstants", Assert.Single(finding.Evidence, item => item.Kind == "RouteFactSignature").Summary);
        var coverage = Assert.Single(Assert.Single(report.RuleBreakdown).Contracts);
        Assert.Equal(4, coverage.EvaluatedOccurrenceCount);
        Assert.Equal(3, coverage.FindingFreeOccurrenceCount);
        var receipt = Assert.Single(report.RouteFacts);
        Assert.Equal(2, receipt.SignatureCount);
        Assert.Equal(2, Assert.Single(receipt.Routes, route => route.RouteId == "editor").SignatureCount);
        Assert.Equal(1, Assert.Single(receipt.Routes, route => route.RouteId == "player").SignatureCount);
    }

    [Fact]
    public void RouteFact_AllowedRoutesOnly_ReportsExactTargetBypasses()
    {
        const string field = "field:Acme.State.Owner._generation";
        const string root = "method:Acme.State.Owner.Publish()";
        const string owner = "method:Acme.State.Owner.Commit()";
        var engine = RouteFactEngine(
            new RouteFactContract
            {
                Id = "generation-owner",
                Policy = RouteFactPolicy.AllowedRoutesOnly,
                CallRouteIds = new[] { "owner" },
                OperationKinds = new[] { OperationFactKind.MemberWrite },
                TargetSymbolIds = new[] { field },
                Categories = new[] { "Ownership" },
            },
            RoutePlan(truncated: true, ("owner", root, new[] { owner })));

        engine.Observe(ShapeFact(
            "owned-write",
            OperationFactKind.MemberWrite,
            owner,
            targetSymbolId: field));
        engine.Observe(ShapeFact(
            "bypass-write",
            OperationFactKind.MemberWrite,
            "method:Acme.State.Bypass.Commit()",
            targetSymbolId: field));

        var report = engine.Complete(Receipt(emitted: 2));

        var finding = Assert.Single(report.Findings);
        Assert.Equal(ContractFindingKind.RouteFactOutsideOwner, finding.Kind);
        Assert.Equal("bypass-write", finding.FactId);
        Assert.Equal(ConfidenceBand.Advisory, finding.Confidence);
        var coverage = Assert.Single(Assert.Single(report.RuleBreakdown).Contracts);
        Assert.Equal(2, coverage.EvaluatedOccurrenceCount);
        Assert.Equal(1, coverage.FindingFreeOccurrenceCount);
        var receipt = Assert.Single(report.RouteFacts);
        Assert.True(receipt.Incomplete);
        Assert.False(Assert.Single(receipt.Routes).RequirementSatisfied);
    }

    [Fact]
    public void StableFindingIdentity_DoesNotDependOnEngineInstance()
    {
        var first = AuditOneUnsafeGuard();
        var second = AuditOneUnsafeGuard();

        Assert.Equal(Assert.Single(first.Findings).Id, Assert.Single(second.Findings).Id);
    }

    [Fact]
    public void ControlGuard_RequiresTheArgumentSourceAndDeclaredOperator()
    {
        var manifest = Manifest();
        manifest = new ContractManifest
        {
            Id = manifest.Id,
            Version = manifest.Version,
            OperationGuards = new[]
            {
                new OperationGuardContract
                {
                    Id = "rate-is-range-checked",
                    TargetSymbolIds = new[] { GuardTarget },
                    ArgumentOrdinal = 0,
                    AllowedControlContextKinds = new[] { OperationControlContextKind.Branch },
                    AllowedControlOperators = new[] { "GreaterThan" },
                },
            },
        };
        var engine = new ContractAuditEngine(new ContractAuditRequest { Manifest = manifest });
        const string rate = "parameter:method:Acme.Dsp.Run(float)#0:rate";
        const string unrelated = "parameter:method:Acme.Dsp.Run(float)#1:enabled";

        engine.Observe(Call(
            "unrelated-branch",
            GuardTarget,
            "Dsp.cs",
            10,
            Argument(OperationValueKind.Parameter, rate),
            OperationControlContextKind.Branch,
            new[] { rate, unrelated },
            new[] { "NotEquals", "GreaterThan" },
            new[]
            {
                Predicate("NotEquals", rate),
                Predicate("GreaterThan", unrelated),
            }));
        engine.Observe(Call(
            "related-branch",
            GuardTarget,
            "Dsp.cs",
            20,
            Argument(OperationValueKind.Parameter, rate),
            OperationControlContextKind.Branch,
            new[] { rate },
            new[] { "GreaterThan" },
            new[] { Predicate("GreaterThan", rate) }));

        var report = engine.Complete(Receipt(emitted: 2));

        Assert.Equal(1, report.FindingCount);
        Assert.Equal("unrelated-branch", Assert.Single(report.Findings).FactId);
    }

    [Fact]
    public void ValueDomain_AcceptsDirectDomainAndExactDeclaredConversionButReportsMismatch()
    {
        var engine = new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = DomainManifest(),
        });

        engine.Observe(Call("direct", DomainTarget, "Clock.cs", 10, Argument(
            OperationValueKind.Parameter,
            new[] { Seconds })));
        engine.Observe(Call("converted", DomainTarget, "Clock.cs", 20, Argument(
            OperationValueKind.Binary,
            new[] { Frames, SampleRate },
            new[] { "Divide" },
            "frames / sampleRate")));
        engine.Observe(Call("mismatch", DomainTarget, "Clock.cs", 30, Argument(
            OperationValueKind.Parameter,
            new[] { Frames })));

        var report = engine.Complete(Receipt(emitted: 3));

        Assert.Equal(new[] { ContractRuleId.ValueDomain }, report.SelectedRuleIds);
        var finding = Assert.Single(report.Findings);
        Assert.Equal("mismatch", finding.FactId);
        Assert.Equal(ContractFindingKind.ValueDomainMismatch, finding.Kind);
        Assert.Equal(ConfidenceBand.Proven, finding.Confidence);
        Assert.Contains(finding.Evidence, evidence =>
            evidence.Kind == "ObservedDomains"
            && evidence.Summary.Contains("Frames", StringComparison.Ordinal));
    }

    [Fact]
    public void ValueDomain_UnclassifiedPolicyIsExplicitAndAdvisory()
    {
        var engine = new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = DomainManifest(reportUnclassifiedValues: true),
        });

        engine.Observe(Call("unknown", DomainTarget, "Clock.cs", 10, Argument(
            OperationValueKind.Parameter,
            new[] { "parameter:method:Acme.Clock.Other(float)#0:value" })));

        var finding = Assert.Single(engine.Complete(Receipt(emitted: 1)).Findings);

        Assert.Equal(ConfidenceBand.Advisory, finding.Confidence);
        Assert.Contains("unclassified", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValueDomain_DeclaredSourceDomainsDoNotBypassRequiredConversionEvidence()
    {
        var engine = new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = DomainManifest(),
        });

        engine.Observe(Call("missing-operator", DomainTarget, "Clock.cs", 10, Argument(
            OperationValueKind.Binary,
            new[] { Frames, SampleRate },
            Array.Empty<string>(),
            "frames + sampleRate")));

        var finding = Assert.Single(engine.Complete(Receipt(emitted: 1)).Findings);

        Assert.Equal("missing-operator", finding.FactId);
        Assert.Equal(ConfidenceBand.Proven, finding.Confidence);
    }

    [Fact]
    public void ValueDomain_ValidationRejectsUndeclaredConversionDomain()
    {
        var manifest = DomainManifest();
        manifest = new ContractManifest
        {
            Id = manifest.Id,
            Version = manifest.Version,
            ValueDomains = new[]
            {
                new ValueDomainContract
                {
                    Id = "seconds-input",
                    TargetSymbolIds = new[] { DomainTarget },
                    TargetDomain = "Seconds",
                    Bindings = new[]
                    {
                        new ValueDomainBinding { Domain = "Frames", SourceSymbolIds = new[] { Frames } },
                    },
                    AllowedConversions = new[]
                    {
                        new ValueDomainConversion
                        {
                            Id = "ticks-to-seconds",
                            SourceDomains = new[] { "Ticks" },
                        },
                    },
                },
            },
        };

        var error = Assert.Throws<ArgumentException>(() => new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = manifest,
        }));

        Assert.Contains("undeclared source domain 'Ticks'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValueDomain_NonFinitePolicyReportsExplicitValueAndAcceptsDeclaredEvidence()
    {
        const string sanitizer = "method:Acme.Math.RejectNonFinite(float)";
        var engine = new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = DomainManifest(nonFinitePolicy: new ValueDomainNonFinitePolicy
            {
                Action = NonFinitePolicyAction.Reject,
                EvidenceSymbolIds = new[] { sanitizer },
            }),
        });
        var infinity = Constant(
            OperationConstantOrigin.NamedConstant,
            "Infinity",
            OperationNumericClassification.PositiveInfinity,
            "field:float.PositiveInfinity");

        engine.Observe(Call("unsafe-infinity", DomainTarget, "Clock.cs", 10, ArgumentWithConstants(
            OperationValueKind.Constant,
            new[] { Seconds, "field:float.PositiveInfinity" },
            infinity)));
        engine.Observe(Call("sanitized-infinity", DomainTarget, "Clock.cs", 20, ArgumentWithConstants(
            OperationValueKind.Invocation,
            new[] { Seconds, sanitizer, "field:float.PositiveInfinity" },
            infinity)));

        var finding = Assert.Single(engine.Complete(Receipt(emitted: 2)).Findings);

        Assert.Equal("unsafe-infinity", finding.FactId);
        Assert.Equal(ContractFindingKind.NonFinitePolicyMismatch, finding.Kind);
        Assert.Equal(ConfidenceBand.Proven, finding.Confidence);
        Assert.Equal(new[] { "NonFinite", NonFinitePolicyAction.Reject }, finding.Categories);
        Assert.Contains(finding.Evidence, evidence =>
            evidence.Kind == "ConstantProvenance"
            && evidence.Summary.Contains("PositiveInfinity", StringComparison.Ordinal));
    }

    [Fact]
    public void ValueDomain_NonFinitePolicyCanRequireLocalEvidenceForRuntimeValues()
    {
        const string sanitizer = "method:Acme.Math.ClampFinite(float)";
        var engine = new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = DomainManifest(nonFinitePolicy: new ValueDomainNonFinitePolicy
            {
                Action = NonFinitePolicyAction.UseNeutral,
                EvidenceSymbolIds = new[] { sanitizer },
                RequireEvidenceForAllValues = true,
            }),
        });

        engine.Observe(Call("raw-runtime", DomainTarget, "Clock.cs", 10, Argument(
            OperationValueKind.Parameter,
            new[] { Seconds })));
        engine.Observe(Call("sanitized-runtime", DomainTarget, "Clock.cs", 20, Argument(
            OperationValueKind.Invocation,
            new[] { Seconds, sanitizer })));

        var finding = Assert.Single(engine.Complete(Receipt(emitted: 2)).Findings);

        Assert.Equal("raw-runtime", finding.FactId);
        Assert.Equal(ContractFindingKind.NonFinitePolicyMismatch, finding.Kind);
        Assert.Equal(ConfidenceBand.Advisory, finding.Confidence);
        Assert.Contains("UseNeutral", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValueDomain_ConstantPolicyDistinguishesRawLiteralsFromNamedPolicyConstants()
    {
        var engine = new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = DomainManifest(constantPolicy: new ValueDomainConstantPolicy
            {
                ReportRawNumericLiterals = true,
                AllowedLiteralValues = new[] { "1" },
            }),
        });

        engine.Observe(Call("identity", DomainTarget, "Clock.cs", 10, ArgumentWithConstants(
            OperationValueKind.Binary,
            new[] { Seconds },
            Constant(OperationConstantOrigin.Literal, "1", OperationNumericClassification.Finite))));
        engine.Observe(Call("raw-half", DomainTarget, "Clock.cs", 20, ArgumentWithConstants(
            OperationValueKind.Binary,
            new[] { Seconds },
            Constant(OperationConstantOrigin.Literal, "0.5", OperationNumericClassification.Finite))));
        engine.Observe(Call("named-half", DomainTarget, "Clock.cs", 30, ArgumentWithConstants(
            OperationValueKind.Binary,
            new[] { Seconds, "field:Acme.Clock.Half" },
            Constant(
                OperationConstantOrigin.NamedConstant,
                "0.5",
                OperationNumericClassification.Finite,
                "field:Acme.Clock.Half"))));

        var finding = Assert.Single(engine.Complete(Receipt(emitted: 3)).Findings);

        Assert.Equal("raw-half", finding.FactId);
        Assert.Equal(ContractFindingKind.ConstantProvenanceMismatch, finding.Kind);
        Assert.Equal(new[] { "ConstantProvenance", "RawNumericLiteral" }, finding.Categories);
    }

    [Fact]
    public void ValueDomain_NearEqualPolicyGroupsDistinctRawLiteralsAcrossOneBoundedStream()
    {
        var engine = new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = DomainManifest(constantPolicy: new ValueDomainConstantPolicy
            {
                NearEqualPolicy = new ValueDomainNearEqualPolicy
                {
                    AbsoluteTolerance = 0.0001,
                    MinimumOccurrences = 2,
                },
            }),
        });

        engine.Observe(Call("raw-low", DomainTarget, "Clock.cs", 10, ArgumentWithConstants(
            OperationValueKind.Binary,
            new[] { Seconds },
            Constant(OperationConstantOrigin.Literal, "0.001", OperationNumericClassification.Finite))));
        engine.Observe(Call("raw-high", DomainTarget, "Clock.cs", 20, ArgumentWithConstants(
            OperationValueKind.Binary,
            new[] { Seconds },
            Constant(OperationConstantOrigin.Literal, "0.00105", OperationNumericClassification.Finite))));
        engine.Observe(Call("named-near", DomainTarget, "Clock.cs", 30, ArgumentWithConstants(
            OperationValueKind.Binary,
            new[] { Seconds, "field:Acme.Clock.PolicyEpsilon" },
            Constant(
                OperationConstantOrigin.NamedConstant,
                "0.00104",
                OperationNumericClassification.Finite,
                "field:Acme.Clock.PolicyEpsilon"))));
        engine.Observe(Call("raw-far", DomainTarget, "Clock.cs", 40, ArgumentWithConstants(
            OperationValueKind.Binary,
            new[] { Seconds },
            Constant(OperationConstantOrigin.Literal, "0.002", OperationNumericClassification.Finite))));

        var finding = Assert.Single(engine.Complete(Receipt(emitted: 4)).Findings);

        Assert.Equal(ContractFindingKind.NearEqualConstantGroup, finding.Kind);
        Assert.Equal(new[] { "ConstantProvenance", "NearEqual" }, finding.Categories);
        Assert.Equal(2, finding.Evidence.Count(evidence => evidence.Kind == "NearEqualConstant"));
        Assert.Contains(finding.Evidence, evidence =>
            evidence.Kind == "NearEqualPolicy"
            && evidence.Summary.Contains("absoluteTolerance=0.0001", StringComparison.Ordinal));
        Assert.DoesNotContain(finding.Evidence, evidence =>
            evidence.Summary.Contains("0.00104", StringComparison.Ordinal));
    }

    [Fact]
    public void ValueDomain_BoundaryPolicyAcceptsDeclaredCadenceShapesAndReportsMismatchOrAbsence()
    {
        var policy = new ValueDomainBoundaryPolicy
        {
            BoundarySourceSymbolIds = new[] { Frames },
            AllowedShapes = new[]
            {
                new ValueDomainBoundaryShape
                {
                    ComparisonOperator = "LessThan",
                    BoundarySide = BoundaryOperandSide.Right,
                    BoundaryValueKinds = new[] { OperationValueKind.Parameter },
                },
                new ValueDomainBoundaryShape
                {
                    ComparisonOperator = "LessThanOrEqual",
                    BoundarySide = BoundaryOperandSide.Right,
                    BoundaryValueKinds = new[] { OperationValueKind.Binary },
                    BoundaryOperators = new[] { "Subtract" },
                    BoundaryConstantValues = new[] { "1" },
                },
            },
        };
        var engine = new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = DomainManifest(boundaryPolicy: policy),
        });
        var cursor = Value(OperationValueKind.Parameter, new[] { Seconds });
        var directBoundary = Value(OperationValueKind.Parameter, new[] { Frames });
        var offsetBoundary = Value(
            OperationValueKind.Binary,
            new[] { Frames },
            new[] { "Subtract" },
            Constant(OperationConstantOrigin.Literal, "1", OperationNumericClassification.Finite));
        var duplicateOffsetBoundary = Value(
            OperationValueKind.Binary,
            new[] { Frames },
            new[] { "Subtract", "Subtract" },
            Constant(OperationConstantOrigin.Literal, "1", OperationNumericClassification.Finite),
            Constant(OperationConstantOrigin.Literal, "1", OperationNumericClassification.Finite));

        engine.Observe(Call(
            "direct",
            DomainTarget,
            "Clock.cs",
            10,
            Argument(OperationValueKind.Parameter, Seconds),
            OperationControlContextKind.Loop,
            new[] { Seconds, Frames },
            new[] { "LessThan" },
            new[] { BoundaryPredicate("LessThan", cursor, directBoundary, 9) }));
        engine.Observe(Call(
            "offset",
            DomainTarget,
            "Clock.cs",
            20,
            Argument(OperationValueKind.Parameter, Seconds),
            OperationControlContextKind.Loop,
            new[] { Seconds, Frames },
            new[] { "LessThanOrEqual" },
            new[] { BoundaryPredicate("LessThanOrEqual", cursor, offsetBoundary, 19) }));
        engine.Observe(Call(
            "mismatch",
            DomainTarget,
            "Clock.cs",
            30,
            Argument(OperationValueKind.Parameter, Seconds),
            OperationControlContextKind.Loop,
            new[] { Seconds, Frames },
            new[] { "LessThanOrEqual" },
            new[] { BoundaryPredicate("LessThanOrEqual", cursor, directBoundary, 29) }));
        engine.Observe(Call(
            "duplicate-offset",
            DomainTarget,
            "Clock.cs",
            35,
            Argument(OperationValueKind.Parameter, Seconds),
            OperationControlContextKind.Loop,
            new[] { Seconds, Frames },
            new[] { "LessThanOrEqual" },
            new[] { BoundaryPredicate("LessThanOrEqual", cursor, duplicateOffsetBoundary, 34) }));
        engine.Observe(Call(
            "missing",
            DomainTarget,
            "Clock.cs",
            40,
            Argument(OperationValueKind.Parameter, Seconds)));

        var findings = engine.Complete(Receipt(emitted: 5)).Findings;

        Assert.Equal(3, findings.Length);
        var mismatch = Assert.Single(findings, finding => finding.FactId == "mismatch");
        Assert.Equal(ContractFindingKind.CadenceBoundaryMismatch, mismatch.Kind);
        Assert.Equal(ConfidenceBand.Proven, mismatch.Confidence);
        Assert.Equal(new[] { "Cadence", "Boundary", "Mismatch" }, mismatch.Categories);
        Assert.Contains(mismatch.Evidence, evidence =>
            evidence.Kind == "CadenceBoundary"
            && evidence.Source?.Line == 29);
        var duplicateOffset = Assert.Single(findings, finding => finding.FactId == "duplicate-offset");
        Assert.Equal(ConfidenceBand.Proven, duplicateOffset.Confidence);
        var missing = Assert.Single(findings, finding => finding.FactId == "missing");
        Assert.Equal(ConfidenceBand.Advisory, missing.Confidence);
        Assert.Equal(new[] { "Cadence", "Boundary", "Missing" }, missing.Categories);
    }

    [Fact]
    public void ValueDomain_PolicyValidationRejectsUnknownOrUnsatisfiableShapes()
    {
        var unknownAction = Assert.Throws<ArgumentException>(() => new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = DomainManifest(nonFinitePolicy: new ValueDomainNonFinitePolicy
            {
                Action = "MakeItFine",
            }),
        }));
        Assert.Contains("unknown nonFinitePolicy action", unknownAction.Message, StringComparison.Ordinal);

        var inertException = Assert.Throws<ArgumentException>(() => new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = DomainManifest(constantPolicy: new ValueDomainConstantPolicy
            {
                AllowedLiteralValues = new[] { "0" },
            }),
        }));
        Assert.Contains("raw-literal reporting is disabled", inertException.Message, StringComparison.Ordinal);

        var inertNearEqual = Assert.Throws<ArgumentException>(() => new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = DomainManifest(constantPolicy: new ValueDomainConstantPolicy
            {
                NearEqualPolicy = new ValueDomainNearEqualPolicy(),
            }),
        }));
        Assert.Contains("requires a finite positive", inertNearEqual.Message, StringComparison.Ordinal);

        var missingBoundaryShape = Assert.Throws<ArgumentException>(() => new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = DomainManifest(boundaryPolicy: new ValueDomainBoundaryPolicy
            {
                BoundarySourceSymbolIds = new[] { Frames },
            }),
        }));
        Assert.Contains("allowedShapes must contain", missingBoundaryShape.Message, StringComparison.Ordinal);

        var nullBoundaryShape = Assert.Throws<ArgumentException>(() => new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = DomainManifest(boundaryPolicy: new ValueDomainBoundaryPolicy
            {
                BoundarySourceSymbolIds = new[] { Frames },
                AllowedShapes = new ValueDomainBoundaryShape[] { null! },
            }),
        }));
        Assert.Contains("cannot contain null", nullBoundaryShape.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestValidation_RejectsUnknownSchemaAndSelectorlessSuppression()
    {
        var schemaError = Assert.Throws<ArgumentException>(() => new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = new ContractManifest
            {
                SchemaVersion = "99",
                Id = "bad",
                Version = "1",
            },
        }));
        Assert.Contains("Unsupported contract manifest schema", schemaError.Message, StringComparison.Ordinal);

        var suppressionError = Assert.Throws<ArgumentException>(() => new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = Manifest(suppressions: new[]
            {
                new ContractSuppression { Id = "too-broad", Reason = "No selector." },
            }),
        }));
        Assert.Contains("at least one selector", suppressionError.Message, StringComparison.Ordinal);

        var emptyError = Assert.Throws<ArgumentException>(() => new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = new ContractManifest { Id = "empty", Version = "1" },
        }));
        Assert.Contains("did not select any", emptyError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationShape_OneStreamChecksBufferSidecarAndMaskRepresentation()
    {
        const string containing = "method:Acme.Buffer.Process(int[],int,int)";
        const string samples = "parameter:method:Acme.Buffer.Process(int[],int,int)#0:samples";
        const string frameIndex = "parameter:method:Acme.Buffer.Process(int[],int,int)#1:frameIndex";
        const string maskBit = "parameter:method:Acme.Buffer.Process(int[],int,int)#2:maskBit";
        const string channelIndex = "local:method:Acme.Buffer.Process(int[],int,int)@42:channelIndex";
        var manifest = new ContractManifest
        {
            Id = "acme-shapes",
            Version = "2026.07.17",
            OperationShapes = new[]
            {
                new OperationShapeContract
                {
                    Id = "sample-index-shape",
                    OperationKinds = new[] { OperationFactKind.ElementAccess },
                    ContainingSymbolIds = new[] { containing },
                    AllowedShapes = new[]
                    {
                        new OperationAllowedShape
                        {
                            Id = "frames-index-samples",
                            AllowedResultTypes = new[] { "float" },
                            Inputs = new[]
                            {
                                new OperationInputShape
                                {
                                    Role = OperationInputRole.Receiver,
                                    AnySourceSymbolIds = new[] { samples },
                                },
                                new OperationInputShape
                                {
                                    Role = OperationInputRole.Index,
                                    Ordinal = 0,
                                    RequiredSourceSymbolIds = new[] { frameIndex },
                                },
                            },
                            ControlContexts = new[]
                            {
                                new OperationControlShape
                                {
                                    Kind = OperationControlContextKind.Loop,
                                    RequiredSourceSymbolIds = new[] { frameIndex },
                                    RequiredOperators = new[] { "LessThan" },
                                },
                            },
                        },
                    },
                    Categories = new[] { "Sidecar", "BufferShape" },
                },
                new OperationShapeContract
                {
                    Id = "mask-shift-width",
                    OperationKinds = new[] { OperationFactKind.Binary },
                    ContainingSymbolIds = new[] { containing },
                    Operators = new[] { "LeftShift" },
                    AllowedShapes = new[]
                    {
                        new OperationAllowedShape
                        {
                            Id = "unsigned-64-bit-mask",
                            AllowedResultTypes = new[] { "ulong" },
                            Inputs = new[]
                            {
                                new OperationInputShape
                                {
                                    Role = OperationInputRole.Left,
                                    AllowedTypes = new[] { "ulong" },
                                    RequiredConstantValues = new[] { "1" },
                                    CompileTimeConstant = true,
                                },
                                new OperationInputShape
                                {
                                    Role = OperationInputRole.Right,
                                    RequiredSourceSymbolIds = new[] { maskBit },
                                },
                            },
                        },
                    },
                    Categories = new[] { "MaskWidth" },
                },
            },
        };
        var engine = new ContractAuditEngine(new ContractAuditRequest { Manifest = manifest });

        Assert.Null(engine.Query.TargetSymbolIds);
        Assert.Equal(2, engine.Query.Selectors!.Count);
        Assert.Contains(engine.Query.Selectors, selector =>
            selector.IncludeKinds.SequenceEqual(new[] { OperationFactKind.ElementAccess })
            && selector.ContainingSymbolIds.SequenceEqual(new[] { containing }));
        engine.Observe(ShapeFact(
            "valid-sidecar",
            OperationFactKind.ElementAccess,
            containing,
            resultType: "float",
            inputs: new[]
            {
                ShapeInput(OperationInputRole.Receiver, null, OperationValueKind.Parameter, "float[]", samples),
                ShapeInput(OperationInputRole.Index, 0, OperationValueKind.Parameter, "int", frameIndex),
            },
            controls: new[] { ShapeLoop(frameIndex) }));
        engine.Observe(ShapeFact(
            "wrong-sidecar-dimension",
            OperationFactKind.ElementAccess,
            containing,
            resultType: "float",
            inputs: new[]
            {
                ShapeInput(OperationInputRole.Receiver, null, OperationValueKind.Parameter, "float[]", samples),
                ShapeInput(OperationInputRole.Index, 0, OperationValueKind.Local, "int", channelIndex),
            },
            controls: new[] { ShapeLoop(channelIndex) }));
        engine.Observe(ShapeFact(
            "valid-mask",
            OperationFactKind.Binary,
            containing,
            operation: "LeftShift",
            resultType: "ulong",
            inputs: new[]
            {
                ShapeInput(
                    OperationInputRole.Left,
                    null,
                    OperationValueKind.Literal,
                    "ulong",
                    compileTimeConstant: true,
                    constants: new[] { Constant(OperationConstantOrigin.Literal, "1", OperationNumericClassification.Finite) }),
                ShapeInput(OperationInputRole.Right, null, OperationValueKind.Parameter, "int", maskBit),
            }));
        engine.Observe(ShapeFact(
            "narrow-mask",
            OperationFactKind.Binary,
            containing,
            operation: "LeftShift",
            resultType: "int",
            inputs: new[]
            {
                ShapeInput(
                    OperationInputRole.Left,
                    null,
                    OperationValueKind.Literal,
                    "int",
                    compileTimeConstant: true,
                    constants: new[] { Constant(OperationConstantOrigin.Literal, "1", OperationNumericClassification.Finite) }),
                ShapeInput(OperationInputRole.Right, null, OperationValueKind.Parameter, "int", maskBit),
            }));

        var report = engine.Complete(Receipt(emitted: 4));

        Assert.Equal(new[] { ContractRuleId.OperationShape }, report.SelectedRuleIds);
        Assert.Equal(2, report.FindingCount);
        var sidecar = Assert.Single(report.Findings, finding => finding.FactId == "wrong-sidecar-dimension");
        Assert.Equal(ContractFindingKind.OperationShapeMismatch, sidecar.Kind);
        Assert.Equal(ConfidenceBand.Proven, sidecar.Confidence);
        Assert.Equal(new[] { "BufferShape", "Sidecar" }, sidecar.Categories);
        Assert.Contains(sidecar.Evidence, evidence =>
            evidence.Kind == "AllowedShapeMismatch"
            && evidence.Summary.Contains("frameIndex", StringComparison.Ordinal));
        var mask = Assert.Single(report.Findings, finding => finding.FactId == "narrow-mask");
        Assert.Equal(new[] { "MaskWidth" }, mask.Categories);
        Assert.Contains(mask.Evidence, evidence =>
            evidence.Kind == "AllowedShapeMismatch"
            && evidence.Summary.Contains("result type 'int'", StringComparison.Ordinal));
        Assert.Contains(report.Limitations, limitation =>
            limitation.Contains("enum/table coverage", StringComparison.Ordinal));
        Assert.Equal(0, report.ScanReceipt.AdditionalSemanticBaseCount);
    }

    [Fact]
    public void OperationShape_OneStreamChecksLifecycleSmoothingAndDiscontinuityEvidence()
    {
        const string render = "method:Acme.Voice.Render(float,float)";
        const string tail = "field:Acme.Voice._tail";
        const string level = "parameter:method:Acme.Voice.Render(float,float)#0:level";
        const string control = "parameter:method:Acme.Voice.Render(float,float)#1:control";
        const string smoother = "method:Acme.Smoother.Step(float)";
        const string sink = "method:Acme.Voice.Apply(float)";
        var manifest = new ContractManifest
        {
            Id = "acme-temporal-contracts",
            Version = "2026.07.17",
            OperationShapes = new[]
            {
                new OperationShapeContract
                {
                    Id = "tail-reset-only-on-idle-arm",
                    OperationKinds = new[] { OperationFactKind.Assignment },
                    TargetSymbolIds = new[] { tail },
                    ContainingSymbolIds = new[] { render },
                    AllowedShapes = new[]
                    {
                        new OperationAllowedShape
                        {
                            Id = "zero-on-true-idle-arm",
                            Inputs = new[]
                            {
                                new OperationInputShape
                                {
                                    Role = OperationInputRole.Value,
                                    RequiredConstantValues = new[] { "0" },
                                },
                            },
                            ControlContexts = new[]
                            {
                                new OperationControlShape
                                {
                                    Kind = OperationControlContextKind.Branch,
                                    AllowedBranchArms = new[] { OperationBranchArm.WhenTrue },
                                    RequiredSourceSymbolIds = new[] { level },
                                    RequiredOperators = new[] { "LessThanOrEqual" },
                                },
                            },
                        },
                    },
                    Categories = new[] { "Lifecycle", "StateReset" },
                },
                new OperationShapeContract
                {
                    Id = "control-is-smoothed-in-sample-loop",
                    OperationKinds = new[] { OperationFactKind.Call },
                    TargetSymbolIds = new[] { sink },
                    ContainingSymbolIds = new[] { render },
                    AllowedShapes = new[]
                    {
                        new OperationAllowedShape
                        {
                            Id = "declared-smoother-route",
                            Inputs = new[]
                            {
                                new OperationInputShape
                                {
                                    Role = OperationInputRole.Argument,
                                    Ordinal = 0,
                                    RequiredSourceSymbolIds = new[] { control, smoother },
                                },
                            },
                            ControlContexts = new[]
                            {
                                new OperationControlShape { Kind = OperationControlContextKind.Loop },
                            },
                        },
                    },
                    Categories = new[] { "RateTransition", "Smoothing" },
                },
                new OperationShapeContract
                {
                    Id = "branch-return-has-no-hard-zero",
                    OperationKinds = new[] { OperationFactKind.Return },
                    ContainingSymbolIds = new[] { render },
                    AllowedShapes = new[]
                    {
                        new OperationAllowedShape
                        {
                            Id = "continuous-return",
                            Inputs = new[]
                            {
                                new OperationInputShape
                                {
                                    Role = OperationInputRole.ReturnedValue,
                                    ForbiddenConstantValues = new[] { "0" },
                                },
                            },
                            ControlContexts = new[]
                            {
                                new OperationControlShape
                                {
                                    Kind = OperationControlContextKind.Branch,
                                    AllowedBranchArms = new[]
                                    {
                                        OperationBranchArm.WhenTrue,
                                        OperationBranchArm.WhenFalse,
                                    },
                                },
                            },
                        },
                    },
                    Categories = new[] { "Discontinuity", "HardGate" },
                    Severity = ContractSeverity.Info,
                },
            },
        };
        var engine = new ContractAuditEngine(new ContractAuditRequest { Manifest = manifest });

        engine.Observe(ShapeFact(
            "safe-reset",
            OperationFactKind.Assignment,
            render,
            inputs: new[] { ConstantShapeInput(OperationInputRole.Value, "0") },
            controls: new[] { ShapeBranch(level, "LessThanOrEqual", OperationBranchArm.WhenTrue) },
            targetSymbolId: tail));
        engine.Observe(ShapeFact(
            "wrong-arm-reset",
            OperationFactKind.Assignment,
            render,
            inputs: new[] { ConstantShapeInput(OperationInputRole.Value, "0") },
            controls: new[] { ShapeBranch(level, "LessThanOrEqual", OperationBranchArm.WhenFalse) },
            targetSymbolId: tail));
        engine.Observe(ShapeFact(
            "smoothed-control",
            OperationFactKind.Call,
            render,
            inputs: new[]
            {
                ShapeInput(
                    OperationInputRole.Argument,
                    0,
                    OperationValueKind.Invocation,
                    "float",
                    sourceIds: new[] { control, smoother }),
            },
            controls: new[] { ShapeLoop("local:sample") },
            targetSymbolId: sink));
        engine.Observe(ShapeFact(
            "direct-control",
            OperationFactKind.Call,
            render,
            inputs: new[]
            {
                ShapeInput(OperationInputRole.Argument, 0, OperationValueKind.Parameter, "float", control),
            },
            controls: new[] { ShapeLoop("local:sample") },
            targetSymbolId: sink));
        engine.Observe(ShapeFact(
            "continuous-return",
            OperationFactKind.Return,
            render,
            inputs: new[]
            {
                ShapeInput(OperationInputRole.ReturnedValue, null, OperationValueKind.Parameter, "float", control),
            },
            controls: new[] { ShapeBranch(level, "GreaterThan", OperationBranchArm.WhenTrue) }));
        engine.Observe(ShapeFact(
            "hard-zero-return",
            OperationFactKind.Return,
            render,
            inputs: new[] { ConstantShapeInput(OperationInputRole.ReturnedValue, "0") },
            controls: new[] { ShapeBranch(level, "GreaterThan", OperationBranchArm.WhenFalse) }));

        var report = engine.Complete(Receipt(emitted: 6));

        Assert.Equal(3, report.FindingCount);
        Assert.Contains(report.Findings, finding =>
            finding.FactId == "wrong-arm-reset"
            && finding.Categories.SequenceEqual(new[] { "Lifecycle", "StateReset" })
            && finding.Evidence.Any(evidence =>
                evidence.Kind == "ControlContext"
                && evidence.Summary.Contains("Branch[WhenFalse]", StringComparison.Ordinal)));
        Assert.Contains(report.Findings, finding =>
            finding.FactId == "direct-control"
            && finding.Evidence.Any(evidence =>
                evidence.Kind == "AllowedShapeMismatch"
                && evidence.Summary.Contains(smoother, StringComparison.Ordinal)));
        Assert.Contains(report.Findings, finding =>
            finding.FactId == "hard-zero-return"
            && finding.Severity == ContractSeverity.Info
            && finding.Evidence.Any(evidence =>
                evidence.Kind == "AllowedShapeMismatch"
                && evidence.Summary.Contains("forbidden constants [0]", StringComparison.Ordinal)));

        var rule = Assert.Single(report.RuleBreakdown);
        Assert.Equal(3, rule.Contracts.Length);
        Assert.All(rule.Contracts, contract =>
        {
            Assert.Equal(2, contract.EvaluatedOccurrenceCount);
            Assert.Equal(1, contract.FindingFreeOccurrenceCount);
            Assert.Equal(1, contract.FindingCount);
            Assert.Equal(0, contract.SuppressedFindingCount);
        });
        Assert.Equal(0, report.ScanReceipt.AdditionalSemanticBaseCount);
    }

    [Fact]
    public void OperationShape_ValidationRejectsInertAllowedShape()
    {
        var error = Assert.Throws<ArgumentException>(() => new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = new ContractManifest
            {
                Id = "inert-shape",
                Version = "1",
                OperationShapes = new[]
                {
                    new OperationShapeContract
                    {
                        Id = "inert",
                        OperationKinds = new[] { OperationFactKind.Binary },
                        AllowedShapes = new[] { new OperationAllowedShape { Id = "accepts-everything" } },
                    },
                },
            },
        }));

        Assert.Contains("must constrain an input, result type, or control context", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationShape_UniquenessReportsDuplicateMaskPositionsOnce()
    {
        const string containing = "type:Acme.FieldMask";
        var allowedBits = Enumerable.Range(0, 64)
            .Select(value => value.ToString(CultureInfo.InvariantCulture))
            .ToArray();
        var manifest = new ContractManifest
        {
            Id = "field-mask-shapes",
            Version = "1",
            OperationShapes = new[]
            {
                new OperationShapeContract
                {
                    Id = "field-mask-bits",
                    OperationKinds = new[] { OperationFactKind.Binary },
                    ContainingSymbolIds = new[] { containing },
                    Operators = new[] { "LeftShift" },
                    AllowedShapes = new[]
                    {
                        new OperationAllowedShape
                        {
                            Id = "ulong-bit",
                            AllowedResultTypes = new[] { "ulong" },
                            Inputs = new[]
                            {
                                new OperationInputShape
                                {
                                    Role = OperationInputRole.Left,
                                    AllowedTypes = new[] { "ulong" },
                                    RequiredConstantValues = new[] { "1" },
                                },
                                new OperationInputShape
                                {
                                    Role = OperationInputRole.Right,
                                    AllowedConstantValues = allowedBits,
                                },
                            },
                        },
                    },
                    UniquenessPolicy = new OperationShapeUniquenessPolicy
                    {
                        InputRole = OperationInputRole.Right,
                        KeyKind = OperationShapeKeyKind.ConstantValue,
                    },
                    Categories = new[] { "MaskWidth" },
                },
            },
        };
        var engine = new ContractAuditEngine(new ContractAuditRequest { Manifest = manifest });

        engine.Observe(MaskShift("bit-three-a", containing, "3"));
        engine.Observe(MaskShift("bit-four", containing, "4"));
        engine.Observe(MaskShift("bit-three-b", containing, "3"));

        var finding = Assert.Single(engine.Complete(Receipt(emitted: 3)).Findings);

        Assert.Equal(ContractFindingKind.DuplicateOperationShapeKey, finding.Kind);
        Assert.Equal(new[] { "Duplicate", "MaskWidth" }, finding.Categories);
        Assert.Contains("'3' occurs 2 times", finding.Message, StringComparison.Ordinal);
        Assert.Equal(2, finding.Evidence.Count(evidence => evidence.Kind == "DuplicateOperationShapeKey"));
    }

    [Fact]
    public void ApplicationUseCase_DelegatesOneExactBoundedStream()
    {
        var expected = new OperationFactQuery
        {
            TargetSymbolIds = new[] { GuardTarget },
            MaxFacts = 17,
        };
        var provider = new RecordingProvider();
        var consumed = 0;

        var receipt = new ScanOperationFactsUseCase(provider).Execute(
            expected,
            _ =>
            {
                consumed++;
                return true;
            });

        Assert.Same(expected, provider.Query);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(1, consumed);
        Assert.Equal(1, receipt.EmittedFactCount);
    }

    private static ContractAuditEngine RouteFactEngine(
        RouteFactContract contract,
        ContractCallRoutePlan plan)
        => new(new ContractAuditRequest
        {
            Manifest = new ContractManifest
            {
                Id = "route-fact-policy",
                Version = "1",
                CallRoutes = plan.Routes.Select(receipt => new ContractCallRoute
                {
                    Id = receipt.RouteId,
                    RootSymbolIds = receipt.RootSymbolIds,
                    MaxDepth = receipt.MaxDepth,
                    MaxMembers = receipt.MaxMembers,
                }).ToArray(),
                RouteFacts = new[] { contract },
            },
            CallRoutePlan = plan,
        });

    private static ContractCallRoutePlan RoutePlan(
        params (string RouteId, string RootId, string[] MemberIds)[] routes)
        => RoutePlan(truncated: false, routes);

    private static ContractCallRoutePlan RoutePlan(
        bool truncated,
        params (string RouteId, string RootId, string[] MemberIds)[] routes)
        => new()
        {
            Routes = routes.Select(route =>
            {
                var members = route.MemberIds
                    .Prepend(route.RootId)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                return new ContractCallRouteReceipt
                {
                    RouteId = route.RouteId,
                    RootSymbolIds = new[] { route.RootId },
                    Roots = new[] { RootEvidence(route.RootId) },
                    MaxDepth = 8,
                    MaxMembers = 64,
                    ReachableMemberCount = members.Length,
                    MembershipCount = members.Length,
                    Truncated = truncated,
                };
            }).ToArray(),
            Matches = routes.SelectMany(route => route.MemberIds
                .Prepend(route.RootId)
                .Distinct(StringComparer.Ordinal)
                .Select(memberId => new ContractCallRouteMatch
                {
                    RouteId = route.RouteId,
                    RootSymbolId = route.RootId,
                    ContainingSymbolId = memberId,
                    Distance = string.Equals(memberId, route.RootId, StringComparison.Ordinal) ? 0 : 1,
                    Placement = string.Equals(memberId, route.RootId, StringComparison.Ordinal)
                        ? ContractCallRoutePlacement.Direct
                        : ContractCallRoutePlacement.Transitive,
                    PathSymbolIds = string.Equals(memberId, route.RootId, StringComparison.Ordinal)
                        ? new[] { route.RootId }
                        : new[] { route.RootId, memberId },
                }))
                .ToArray(),
        };

    private static ContractCallRouteRootReceipt RootEvidence(string symbolId)
        => new()
        {
            SymbolId = symbolId,
            Source = Span(
                symbolId.Replace(':', '_').Replace('(', '_').Replace(')', '_') + ".cs",
                1),
        };

    private static ContractAuditReport AuditOneUnsafeGuard()
    {
        var engine = new ContractAuditEngine(new ContractAuditRequest { Manifest = Manifest() });
        engine.Observe(Call("unsafe", GuardTarget, "Dsp.cs", 7, Argument(OperationValueKind.Parameter)));
        return engine.Complete(Receipt(emitted: 1));
    }

    private static ContractManifest Manifest(ContractSuppression[]? suppressions = null)
        => new()
        {
            Id = "acme-runtime-contracts",
            Version = "2026.07.16",
            OperationGuards = new[]
            {
                new OperationGuardContract
                {
                    Id = "rate-is-normalized",
                    TargetSymbolIds = new[] { GuardTarget },
                    ArgumentOrdinal = 0,
                    AllowedSourceSymbolIds = new[] { "method:Acme.Math.Clamp(float,float,float)" },
                    Guidance = "Clamp the rate through the owned math authority.",
                },
            },
            ExternalApiCosts = new[]
            {
                new ExternalApiCostContract
                {
                    Id = "vendor-allocation",
                    TargetSymbolIds = new[] { CostTarget },
                    Categories = new[] { "CacheRequired", "Allocation" },
                    ControlContextKinds = new[] { OperationControlContextKind.Loop },
                    AnnotationSource = "Acme performance policy 4",
                    AppliesToVersion = "Vendor.Api >= 3",
                },
            },
            Suppressions = suppressions ?? Array.Empty<ContractSuppression>(),
        };

    private static ContractManifest DomainManifest(
        bool reportUnclassifiedValues = false,
        ValueDomainNonFinitePolicy? nonFinitePolicy = null,
        ValueDomainConstantPolicy? constantPolicy = null,
        ValueDomainBoundaryPolicy? boundaryPolicy = null)
        => new()
        {
            Id = "acme-time-domains",
            Version = "2026.07.16",
            ValueDomains = new[]
            {
                new ValueDomainContract
                {
                    Id = "seconds-input",
                    TargetSymbolIds = new[] { DomainTarget },
                    TargetDomain = "Seconds",
                    Bindings = new[]
                    {
                        new ValueDomainBinding { Domain = "Frames", SourceSymbolIds = new[] { Frames } },
                        new ValueDomainBinding { Domain = "SampleRate", SourceSymbolIds = new[] { SampleRate } },
                        new ValueDomainBinding { Domain = "Seconds", SourceSymbolIds = new[] { Seconds } },
                    },
                    AllowedConversions = new[]
                    {
                        new ValueDomainConversion
                        {
                            Id = "frames-at-rate-to-seconds",
                            SourceDomains = new[] { "Frames", "SampleRate" },
                            RequiredSourceSymbolIds = new[] { SampleRate },
                            RequiredOperators = new[] { "Divide" },
                        },
                    },
                    NonFinitePolicy = nonFinitePolicy,
                    ConstantPolicy = constantPolicy,
                    BoundaryPolicy = boundaryPolicy,
                    ReportUnclassifiedValues = reportUnclassifiedValues,
                },
            },
        };

    private static OperationFact Call(
        string id,
        string target,
        string path,
        int line,
        OperationInputFact argument,
        string? context = null,
        string[]? contextSourceIds = null,
        string[]? contextOperators = null,
        OperationControlPredicate[]? contextPredicates = null,
        string containingSymbolId = "method:Acme.Dsp.Run()")
        => new()
        {
            Id = id,
            Kind = OperationFactKind.Call,
            ModuleName = "Acme.Runtime",
            ProfileScope = "Player",
            ContainingSymbolId = containingSymbolId,
            TargetSymbolId = target,
            Source = Span(path, line),
            IsImplicit = false,
            Inputs = new[] { argument },
            ControlContexts = context == null
                ? Array.Empty<OperationControlContext>()
                : new[]
                {
                    new OperationControlContext
                    {
                        Kind = context,
                        Condition = "i < frames",
                        ConditionValue = new OperationValueFact
                        {
                            Kind = OperationValueKind.Binary,
                            Expression = "i < frames",
                            IsCompileTimeConstant = false,
                            SourceSymbolIds = contextSourceIds ?? Array.Empty<string>(),
                        },
                        Operators = contextOperators ?? new[] { "LessThan" },
                        Predicates = contextPredicates
                            ?? new[] { Predicate((contextOperators ?? new[] { "LessThan" })[0], contextSourceIds ?? Array.Empty<string>()) },
                        Source = Span(path, Math.Max(1, line - 1)),
                    },
                },
        };

    private static OperationControlPredicate Predicate(string operatorName, params string[] sourceIds)
        => new()
        {
            Operator = operatorName,
            Expression = "predicate",
            SourceSymbolIds = sourceIds,
            Source = Span("Clock.cs", 1),
        };

    private static OperationControlPredicate BoundaryPredicate(
        string operatorName,
        OperationValueFact left,
        OperationValueFact right,
        int line)
        => new()
        {
            Operator = operatorName,
            Expression = $"{left.Expression} {operatorName} {right.Expression}",
            SourceSymbolIds = left.SourceSymbolIds.Concat(right.SourceSymbolIds).Distinct(StringComparer.Ordinal).ToArray(),
            LeftValue = left,
            RightValue = right,
            Source = Span("Clock.cs", line),
        };

    private static OperationValueFact Value(
        string kind,
        string[] sourceIds,
        string[]? operators = null,
        params OperationConstantFact[] constants)
        => new()
        {
            Kind = kind,
            Expression = "value",
            IsCompileTimeConstant = false,
            SourceSymbolIds = sourceIds,
            Operators = operators ?? Array.Empty<string>(),
            Constants = constants,
        };

    private static OperationInputFact Argument(string kind, params string[] sourceIds)
        => Argument(kind, sourceIds, Array.Empty<string>(), "value");

    private static OperationInputFact Argument(
        string kind,
        string[] sourceIds,
        string[] operators,
        string expression = "value")
        => new()
        {
            Role = OperationInputRole.Argument,
            Ordinal = 0,
            ParameterName = "value",
            AuthorSupplied = true,
            Value = new OperationValueFact
            {
                Kind = kind,
                Expression = expression,
                IsCompileTimeConstant = kind == OperationValueKind.Literal,
                SourceSymbolIds = sourceIds,
                Operators = operators,
            },
        };

    private static OperationInputFact ArgumentWithConstants(
        string kind,
        string[] sourceIds,
        params OperationConstantFact[] constants)
        => new()
        {
            Role = OperationInputRole.Argument,
            Ordinal = 0,
            ParameterName = "value",
            AuthorSupplied = true,
            Value = new OperationValueFact
            {
                Kind = kind,
                Expression = "value expression",
                IsCompileTimeConstant = kind == OperationValueKind.Constant,
                SourceSymbolIds = sourceIds,
                Constants = constants,
            },
        };

    private static OperationConstantFact Constant(
        string origin,
        string value,
        string numericClassification,
        string? symbolId = null)
        => new()
        {
            Origin = origin,
            SymbolId = symbolId,
            Type = "float",
            Value = value,
            NumericClassification = numericClassification,
            Expression = symbolId == null ? value : symbolId,
            Source = Span("Clock.cs", 1),
        };

    private static OperationFact ShapeFact(
        string id,
        string kind,
        string containing,
        string? operation = null,
        string? resultType = null,
        OperationInputFact[]? inputs = null,
        OperationControlContext[]? controls = null,
        string? targetSymbolId = null)
        => new()
        {
            Id = id,
            Kind = kind,
            ModuleName = "Acme.Runtime",
            ProfileScope = "Player",
            ContainingSymbolId = containing,
            TargetSymbolId = targetSymbolId,
            Operator = operation,
            ResultType = resultType,
            Source = Span("Buffer.cs", 10),
            IsImplicit = false,
            Inputs = inputs ?? Array.Empty<OperationInputFact>(),
            ControlContexts = controls ?? Array.Empty<OperationControlContext>(),
        };

    private static OperationInputFact ShapeInput(
        string role,
        int? ordinal,
        string kind,
        string type,
        string? sourceId = null,
        bool compileTimeConstant = false,
        OperationConstantFact[]? constants = null,
        string[]? sourceIds = null)
        => new()
        {
            Role = role,
            Ordinal = ordinal,
            AuthorSupplied = true,
            Value = new OperationValueFact
            {
                Kind = kind,
                Type = type,
                Expression = sourceId ?? constants?.FirstOrDefault()?.Value ?? "value",
                IsCompileTimeConstant = compileTimeConstant,
                SourceSymbolIds = sourceIds ?? (sourceId == null ? Array.Empty<string>() : new[] { sourceId }),
                Constants = constants ?? Array.Empty<OperationConstantFact>(),
            },
        };

    private static OperationInputFact ConstantShapeInput(string role, string value)
        => ShapeInput(
            role,
            null,
            OperationValueKind.Literal,
            "float",
            compileTimeConstant: true,
            constants: new[]
            {
                Constant(OperationConstantOrigin.Literal, value, OperationNumericClassification.Finite),
            });

    private static OperationControlContext ShapeBranch(
        string conditionSourceId,
        string comparisonOperator,
        string branchArm)
        => new()
        {
            Kind = OperationControlContextKind.Branch,
            BranchArm = branchArm,
            Condition = "condition",
            ConditionValue = new OperationValueFact
            {
                Kind = OperationValueKind.Binary,
                Expression = "condition",
                IsCompileTimeConstant = false,
                SourceSymbolIds = new[] { conditionSourceId },
            },
            Operators = new[] { comparisonOperator },
            Source = Span("Voice.cs", 9),
        };

    private static OperationControlContext ShapeLoop(string boundarySourceId)
        => new()
        {
            Kind = OperationControlContextKind.Loop,
            Condition = "index < boundary",
            ConditionValue = new OperationValueFact
            {
                Kind = OperationValueKind.Binary,
                Expression = "index < boundary",
                IsCompileTimeConstant = false,
                SourceSymbolIds = new[] { boundarySourceId },
            },
            Operators = new[] { "LessThan" },
            Source = Span("Buffer.cs", 9),
        };

    private static OperationFact MaskShift(string id, string containing, string bit)
        => ShapeFact(
            id,
            OperationFactKind.Binary,
            containing,
            operation: "LeftShift",
            resultType: "ulong",
            inputs: new[]
            {
                ShapeInput(
                    OperationInputRole.Left,
                    null,
                    OperationValueKind.Literal,
                    "ulong",
                    compileTimeConstant: true,
                    constants: new[]
                    {
                        Constant(OperationConstantOrigin.Literal, "1", OperationNumericClassification.Finite),
                    }),
                ShapeInput(
                    OperationInputRole.Right,
                    null,
                    OperationValueKind.Literal,
                    "int",
                    compileTimeConstant: true,
                    constants: new[]
                    {
                        Constant(OperationConstantOrigin.Literal, bit, OperationNumericClassification.Finite),
                    }),
            });

    private static OperationSourceSpan Span(string path, int line)
        => new()
        {
            FilePath = path,
            Line = line,
            Column = 1,
            EndLine = line,
            EndColumn = 2,
        };

    private static OperationFactScanReceipt Receipt(int emitted, bool truncated = false)
        => new()
        {
            Status = OperationFactScanStatus.Completed,
            ProfileScope = "Player",
            ExecutionMode = OperationFactExecutionMode.RetainedCompilation,
            InputIdentityVerifiedAtStart = true,
            AdditionalSemanticBaseCount = 0,
            CompiledModuleCount = 0,
            ScannedModuleCount = 1,
            ScannedFileCount = 1,
            ObservedOperationCount = emitted,
            EmittedFactCount = emitted,
            Truncated = truncated,
            StoppedByConsumer = false,
        };

    private sealed class RecordingProvider : IOperationFactProvider
    {
        public OperationFactQuery? Query { get; private set; }
        public int CallCount { get; private set; }

        public OperationFactScanReceipt ScanOperationFacts(
            OperationFactQuery query,
            Func<OperationFact, bool> consume,
            CancellationToken cancellationToken = default)
        {
            Query = query;
            CallCount++;
            consume(Call("provider", GuardTarget, "Provider.cs", 1, Argument(OperationValueKind.Parameter)));
            return Receipt(emitted: 1);
        }
    }
}

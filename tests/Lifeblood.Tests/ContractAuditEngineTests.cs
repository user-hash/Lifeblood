using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Application.UseCases;
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
        Assert.Equal(1, Assert.Single(report.RuleBreakdown, row => row.RuleId == ContractRuleId.OperationGuard).FindingCount);
        Assert.Equal(1, Assert.Single(report.RuleBreakdown, row => row.RuleId == ContractRuleId.ExternalApiCost).FindingCount);
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
        OperationControlPredicate[]? contextPredicates = null)
        => new()
        {
            Id = id,
            Kind = OperationFactKind.Call,
            ModuleName = "Acme.Runtime",
            ProfileScope = "Player",
            ContainingSymbolId = "method:Acme.Dsp.Run()",
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

    private static OperationSourceSpan Span(string path, int line)
        => new()
        {
            FilePath = path,
            Line = line,
            Column = 1,
            EndLine = line,
            EndColumn = 2,
        };

    private static OperationFactScanReceipt Receipt(int emitted)
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
            Truncated = false,
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

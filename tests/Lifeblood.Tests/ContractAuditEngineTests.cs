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
        };

    private static OperationInputFact Argument(string kind, params string[] sourceIds)
        => new()
        {
            Role = OperationInputRole.Argument,
            Ordinal = 0,
            ParameterName = "value",
            AuthorSupplied = true,
            Value = new OperationValueFact
            {
                Kind = kind,
                Expression = "value",
                IsCompileTimeConstant = kind == OperationValueKind.Literal,
                SourceSymbolIds = sourceIds,
            },
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

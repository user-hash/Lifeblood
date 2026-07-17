using Lifeblood.Analysis;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Xunit;

namespace Lifeblood.Tests;

public sealed class ContractEvidenceProjectorTests
{
    [Fact]
    public void Project_JoinsDeclarationSourceTestRouteOperationAndCallerReceiptWithoutScore()
    {
        var graph = Graph();
        var manifest = new ContractManifest
        {
            Id = "coverage",
            Version = "1",
            CallRoutes = new[]
            {
                new ContractCallRoute
                {
                    Id = "production",
                    RootSymbolIds = new[] { "method:Acme.Engine.Run()" },
                },
            },
            OperationGuards = new[]
            {
                new OperationGuardContract
                {
                    Id = "guard",
                    TargetSymbolIds = new[] { "method:Acme.Engine.Sink(float)" },
                },
            },
            InvariantEvidence = new[]
            {
                new InvariantEvidenceContract
                {
                    Id = "critical-path-evidence",
                    InvariantIds = new[] { "INV-ACME-001" },
                    CallRouteIds = new[] { "production" },
                    OperationContractIds = new[] { "guard" },
                    RequiredEvidenceKinds = new[]
                    {
                        ContractEvidenceKind.InvariantDeclaration,
                        ContractEvidenceKind.SourceReference,
                        ContractEvidenceKind.TestReference,
                        ContractEvidenceKind.ReachableTest,
                        ContractEvidenceKind.OperationContract,
                        "CompileCheck",
                    },
                    ExternalEvidence = new[]
                    {
                        new ContractExternalEvidence
                        {
                            Kind = "CompileCheck",
                            Reference = "receipt:compile-17",
                        },
                    },
                },
            },
        };
        var routePlan = RoutePlan();
        var report = OperationReport(manifest, routePlan);
        var sourceFacts = new[]
        {
            SourceFact(
                "prod",
                "INV-ACME-001",
                "method:Acme.Engine.Run()",
                "src/Engine.cs",
                8),
            SourceFact(
                "test",
                "INV-ACME-001",
                "method:Acme.Tests.EngineTests.Run_is_guarded()",
                "tests/EngineTests.cs",
                9),
        };

        var projected = ContractEvidenceProjector.Project(
            graph,
            manifest,
            report,
            routePlan,
            new[] { Declaration("INV-ACME-001") },
            sourceFacts,
            SourceReceipt(sourceFacts.Length),
            maxTextMatches: 20,
            maxEvidencePerCategory: 8);

        var row = Assert.Single(projected.InvariantEvidence);
        Assert.True(row.RequirementsSatisfied);
        Assert.Contains(InvariantEvidenceState.Covered, row.States);
        Assert.Empty(row.Gaps);
        Assert.All(row.Evidence.Where(category => category.Required), category =>
            Assert.Contains(
                category.Status,
                new[] { ContractEvidenceStatus.Present, ContractEvidenceStatus.CallerDeclared }));
        Assert.Equal(
            ContractEvidenceStatus.CallerDeclared,
            Assert.Single(row.Evidence, category => category.Kind == "CompileCheck").Status);
        Assert.DoesNotContain(projected.GetType().GetProperties(), property =>
            property.Name.Contains("Score", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, projected.SourceEvidenceScan!.AdditionalSemanticBaseCount);
        var policy = Assert.Single(projected.InvariantEvidencePolicies);
        Assert.Equal(1, policy.SelectedInvariantCount);
        Assert.Equal(1, policy.ReturnedInvariantCount);
        Assert.False(policy.Truncated);
    }

    [Fact]
    public void Project_ClassifiesAdvisoryTextAndInvariantGapStatesWithConcreteEvidence()
    {
        var graph = Graph();
        var ids = new[] { "INV-PROSE", "INV-SOURCE", "INV-TEST", "INV-ORPHAN", "INV-STALE" };
        var manifest = new ContractManifest
        {
            Id = "governance",
            Version = "1",
            SourceTextPolicies = new[]
            {
                new SourceTextPolicyContract
                {
                    Id = "retired-authority",
                    Terms = new[] { "managed mirror" },
                    SuggestedAction = SourceTextSuggestedAction.UpdateAuthority,
                    InvariantIds = new[] { "INV-SOURCE" },
                },
            },
            InvariantEvidence = new[]
            {
                new InvariantEvidenceContract
                {
                    Id = "mapping",
                    InvariantIds = ids,
                    RequiredEvidenceKinds = new[]
                    {
                        ContractEvidenceKind.InvariantDeclaration,
                        ContractEvidenceKind.SourceReference,
                        ContractEvidenceKind.TestReference,
                    },
                },
            },
        };
        ContractManifestValidator.Validate(manifest);
        var report = EmptyReport(manifest);
        var facts = new[]
        {
            SourceFact("source", "INV-SOURCE", "method:Acme.Engine.Run()", "src/Engine.cs", 8),
            SourceFact("test", "INV-TEST", "method:Acme.Tests.EngineTests.Run_is_guarded()", "tests/EngineTests.cs", 9),
            SourceFact("stale", "INV-STALE", "method:Acme.Engine.Run()", "src/Engine.cs", 10),
            SourceFact("text", "managed mirror", "method:Acme.Engine.Run()", "src/Engine.cs", 11),
        };
        var declarations = ids
            .Where(id => id != "INV-STALE")
            .Select(Declaration)
            .ToArray();

        var projected = ContractEvidenceProjector.Project(
            graph,
            manifest,
            report,
            ContractCallRoutePlan.Empty,
            declarations,
            facts,
            SourceReceipt(facts.Length),
            maxTextMatches: 20,
            maxEvidencePerCategory: 4);

        var match = Assert.Single(projected.SourceTextMatches);
        Assert.Equal(ConfidenceBand.Advisory, match.Confidence);
        Assert.Equal("CallerPolicy", match.Authority);
        Assert.Equal(SourceTextSuggestedAction.UpdateAuthority, match.SuggestedAction);
        Assert.Equal("method:Acme.Engine.Run()", match.ContainingSymbolId);

        Assert.Contains(InvariantEvidenceState.SourceOnly, Row(projected, "INV-SOURCE").States);
        Assert.Contains(InvariantEvidenceState.TestOnly, Row(projected, "INV-TEST").States);
        Assert.Contains(InvariantEvidenceState.ProseOnly, Row(projected, "INV-PROSE").States);
        Assert.Contains(InvariantEvidenceState.Orphan, Row(projected, "INV-ORPHAN").States);
        Assert.Contains(InvariantEvidenceState.StaleReference, Row(projected, "INV-STALE").States);
        Assert.All(projected.InvariantEvidence, row =>
            Assert.NotEmpty(row.Gaps));
    }

    [Fact]
    public void Validate_RejectsUnboundedOrMiswiredEvidencePolicy()
    {
        var manifest = new ContractManifest
        {
            Id = "bad",
            Version = "1",
            InvariantEvidence = new[]
            {
                new InvariantEvidenceContract
                {
                    Id = "mapping",
                    InvariantIds = new[] { "INV-ACME-001" },
                    RequiredEvidenceKinds = new[] { ContractEvidenceKind.OperationContract },
                },
            },
        };

        var error = Assert.Throws<ArgumentException>(() => ContractManifestValidator.Validate(manifest));
        Assert.Contains("declares no operationContractIds", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_ReportsAnEmptyPrefixSelectionInsteadOfSilentlyOmittingThePolicy()
    {
        var manifest = new ContractManifest
        {
            Id = "empty-prefix",
            Version = "1",
            InvariantEvidence = new[]
            {
                new InvariantEvidenceContract
                {
                    Id = "missing-family",
                    InvariantIdPrefixes = new[] { "INV-NOT-PRESENT-" },
                },
            },
        };
        ContractManifestValidator.Validate(manifest);

        var projected = ContractEvidenceProjector.Project(
            Graph(),
            manifest,
            EmptyReport(manifest),
            ContractCallRoutePlan.Empty,
            new[] { Declaration("INV-ACME-001") },
            Array.Empty<SourceEvidenceFact>(),
            SourceReceipt(0),
            maxTextMatches: 20,
            maxEvidencePerCategory: 4);

        var policy = Assert.Single(projected.InvariantEvidencePolicies);
        Assert.Equal("missing-family", policy.ContractId);
        Assert.Equal(0, policy.SelectedInvariantCount);
        Assert.Equal(0, policy.ReturnedInvariantCount);
        Assert.False(policy.Truncated);
        Assert.Empty(projected.InvariantEvidence);
    }

    private static ContractInvariantEvidenceReceipt Row(ContractAuditReport report, string invariantId)
        => Assert.Single(report.InvariantEvidence, row => row.InvariantId == invariantId);

    private static SemanticGraph Graph()
        => new GraphBuilder()
            .AddSymbol(new Symbol
            {
                Id = "type:Acme.Engine",
                Name = "Engine",
                QualifiedName = "Acme.Engine",
                Kind = SymbolKind.Type,
                FilePath = "src/Engine.cs",
            })
            .AddSymbol(new Symbol
            {
                Id = "method:Acme.Engine.Run()",
                Name = "Run",
                QualifiedName = "Acme.Engine.Run",
                Kind = SymbolKind.Method,
                ParentId = "type:Acme.Engine",
                FilePath = "src/Engine.cs",
                Line = 7,
            })
            .AddSymbol(new Symbol
            {
                Id = "type:Acme.Tests.EngineTests",
                Name = "EngineTests",
                QualifiedName = "Acme.Tests.EngineTests",
                Kind = SymbolKind.Type,
                FilePath = "tests/EngineTests.cs",
            })
            .AddSymbol(new Symbol
            {
                Id = "method:Acme.Tests.EngineTests.Run_is_guarded()",
                Name = "Run_is_guarded",
                QualifiedName = "Acme.Tests.EngineTests.Run_is_guarded",
                Kind = SymbolKind.Method,
                ParentId = "type:Acme.Tests.EngineTests",
                FilePath = "tests/EngineTests.cs",
                Line = 8,
                Properties = new Dictionary<string, string> { ["attributes"] = "Test" },
            })
            .AddEdge(new Edge
            {
                SourceId = "method:Acme.Tests.EngineTests.Run_is_guarded()",
                TargetId = "method:Acme.Engine.Run()",
                Kind = EdgeKind.Calls,
            })
            .Build();

    private static ContractCallRoutePlan RoutePlan()
        => new()
        {
            Routes = new[]
            {
                new ContractCallRouteReceipt
                {
                    RouteId = "production",
                    RootSymbolIds = new[] { "method:Acme.Engine.Run()" },
                    Roots = new[]
                    {
                        new ContractCallRouteRootReceipt
                        {
                            SymbolId = "method:Acme.Engine.Run()",
                            Source = Span("src/Engine.cs", 7),
                        },
                    },
                    MaxDepth = 8,
                    MaxMembers = 4_096,
                    ReachableMemberCount = 1,
                    MembershipCount = 1,
                    Truncated = false,
                },
            },
            Matches = new[]
            {
                new ContractCallRouteMatch
                {
                    RouteId = "production",
                    RootSymbolId = "method:Acme.Engine.Run()",
                    ContainingSymbolId = "method:Acme.Engine.Run()",
                    Distance = 0,
                    Placement = ContractCallRoutePlacement.Direct,
                    PathSymbolIds = new[] { "method:Acme.Engine.Run()" },
                },
            },
        };

    private static ContractAuditReport OperationReport(
        ContractManifest manifest,
        ContractCallRoutePlan routePlan)
        => new()
        {
            Status = OperationFactScanStatus.Completed,
            ManifestId = manifest.Id,
            ManifestVersion = manifest.Version,
            ManifestSchemaVersion = manifest.SchemaVersion,
            SelectedRuleIds = new[] { ContractRuleId.OperationGuard, ContractRuleId.InvariantEvidence },
            SelectedContractIds = new[] { "guard", "critical-path-evidence" },
            ScanReceipt = OperationReceipt(),
            FindingCount = 0,
            ReturnedFindingCount = 0,
            SuppressedFindingCount = 0,
            Truncated = false,
            RuleBreakdown = new[]
            {
                new ContractRuleBreakdown
                {
                    RuleId = ContractRuleId.OperationGuard,
                    FindingCount = 0,
                    SuppressedFindingCount = 0,
                    Contracts = new[]
                    {
                        new ContractEvaluationBreakdown
                        {
                            ContractId = "guard",
                            EvaluatedOccurrenceCount = 1,
                            FindingFreeOccurrenceCount = 1,
                            FindingCount = 0,
                            SuppressedFindingCount = 0,
                        },
                    },
                },
            },
            CallRoutes = routePlan.Routes,
        };

    private static ContractAuditReport EmptyReport(ContractManifest manifest)
        => new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = manifest,
            CallRoutePlan = ContractCallRoutePlan.Empty,
            StatePlan = ContractStatePlan.Empty,
        }).Complete(OperationReceipt(OperationFactExecutionMode.NotRequested));

    private static SourceEvidenceFact SourceFact(
        string id,
        string term,
        string symbolId,
        string path,
        int line)
        => new()
        {
            Id = id,
            Kind = SourceEvidenceKind.Comment,
            ModuleName = "Test",
            ProfileScope = "Editor",
            MatchedTerm = term,
            Text = $"// {term}",
            ContainingSymbolId = symbolId,
            Source = Span(path, line),
        };

    private static InvariantDeclarationEvidence Declaration(string id)
        => new()
        {
            Id = id,
            Category = "ACME",
            Title = id + " title",
            Body = "body",
            Source = Span("docs/invariants/acme.md", 5),
        };

    private static SourceEvidenceScanReceipt SourceReceipt(int emitted)
        => new()
        {
            Status = SourceEvidenceScanStatus.Completed,
            ProfileScope = "Editor",
            AvailableProfiles = new[] { "Editor" },
            ExecutionMode = SourceEvidenceExecutionMode.RetainedSyntaxTrees,
            InputIdentityVerifiedAtStart = true,
            AdditionalSemanticBaseCount = 0,
            ScannedModuleCount = 1,
            ScannedFileCount = 2,
            ObservedEvidenceCount = emitted,
            EmittedFactCount = emitted,
            Truncated = false,
            StoppedByConsumer = false,
        };

    private static OperationFactScanReceipt OperationReceipt(
        string mode = OperationFactExecutionMode.RetainedCompilation)
        => new()
        {
            Status = OperationFactScanStatus.Completed,
            ProfileScope = "Editor",
            AvailableProfiles = new[] { "Editor" },
            ExecutionMode = mode,
            InputIdentityVerifiedAtStart = true,
            AdditionalSemanticBaseCount = 0,
            CompiledModuleCount = 0,
            ScannedModuleCount = 1,
            ScannedFileCount = 1,
            ObservedOperationCount = 1,
            EmittedFactCount = 1,
            Truncated = false,
            StoppedByConsumer = false,
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
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-MCP-TOOL-ARG-CONTRACT-001. Tool argument validation is sourced from
/// typed server-edge contracts, not ad-hoc handler parsing.
/// </summary>
public class ToolArgumentContractTests
{
    private static readonly JsonSerializerOptions SchemaJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    [Fact]
    public void ToolDefinitions_ExposeTypedInputContracts_WithRequiredFields()
    {
        var lookup = ToolRegistry.GetDefinitions().Single(d => d.Name == "lifeblood_lookup");
        var contract = lookup.InputContract;

        Assert.True(contract.Arguments.TryGetValue("symbolId", out var symbolId));
        Assert.True(symbolId.Required);
        Assert.Equal(ToolArgumentType.String, symbolId.Type);
    }

    [Fact]
    public void ToolDefinitions_ExposeCatalogContract_AsAuthoritativeSource()
    {
        foreach (var definition in ToolRegistry.GetDefinitions())
        {
            Assert.Same(
                ToolInputContractCatalog.Get(definition.Name, definition.SupportsSnapshotRead),
                definition.InputContract);
        }
    }

    [Fact]
    public void ToolInputContractCatalog_HasNoMissingOrOrphanContracts()
    {
        var definitionNames = ToolRegistry.GetDefinitions()
            .Select(d => d.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        var contractNames = ToolInputContractCatalog.All
            .Select(c => c.ToolName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(definitionNames, contractNames);
    }

    [Fact]
    public void SnapshotReadArguments_DeriveOnlyFromObserveSharedReadBehavior()
    {
        foreach (var definition in ToolRegistry.GetDefinitions())
        {
            var hasSnapshotId = definition.InputContract.Arguments.ContainsKey("expectedSnapshotId");
            var hasGeneration = definition.InputContract.Arguments.ContainsKey("expectedAnalysisGeneration");

            Assert.Equal(definition.SupportsSnapshotRead, hasSnapshotId);
            Assert.Equal(definition.SupportsSnapshotRead, hasGeneration);
        }
    }

    [Fact]
    public void ToolRegistry_NoLongerAuthorsAnonymousInputSchemas()
    {
        var registryPath = Path.Combine(RepoRoot, "src", "Lifeblood.Server.Mcp", "ToolRegistry.cs");
        var registryText = File.ReadAllText(registryPath);

        Assert.DoesNotContain("InputSchema = new", registryText, StringComparison.Ordinal);
        Assert.DoesNotContain("required object InputSchema", File.ReadAllText(Path.Combine(RepoRoot, "src", "Lifeblood.Server.Mcp", "McpProtocol.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void ToolInputContract_GeneratesEveryRegisteredInputSchema()
    {
        foreach (var definition in ToolRegistry.GetDefinitions())
        {
            var expected = Canonicalize(JsonSerializer.Serialize(definition.InputSchema, SchemaJsonOptions));
            var actual = Canonicalize(JsonSerializer.Serialize(definition.InputContract.ToInputSchema(), SchemaJsonOptions));

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void ToolInputContract_GeneratedSchemasProjectBackToSameTypedContract()
    {
        foreach (var definition in ToolRegistry.GetDefinitions())
        {
            var projected = ToolInputContract.FromSchema(definition.Name, definition.InputSchema);

            AssertContractEquivalent(definition.InputContract, projected);
        }
    }

    [Fact]
    public void ToolInputContract_PreservesEnumValues()
    {
        var definition = ToolRegistry.GetDefinitions().Single(d => d.Name == "lifeblood_resolve_short_name");
        var contract = definition.InputContract;

        Assert.True(contract.Arguments.TryGetValue("mode", out var mode));
        Assert.Equal(new[] { "exact", "contains", "fuzzy" }, mode.EnumValues);
    }

    [Fact]
    public void Binder_LegacyMode_AllowsUnknownAndMissingArguments()
    {
        var binder = NewBinder();

        var result = binder.Validate(
            "lifeblood_lookup",
            JsonArgs(new { unexpected = true }),
            ToolJsonCompatibilityMode.Legacy);

        Assert.True(result.Accepted);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Binder_WarnMode_AcceptsButReportsUnknownMissingAndTypeMismatch()
    {
        var binder = NewBinder();

        var result = binder.Validate(
            "lifeblood_lookup",
            JsonArgs(new { symbolId = 42, unexpected = true }),
            ToolJsonCompatibilityMode.Warn);

        Assert.True(result.Accepted);
        Assert.Contains(result.Diagnostics, d => d.Kind == "unknown" && d.Argument == "unexpected");
        Assert.Contains(result.Diagnostics, d => d.Kind == "typeMismatch" && d.Argument == "symbolId");
    }

    [Fact]
    public void Binder_StrictMode_RejectsMissingRequiredArgument()
    {
        var binder = NewBinder();

        var result = binder.Validate(
            "lifeblood_lookup",
            JsonArgs(new { }),
            ToolJsonCompatibilityMode.Strict);

        Assert.False(result.Accepted);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("missingRequired", diagnostic.Kind);
        Assert.Equal("symbolId", diagnostic.Argument);
    }

    [Fact]
    public void Binder_StrictMode_RejectsDuplicateArgumentNames()
    {
        var binder = NewBinder();
        using var document = JsonDocument.Parse("""{"symbolId":"type:A","symbolId":"type:B"}""");

        var result = binder.Validate(
            "lifeblood_lookup",
            document.RootElement,
            ToolJsonCompatibilityMode.Strict);

        Assert.False(result.Accepted);
        Assert.Contains(result.Diagnostics, d => d.Kind == "duplicate" && d.Argument == "symbolId");
    }

    [Fact]
    public void Binder_StrictMode_RejectsEnumValuesOutsideSchema()
    {
        var binder = NewBinder();

        var result = binder.Validate(
            "lifeblood_resolve_short_name",
            JsonArgs(new { name = "MidiLearnManager", mode = "wild" }),
            ToolJsonCompatibilityMode.Strict);

        Assert.False(result.Accepted);
        Assert.Contains(result.Diagnostics, d => d.Kind == "enumMismatch" && d.Argument == "mode");
    }

    [Fact]
    public void ToolRequestBinder_BindsAnalyzeRequestRecord()
    {
        var request = ToolRequestBinder.BindAnalyze(JsonArgs(new
        {
            projectPath = "D:/repo",
            rulesPath = "lifeblood",
            incremental = true,
            allowFullFallback = true,
            defineProfiles = new[] { "Editor", "", " Player " },
            excludePaths = new[] { "*/Examples*/*", "", " Packages/* " },
            authoritativeChangedFiles = new[] { "Assets/Foo.cs", "", " Packages/Bar.cs " },
            changeReceiptMode = "detail",
            changeReceiptLimit = 999,
            packageSourceVisibilityMode = "detail",
            profileApplicabilityMode = "detail",
        }));

        Assert.Equal("D:/repo", request.ProjectPath);
        Assert.Equal("lifeblood", request.RulesPath);
        Assert.True(request.Incremental);
        Assert.True(request.AllowFullFallback);
        Assert.Equal(new[] { "Editor", "Player" }, request.DefineProfiles);
        Assert.Equal(new[] { "*/Examples*/*", "Packages/*" }, request.ExcludePaths);
        Assert.Equal(new[] { "Assets/Foo.cs", "Packages/Bar.cs" }, request.AuthoritativeChangedFiles);
        Assert.Equal(AcceptedChangeReceiptMode.Detail, request.EffectiveChangeReceipt.Mode);
        Assert.Equal(AcceptedChangeReceiptRequest.MaximumLimit, request.EffectiveChangeReceipt.Limit);
        Assert.Equal(PackageSourceVisibilityProjection.Detail, request.EffectivePackageSourceVisibilityProjection);
        Assert.Equal(ProfileApplicabilityProjection.Detail, request.EffectiveProfileApplicabilityProjection);
        Assert.Same(AnalyzeToolRequest.Empty, ToolRequestBinder.BindAnalyze(null));

        var legacyBadTypes = ToolRequestBinder.BindAnalyze(JsonArgs(new
        {
            projectPath = 42,
            incremental = "true",
            defineProfiles = new object[] { 42, " Editor " },
            excludePaths = new object[] { 42, " */Samples*/* " },
            authoritativeChangedFiles = new object[] { 42, " Assets/Changed.cs " },
            changeReceiptMode = 42,
            changeReceiptLimit = "10",
            packageSourceVisibilityMode = 42,
            profileApplicabilityMode = 42,
        }));
        Assert.Null(legacyBadTypes.ProjectPath);
        Assert.False(legacyBadTypes.Incremental);
        Assert.Equal(new[] { "Editor" }, legacyBadTypes.DefineProfiles);
        Assert.Equal(new[] { "*/Samples*/*" }, legacyBadTypes.ExcludePaths);
        Assert.Equal(new[] { "Assets/Changed.cs" }, legacyBadTypes.AuthoritativeChangedFiles);
        Assert.Equal(AcceptedChangeReceiptMode.Summary, legacyBadTypes.EffectiveChangeReceipt.Mode);
        Assert.Equal(AcceptedChangeReceiptRequest.DefaultLimit, legacyBadTypes.EffectiveChangeReceipt.Limit);
        Assert.Equal(PackageSourceVisibilityProjection.Summary, legacyBadTypes.EffectivePackageSourceVisibilityProjection);
        Assert.Equal(ProfileApplicabilityProjection.Summary, legacyBadTypes.EffectiveProfileApplicabilityProjection);

        var explicitEmpty = ToolRequestBinder.BindAnalyze(JsonArgs(new
        {
            authoritativeChangedFiles = Array.Empty<string>(),
        }));
        Assert.NotNull(explicitEmpty.AuthoritativeChangedFiles);
        Assert.Empty(explicitEmpty.AuthoritativeChangedFiles);
    }

    [Fact]
    public void Binder_StrictMode_RejectsUnknownAnalyzeReceiptMode()
    {
        var result = NewBinder().Validate(
            "lifeblood_analyze",
            JsonArgs(new { changeReceiptMode = "verbose", profileApplicabilityMode = "verbose" }),
            ToolJsonCompatibilityMode.Strict);

        Assert.False(result.Accepted);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Kind == "enumMismatch"
            && diagnostic.Argument == "changeReceiptMode");
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Kind == "enumMismatch"
            && diagnostic.Argument == "profileApplicabilityMode");
    }

    [Fact]
    public void ToolRequestBinder_BindsCompileCheckRequestWithBackCompatDefaults()
    {
        var request = ToolRequestBinder.BindCompileCheck(JsonArgs(new
        {
            filePath = "src/Foo.cs",
            filePaths = new[] { " src/A.cs ", "src/B.cs" },
            moduleName = "App",
            verbosity = "compact",
        }));

        Assert.Equal("src/Foo.cs", request.FilePath);
        Assert.Equal(new[] { "src/A.cs", "src/B.cs" }, request.FilePaths);
        Assert.Equal("App", request.ModuleName);
        Assert.Equal("compact", request.Verbosity);
        Assert.True(request.EffectiveStaleRefresh);

        var explicitEmpty = ToolRequestBinder.BindCompileCheck(JsonArgs(new
        {
            filePaths = Array.Empty<string>(),
        }));
        Assert.NotNull(explicitEmpty.FilePaths);
        Assert.Empty(explicitEmpty.FilePaths);

        var explicitFalse = ToolRequestBinder.BindCompileCheck(JsonArgs(new
        {
            code = "class C {}",
            staleRefresh = false,
        }));
        Assert.Equal("class C {}", explicitFalse.Code);
        Assert.False(explicitFalse.EffectiveStaleRefresh);

        var legacyBadTypes = ToolRequestBinder.BindCompileCheck(JsonArgs(new
        {
            filePath = 42,
            staleRefresh = "false",
        }));
        Assert.Null(legacyBadTypes.FilePath);
        Assert.True(legacyBadTypes.EffectiveStaleRefresh);
    }

    [Fact]
    public void ToolRequestBinder_BindsContractAuditRequestWithSummaryFirstDefaults()
    {
        var request = ToolRequestBinder.BindContractAudit(JsonArgs(new
        {
            manifest = new { schemaVersion = 1, manifestId = "contracts" },
            profileScope = "Player",
            moduleScope = "Runtime",
            filePaths = new[] { " Assets/Foo.cs ", "" },
            containingSymbolIds = new[] { " method:Acme.Foo() ", "" },
            includeRuleIds = new[] { " operationGuard ", "" },
            maxFacts = 123,
            maxFindings = 17,
            maxEvidencePerFinding = 3,
            summarize = false,
        }));

        Assert.True(request.Manifest.HasValue);
        Assert.Equal("contracts", request.Manifest.Value.GetProperty("manifestId").GetString());
        Assert.Equal("Player", request.ProfileScope);
        Assert.Equal("Runtime", request.ModuleScope);
        Assert.Equal(new[] { "Assets/Foo.cs" }, request.FilePaths);
        Assert.Equal(new[] { "method:Acme.Foo()" }, request.ContainingSymbolIds);
        Assert.Equal(new[] { "operationGuard" }, request.IncludeRuleIds);
        Assert.Equal(123, request.EffectiveMaxFacts);
        Assert.Equal(17, request.EffectiveMaxFindings);
        Assert.Equal(3, request.EffectiveMaxEvidencePerFinding);
        Assert.False(request.EffectiveSummarize);

        var empty = ToolRequestBinder.BindContractAudit(null);
        Assert.Same(ContractAuditToolRequest.Empty, empty);
        Assert.Equal(50_000, empty.EffectiveMaxFacts);
        Assert.Equal(200, empty.EffectiveMaxFindings);
        Assert.Equal(8, empty.EffectiveMaxEvidencePerFinding);
        Assert.True(empty.EffectiveSummarize);
    }

    [Fact]
    public void ToolRequestBinder_BindsEvidenceDriftRequestWithVisibleToleranceDefault()
    {
        var request = ToolRequestBinder.BindEvidenceDrift(JsonArgs(new
        {
            baselinePath = " docs/evidence.md ",
            relativeTolerancePercent = 1.25,
        }));

        Assert.Equal(" docs/evidence.md ", request.BaselinePath);
        Assert.Equal(1.25, request.EffectiveRelativeTolerancePercent);

        var empty = ToolRequestBinder.BindEvidenceDrift(null);
        Assert.Same(EvidenceDriftToolRequest.Empty, empty);
        Assert.Equal(
            Lifeblood.Analysis.EvidenceBaselineDriftEvaluator.DefaultRelativeTolerancePercent,
            empty.EffectiveRelativeTolerancePercent);
    }

    [Fact]
    public void ReadFromEnvironment_StrictJsonAlias_RemainsStrictAlias()
    {
        const string compatName = "LIFEBLOOD_JSON_COMPAT_TEST";
        const string strictName = "LIFEBLOOD_STRICT_JSON_TEST";
        try
        {
            Environment.SetEnvironmentVariable(compatName, null);
            Environment.SetEnvironmentVariable(strictName, "true");

            Assert.Equal(
                ToolJsonCompatibilityMode.Strict,
                ToolJsonCompatibilityModeReader.ReadFromEnvironment(compatName, strictName));
        }
        finally
        {
            Environment.SetEnvironmentVariable(compatName, null);
            Environment.SetEnvironmentVariable(strictName, null);
        }
    }

    [Theory]
    [InlineData("legacy", ToolJsonCompatibilityMode.Legacy)]
    [InlineData("warn", ToolJsonCompatibilityMode.Warn)]
    [InlineData("strict", ToolJsonCompatibilityMode.Strict)]
    [InlineData("true", ToolJsonCompatibilityMode.Strict)]
    [InlineData("nonsense", ToolJsonCompatibilityMode.Legacy)]
    public void Parse_RecognizesCompatibilityModes(string value, ToolJsonCompatibilityMode expected)
    {
        Assert.Equal(expected, ToolJsonCompatibilityModeReader.Parse(value));
    }

    private static JsonElement? JsonArgs(object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private static ToolArgumentBinder NewBinder()
        => new(ToolRegistry.GetDefinitions()
            .Select(d => d.InputContract));

    private static string RepoRoot
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null && !File.Exists(Path.Combine(current.FullName, "Lifeblood.sln")))
            {
                current = current.Parent;
            }

            Assert.NotNull(current);
            return current!.FullName;
        }
    }

    private static void AssertContractEquivalent(ToolInputContract expected, ToolInputContract actual)
    {
        Assert.Equal(expected.ToolName, actual.ToolName);
        Assert.Equal(expected.ArgumentList.Count, actual.ArgumentList.Count);
        for (var i = 0; i < expected.ArgumentList.Count; i++)
        {
            var expectedArgument = expected.ArgumentList[i];
            var actualArgument = actual.ArgumentList[i];
            Assert.Equal(expectedArgument.Name, actualArgument.Name);
            Assert.Equal(expectedArgument.Type, actualArgument.Type);
            Assert.Equal(expectedArgument.Required, actualArgument.Required);
            Assert.Equal(expectedArgument.ArrayItemType, actualArgument.ArrayItemType);
            Assert.Equal(expectedArgument.Description, actualArgument.Description);
            Assert.Equal(expectedArgument.EnumValues, actualArgument.EnumValues);
        }
    }

    private static string Canonicalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        return JsonSerializer.Serialize(document.RootElement, SchemaJsonOptions);
    }
}

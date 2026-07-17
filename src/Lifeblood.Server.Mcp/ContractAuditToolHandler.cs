using System.Text.Json;
using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Right.Invariants;
using Lifeblood.Application.UseCases;
using Lifeblood.Domain.Results;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// MCP-edge binding for contract audits. It owns JSON/path safety and composes
/// the Application scan use case with the stateless Analysis engine; semantic
/// extraction and policy remain behind their existing hexagonal seams.
/// </summary>
internal sealed class ContractAuditToolHandler
{
    private const int MaximumManifestCharacters = 1_048_576;

    private readonly GraphSession _session;
    private readonly IInvariantProvider _invariants;
    private readonly JsonSerializerOptions _jsonOptions;

    public ContractAuditToolHandler(
        GraphSession session,
        IInvariantProvider invariants,
        JsonSerializerOptions jsonOptions)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _invariants = invariants ?? throw new ArgumentNullException(nameof(invariants));
        _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
    }

    public ContractAuditReport Execute(
        JsonElement? arguments,
        CancellationToken cancellationToken = default)
    {
        var request = ToolRequestBinder.BindContractAudit(arguments);
        var hasInline = request.Manifest.HasValue;
        var hasPath = !string.IsNullOrWhiteSpace(request.ManifestPath);
        if (hasInline == hasPath)
        {
            throw new ArgumentException(
                "Supply exactly one of 'manifest' or 'manifestPath' for lifeblood_contract_audit.");
        }

        var manifest = hasInline
            ? DeserializeManifest(request.Manifest!.Value.GetRawText(), "inline manifest")
            : DeserializeManifest(ReadManifestFile(request.ManifestPath!), request.ManifestPath!);
        ContractManifestValidator.Validate(manifest);
        var profileScope = string.IsNullOrWhiteSpace(request.ProfileScope)
            ? _session.RetainedProfileName
            : request.ProfileScope.Trim();
        var graph = manifest.CallRoutes.Length > 0
                || manifest.StateAccesses.Length > 0
                || manifest.SourceTextPolicies.Length > 0
                || manifest.InvariantEvidence.Length > 0
            ? _session.Graph
                ?? throw new InvalidOperationException(
                    "Call routes, state access, and invariant evidence require a loaded semantic graph from the selected publication.")
            : null;
        var callRoutePlan = manifest.CallRoutes.Length == 0
            ? ContractCallRoutePlan.Empty
            : ContractCallRoutePlanner.Plan(
                graph!,
                manifest.CallRoutes,
                profileScope);
        var statePlan = manifest.StateAccesses.Length == 0
            ? ContractStatePlan.Empty
            : ContractStatePlanner.Plan(
                graph!,
                manifest.StateAccesses,
                callRoutePlan,
                profileScope);
        var engine = new ContractAuditEngine(new ContractAuditRequest
        {
            Manifest = manifest,
            ProfileScope = profileScope,
            ModuleScope = request.ModuleScope,
            FilePaths = request.FilePaths,
            ContainingSymbolIds = request.ContainingSymbolIds,
            IncludeRuleIds = request.IncludeRuleIds,
            CallRoutePlan = callRoutePlan,
            StatePlan = statePlan,
            MaxFacts = request.EffectiveMaxFacts,
            MaxFindings = request.EffectiveMaxFindings,
            MaxEvidencePerFinding = request.EffectiveMaxEvidencePerFinding,
            Summarize = request.EffectiveSummarize,
        });
        var receipt = engine.RequiresOperationFacts
            ? ScanOperationFacts(engine, cancellationToken)
            : NoOperationScan(profileScope ?? "default");
        var report = engine.Complete(receipt);
        if (!engine.RequiresSourceEvidence) return report;

        var selected = report.SelectedContractIds.ToHashSet(StringComparer.Ordinal);
        var textContracts = manifest.SourceTextPolicies.Where(contract => selected.Contains(contract.Id)).ToArray();
        var evidenceContracts = manifest.InvariantEvidence.Where(contract => selected.Contains(contract.Id)).ToArray();
        var declarations = BuildInvariantDeclarations(_session.ProjectRoot);
        var selectedInvariantIds = evidenceContracts
            .SelectMany(contract => contract.InvariantIds
                .Concat(declarations
                    .Where(declaration => contract.InvariantIdPrefixes.Any(prefix =>
                        declaration.Id.StartsWith(prefix, StringComparison.Ordinal)))
                    .Select(declaration => declaration.Id))
                .Concat(contract.ReferenceAliases.Select(alias => alias.InvariantId)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var terms = textContracts.SelectMany(contract => contract.Terms)
            .Concat(selectedInvariantIds)
            .Concat(evidenceContracts.SelectMany(contract => contract.InvariantIdPrefixes))
            .Concat(evidenceContracts.SelectMany(contract => contract.ReferenceAliases.SelectMany(alias => alias.Terms)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(term => term, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var kinds = textContracts.SelectMany(contract => contract.IncludeKinds)
            .Concat(evidenceContracts.Length == 0
                ? Array.Empty<string>()
                : SourceEvidenceKind.All)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(kind => kind, StringComparer.Ordinal)
            .ToArray();
        var sourceFacts = new List<SourceEvidenceFact>();
        var sourceProvider = _session.SourceEvidenceProvider
            ?? throw new InvalidOperationException(
                "The selected publication does not retain a source-evidence provider. " +
                "Analyze a C# project with readOnly:false or select the current live publication.");
        var sourceReceipt = new ScanSourceEvidenceUseCase(sourceProvider).Execute(
            new SourceEvidenceQuery
            {
                ModuleScope = request.ModuleScope,
                ProfileScope = profileScope,
                FilePaths = request.FilePaths,
                SearchTerms = terms,
                IncludeKinds = kinds,
                MaxFacts = request.EffectiveMaxFacts,
            },
            fact =>
            {
                sourceFacts.Add(fact);
                return true;
            },
            cancellationToken);
        return ContractEvidenceProjector.Project(
            graph!,
            manifest,
            report,
            callRoutePlan,
            declarations,
            sourceFacts,
            sourceReceipt,
            request.EffectiveSummarize ? Math.Min(25, request.EffectiveMaxFindings) : request.EffectiveMaxFindings,
            request.EffectiveSummarize ? 0 : request.EffectiveMaxEvidencePerFinding);
    }

    private OperationFactScanReceipt ScanOperationFacts(
        ContractAuditEngine engine,
        CancellationToken cancellationToken)
    {
        var provider = _session.OperationFactProvider
            ?? throw new InvalidOperationException(
                "The selected publication does not retain an operation-fact provider. " +
                "Analyze a C# project or select the current live publication.");
        return new ScanOperationFactsUseCase(provider).Execute(
            engine.Query,
            engine.Observe,
            cancellationToken);
    }

    private OperationFactScanReceipt NoOperationScan(string profileScope) => new()
    {
        Status = OperationFactScanStatus.Completed,
        ProfileScope = profileScope,
        AvailableProfiles = _session.RetainedProfileNames.ToArray(),
        ExecutionMode = OperationFactExecutionMode.NotRequested,
        InputIdentityVerifiedAtStart = true,
        AdditionalSemanticBaseCount = 0,
        CompiledModuleCount = 0,
        ScannedModuleCount = 0,
        ScannedFileCount = 0,
        ObservedOperationCount = 0,
        EmittedFactCount = 0,
        Truncated = false,
        StoppedByConsumer = false,
    };

    private InvariantDeclarationEvidence[] BuildInvariantDeclarations(string projectRoot)
        => _invariants.GetAll(projectRoot)
            .Select(invariant => new InvariantDeclarationEvidence
            {
                Id = invariant.Id,
                Category = invariant.Category,
                Title = invariant.Title,
                Body = invariant.Body,
                Source = new OperationSourceSpan
                {
                    FilePath = invariant.SourcePath,
                    Line = invariant.SourceLine,
                    Column = 1,
                    EndLine = invariant.SourceLine,
                    EndColumn = 1,
                },
            })
            .ToArray();

    private string ReadManifestFile(string rawPath)
    {
        var projectRoot = _session.ProjectRoot;
        if (string.IsNullOrWhiteSpace(projectRoot))
            throw new InvalidOperationException("manifestPath requires an analyzed workspace root.");

        var root = Path.GetFullPath(projectRoot);
        var path = Path.GetFullPath(Path.IsPathRooted(rawPath)
            ? rawPath
            : Path.Combine(root, rawPath));
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative)
            || string.Equals(relative, "..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"manifestPath must stay inside the analyzed workspace root '{root}'.");
        }
        if (!_session.FileSystem.FileExists(path))
            throw new FileNotFoundException("Contract manifest was not found.", path);

        return _session.FileSystem.ReadAllText(path);
    }

    private ContractManifest DeserializeManifest(string json, string source)
    {
        if (json.Length > MaximumManifestCharacters)
        {
            throw new ArgumentException(
                $"Contract manifest '{source}' exceeds the {MaximumManifestCharacters}-character limit.");
        }

        try
        {
            return JsonSerializer.Deserialize<ContractManifest>(json, _jsonOptions)
                ?? throw new ArgumentException($"Contract manifest '{source}' deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(
                $"Contract manifest '{source}' is invalid JSON: {ex.Message}",
                ex);
        }
    }
}

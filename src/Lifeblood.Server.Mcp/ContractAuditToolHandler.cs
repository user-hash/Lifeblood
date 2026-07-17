using System.Text.Json;
using Lifeblood.Analysis;
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
    private readonly JsonSerializerOptions _jsonOptions;

    public ContractAuditToolHandler(GraphSession session, JsonSerializerOptions jsonOptions)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
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
        var callRoutePlan = manifest.CallRoutes.Length == 0
            ? ContractCallRoutePlan.Empty
            : ContractCallRoutePlanner.Plan(
                _session.Graph
                    ?? throw new InvalidOperationException(
                        "Call routes require a loaded semantic graph from the selected publication."),
                manifest.CallRoutes,
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
            MaxFacts = request.EffectiveMaxFacts,
            MaxFindings = request.EffectiveMaxFindings,
            MaxEvidencePerFinding = request.EffectiveMaxEvidencePerFinding,
            Summarize = request.EffectiveSummarize,
        });
        var provider = _session.OperationFactProvider
            ?? throw new InvalidOperationException(
                "The selected publication does not retain an operation-fact provider. " +
                "Analyze a C# project or select the current live publication.");
        var receipt = new ScanOperationFactsUseCase(provider).Execute(
            engine.Query,
            engine.Observe,
            cancellationToken);
        return engine.Complete(receipt);
    }

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

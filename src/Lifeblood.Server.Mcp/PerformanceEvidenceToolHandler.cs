using System.Text;
using System.Text.Json;
using Lifeblood.Adapters.Performance;
using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Application.Ports.Right.Invariants;
using Lifeblood.Application.UseCases;
using Lifeblood.Domain.Results;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// MCP-edge orchestration for request-local runtime evidence. This boundary
/// owns workspace containment, bounded UTF-8 input, and wire projection; the
/// format adapter and stateless analysis engine own parsing and policy.
/// </summary>
internal sealed class PerformanceEvidenceToolHandler
{
    internal const int MaximumInputBytes = 32 * 1024 * 1024;
    internal const int MaximumMarkerAliases = 256;
    internal const int MaximumMarkers = 1_000;
    internal const int MaximumTestsPerSymbol = 50;
    internal const int MaximumDeltas = 5_000;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly GraphSession _session;
    private readonly IInvariantProvider _invariants;
    private readonly ImportPerformanceEvidenceUseCase _import;

    public PerformanceEvidenceToolHandler(
        GraphSession session,
        IInvariantProvider invariants,
        IPerformanceEvidenceImporter? importer = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _invariants = invariants ?? throw new ArgumentNullException(nameof(invariants));
        _import = new ImportPerformanceEvidenceUseCase(importer ?? new RuntimePerformanceEvidenceImporter());
    }

    public object Execute(JsonElement? arguments, CancellationToken cancellationToken = default)
    {
        var request = ToolRequestBinder.BindPerformanceEvidence(arguments);
        Validate(request);
        cancellationToken.ThrowIfCancellationRequested();
        var source = Import(request.SourcePath!, request.Format, request.EffectiveMaxMeasurements);
        return request.EffectiveAction switch
        {
            "import" => BuildImportResult(source, request),
            "correlate" => BuildCorrelationResult(source, request, cancellationToken),
            "compare" => BuildComparisonResult(
                source,
                Import(request.CandidatePath!, request.CandidateFormat, request.EffectiveMaxMeasurements),
                request),
            _ => throw new ArgumentException("action must be 'import', 'correlate', or 'compare'."),
        };
    }

    private void Validate(PerformanceEvidenceToolRequest request)
    {
        if (request.EffectiveAction is not ("import" or "correlate" or "compare"))
            throw new ArgumentException("action must be 'import', 'correlate', or 'compare'.");
        if (string.IsNullOrWhiteSpace(request.SourcePath))
            throw new ArgumentException("sourcePath is required.");
        if (request.EffectiveAction == "compare" && string.IsNullOrWhiteSpace(request.CandidatePath))
            throw new ArgumentException("candidatePath is required for action:'compare'.");
        if (request.EffectiveAction != "compare" && !string.IsNullOrWhiteSpace(request.CandidatePath))
            throw new ArgumentException("candidatePath is only valid for action:'compare'.");
        if (request.EffectiveMaxMeasurements is <= 0 or > ImportPerformanceEvidenceUseCase.HardMaximumMeasurements)
            throw new ArgumentOutOfRangeException("maxMeasurements", $"maxMeasurements must be between 1 and {ImportPerformanceEvidenceUseCase.HardMaximumMeasurements}.");
        if (request.EffectiveMaxMarkers is <= 0 or > MaximumMarkers)
            throw new ArgumentOutOfRangeException("maxMarkers", $"maxMarkers must be between 1 and {MaximumMarkers}.");
        if (request.EffectiveMaxTestsPerSymbol is < 0 or > MaximumTestsPerSymbol)
            throw new ArgumentOutOfRangeException("maxTestsPerSymbol", $"maxTestsPerSymbol must be between 0 and {MaximumTestsPerSymbol}.");
        if (request.EffectiveMaxDeltas is <= 0 or > MaximumDeltas)
            throw new ArgumentOutOfRangeException("maxDeltas", $"maxDeltas must be between 1 and {MaximumDeltas}.");
        if (request.MarkerAliases is { Length: > MaximumMarkerAliases })
            throw new ArgumentException($"markerAliases is capped at {MaximumMarkerAliases} entries.");
    }

    private PerformanceCapture Import(string rawPath, string? formatHint, int maximumMeasurements)
    {
        var path = ResolveContainedPath(rawPath);
        var content = ReadBoundedUtf8(path.FullPath);
        return _import.Execute(new PerformanceEvidenceDocument(
            path.RelativePath,
            content,
            formatHint,
            maximumMeasurements));
    }

    private object BuildImportResult(PerformanceCapture capture, PerformanceEvidenceToolRequest request)
        => new
        {
            kind = "lifeblood.performance_evidence",
            action = "import",
            runtimeTruth = ProjectCapture(capture, request.EffectiveSummarize),
            semanticCorrelation = new
            {
                status = "NotRequested",
                additionalSemanticBaseCount = 0,
            },
            retainedGraphBaseCount = 1,
            retainedSemanticBaseCount = _session.CurrentSnapshot.RetainsSemanticServices ? 1 : 0,
            additionalSemanticBaseCount = 0,
        };

    private object BuildCorrelationResult(
        PerformanceCapture capture,
        PerformanceEvidenceToolRequest request,
        CancellationToken cancellationToken)
    {
        var graph = _session.Graph
            ?? throw new InvalidOperationException("Correlation requires a loaded semantic graph.");
        var provider = _session.SourceEvidenceProvider
            ?? throw new InvalidOperationException(
                "Correlation requires the live source-evidence provider. Analyze a C# workspace and select the current publication.");
        var invariants = _invariants.GetAll(_session.ProjectRoot);
        var selectedMarkers = capture.Measurements
            .GroupBy(measurement => measurement.Marker, StringComparer.Ordinal)
            .Select(group => new
            {
                Marker = group.Key,
                Hottest = group.Where(row => row.Available && row.Value.HasValue)
                    .Select(row => row.Value!.Value)
                    .DefaultIfEmpty(double.NegativeInfinity)
                    .Max(),
            })
            .OrderByDescending(row => row.Hottest)
            .ThenBy(row => row.Marker, StringComparer.Ordinal)
            .Take(request.EffectiveMaxMarkers)
            .Select(row => row.Marker)
            .ToArray();
        var terms = selectedMarkers
            .Concat(invariants.Select(invariant => invariant.Id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var facts = new List<SourceEvidenceFact>();
        var receipt = new ScanSourceEvidenceUseCase(provider).Execute(
            new SourceEvidenceQuery
            {
                SearchTerms = terms,
                IncludeKinds = SourceEvidenceKind.All,
                MaxFacts = Math.Min(250_000, Math.Max(1_000, terms.Length * 100)),
            },
            fact =>
            {
                facts.Add(fact);
                return true;
            },
            cancellationToken);
        var aliases = ParseAliases(request.MarkerAliases);
        var correlation = PerformanceEvidenceAnalyzer.Correlate(
            graph,
            capture,
            aliases,
            invariants.Select(invariant => invariant.Id).ToArray(),
            facts,
            receipt,
            request.EffectiveMaxMarkers,
            request.EffectiveMaxTestsPerSymbol);

        return new
        {
            kind = "lifeblood.performance_evidence",
            action = "correlate",
            runtimeTruth = ProjectCapture(capture, request.EffectiveSummarize),
            semanticCorrelation = correlation,
            publication = ProjectPublication(),
            retainedGraphBaseCount = 1,
            retainedSemanticBaseCount = _session.CurrentSnapshot.RetainsSemanticServices ? 1 : 0,
            additionalSemanticBaseCount = 0,
        };
    }

    private object BuildComparisonResult(
        PerformanceCapture baseline,
        PerformanceCapture candidate,
        PerformanceEvidenceToolRequest request)
    {
        var comparison = PerformanceEvidenceAnalyzer.Compare(
            baseline,
            candidate,
            request.EffectiveComparisonMode,
            request.AllowedDifferences ?? Array.Empty<string>(),
            request.EffectiveMaxDeltas);
        return new
        {
            kind = "lifeblood.performance_evidence",
            action = "compare",
            comparisonMode = request.EffectiveComparisonMode,
            baseline = ProjectCapture(baseline, summarize: true),
            candidate = ProjectCapture(candidate, summarize: true),
            comparison,
            semanticCorrelation = new
            {
                status = "NotRequested",
                additionalSemanticBaseCount = 0,
            },
            retainedGraphBaseCount = 1,
            retainedSemanticBaseCount = _session.CurrentSnapshot.RetainsSemanticServices ? 1 : 0,
            additionalSemanticBaseCount = 0,
        };
    }

    private object ProjectCapture(PerformanceCapture capture, bool summarize)
    {
        var previewLimit = summarize ? 8 : 50;
        var available = capture.Measurements.Count(row => row.Available && row.Value.HasValue);
        var unavailable = capture.Measurements.Length - available;
        var breakdown = capture.Measurements
            .GroupBy(row => new { row.Stage, row.Category, row.Marker, row.Thread, row.Unit })
            .Select(group => new
            {
                group.Key.Stage,
                group.Key.Category,
                group.Key.Marker,
                group.Key.Thread,
                group.Key.Unit,
                count = group.Count(),
                availableCount = group.Count(row => row.Available && row.Value.HasValue),
            })
            .OrderByDescending(row => row.count)
            .ThenBy(row => row.Marker, StringComparer.Ordinal)
            .Take(previewLimit)
            .ToArray();
        return new
        {
            capture.CaptureId,
            capture.SourceFormat,
            capture.SourceSchema,
            capture.Identity,
            capture.Device,
            capture.Workload,
            measurementCount = capture.Measurements.Length,
            availableMeasurementCount = available,
            unavailableMeasurementCount = unavailable,
            measurementBreakdown = breakdown,
            measurementBreakdownTruncated = breakdown.Length < capture.Measurements
                .Select(row => (row.Stage, row.Category, row.Marker, row.Thread, row.Unit))
                .Distinct()
                .Count(),
            measurementPreview = capture.Measurements.Take(previewLimit).ToArray(),
            measurementPreviewTruncated = capture.Measurements.Length > previewLimit,
            capture.ImportReceipt,
        };
    }

    private object ProjectPublication()
    {
        var snapshot = _session.CurrentSnapshot;
        return new
        {
            snapshotId = snapshot.SnapshotId.ToString(),
            analysisGeneration = snapshot.AnalysisGeneration,
            analyzedAtUtc = snapshot.AnalyzedAtUtc,
            sourceControl = snapshot.SourceControl,
        };
    }

    private static PerformanceMarkerAlias[] ParseAliases(JsonElement[]? rows)
    {
        if (rows is not { Length: > 0 }) return Array.Empty<PerformanceMarkerAlias>();
        var aliases = new List<PerformanceMarkerAlias>(rows.Length);
        foreach (var row in rows)
        {
            var marker = row.TryGetProperty("marker", out var markerNode) && markerNode.ValueKind == JsonValueKind.String
                ? markerNode.GetString()?.Trim()
                : null;
            var symbolId = row.TryGetProperty("symbolId", out var symbolNode) && symbolNode.ValueKind == JsonValueKind.String
                ? symbolNode.GetString()?.Trim()
                : null;
            if (string.IsNullOrWhiteSpace(marker) || string.IsNullOrWhiteSpace(symbolId))
                throw new ArgumentException("Every markerAliases entry requires non-empty marker and symbolId strings.");
            aliases.Add(new PerformanceMarkerAlias(marker, symbolId));
        }
        return aliases.Distinct().ToArray();
    }

    private (string FullPath, string RelativePath) ResolveContainedPath(string rawPath)
    {
        var workspaceRoot = _session.ProjectRoot;
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new InvalidOperationException("Runtime-evidence paths require an analyzed workspace root.");
        if (string.IsNullOrWhiteSpace(rawPath))
            throw new ArgumentException("Runtime-evidence path cannot be empty.", nameof(rawPath));
        var root = Path.GetFullPath(workspaceRoot);
        var path = WorkspacePathIdentity.ResolveFromWorkspace(root, rawPath);
        if (!WorkspacePathIdentity.Contains(root, path))
            throw new ArgumentException($"Runtime-evidence path must stay inside analyzed workspace root '{root}'.");
        if (!_session.FileSystem.FileExists(path))
            throw new FileNotFoundException("Runtime-evidence capture was not found.", path);
        return (path, Path.GetRelativePath(root, path).Replace('\\', '/'));
    }

    private string ReadBoundedUtf8(string path)
    {
        using var source = _session.FileSystem.OpenRead(path);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = source.Read(chunk, 0, chunk.Length);
            if (read == 0) break;
            if (buffer.Length + read > MaximumInputBytes)
                throw new ArgumentException($"Runtime-evidence capture exceeds the {MaximumInputBytes}-byte limit.");
            buffer.Write(chunk, 0, read);
        }
        try
        {
            return StrictUtf8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
        }
        catch (DecoderFallbackException ex)
        {
            throw new ArgumentException("Runtime-evidence capture must be valid UTF-8.", ex);
        }
    }
}

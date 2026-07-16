using System.Text;
using System.Text.Json;
using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Right.Invariants;
using Lifeblood.Domain.Workspaces;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// MCP-edge orchestration for repository evidence drift. Path containment and
/// bounded UTF-8 input stay at the outer edge; metric parsing/comparison stays
/// in the stateless Analysis evaluator and reads the one leased publication.
/// </summary>
internal sealed class EvidenceDriftToolHandler
{
    internal const string DefaultBaselinePath = "docs/code-maps/EVIDENCE.generated.md";
    internal const int MaximumBaselineBytes = 1_048_576;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly GraphSession _session;
    private readonly IInvariantProvider _invariants;

    public EvidenceDriftToolHandler(GraphSession session, IInvariantProvider invariants)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _invariants = invariants ?? throw new ArgumentNullException(nameof(invariants));
    }

    public object Execute(JsonElement? arguments)
    {
        var request = ToolRequestBinder.BindEvidenceDrift(arguments);
        var tolerance = request.EffectiveRelativeTolerancePercent;
        if (!double.IsFinite(tolerance) || tolerance < 0 || tolerance > 100)
        {
            throw new ArgumentOutOfRangeException(
                "relativeTolerancePercent",
                "relativeTolerancePercent must be finite and between 0 and 100 inclusive.");
        }

        var snapshot = _session.CurrentSnapshot;
        var graph = snapshot.Graph
            ?? throw new InvalidOperationException("Evidence drift requires a loaded graph.");
        var analysis = snapshot.Analysis
            ?? throw new InvalidOperationException("Evidence drift requires loaded analysis results.");
        var workspaceRoot = snapshot.Context?.RootPath;
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new InvalidOperationException("Evidence drift requires an analyzed workspace root.");

        var baselinePath = ResolveContainedBaselinePath(
            workspaceRoot,
            request.BaselinePath ?? DefaultBaselinePath);
        var baselineText = ReadBoundedUtf8(baselinePath.FullPath);
        var baselineFingerprint = ContentFingerprint.ComputeUtf8(
            "lifeblood.evidence-baseline.v1",
            baselineText).Value;
        var baseline = EvidenceBaselineDriftEvaluator.ParseBaseline(baselineText);
        var audit = _invariants.Audit(workspaceRoot);
        var live = EvidenceBaselineDriftEvaluator.CaptureLiveMetrics(
            graph,
            analysis,
            snapshot.Identity?.Spec.DefineProfiles,
            new EvidenceInvariantMetrics(
                audit.TotalCount,
                audit.DeclaredCount,
                audit.DuplicateDeclarationCount,
                audit.Duplicates.Length,
                audit.ParseWarnings.Length));
        var comparison = EvidenceBaselineDriftEvaluator.Evaluate(baseline, live, tolerance);
        var freshness = _session.CheckSnapshotDrift(new[] { snapshot })[snapshot.SnapshotId];
        var semanticCurrent = string.Equals(freshness.Status, "current", StringComparison.Ordinal);
        var verdict = semanticCurrent ? comparison.Verdict : "unavailable";
        var refreshAnalysisRecommended = !semanticCurrent;
        var refreshEvidenceRecommended = semanticCurrent
            && !string.Equals(comparison.Verdict, "current", StringComparison.Ordinal);
        var analyzedCommit = snapshot.SourceControl?.CommitHash ?? "";
        bool? commitMatches = baseline.CommitStamp.Length == 0 || analyzedCommit.Length == 0
            ? null
            : CommitsMatch(baseline.CommitStamp, analyzedCommit);

        return new
        {
            kind = "lifeblood.evidence_drift",
            readOnly = true,
            workspaceRoot,
            baseline = new
            {
                path = baselinePath.RelativePath,
                contentFingerprint = baselineFingerprint,
                commitStamp = baseline.CommitStamp,
                parsedMetricCount = baseline.Values.Count,
                parseErrors = baseline.ParseErrors,
            },
            analyzedProvenance = new
            {
                commitHash = analyzedCommit,
                shortCommitHash = snapshot.SourceControl?.ShortCommitHash ?? "",
                dirty = snapshot.SourceControl?.Dirty,
                state = snapshot.SourceControl?.State ?? "unknown",
                captureTiming = "analyzeAdmission",
                semanticEqualityAuthority = false,
                baselineCommitMatches = commitMatches,
            },
            snapshot = new
            {
                snapshotId = snapshot.SnapshotId.ToString(),
                analysisGeneration = snapshot.AnalysisGeneration,
                analysisIdentity = snapshot.Identity == null
                    ? null
                    : WorkspaceAnalysisDescriptor.From(snapshot.Identity),
                freshness,
            },
            policy = new
            {
                name = "semanticEvidenceV1",
                relativeTolerancePercent = tolerance,
                noIncreaseMetrics = new[]
                {
                    EvidenceMetricKeys.Violations,
                    EvidenceMetricKeys.Cycles,
                    EvidenceMetricKeys.InvariantDuplicateDeclarations,
                    EvidenceMetricKeys.InvariantDuplicateIds,
                },
                zeroRequiredMetrics = new[] { EvidenceMetricKeys.InvariantParseWarnings },
            },
            verdict,
            metricVerdict = comparison.Verdict,
            comparison.BaselineComplete,
            refreshAnalysisRecommended,
            refreshEvidenceRecommended,
            recommendation = Recommendation(
                verdict,
                refreshAnalysisRecommended,
                refreshEvidenceRecommended),
            comparison.MissingBaselineMetrics,
            comparison.ParseErrors,
            comparison.Metrics,
            activeProfiles = live.ActiveProfiles,
            retainedGraphBaseCount = 1,
            retainedSemanticBaseCount = snapshot.RetainsSemanticServices ? 1 : 0,
            additionalSemanticBaseCount = 0,
        };
    }

    private (string FullPath, string RelativePath) ResolveContainedBaselinePath(
        string workspaceRoot,
        string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            throw new ArgumentException("baselinePath cannot be empty.", nameof(rawPath));

        var root = Path.GetFullPath(workspaceRoot);
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
                $"baselinePath must stay inside the analyzed workspace root '{root}'.");
        }
        if (!_session.FileSystem.FileExists(path))
            throw new FileNotFoundException("Evidence baseline was not found.", path);

        return (path, relative.Replace('\\', '/'));
    }

    private string ReadBoundedUtf8(string path)
    {
        using var source = _session.FileSystem.OpenRead(path);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var total = 0;
        while (true)
        {
            var read = source.Read(chunk, 0, chunk.Length);
            if (read == 0)
                break;
            total = checked(total + read);
            if (total > MaximumBaselineBytes)
            {
                throw new ArgumentException(
                    $"Evidence baseline exceeds the {MaximumBaselineBytes}-byte limit.");
            }
            buffer.Write(chunk, 0, read);
        }

        try
        {
            return StrictUtf8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
        }
        catch (DecoderFallbackException ex)
        {
            throw new ArgumentException("Evidence baseline must be valid UTF-8.", ex);
        }
    }

    private static bool CommitsMatch(string baseline, string analyzed)
        => baseline.Length >= 7
            && analyzed.Length >= 7
            && (baseline.StartsWith(analyzed, StringComparison.OrdinalIgnoreCase)
                || analyzed.StartsWith(baseline, StringComparison.OrdinalIgnoreCase));

    private static string Recommendation(
        string verdict,
        bool refreshAnalysisRecommended,
        bool refreshEvidenceRecommended)
    {
        if (refreshAnalysisRecommended)
            return "Semantic publication is not current. Run lifeblood_analyze before trusting or refreshing evidence.";
        if (refreshEvidenceRecommended)
            return verdict == "flag"
                ? "Evidence is stale and a safety metric regressed. Investigate the flagged rows, then run the repository evidence refresh workflow."
                : "Evidence is stale. Run the repository evidence refresh workflow before citing the generated baseline.";
        return "Evidence current.";
    }
}

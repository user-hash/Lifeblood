using System.Globalization;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Analysis;

/// <summary>
/// Canonical metric keys shared by analyze receipts and evidence-baseline
/// comparison. Profile keys are normalized case-insensitively because profile
/// names in generated Markdown are presentation text, while the retained
/// analysis identity remains the authority for their spelling and order.
/// </summary>
public static class EvidenceMetricKeys
{
    public const string Symbols = "symbols";
    public const string Edges = "edges";
    public const string Modules = "modules";
    public const string Types = "types";
    public const string Files = "files";
    public const string Violations = "violations";
    public const string Cycles = "cycles";
    public const string InvariantUnique = "invariants.unique";
    public const string InvariantDeclared = "invariants.declared";
    public const string InvariantDuplicateDeclarations = "invariants.duplicateDeclarations";
    public const string InvariantDuplicateIds = "invariants.duplicateIds";
    public const string InvariantParseWarnings = "invariants.parseWarnings";

    public static string ProfileEdges(string profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        return "profileEdges:" + profile.Trim().ToLowerInvariant();
    }
}

/// <summary>Invariant-tree counts joined to the graph metrics for one check.</summary>
public sealed record EvidenceInvariantMetrics(
    int Unique,
    int Declared,
    int DuplicateDeclarations,
    int DuplicateIds,
    int ParseWarnings);

/// <summary>
/// One immutable projection of the existing graph, analysis, profile edge
/// provenance, and live invariant audit. It contains no graph or compiler
/// services and is discarded after the request.
/// </summary>
public sealed record EvidenceLiveMetrics(
    IReadOnlyDictionary<string, long> Values,
    IReadOnlyList<string> OrderedMetricKeys,
    IReadOnlyList<string> ActiveProfiles);

/// <summary>Parsed repository-owned generated evidence baseline.</summary>
public sealed record EvidenceBaseline(
    IReadOnlyDictionary<string, long> Values,
    string CommitStamp,
    IReadOnlyList<string> ParseErrors);

/// <summary>One baseline-to-live comparison row.</summary>
public sealed record EvidenceMetricDrift(
    string Metric,
    string DisplayName,
    long? Baseline,
    long Current,
    long? Delta,
    double? AbsolutePercentDrift,
    string Status,
    string Policy);

/// <summary>Pure metric verdict before live snapshot freshness is applied.</summary>
public sealed record EvidenceBaselineDriftResult(
    string Verdict,
    bool BaselineComplete,
    double RelativeTolerancePercent,
    IReadOnlyList<string> MissingBaselineMetrics,
    IReadOnlyList<string> ParseErrors,
    IReadOnlyList<EvidenceMetricDrift> Metrics);

/// <summary>
/// Stateless evidence baseline parser and evaluator. Input is one already
/// retained semantic graph plus repository-authored Markdown; output is a
/// bounded typed comparison. The evaluator never reads files, runs Git,
/// analyzes source, mutates evidence, or retains another semantic base.
/// </summary>
public static partial class EvidenceBaselineDriftEvaluator
{
    public const double DefaultRelativeTolerancePercent = 0.5;

    private static readonly string[] BaseMetricOrder =
    {
        EvidenceMetricKeys.Symbols,
        EvidenceMetricKeys.Edges,
        EvidenceMetricKeys.Modules,
        EvidenceMetricKeys.Types,
        EvidenceMetricKeys.Files,
        EvidenceMetricKeys.Violations,
        EvidenceMetricKeys.Cycles,
        EvidenceMetricKeys.InvariantUnique,
        EvidenceMetricKeys.InvariantDeclared,
        EvidenceMetricKeys.InvariantDuplicateDeclarations,
        EvidenceMetricKeys.InvariantDuplicateIds,
        EvidenceMetricKeys.InvariantParseWarnings,
    };

    public static EvidenceLiveMetrics CaptureLiveMetrics(
        SemanticGraph graph,
        AnalysisResult analysis,
        IEnumerable<string>? activeProfiles,
        EvidenceInvariantMetrics invariants)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(invariants);

        var profiles = activeProfiles?
            .Where(profile => !string.IsNullOrWhiteSpace(profile))
            .Select(profile => profile.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? Array.Empty<string>();
        var values = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [EvidenceMetricKeys.Symbols] = graph.Symbols.Count,
            [EvidenceMetricKeys.Edges] = graph.Edges.Count,
            [EvidenceMetricKeys.Modules] = analysis.Metrics.TotalModules,
            [EvidenceMetricKeys.Types] = analysis.Metrics.TotalTypes,
            [EvidenceMetricKeys.Files] = analysis.Metrics.TotalFiles,
            [EvidenceMetricKeys.Violations] = analysis.Violations.Length,
            [EvidenceMetricKeys.Cycles] = analysis.Cycles.Length,
            [EvidenceMetricKeys.InvariantUnique] = invariants.Unique,
            [EvidenceMetricKeys.InvariantDeclared] = invariants.Declared,
            [EvidenceMetricKeys.InvariantDuplicateDeclarations] = invariants.DuplicateDeclarations,
            [EvidenceMetricKeys.InvariantDuplicateIds] = invariants.DuplicateIds,
            [EvidenceMetricKeys.InvariantParseWarnings] = invariants.ParseWarnings,
        };

        var ordered = BaseMetricOrder.ToList();
        if (profiles.Length > 1)
        {
            var profileCounts = CountProfileEdges(graph, profiles);
            var insertAt = ordered.IndexOf(EvidenceMetricKeys.Modules);
            foreach (var profile in profiles)
            {
                var key = EvidenceMetricKeys.ProfileEdges(profile);
                values[key] = profileCounts[profile];
                ordered.Insert(insertAt++, key);
            }
        }

        return new EvidenceLiveMetrics(
            new ReadOnlyDictionary<string, long>(values),
            ordered.ToArray(),
            profiles);
    }

    /// <summary>
    /// Counts union-graph edges observed by each requested profile. This is the
    /// single projection used by both analyze summary and evidence drift.
    /// </summary>
    public static IReadOnlyDictionary<string, int> CountProfileEdges(
        SemanticGraph graph,
        IEnumerable<string> activeProfiles)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(activeProfiles);

        var counts = activeProfiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile))
            .Select(profile => profile.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(profile => profile, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var edge in graph.Edges)
        {
            if (edge.Profiles == null)
                continue;
            foreach (var profile in edge.Profiles)
            {
                if (counts.ContainsKey(profile))
                    counts[profile]++;
            }
        }

        return counts;
    }

    public static EvidenceBaseline ParseBaseline(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        var errors = new List<string>();
        var commitStamp = "";
        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith('|') || !line.EndsWith('|'))
                continue;

            var cells = line.Trim('|')
                .Split('|')
                .Select(cell => cell.Trim())
                .ToArray();
            if (cells.Length < 2 || IsSeparator(cells[0]) || IsSeparator(cells[1]))
                continue;

            var normalizedLabel = NormalizeLabel(cells[0]);
            if (IsHeadLabel(normalizedLabel))
            {
                var match = CommitRegex().Match(cells[1]);
                if (match.Success)
                {
                    if (commitStamp.Length > 0 && !string.Equals(commitStamp, match.Value, StringComparison.OrdinalIgnoreCase))
                        errors.Add("Baseline declares more than one conflicting HEAD commit stamp.");
                    else
                        commitStamp = match.Value.ToLowerInvariant();
                }
                continue;
            }

            var metric = TryResolveMetric(normalizedLabel);
            if (metric == null)
                continue;
            if (!TryParseCount(cells[1], out var count))
            {
                errors.Add($"Baseline metric '{cells[0]}' does not contain a non-negative integer count.");
                continue;
            }
            if (!values.TryAdd(metric, count))
                errors.Add($"Baseline metric '{cells[0]}' maps to duplicate canonical key '{metric}'.");
        }

        return new EvidenceBaseline(
            new ReadOnlyDictionary<string, long>(values),
            commitStamp,
            errors.ToArray());
    }

    public static EvidenceBaselineDriftResult Evaluate(
        EvidenceBaseline baseline,
        EvidenceLiveMetrics live,
        double relativeTolerancePercent = DefaultRelativeTolerancePercent)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(live);
        if (!double.IsFinite(relativeTolerancePercent)
            || relativeTolerancePercent < 0
            || relativeTolerancePercent > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(relativeTolerancePercent),
                "Relative tolerance percent must be finite and between 0 and 100 inclusive.");
        }

        var rows = new List<EvidenceMetricDrift>(live.OrderedMetricKeys.Count);
        var missing = new List<string>();
        foreach (var key in live.OrderedMetricKeys)
        {
            var current = live.Values[key];
            if (!baseline.Values.TryGetValue(key, out var stamped))
            {
                missing.Add(key);
                rows.Add(new EvidenceMetricDrift(
                    key,
                    DisplayName(key, live.ActiveProfiles),
                    Baseline: null,
                    Current: current,
                    Delta: null,
                    AbsolutePercentDrift: null,
                    Status: "missingBaseline",
                    Policy: PolicyName(key)));
                continue;
            }

            var delta = current - stamped;
            var percent = stamped == 0
                ? (double?)null
                : Math.Round(Math.Abs(delta) * 100.0 / Math.Abs(stamped), 6);
            rows.Add(new EvidenceMetricDrift(
                key,
                DisplayName(key, live.ActiveProfiles),
                stamped,
                current,
                delta,
                percent,
                Classify(key, stamped, current, percent, relativeTolerancePercent),
                PolicyName(key)));
        }

        var complete = missing.Count == 0 && baseline.ParseErrors.Count == 0;
        var verdict = !complete
            ? "unavailable"
            : rows.Any(row => row.Status == "flag")
                ? "flag"
                : rows.Any(row => row.Status == "stale")
                    ? "stale"
                    : "current";
        return new EvidenceBaselineDriftResult(
            verdict,
            complete,
            relativeTolerancePercent,
            missing,
            baseline.ParseErrors,
            rows);
    }

    private static string Classify(
        string key,
        long baseline,
        long current,
        double? percent,
        double tolerance)
    {
        if (key == EvidenceMetricKeys.InvariantParseWarnings)
            return current > 0 ? "flag" : baseline == current ? "exact" : "stale";

        if (key is EvidenceMetricKeys.Violations
            or EvidenceMetricKeys.Cycles
            or EvidenceMetricKeys.InvariantDuplicateDeclarations
            or EvidenceMetricKeys.InvariantDuplicateIds)
        {
            if (current > baseline)
                return "flag";
            return current == baseline ? "exact" : "stale";
        }

        if (current == baseline)
            return "exact";
        if (percent.HasValue && percent.Value <= tolerance)
            return "withinTolerance";
        return "stale";
    }

    private static string PolicyName(string key)
        => key == EvidenceMetricKeys.InvariantParseWarnings
            ? "zeroRequired"
            : key is EvidenceMetricKeys.Violations
                or EvidenceMetricKeys.Cycles
                or EvidenceMetricKeys.InvariantDuplicateDeclarations
                or EvidenceMetricKeys.InvariantDuplicateIds
                    ? "noIncrease"
                    : "relativeTolerance";

    private static string DisplayName(string key, IReadOnlyList<string> activeProfiles)
    {
        if (key.StartsWith("profileEdges:", StringComparison.Ordinal))
        {
            var normalized = key["profileEdges:".Length..];
            var profile = activeProfiles.FirstOrDefault(candidate =>
                string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase));
            return $"{profile ?? normalized} profile edges";
        }

        return key switch
        {
            EvidenceMetricKeys.Symbols => "Symbols",
            EvidenceMetricKeys.Edges => "Edges",
            EvidenceMetricKeys.Modules => "Modules",
            EvidenceMetricKeys.Types => "Types",
            EvidenceMetricKeys.Files => "File symbols",
            EvidenceMetricKeys.Violations => "Architecture violations",
            EvidenceMetricKeys.Cycles => "Cycles",
            EvidenceMetricKeys.InvariantUnique => "Unique invariants",
            EvidenceMetricKeys.InvariantDeclared => "Declared occurrences",
            EvidenceMetricKeys.InvariantDuplicateDeclarations => "Duplicate declarations",
            EvidenceMetricKeys.InvariantDuplicateIds => "Duplicate IDs",
            EvidenceMetricKeys.InvariantParseWarnings => "Parse warnings",
            _ => key,
        };
    }

    private static string? TryResolveMetric(string label)
    {
        var profile = ProfileEdgesRegex().Match(label);
        if (profile.Success)
            return EvidenceMetricKeys.ProfileEdges(profile.Groups["profile"].Value);
        if (label == "symbols") return EvidenceMetricKeys.Symbols;
        if (label == "edges" || label.StartsWith("edges (", StringComparison.Ordinal)) return EvidenceMetricKeys.Edges;
        if (label == "modules") return EvidenceMetricKeys.Modules;
        if (label == "types") return EvidenceMetricKeys.Types;
        if (label is "files" or "file symbols" or "lifeblood file symbols") return EvidenceMetricKeys.Files;
        if (label is "violations" or "architecture violations") return EvidenceMetricKeys.Violations;
        if (label == "cycles" || label.StartsWith("cycles (", StringComparison.Ordinal)) return EvidenceMetricKeys.Cycles;
        if (label is "unique invariants" or "invariant total") return EvidenceMetricKeys.InvariantUnique;
        if (label is "declared occurrences" or "declared count") return EvidenceMetricKeys.InvariantDeclared;
        if (label is "duplicate declarations" or "duplicate declaration count") return EvidenceMetricKeys.InvariantDuplicateDeclarations;
        if (label is "duplicate ids" or "duplicate id count") return EvidenceMetricKeys.InvariantDuplicateIds;
        if (label is "parse warnings" or "invariant parse warnings") return EvidenceMetricKeys.InvariantParseWarnings;
        return null;
    }

    private static string NormalizeLabel(string value)
    {
        var normalized = value
            .Replace("**", "", StringComparison.Ordinal)
            .Replace("`", "", StringComparison.Ordinal)
            .Trim()
            .TrimStart('—', '–', '-', ' ')
            .Trim();
        return WhitespaceRegex().Replace(normalized, " ").ToLowerInvariant();
    }

    private static bool TryParseCount(string value, out long count)
    {
        count = 0;
        var match = CountRegex().Match(value);
        if (!match.Success)
            return false;
        var normalized = match.Value
            .Replace(",", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal);
        return long.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out count)
            && count >= 0;
    }

    private static bool IsSeparator(string cell)
        => cell.Length > 0 && cell.All(character => character is '-' or ':');

    private static bool IsHeadLabel(string label)
        => label == "head" || label.EndsWith(" head", StringComparison.Ordinal);

    [GeneratedRegex(@"^(?<profile>.+?) profile edges$", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileEdgesRegex();

    [GeneratedRegex(@"\b[0-9a-fA-F]{7,64}\b", RegexOptions.CultureInvariant)]
    private static partial Regex CommitRegex();

    [GeneratedRegex(@"\d[\d,_ ]*", RegexOptions.CultureInvariant)]
    private static partial Regex CountRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}

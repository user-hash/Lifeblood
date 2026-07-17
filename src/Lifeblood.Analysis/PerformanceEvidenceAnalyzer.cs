using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Analysis;

/// <summary>
/// Stateless joins over imported runtime evidence and one immutable semantic
/// graph. It owns no parser, file access, runtime sampler, or retained cache.
/// </summary>
public static class PerformanceEvidenceAnalyzer
{
    public static PerformanceCorrelationReport Correlate(
        SemanticGraph graph,
        PerformanceCapture capture,
        IReadOnlyList<PerformanceMarkerAlias> aliases,
        IReadOnlyList<string> invariantIds,
        IReadOnlyList<SourceEvidenceFact> sourceFacts,
        SourceEvidenceScanReceipt sourceReceipt,
        int maximumMarkers,
        int maximumTestsPerSymbol)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(invariantIds);
        ArgumentNullException.ThrowIfNull(sourceFacts);
        ArgumentNullException.ThrowIfNull(sourceReceipt);
        if (maximumMarkers <= 0) throw new ArgumentOutOfRangeException(nameof(maximumMarkers));
        if (maximumTestsPerSymbol < 0) throw new ArgumentOutOfRangeException(nameof(maximumTestsPerSymbol));

        var aliasMap = aliases
            .GroupBy(alias => alias.Marker, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(alias => alias.SymbolId).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        var invariantSet = invariantIds.ToHashSet(StringComparer.Ordinal);
        var factsByTerm = sourceFacts
            .GroupBy(fact => fact.MatchedTerm, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var groups = capture.Measurements
            .GroupBy(measurement => measurement.Marker, StringComparer.Ordinal)
            .Select(group => new
            {
                Marker = group.Key,
                Measurements = group.ToArray(),
                Hottest = group
                    .Where(measurement => measurement.Available && measurement.Value.HasValue)
                    .OrderByDescending(measurement => measurement.Value)
                    .FirstOrDefault(),
            })
            .OrderByDescending(group => group.Hottest?.Value ?? double.NegativeInfinity)
            .ThenBy(group => group.Marker, StringComparer.Ordinal)
            .ToArray();

        var output = new List<PerformanceMarkerCorrelation>(Math.Min(groups.Length, maximumMarkers));
        var unique = 0;
        var ambiguous = 0;
        var unmapped = 0;
        foreach (var group in groups)
        {
            var resolution = ResolveCandidates(graph, group.Marker, group.Measurements, aliasMap, factsByTerm);
            var outcome = resolution.SymbolIds.Length switch
            {
                0 => PerformanceCorrelationOutcome.Unmapped,
                1 => PerformanceCorrelationOutcome.Unique,
                _ => PerformanceCorrelationOutcome.Ambiguous,
            };
            if (outcome == PerformanceCorrelationOutcome.Unique) unique++;
            else if (outcome == PerformanceCorrelationOutcome.Ambiguous) ambiguous++;
            else unmapped++;

            if (output.Count >= maximumMarkers) continue;
            var candidates = resolution.SymbolIds
                .Select(id => ProjectSymbol(
                    graph,
                    id,
                    invariantSet,
                    sourceFacts,
                    maximumTestsPerSymbol))
                .Where(candidate => candidate != null)
                .Select(candidate => candidate!)
                .ToArray();
            output.Add(new PerformanceMarkerCorrelation
            {
                Marker = group.Marker,
                Outcome = outcome,
                ResolutionSource = resolution.Source,
                MeasurementCount = group.Measurements.Length,
                HottestValue = group.Hottest?.Value,
                HottestStatistic = group.Hottest?.Statistic ?? "",
                HottestUnit = group.Hottest?.Unit ?? "",
                Candidates = candidates,
            });
        }

        return new PerformanceCorrelationReport
        {
            MarkerCount = groups.Length,
            UniqueCount = unique,
            AmbiguousCount = ambiguous,
            UnmappedCount = unmapped,
            ReturnedCount = output.Count,
            Truncated = output.Count < groups.Length,
            Markers = output.ToArray(),
            SourceEvidence = sourceReceipt,
            Limitations = new[]
            {
                "Correlation is evidence, not runtime attribution proof: aliases and exact symbol names outrank request-local string-literal ownership.",
                "Recent code owners are not inferred from Git authorship. Source-control publication provenance remains separate until the workspace supplies an ownership authority.",
            },
        };
    }

    public static PerformanceComparisonReport Compare(
        PerformanceCapture baseline,
        PerformanceCapture candidate,
        string comparisonMode,
        IReadOnlyCollection<string> allowedDifferences,
        int maximumDeltas)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(allowedDifferences);
        if (maximumDeltas <= 0) throw new ArgumentOutOfRangeException(nameof(maximumDeltas));
        var crossDevice = string.Equals(comparisonMode, "crossDevice", StringComparison.OrdinalIgnoreCase);
        if (!crossDevice && !string.Equals(comparisonMode, "strict", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("comparisonMode must be 'crossDevice' or 'strict'.", nameof(comparisonMode));
        var allowed = allowedDifferences.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var left = NormalizeMeasurements(baseline.Measurements);
        var right = NormalizeMeasurements(candidate.Measurements);
        var checks = BuildIdentityChecks(baseline, candidate, crossDevice, allowed).ToList();
        AddMeasurementIdentityChecks(checks, left, right);
        var reject = checks.Any(check => check.Status == "Reject");
        var partial = checks.Any(check => check.Status == "Partial");
        var verdict = reject
            ? PerformanceComparisonVerdict.RejectComparison
            : partial
                ? PerformanceComparisonVerdict.PartiallyComparable
                : PerformanceComparisonVerdict.Comparable;

        if (reject)
        {
            return new PerformanceComparisonReport
            {
                Verdict = verdict,
                IdentityChecks = checks.ToArray(),
                Deltas = Array.Empty<PerformanceDelta>(),
                MatchedMeasurementCount = 0,
                ReturnedDeltaCount = 0,
                Truncated = false,
                Limitations = new[] { "Numeric deltas are withheld because capture identity failed a required comparability gate." },
            };
        }

        var keys = left.Measurements.Keys
            .Intersect(right.Measurements.Keys, StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        var deltas = new List<PerformanceDelta>(Math.Min(keys.Length, maximumDeltas));
        foreach (var key in keys)
        {
            if (deltas.Count >= maximumDeltas) break;
            var a = left.Measurements[key];
            var b = right.Measurements[key];
            var delta = b.Value - a.Value;
            deltas.Add(new PerformanceDelta(
                a.DisplayKey,
                a.Stage,
                a.Category,
                a.Marker,
                a.Thread,
                a.Statistic,
                a.Unit,
                a.Value,
                b.Value,
                delta,
                a.Value == 0 ? null : delta / Math.Abs(a.Value) * 100d,
                a.DerivedFromSamples || b.DerivedFromSamples ? "derivedFromSamples" : "preAggregated"));
        }

        return new PerformanceComparisonReport
        {
            Verdict = verdict,
            IdentityChecks = checks.ToArray(),
            Deltas = deltas.ToArray(),
            MatchedMeasurementCount = keys.Length,
            ReturnedDeltaCount = deltas.Count,
            Truncated = deltas.Count < keys.Length,
            Limitations = partial
                ? new[] { "Deltas are emitted, but at least one provenance or environment field is missing or materially different; read identityChecks before ranking changes." }
                : Array.Empty<string>(),
        };
    }

    private static (string[] SymbolIds, string Source) ResolveCandidates(
        SemanticGraph graph,
        string marker,
        IReadOnlyList<PerformanceMeasurement> measurements,
        IReadOnlyDictionary<string, string[]> aliases,
        IReadOnlyDictionary<string, SourceEvidenceFact[]> factsByTerm)
    {
        if (aliases.TryGetValue(marker, out var aliasIds))
        {
            return (
                aliasIds.Where(id => graph.GetSymbol(id) != null).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                "CallerAlias");
        }

        var hints = measurements
            .Select(measurement => measurement.SymbolHint)
            .Where(hint => !string.IsNullOrWhiteSpace(hint))
            .Distinct(StringComparer.Ordinal)
            .SelectMany(hint => ResolveExact(graph, hint))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (hints.Length > 0) return (hints, "CaptureSymbolHint");

        var exact = ResolveExact(graph, marker).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        if (exact.Length > 0) return (exact, "ExactSymbolName");

        if (factsByTerm.TryGetValue(marker, out var facts))
        {
            var sourceOwned = facts
                .Where(fact => fact.Kind == SourceEvidenceKind.StringLiteral)
                .Select(fact => fact.ContainingSymbolId)
                .Where(id => !string.IsNullOrWhiteSpace(id) && graph.GetSymbol(id!) != null)
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            if (sourceOwned.Length > 0) return (sourceOwned, "StringLiteralOwnership");
        }

        return (Array.Empty<string>(), "None");
    }

    private static IEnumerable<string> ResolveExact(SemanticGraph graph, string value)
    {
        var canonical = graph.GetSymbol(value);
        if (canonical != null) yield return canonical.Id;
        foreach (var symbol in graph.FindByShortName(value)) yield return symbol.Id;
        foreach (var symbol in graph.Symbols)
        {
            if (string.Equals(symbol.QualifiedName, value, StringComparison.Ordinal))
                yield return symbol.Id;
        }
    }

    private static PerformanceSymbolCorrelation? ProjectSymbol(
        SemanticGraph graph,
        string symbolId,
        IReadOnlySet<string> invariantIds,
        IReadOnlyList<SourceEvidenceFact> sourceFacts,
        int maximumTests)
    {
        var symbol = graph.GetSymbol(symbolId);
        if (symbol == null) return null;
        var relatedInvariants = sourceFacts
            .Where(fact => string.Equals(fact.ContainingSymbolId, symbolId, StringComparison.Ordinal)
                           && invariantIds.Contains(fact.MatchedTerm))
            .Select(fact => fact.MatchedTerm)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var impact = TestImpactAnalyzer.AnalyzeSymbol(graph, symbolId);
        var tests = impact.AffectedTestClasses
            .Take(maximumTests)
            .Select(test => new PerformanceTestCorrelation(test.TypeId, test.QualifiedName, test.FilePath, test.MinDistance))
            .ToArray();
        return new PerformanceSymbolCorrelation
        {
            SymbolId = symbol.Id,
            Name = symbol.Name,
            QualifiedName = symbol.QualifiedName,
            Kind = symbol.Kind.ToString(),
            FilePath = symbol.FilePath,
            Line = symbol.Line,
            ModuleName = FindModuleName(graph, symbol),
            InvariantIds = relatedInvariants,
            Tests = tests,
            TotalAffectedTestClassCount = impact.AffectedTestClasses.Length,
            TestsTruncated = tests.Length < impact.AffectedTestClasses.Length,
        };
    }

    private static string FindModuleName(SemanticGraph graph, Symbol symbol)
    {
        var current = symbol;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (current.Kind != SymbolKind.Module && current.ParentId.Length > 0 && visited.Add(current.Id))
        {
            var parent = graph.GetSymbol(current.ParentId);
            if (parent == null) break;
            current = parent;
        }
        return current.Kind == SymbolKind.Module ? current.Name : "";
    }

    private static PerformanceIdentityCheck[] BuildIdentityChecks(
        PerformanceCapture baseline,
        PerformanceCapture candidate,
        bool crossDevice,
        IReadOnlySet<string> allowed)
    {
        var checks = new List<PerformanceIdentityCheck>();
        AddRequired(checks, "scenarioId", baseline.Identity.ScenarioId, candidate.Identity.ScenarioId);
        AddRequired(checks, "productName", baseline.Identity.ProductName, candidate.Identity.ProductName);
        AddRequired(checks, "appVersion", baseline.Identity.AppVersion, candidate.Identity.AppVersion);
        AddRequired(checks, "buildId", baseline.Identity.BuildId, candidate.Identity.BuildId);
        AddRequired(checks, "gitCommit", baseline.Identity.GitCommit, candidate.Identity.GitCommit);
        AddDirty(checks, baseline.Identity.Dirty, candidate.Identity.Dirty);
        AddSet(checks, "defineProfiles", baseline.Identity.DefineProfiles, candidate.Identity.DefineProfiles);
        AddSet(checks, "featureFlags", baseline.Identity.FeatureFlags, candidate.Identity.FeatureFlags);
        AddRequired(checks, "platform", baseline.Device.Platform, candidate.Device.Platform);
        AddRequired(checks, "graphicsApi", baseline.Device.GraphicsApi, candidate.Device.GraphicsApi);
        AddDevice(checks, "deviceModel", baseline.Device.DeviceModel, candidate.Device.DeviceModel, crossDevice, allowed);
        AddDevice(checks, "deviceClass", baseline.Device.DeviceClass, candidate.Device.DeviceClass, crossDevice, allowed);
        AddDevice(checks, "operatingSystem", baseline.Device.OperatingSystem, candidate.Device.OperatingSystem, crossDevice, allowed);
        AddDevice(checks, "processor", baseline.Device.Processor, candidate.Device.Processor, crossDevice, allowed);
        AddDevice(checks, "graphicsDevice", baseline.Device.GraphicsDevice, candidate.Device.GraphicsDevice, crossDevice, allowed);
        AddEnvironmental(checks, "width", baseline.Device.Width, candidate.Device.Width, allowed);
        AddEnvironmental(checks, "height", baseline.Device.Height, candidate.Device.Height, allowed);
        AddEnvironmental(checks, "refreshRateHz", baseline.Device.RefreshRateHz, candidate.Device.RefreshRateHz, allowed);
        AddRequired(checks, "workloadFingerprint", baseline.Workload.Fingerprint, candidate.Workload.Fingerprint);
        AddRequired(checks, "captureMode", baseline.Workload.CaptureMode, candidate.Workload.CaptureMode);
        AddRequired(checks, "audioSampleRate", baseline.Workload.AudioSampleRate, candidate.Workload.AudioSampleRate);
        AddRequired(checks, "audioBufferFrames", baseline.Workload.AudioBufferFrames, candidate.Workload.AudioBufferFrames);
        AddRequired(checks, "targetFrameRate", baseline.Workload.TargetFrameRate, candidate.Workload.TargetFrameRate);
        foreach (var name in baseline.Workload.Counters.Keys.Union(candidate.Workload.Counters.Keys, StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal))
        {
            baseline.Workload.Counters.TryGetValue(name, out var left);
            candidate.Workload.Counters.TryGetValue(name, out var right);
            var leftExists = baseline.Workload.Counters.ContainsKey(name);
            var rightExists = candidate.Workload.Counters.ContainsKey(name);
            checks.Add(new PerformanceIdentityCheck(
                "counter." + name,
                leftExists ? Format(left) : "",
                rightExists ? Format(right) : "",
                leftExists && rightExists && NearlyEqual(left, right) ? "Match" : "Reject",
                leftExists && rightExists && NearlyEqual(left, right)
                    ? "Workload counter matches."
                    : "Domain workload counters must be present and equal."));
        }
        return checks.ToArray();
    }

    private static void AddRequired(List<PerformanceIdentityCheck> checks, string field, string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
            checks.Add(new PerformanceIdentityCheck(field, left, right, "Partial", "Required provenance is missing from one or both captures."));
        else if (string.Equals(left, right, StringComparison.Ordinal))
            checks.Add(new PerformanceIdentityCheck(field, left, right, "Match", "Required identity matches."));
        else
            checks.Add(new PerformanceIdentityCheck(field, left, right, "Reject", "Required identity differs."));
    }

    private static void AddRequired<T>(List<PerformanceIdentityCheck> checks, string field, T? left, T? right) where T : struct
    {
        if (!left.HasValue || !right.HasValue)
            checks.Add(new PerformanceIdentityCheck(field, Format(left), Format(right), "Partial", "Required environment value is missing from one or both captures."));
        else if (EqualityComparer<T>.Default.Equals(left.Value, right.Value))
            checks.Add(new PerformanceIdentityCheck(field, Format(left), Format(right), "Match", "Required environment value matches."));
        else
            checks.Add(new PerformanceIdentityCheck(field, Format(left), Format(right), "Reject", "Required environment value differs."));
    }

    private static void AddDirty(List<PerformanceIdentityCheck> checks, bool? left, bool? right)
    {
        var status = left == false && right == false ? "Match" : "Partial";
        checks.Add(new PerformanceIdentityCheck(
            "dirty",
            Format(left),
            Format(right),
            status,
            status == "Match" ? "Both captures report clean source state." : "Dirty or unknown source state weakens provenance."));
    }

    private static void AddSet(List<PerformanceIdentityCheck> checks, string field, string[] left, string[] right)
    {
        var a = left.OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var b = right.OrderBy(item => item, StringComparer.Ordinal).ToArray();
        if (a.Length == 0 || b.Length == 0)
            checks.Add(new PerformanceIdentityCheck(field, string.Join(',', a), string.Join(',', b), "Partial", "Required configuration set is missing from one or both captures."));
        else if (a.SequenceEqual(b, StringComparer.Ordinal))
            checks.Add(new PerformanceIdentityCheck(field, string.Join(',', a), string.Join(',', b), "Match", "Configuration set matches."));
        else
            checks.Add(new PerformanceIdentityCheck(field, string.Join(',', a), string.Join(',', b), "Reject", "Configuration set differs."));
    }

    private static void AddDevice(
        List<PerformanceIdentityCheck> checks,
        string field,
        string left,
        string right,
        bool crossDevice,
        IReadOnlySet<string> allowed)
    {
        if (left.Length == 0 || right.Length == 0)
            checks.Add(new PerformanceIdentityCheck(field, left, right, "Partial", "Device evidence is missing from one or both captures."));
        else if (string.Equals(left, right, StringComparison.Ordinal))
            checks.Add(new PerformanceIdentityCheck(field, left, right, "Match", "Device field matches."));
        else if (crossDevice || allowed.Contains(field))
            checks.Add(new PerformanceIdentityCheck(field, left, right, "ExpectedDifference", "Difference is explicit in cross-device comparison."));
        else
            checks.Add(new PerformanceIdentityCheck(field, left, right, "Reject", "Strict comparison requires identical device evidence."));
    }

    private static void AddEnvironmental<T>(
        List<PerformanceIdentityCheck> checks,
        string field,
        T? left,
        T? right,
        IReadOnlySet<string> allowed) where T : struct
    {
        if (!left.HasValue || !right.HasValue)
            checks.Add(new PerformanceIdentityCheck(field, Format(left), Format(right), "Partial", "Environmental evidence is missing."));
        else if (EqualityComparer<T>.Default.Equals(left.Value, right.Value))
            checks.Add(new PerformanceIdentityCheck(field, Format(left), Format(right), "Match", "Environmental value matches."));
        else if (allowed.Contains(field))
            checks.Add(new PerformanceIdentityCheck(field, Format(left), Format(right), "ExpectedDifference", "Caller explicitly accepted this environmental difference."));
        else
            checks.Add(new PerformanceIdentityCheck(field, Format(left), Format(right), "Partial", "Environmental difference may affect the measured result."));
    }

    private static void AddMeasurementIdentityChecks(
        ICollection<PerformanceIdentityCheck> checks,
        NormalizationResult baseline,
        NormalizationResult candidate)
    {
        var baselineKeys = baseline.Measurements.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
        var candidateKeys = candidate.Measurements.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
        var keysMatch = baselineKeys.SequenceEqual(candidateKeys, StringComparer.Ordinal);
        var baselineOnly = baselineKeys.Except(candidateKeys, StringComparer.Ordinal)
            .Select(key => baseline.Measurements[key].DisplayKey)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        var candidateOnly = candidateKeys.Except(baselineKeys, StringComparer.Ordinal)
            .Select(key => candidate.Measurements[key].DisplayKey)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        checks.Add(new PerformanceIdentityCheck(
            "measurementKeys",
            FormatMeasurementKeyEvidence(baselineKeys.Length, baselineOnly),
            FormatMeasurementKeyEvidence(candidateKeys.Length, candidateOnly),
            keysMatch ? "Match" : "Partial",
            keysMatch
                ? "Normalized stage/category/marker/thread/statistic/unit identities match."
                : "Normalized measurement identities differ; deltas cover only the explicit intersection."));
        var baselineConflictCount = baseline.ConflictingKeys.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var candidateConflictCount = candidate.ConflictingKeys.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var hasConflicts = baseline.ConflictingKeys.Length > 0 || candidate.ConflictingKeys.Length > 0;
        checks.Add(new PerformanceIdentityCheck(
            "measurementConflicts",
            baselineConflictCount,
            candidateConflictCount,
            hasConflicts ? "Partial" : "Match",
            !hasConflicts
                ? "No conflicting duplicate pre-aggregated measurement keys."
                : "Conflicting duplicate pre-aggregated keys were withheld instead of selecting an arbitrary value: "
                  + string.Join(", ", baseline.ConflictingKeys
                      .Concat(candidate.ConflictingKeys)
                      .Distinct(StringComparer.Ordinal)
                      .OrderBy(key => key, StringComparer.Ordinal)
                      .Take(8))));
    }

    private static string FormatMeasurementKeyEvidence(int totalCount, IReadOnlyList<string> exclusiveKeys)
    {
        var count = totalCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (exclusiveKeys.Count == 0) return "count=" + count;
        var returned = Math.Min(8, exclusiveKeys.Count);
        return "count=" + count
               + "; only=[" + string.Join(", ", exclusiveKeys.Take(returned)) + "]"
               + "; omitted=" + (exclusiveKeys.Count - returned).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static NormalizationResult NormalizeMeasurements(IReadOnlyList<PerformanceMeasurement> measurements)
    {
        var result = new Dictionary<string, NormalizedMeasurement>(StringComparer.Ordinal);
        var conflicts = new List<string>();
        var preAggregated = measurements
            .Where(row => row.Available && row.Value.HasValue && double.IsFinite(row.Value.Value)
                          && !string.Equals(row.Statistic, PerformanceStatistic.Sample, StringComparison.OrdinalIgnoreCase))
            .Select(measurement => new NormalizedMeasurement(
                measurement.Stage,
                measurement.Category,
                measurement.Marker,
                measurement.Thread,
                CanonicalStatistic(measurement.Statistic),
                measurement.Unit,
                measurement.Value!.Value,
                DerivedFromSamples: false))
            .GroupBy(row => row.InternalKey, StringComparer.Ordinal);
        foreach (var group in preAggregated)
        {
            var rows = group.ToArray();
            var first = rows[0];
            if (rows.Skip(1).Any(row => !NearlyEqual(first.Value, row.Value)))
            {
                conflicts.Add(first.DisplayKey);
                continue;
            }
            result.Add(group.Key, first);
        }

        foreach (var group in measurements
                     .Where(row => row.Available && row.Value.HasValue && double.IsFinite(row.Value.Value)
                                   && string.Equals(row.Statistic, PerformanceStatistic.Sample, StringComparison.OrdinalIgnoreCase))
                     .GroupBy(row => BaseKey(row.Stage, row.Category, row.Marker, row.Thread, row.Unit), StringComparer.Ordinal))
        {
            var values = group.Select(row => row.Value!.Value).OrderBy(value => value).ToArray();
            if (values.Length == 0) continue;
            var seed = group.First();
            AddDerived(result, conflicts, seed, PerformanceStatistic.Average, values.Average());
            AddDerived(result, conflicts, seed, PerformanceStatistic.P95, Percentile(values, 0.95));
            AddDerived(result, conflicts, seed, PerformanceStatistic.Maximum, values[^1]);
            AddDerived(result, conflicts, seed, PerformanceStatistic.Total, values.Sum());
        }
        return new NormalizationResult(
            result,
            conflicts.Distinct(StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToArray());
    }

    private static void AddDerived(
        IDictionary<string, NormalizedMeasurement> result,
        IReadOnlyCollection<string> conflicts,
        PerformanceMeasurement seed,
        string statistic,
        double value)
    {
        var row = new NormalizedMeasurement(
            seed.Stage,
            seed.Category,
            seed.Marker,
            seed.Thread,
            statistic,
            seed.Unit,
            value,
            true);
        if (!conflicts.Contains(row.DisplayKey, StringComparer.Ordinal))
            result.TryAdd(row.InternalKey, row);
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 1) return sorted[0];
        var position = (sorted.Length - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        var fraction = position - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }

    private static string CanonicalStatistic(string statistic)
        => statistic.Trim().ToLowerInvariant() switch
        {
            "average" or "mean" or "avg" => PerformanceStatistic.Average,
            "p95" or "95th" => PerformanceStatistic.P95,
            "maximum" or "max" => PerformanceStatistic.Maximum,
            "total" or "sum" => PerformanceStatistic.Total,
            _ => statistic.Trim(),
        };

    private static string BaseKey(string stage, string category, string marker, string thread, string unit)
        => string.Join('\u001f', stage, category, marker, thread, unit);

    private static string Format<T>(T? value) where T : struct
        => value?.ToString() ?? "";

    private static string Format(double value)
        => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    private static bool NearlyEqual(double left, double right)
        => Math.Abs(left - right) <= Math.Max(1e-9, Math.Max(Math.Abs(left), Math.Abs(right)) * 1e-9);

    private sealed record NormalizedMeasurement(
        string Stage,
        string Category,
        string Marker,
        string Thread,
        string Statistic,
        string Unit,
        double Value,
        bool DerivedFromSamples)
    {
        public string InternalKey => string.Join('\u001f', Stage, Category, Marker, Thread, Statistic, Unit);
        public string DisplayKey => string.Join('/', Stage, Category, Marker, Thread, Statistic, Unit);
    }

    private sealed record NormalizationResult(
        IReadOnlyDictionary<string, NormalizedMeasurement> Measurements,
        string[] ConflictingKeys);
}

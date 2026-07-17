namespace Lifeblood.Domain.Results;

/// <summary>
/// Request-local external runtime evidence. These records deliberately carry
/// no semantic-graph objects: import adapters describe what a runtime measured,
/// while a later analysis step may correlate that evidence with one leased
/// graph publication.
/// </summary>
public sealed class PerformanceCapture
{
    public required string CaptureId { get; init; }
    public required string SourceFormat { get; init; }
    public required string SourceSchema { get; init; }
    public required PerformanceCaptureIdentity Identity { get; init; }
    public required PerformanceDeviceIdentity Device { get; init; }
    public required PerformanceWorkloadIdentity Workload { get; init; }
    public required PerformanceMeasurement[] Measurements { get; init; }
    public required PerformanceImportReceipt ImportReceipt { get; init; }
}

public sealed class PerformanceCaptureIdentity
{
    public string ScenarioId { get; init; } = "";
    public string ProductName { get; init; } = "";
    public string AppVersion { get; init; } = "";
    public string BuildId { get; init; } = "";
    public string GitCommit { get; init; } = "";
    public bool? Dirty { get; init; }
    public string[] DefineProfiles { get; init; } = Array.Empty<string>();
    public string[] FeatureFlags { get; init; } = Array.Empty<string>();
    public DateTimeOffset? CapturedAtUtc { get; init; }
}

public sealed class PerformanceDeviceIdentity
{
    public string DeviceModel { get; init; } = "";
    public string DeviceClass { get; init; } = "";
    public string Platform { get; init; } = "";
    public string OperatingSystem { get; init; } = "";
    public string Processor { get; init; } = "";
    public string GraphicsDevice { get; init; } = "";
    public string GraphicsApi { get; init; } = "";
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? RefreshRateHz { get; init; }
}

public sealed class PerformanceWorkloadIdentity
{
    public string Fingerprint { get; init; } = "";
    public string FingerprintSource { get; init; } = "";
    public string CaptureMode { get; init; } = "";
    public int? AudioSampleRate { get; init; }
    public int? AudioBufferFrames { get; init; }
    public int? TargetFrameRate { get; init; }
    public IReadOnlyDictionary<string, double> Counters { get; init; }
        = new Dictionary<string, double>(StringComparer.Ordinal);
}

/// <summary>
/// One raw sample or pre-aggregated statistic. Unavailable runtime counters are
/// explicit rows and never become numeric zeroes.
/// </summary>
public sealed class PerformanceMeasurement
{
    public string Stage { get; init; } = "";
    public string Category { get; init; } = "";
    public required string Marker { get; init; }
    public string Thread { get; init; } = "";
    public string Statistic { get; init; } = PerformanceStatistic.Sample;
    public string Unit { get; init; } = "";
    public double? Value { get; init; }
    public long? SampleCount { get; init; }
    public long? InvocationCount { get; init; }
    public long? FrameIndex { get; init; }
    public bool Available { get; init; } = true;
    public string Error { get; init; } = "";
    public string SymbolHint { get; init; } = "";
}

public sealed class PerformanceImportReceipt
{
    public required string SourceName { get; init; }
    public required int InputCharacterCount { get; init; }
    public required int ObservedMeasurementCount { get; init; }
    public required int EmittedMeasurementCount { get; init; }
    public required int UnavailableMeasurementCount { get; init; }
    public required bool Truncated { get; init; }
    public string[] Limitations { get; init; } = Array.Empty<string>();
}

public sealed record PerformanceEvidenceDocument(
    string SourceName,
    string Content,
    string? FormatHint,
    int MaximumMeasurements);

public sealed record PerformanceMarkerAlias(string Marker, string SymbolId);

public sealed class PerformanceCorrelationReport
{
    public required int MarkerCount { get; init; }
    public required int UniqueCount { get; init; }
    public required int AmbiguousCount { get; init; }
    public required int UnmappedCount { get; init; }
    public required int ReturnedCount { get; init; }
    public required bool Truncated { get; init; }
    public required PerformanceMarkerCorrelation[] Markers { get; init; }
    public required SourceEvidenceScanReceipt SourceEvidence { get; init; }
    public string[] Limitations { get; init; } = Array.Empty<string>();
}

public sealed class PerformanceMarkerCorrelation
{
    public required string Marker { get; init; }
    public required string Outcome { get; init; }
    public required string ResolutionSource { get; init; }
    public required int MeasurementCount { get; init; }
    public double? HottestValue { get; init; }
    public string HottestStatistic { get; init; } = "";
    public string HottestUnit { get; init; } = "";
    public required PerformanceSymbolCorrelation[] Candidates { get; init; }
}

public sealed class PerformanceSymbolCorrelation
{
    public required string SymbolId { get; init; }
    public required string Name { get; init; }
    public required string QualifiedName { get; init; }
    public required string Kind { get; init; }
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required string ModuleName { get; init; }
    public required string[] InvariantIds { get; init; }
    public required PerformanceTestCorrelation[] Tests { get; init; }
    public required int TotalAffectedTestClassCount { get; init; }
    public required bool TestsTruncated { get; init; }
}

public sealed record PerformanceTestCorrelation(
    string TypeId,
    string QualifiedName,
    string FilePath,
    int MinDistance);

public sealed class PerformanceComparisonReport
{
    public required string Verdict { get; init; }
    public required PerformanceIdentityCheck[] IdentityChecks { get; init; }
    public required PerformanceDelta[] Deltas { get; init; }
    public required int MatchedMeasurementCount { get; init; }
    public required int ReturnedDeltaCount { get; init; }
    public required bool Truncated { get; init; }
    public string[] Limitations { get; init; } = Array.Empty<string>();
}

public sealed record PerformanceIdentityCheck(
    string Field,
    string Baseline,
    string Candidate,
    string Status,
    string Reason);

public sealed record PerformanceDelta(
    string Key,
    string Stage,
    string Category,
    string Marker,
    string Thread,
    string Statistic,
    string Unit,
    double Baseline,
    double Candidate,
    double Delta,
    double? PercentDelta,
    string Normalization);

public static class PerformanceStatistic
{
    public const string Sample = "Sample";
    public const string Average = "Average";
    public const string P95 = "P95";
    public const string Maximum = "Maximum";
    public const string Total = "Total";
    public const string Unavailable = "Unavailable";
}

public static class PerformanceCorrelationOutcome
{
    public const string Unique = "Unique";
    public const string Ambiguous = "Ambiguous";
    public const string Unmapped = "Unmapped";
}

public static class PerformanceComparisonVerdict
{
    public const string Comparable = "Comparable";
    public const string PartiallyComparable = "PartiallyComparable";
    public const string RejectComparison = "RejectComparison";
}

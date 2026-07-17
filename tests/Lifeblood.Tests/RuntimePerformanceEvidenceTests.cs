using Lifeblood.Adapters.Performance;
using Lifeblood.Analysis;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Xunit;

namespace Lifeblood.Tests;

public sealed class RuntimePerformanceEvidenceTests
{
    private readonly RuntimePerformanceEvidenceImporter _importer = new();

    [Fact]
    public void Import_GenericJson_PreservesUnavailableAndBoundsRows()
    {
        const string json = """
        {
          "schema": "lifeblood.performance-capture@1",
          "captureId": "capture-a",
          "identity": {
            "scenarioId": "dense",
            "productName": "DAWG",
            "appVersion": "1.2.3",
            "buildId": "build-1",
            "gitCommit": "abc1234",
            "dirty": false,
            "defineProfiles": ["Editor", "Player"],
            "featureFlags": ["Burst"]
          },
          "device": { "deviceModel": "S23", "platform": "Android", "graphicsApi": "Vulkan" },
          "workload": {
            "fingerprint": "work-1",
            "captureMode": "ProfilerRecorder",
            "audioSampleRate": 48000,
            "audioBufferFrames": 256,
            "targetFrameRate": 60,
            "counters": { "voices": 8 }
          },
          "measurements": [
            { "stage": "play", "category": "CPU", "marker": "Render", "value": 2.5, "unit": "ms" },
            { "stage": "play", "category": "CPU", "marker": "Missing", "unit": "ms" },
            { "stage": "play", "category": "GPU", "marker": "Present", "available": false, "error": "unsupported", "unit": "ms" }
          ]
        }
        """;

        var capture = _importer.Import(new PerformanceEvidenceDocument("capture.json", json, null, 2));

        Assert.Equal("GenericJson", capture.SourceFormat);
        Assert.Equal("dense", capture.Identity.ScenarioId);
        Assert.Equal(3, capture.ImportReceipt.ObservedMeasurementCount);
        Assert.Equal(2, capture.ImportReceipt.EmittedMeasurementCount);
        Assert.Equal(2, capture.ImportReceipt.UnavailableMeasurementCount);
        Assert.True(capture.ImportReceipt.Truncated);
        Assert.Equal(2.5, capture.Measurements[0].Value);
        var missing = capture.Measurements[1];
        Assert.False(missing.Available);
        Assert.Null(missing.Value);
        Assert.Contains("finite numeric value", missing.Error);
    }

    [Fact]
    public void Import_GenericCsv_HandlesQuotedFieldsAndDomainCounters()
    {
        const string csv = "scenarioId,productName,appVersion,buildId,gitCommit,dirty,defineProfiles,featureFlags,deviceModel,deviceClass,platform,graphicsApi,workloadFingerprint,captureMode,audioSampleRate,audioBufferFrames,targetFrameRate,counter.voices,stage,category,marker,statistic,unit,value\n"
            + "dense,DAWG,1.2.3,build-1,abc1234,false,Editor;Player,Burst,S23,phone,Android,Vulkan,work-1,Recorder,48000,256,60,8,play,CPU,\"Render, Main\",Average,ms,3.5\n";

        var capture = _importer.Import(new PerformanceEvidenceDocument("capture.csv", csv, null, 10));

        Assert.Equal("GenericCsv", capture.SourceFormat);
        Assert.Equal("Render, Main", capture.Measurements.Single().Marker);
        Assert.Equal(8d, capture.Workload.Counters["voices"]);
        Assert.Equal(new[] { "Editor", "Player" }, capture.Identity.DefineProfiles);
    }

    [Fact]
    public void Import_UnityProfilerRecorderReceipt_EmitsAggregatesAndExplicitUnavailableRows()
    {
        const string json = """
        {
          "schema": "dawg.mobile-performance-scenario@5",
          "scenario": "baseline",
          "productName": "DAWG",
          "appVersion": "1.2.3",
          "buildGuid": "build-1",
          "deviceModel": "SM-S911B",
          "platform": "Android",
          "graphicsApi": "Vulkan",
          "audioSampleRate": 48000,
          "dspBufferLength": 256,
          "targetFrameRate": 60,
          "workloadHash": "work-1",
          "captureMode": "ProfilerRecorder",
          "stages": [{
            "name": "play",
            "averageFrameMs": 10.0,
            "p95FrameMs": 14.0,
            "maximumFrameMs": 20.0,
            "profilerMetrics": [
              { "category": "CPU", "name": "Audio.Callback", "unit": "ns", "valid": true,
                "sampleCount": 4, "markerInvocationCount": 8, "averageMilliseconds": 0.25,
                "p95Milliseconds": 0.4, "maximumMilliseconds": 0.5, "totalMilliseconds": 1.0 },
              { "category": "GPU", "name": "Gfx.Present", "unit": "ns", "valid": false,
                "error": "counter unavailable" }
            ]
          }]
        }
        """;

        var capture = _importer.Import(new PerformanceEvidenceDocument("scenario-receipt.json", json, null, 100));

        Assert.Equal("UnityProfilerRecorderJson", capture.SourceFormat);
        Assert.Equal("dawg.mobile-performance-scenario@5", capture.SourceSchema);
        Assert.Contains(capture.Measurements, row => row.Marker == "Frame" && row.Statistic == PerformanceStatistic.P95);
        Assert.Contains(capture.Measurements, row => row.Marker == "Audio.Callback" && row.Statistic == PerformanceStatistic.Total && row.Value == 1d);
        var unavailable = Assert.Single(capture.Measurements, row => row.Marker == "Gfx.Present");
        Assert.False(unavailable.Available);
        Assert.Null(unavailable.Value);
        Assert.Equal("counter unavailable", unavailable.Error);
    }

    [Fact]
    public void Correlate_UsesOnePriorityChainAndReturnsUniqueAmbiguousAndUnmapped()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "mod:App", Name = "App", Kind = SymbolKind.Module })
            .AddSymbol(new Symbol { Id = "type:N.Host", Name = "Host", QualifiedName = "N.Host", Kind = SymbolKind.Type, ParentId = "mod:App", FilePath = "Host.cs" })
            .AddSymbol(new Symbol { Id = "method:N.Host.Tick()", Name = "Tick", QualifiedName = "N.Host.Tick", Kind = SymbolKind.Method, ParentId = "type:N.Host", FilePath = "Host.cs", Line = 10 })
            .AddSymbol(new Symbol { Id = "method:N.A.Run()", Name = "Run", QualifiedName = "N.A.Run", Kind = SymbolKind.Method, ParentId = "mod:App", FilePath = "A.cs" })
            .AddSymbol(new Symbol { Id = "method:N.B.Run()", Name = "Run", QualifiedName = "N.B.Run", Kind = SymbolKind.Method, ParentId = "mod:App", FilePath = "B.cs" })
            .AddSymbol(new Symbol { Id = "type:N.HostTests", Name = "HostTests", QualifiedName = "N.HostTests", Kind = SymbolKind.Type, ParentId = "mod:App", FilePath = "HostTests.cs" })
            .AddSymbol(new Symbol
            {
                Id = "method:N.HostTests.Tick_is_fast()",
                Name = "Tick_is_fast",
                QualifiedName = "N.HostTests.Tick_is_fast",
                Kind = SymbolKind.Method,
                ParentId = "type:N.HostTests",
                FilePath = "HostTests.cs",
                Properties = new Dictionary<string, string> { [SymbolPropertyKeys.Attributes] = "Fact" },
            })
            .AddEdge(new Edge { SourceId = "method:N.HostTests.Tick_is_fast()", TargetId = "method:N.Host.Tick()", Kind = EdgeKind.Calls })
            .Build();
        var capture = Capture(
            Measurement("AliasMarker", 5),
            Measurement("HotMarker", 4),
            Measurement("Run", 3),
            Measurement("Unknown", 2));
        var facts = new[]
        {
            Fact("HotMarker", SourceEvidenceKind.StringLiteral, "method:N.Host.Tick()"),
            Fact("INV-PERF-001", SourceEvidenceKind.Comment, "method:N.Host.Tick()"),
        };

        var report = PerformanceEvidenceAnalyzer.Correlate(
            graph,
            capture,
            new[] { new PerformanceMarkerAlias("AliasMarker", "method:N.Host.Tick()") },
            new[] { "INV-PERF-001" },
            facts,
            SourceReceipt(facts.Length),
            maximumMarkers: 10,
            maximumTestsPerSymbol: 2);

        Assert.Equal(4, report.MarkerCount);
        Assert.Equal(2, report.UniqueCount);
        Assert.Equal(1, report.AmbiguousCount);
        Assert.Equal(1, report.UnmappedCount);
        var alias = report.Markers.Single(row => row.Marker == "AliasMarker");
        Assert.Equal("CallerAlias", alias.ResolutionSource);
        var owned = report.Markers.Single(row => row.Marker == "HotMarker");
        Assert.Equal("StringLiteralOwnership", owned.ResolutionSource);
        var symbol = Assert.Single(owned.Candidates);
        Assert.Equal("App", symbol.ModuleName);
        Assert.Equal(new[] { "INV-PERF-001" }, symbol.InvariantIds);
        Assert.Single(symbol.Tests);
        Assert.Equal(2, report.Markers.Single(row => row.Marker == "Run").Candidates.Length);
    }

    [Fact]
    public void Compare_CrossDevice_ProducesNormalizedDeltasOnlyAfterIdentityPasses()
    {
        var baseline = Capture(
            Measurement("Audio.Callback", 1, "Main"),
            Measurement("Audio.Callback", 3, "Main"),
            Measurement("Audio.Callback", 10, "Worker"),
            Measurement("Audio.Callback", 14, "Worker"));
        var candidate = CaptureWithIdentity("dense", "S8", new[]
        {
            Measurement("Audio.Callback", 2, "Main"),
            Measurement("Audio.Callback", 4, "Main"),
            Measurement("Audio.Callback", 12, "Worker"),
            Measurement("Audio.Callback", 16, "Worker"),
        });

        var report = PerformanceEvidenceAnalyzer.Compare(baseline, candidate, "crossDevice", Array.Empty<string>(), 100);

        Assert.Equal(PerformanceComparisonVerdict.Comparable, report.Verdict);
        Assert.Contains(report.IdentityChecks, row => row.Field == "deviceModel" && row.Status == "ExpectedDifference");
        var average = report.Deltas.Single(row =>
            row.Statistic == PerformanceStatistic.Average && row.Thread == "Main");
        Assert.Equal(2d, average.Baseline);
        Assert.Equal(3d, average.Candidate);
        Assert.Equal(50d, average.PercentDelta);
        Assert.Equal("derivedFromSamples", average.Normalization);
        var workerAverage = report.Deltas.Single(row =>
            row.Statistic == PerformanceStatistic.Average && row.Thread == "Worker");
        Assert.Equal(12d, workerAverage.Baseline);
        Assert.Equal(14d, workerAverage.Candidate);
    }

    [Fact]
    public void Compare_MissingProvenanceIsPartial_AndScenarioMismatchRejectsWithoutDeltas()
    {
        var baseline = Capture(Measurement("Frame", 10));
        var missingGit = CaptureWithIdentity("dense", "S8", new[] { Measurement("Frame", 11) }, gitCommit: "");
        var partial = PerformanceEvidenceAnalyzer.Compare(baseline, missingGit, "crossDevice", Array.Empty<string>(), 100);
        Assert.Equal(PerformanceComparisonVerdict.PartiallyComparable, partial.Verdict);
        Assert.NotEmpty(partial.Deltas);

        var unlike = CaptureWithIdentity("other-scenario", "S8", new[] { Measurement("Frame", 11) });
        var rejected = PerformanceEvidenceAnalyzer.Compare(baseline, unlike, "crossDevice", Array.Empty<string>(), 100);
        Assert.Equal(PerformanceComparisonVerdict.RejectComparison, rejected.Verdict);
        Assert.Empty(rejected.Deltas);
        Assert.Contains(rejected.IdentityChecks, row => row.Field == "scenarioId" && row.Status == "Reject");

        var missingMetric = CaptureWithIdentity("dense", "S8", Array.Empty<PerformanceMeasurement>());
        var partialMetrics = PerformanceEvidenceAnalyzer.Compare(baseline, missingMetric, "crossDevice", Array.Empty<string>(), 100);
        Assert.Equal(PerformanceComparisonVerdict.PartiallyComparable, partialMetrics.Verdict);
        Assert.Contains(partialMetrics.IdentityChecks, row => row.Field == "measurementKeys" && row.Status == "Partial");
        Assert.Empty(partialMetrics.Deltas);

        var conflicting = Capture(Aggregate("Frame", 10), Aggregate("Frame", 12));
        var conflictCandidate = CaptureWithIdentity("dense", "S8", new[] { Aggregate("Frame", 11) });
        var conflictReport = PerformanceEvidenceAnalyzer.Compare(conflicting, conflictCandidate, "crossDevice", Array.Empty<string>(), 100);
        Assert.Equal(PerformanceComparisonVerdict.PartiallyComparable, conflictReport.Verdict);
        Assert.Contains(conflictReport.IdentityChecks, row => row.Field == "measurementConflicts" && row.Status == "Partial");
        Assert.Empty(conflictReport.Deltas);
    }

    private static PerformanceCapture Capture(params PerformanceMeasurement[] measurements)
        => CaptureWithIdentity("dense", "S23", measurements);

    private static PerformanceCapture CaptureWithIdentity(
        string scenario,
        string device,
        PerformanceMeasurement[] measurements,
        string gitCommit = "abc1234")
        => new()
        {
            CaptureId = "capture-" + device,
            SourceFormat = "GenericJson",
            SourceSchema = "lifeblood.performance-capture@1",
            Identity = new PerformanceCaptureIdentity
            {
                ScenarioId = scenario,
                ProductName = "DAWG",
                AppVersion = "1.2.3",
                BuildId = "build-1",
                GitCommit = gitCommit,
                Dirty = false,
                DefineProfiles = new[] { "Editor", "Player" },
                FeatureFlags = new[] { "Burst" },
            },
            Device = new PerformanceDeviceIdentity
            {
                DeviceModel = device,
                DeviceClass = "phone",
                Platform = "Android",
                OperatingSystem = "Android 15",
                Processor = device + " CPU",
                GraphicsDevice = device + " GPU",
                GraphicsApi = "Vulkan",
                Width = 1920,
                Height = 1080,
                RefreshRateHz = 60,
            },
            Workload = new PerformanceWorkloadIdentity
            {
                Fingerprint = "work-1",
                CaptureMode = "ProfilerRecorder",
                AudioSampleRate = 48000,
                AudioBufferFrames = 256,
                TargetFrameRate = 60,
                Counters = new Dictionary<string, double> { ["voices"] = 8 },
            },
            Measurements = measurements,
            ImportReceipt = new PerformanceImportReceipt
            {
                SourceName = "fixture",
                InputCharacterCount = 0,
                ObservedMeasurementCount = measurements.Length,
                EmittedMeasurementCount = measurements.Length,
                UnavailableMeasurementCount = 0,
                Truncated = false,
            },
        };

    private static PerformanceMeasurement Measurement(string marker, double value, string thread = "") => new()
    {
        Marker = marker,
        Thread = thread,
        Statistic = PerformanceStatistic.Sample,
        Unit = "ms",
        Value = value,
    };

    private static PerformanceMeasurement Aggregate(string marker, double value) => new()
    {
        Marker = marker,
        Statistic = PerformanceStatistic.Average,
        Unit = "ms",
        Value = value,
    };

    private static SourceEvidenceFact Fact(string term, string kind, string symbolId) => new()
    {
        Id = term + "@1",
        Kind = kind,
        ModuleName = "App",
        ProfileScope = "Editor",
        MatchedTerm = term,
        Text = term,
        ContainingSymbolId = symbolId,
        Source = new OperationSourceSpan { FilePath = "Host.cs", Line = 10, Column = 1, EndLine = 10, EndColumn = 10 },
    };

    private static SourceEvidenceScanReceipt SourceReceipt(int factCount) => new()
    {
        Status = SourceEvidenceScanStatus.Completed,
        ProfileScope = "Editor",
        ExecutionMode = SourceEvidenceExecutionMode.RetainedSyntaxTrees,
        InputIdentityVerifiedAtStart = true,
        AdditionalSemanticBaseCount = 0,
        ScannedModuleCount = 1,
        ScannedFileCount = 1,
        ObservedEvidenceCount = factCount,
        EmittedFactCount = factCount,
        Truncated = false,
        StoppedByConsumer = false,
    };
}

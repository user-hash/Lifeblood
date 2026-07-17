using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;

namespace Lifeblood.Adapters.Performance;

/// <summary>
/// Stateless adapter for the Lifeblood generic JSON/CSV capture contract and
/// the structural Unity ProfilerRecorder receipt shape used by DAWG. It does
/// not parse proprietary Unity .data files and never retains imported rows.
/// </summary>
public sealed class RuntimePerformanceEvidenceImporter : IPerformanceEvidenceImporter
{
    public PerformanceCapture Import(PerformanceEvidenceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var csv = IsCsv(document);
        return csv ? ImportCsv(document) : ImportJson(document);
    }

    private static bool IsCsv(PerformanceEvidenceDocument document)
        => string.Equals(document.FormatHint, "csv", StringComparison.OrdinalIgnoreCase)
           || document.SourceName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

    private static PerformanceCapture ImportJson(PerformanceEvidenceDocument document)
    {
        using var json = JsonDocument.Parse(document.Content, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        if (json.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Runtime-evidence JSON root must be an object.");

        var root = json.RootElement;
        var unity = IsUnityReceipt(root);
        var observed = 0;
        var unavailable = 0;
        var truncated = false;
        var measurements = new List<PerformanceMeasurement>(Math.Min(document.MaximumMeasurements, 1024));
        void Emit(PerformanceMeasurement measurement)
        {
            observed++;
            if (!measurement.Available) unavailable++;
            if (measurements.Count < document.MaximumMeasurements)
                measurements.Add(measurement);
            else
                truncated = true;
        }

        if (unity)
            ReadUnityMeasurements(root, Emit);
        else
            ReadGenericMeasurements(root, Emit);

        var identity = unity ? ReadUnityIdentity(root) : ReadGenericIdentity(root);
        var device = unity ? ReadUnityDevice(root) : ReadGenericDevice(root);
        var workload = unity ? ReadUnityWorkload(root) : ReadGenericWorkload(root);
        var schema = String(root, "schema") ?? String(root, "schemaVersion")
            ?? (unity ? "unity.profiler-recorder-receipt" : "lifeblood.performance-capture@1");
        return new PerformanceCapture
        {
            CaptureId = String(root, "captureId") ?? StableCaptureId(document.SourceName, document.Content),
            SourceFormat = unity ? "UnityProfilerRecorderJson" : "GenericJson",
            SourceSchema = schema,
            Identity = identity,
            Device = device,
            Workload = workload,
            Measurements = measurements.ToArray(),
            ImportReceipt = new PerformanceImportReceipt
            {
                SourceName = document.SourceName,
                InputCharacterCount = document.Content.Length,
                ObservedMeasurementCount = observed,
                EmittedMeasurementCount = measurements.Count,
                UnavailableMeasurementCount = unavailable,
                Truncated = truncated,
                Limitations = unity
                    ? new[] { "Structural Unity ProfilerRecorder receipt import; proprietary Unity .data and .raw capture formats are not parsed." }
                    : Array.Empty<string>(),
            },
        };
    }

    private static bool IsUnityReceipt(JsonElement root)
    {
        if (!root.TryGetProperty("stages", out var stages) || stages.ValueKind != JsonValueKind.Array)
            return false;
        return stages.EnumerateArray().Any(stage =>
            stage.ValueKind == JsonValueKind.Object
            && stage.TryGetProperty("profilerMetrics", out var metrics)
            && metrics.ValueKind == JsonValueKind.Array);
    }

    private static PerformanceCaptureIdentity ReadGenericIdentity(JsonElement root)
    {
        var node = Object(root, "identity") ?? root;
        return new PerformanceCaptureIdentity
        {
            ScenarioId = String(node, "scenarioId") ?? String(root, "scenario") ?? "",
            ProductName = String(node, "productName") ?? String(root, "productName") ?? "",
            AppVersion = String(node, "appVersion") ?? String(root, "appVersion") ?? "",
            BuildId = String(node, "buildId") ?? String(root, "buildId") ?? "",
            GitCommit = String(node, "gitCommit") ?? String(root, "gitCommit") ?? "",
            Dirty = Bool(node, "dirty") ?? Bool(root, "dirty"),
            DefineProfiles = StringArray(node, "defineProfiles"),
            FeatureFlags = StringArray(node, "featureFlags"),
            CapturedAtUtc = Date(node, "capturedAtUtc") ?? Date(root, "capturedAtUtc"),
        };
    }

    private static PerformanceCaptureIdentity ReadUnityIdentity(JsonElement root) => new()
    {
        ScenarioId = String(root, "scenario") ?? String(root, "scenarioId") ?? "",
        ProductName = String(root, "productName") ?? "",
        AppVersion = String(root, "appVersion") ?? "",
        BuildId = String(root, "buildGuid") ?? String(root, "buildId") ?? "",
        GitCommit = String(root, "gitCommit") ?? "",
        Dirty = Bool(root, "dirty"),
        DefineProfiles = StringArray(root, "defineProfiles"),
        FeatureFlags = StringArray(root, "featureFlags"),
        CapturedAtUtc = Date(root, "capturedAtUtc") ?? Date(root, "capturedAt"),
    };

    private static PerformanceDeviceIdentity ReadGenericDevice(JsonElement root)
    {
        var node = Object(root, "device") ?? root;
        return new PerformanceDeviceIdentity
        {
            DeviceModel = String(node, "deviceModel") ?? String(node, "model") ?? "",
            DeviceClass = String(node, "deviceClass") ?? "",
            Platform = String(node, "platform") ?? "",
            OperatingSystem = String(node, "operatingSystem") ?? "",
            Processor = String(node, "processor") ?? "",
            GraphicsDevice = String(node, "graphicsDevice") ?? "",
            GraphicsApi = String(node, "graphicsApi") ?? "",
            Width = Int(node, "width"),
            Height = Int(node, "height"),
            RefreshRateHz = Number(node, "refreshRateHz"),
        };
    }

    private static PerformanceDeviceIdentity ReadUnityDevice(JsonElement root) => new()
    {
        DeviceModel = String(root, "deviceModel") ?? "",
        DeviceClass = String(root, "deviceClass") ?? String(root, "deviceType") ?? "",
        Platform = String(root, "platform") ?? "",
        OperatingSystem = String(root, "operatingSystem") ?? "",
        Processor = String(root, "processorType") ?? String(root, "processor") ?? "",
        GraphicsDevice = String(root, "graphicsDeviceName") ?? String(root, "graphicsDevice") ?? "",
        GraphicsApi = String(root, "graphicsApi") ?? String(root, "graphicsDeviceType") ?? "",
        Width = Int(root, "width"),
        Height = Int(root, "height"),
        RefreshRateHz = Number(root, "refreshRateHz") ?? Number(root, "refreshRate"),
    };

    private static PerformanceWorkloadIdentity ReadGenericWorkload(JsonElement root)
    {
        var node = Object(root, "workload") ?? root;
        return new PerformanceWorkloadIdentity
        {
            Fingerprint = String(node, "fingerprint") ?? String(node, "workloadFingerprint") ?? "",
            CaptureMode = String(node, "captureMode") ?? "",
            AudioSampleRate = Int(node, "audioSampleRate"),
            AudioBufferFrames = Int(node, "audioBufferFrames") ?? Int(node, "dspBufferLength"),
            TargetFrameRate = Int(node, "targetFrameRate"),
            Counters = NumberMap(node, "counters"),
        };
    }

    private static PerformanceWorkloadIdentity ReadUnityWorkload(JsonElement root)
    {
        var counters = new Dictionary<string, double>(NumberMap(root, "workloadCounters"), StringComparer.Ordinal);
        foreach (var name in new[]
                 {
                     "parallelTabs",
                     "patternCommands",
                     "stockPresetCommands",
                     "stockPresetIndex",
                     "presetCommands",
                     "stepCount",
                     "bpm",
                     "requiredCallbacks",
                     "loopLengthTicks",
                     "melodicNoteCount",
                     "melodicNoteCountPerTab",
                     "baselineCallbackCount",
                     "baselineSynthVoiceCount",
                     "activeNotes",
                     "synthVoices",
                     "workerCount",
                     "shaderTier",
                 })
        {
            if (Number(root, name) is { } value) counters[name] = value;
        }
        return new PerformanceWorkloadIdentity
        {
            Fingerprint = String(root, "workloadHash")
                ?? String(root, "audioWorkloadHash")
                ?? String(root, "workloadFingerprint")
                ?? String(root, "midiEventHash")
                ?? "",
            CaptureMode = String(root, "captureMode") ?? "",
            AudioSampleRate = Int(root, "audioSampleRate"),
            AudioBufferFrames = Int(root, "audioDspBufferFrames")
                ?? Int(root, "dspBufferLength")
                ?? Int(root, "audioBufferFrames"),
            TargetFrameRate = Int(root, "targetFrameRate"),
            Counters = counters,
        };
    }

    private static void ReadGenericMeasurements(JsonElement root, Action<PerformanceMeasurement> emit)
    {
        if (!root.TryGetProperty("measurements", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Generic runtime-evidence JSON requires a measurements array.");
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            var marker = String(row, "marker") ?? String(row, "name") ?? "";
            if (marker.Length == 0) continue;
            var declaredAvailable = Bool(row, "available") ?? true;
            var value = Number(row, "value");
            var available = declaredAvailable && value.HasValue;
            emit(new PerformanceMeasurement
            {
                Stage = String(row, "stage") ?? "",
                Category = String(row, "category") ?? "",
                Marker = marker,
                Thread = String(row, "thread") ?? "",
                Statistic = String(row, "statistic") ?? PerformanceStatistic.Sample,
                Unit = String(row, "unit") ?? "",
                Value = available ? value : null,
                SampleCount = Long(row, "sampleCount"),
                InvocationCount = Long(row, "invocationCount"),
                FrameIndex = Long(row, "frameIndex"),
                Available = available,
                Error = String(row, "error")
                    ?? (declaredAvailable && !value.HasValue
                        ? "Measurement did not contain a finite numeric value."
                        : ""),
                SymbolHint = String(row, "symbolHint") ?? "",
            });
        }
    }

    private static void ReadUnityMeasurements(JsonElement root, Action<PerformanceMeasurement> emit)
    {
        foreach (var stage in root.GetProperty("stages").EnumerateArray())
        {
            if (stage.ValueKind != JsonValueKind.Object) continue;
            var stageName = String(stage, "name") ?? "";
            EmitAggregate(stage, stageName, "FrameTime", "Frame", "averageFrameMs", PerformanceStatistic.Average, "milliseconds", emit);
            EmitAggregate(stage, stageName, "FrameTime", "Frame", "p95FrameMs", PerformanceStatistic.P95, "milliseconds", emit);
            EmitAggregate(stage, stageName, "FrameTime", "Frame", "maximumFrameMs", PerformanceStatistic.Maximum, "milliseconds", emit);

            if (!stage.TryGetProperty("profilerMetrics", out var metrics) || metrics.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var metric in metrics.EnumerateArray())
            {
                if (metric.ValueKind != JsonValueKind.Object) continue;
                var marker = String(metric, "name") ?? "";
                if (marker.Length == 0) continue;
                var category = String(metric, "category") ?? "";
                var unit = String(metric, "unit") ?? "";
                var thread = String(metric, "thread") ?? "";
                var valid = Bool(metric, "valid") ?? true;
                if (!valid)
                {
                    emit(new PerformanceMeasurement
                    {
                        Stage = stageName,
                        Category = category,
                        Marker = marker,
                        Thread = thread,
                        Statistic = PerformanceStatistic.Unavailable,
                        Unit = unit,
                        Available = false,
                        Error = String(metric, "error") ?? "ProfilerRecorder metric was unavailable.",
                        SampleCount = Long(metric, "sampleCount"),
                        InvocationCount = Long(metric, "markerInvocationCount"),
                    });
                    continue;
                }

                var emitted = IsTimeUnit(unit)
                    ? EmitMetricAggregates(
                        metric,
                        stageName,
                        category,
                        marker,
                        thread,
                        milliseconds: true,
                        unit,
                        emit)
                    : EmitMetricAggregates(
                        metric,
                        stageName,
                        category,
                        marker,
                        thread,
                        milliseconds: false,
                        unit,
                        emit);
                if (!emitted)
                {
                    emit(new PerformanceMeasurement
                    {
                        Stage = stageName,
                        Category = category,
                        Marker = marker,
                        Thread = thread,
                        Statistic = PerformanceStatistic.Unavailable,
                        Unit = unit,
                        Available = false,
                        Error = "ProfilerRecorder metric was valid but contained no supported aggregate value.",
                        SampleCount = Long(metric, "sampleCount"),
                        InvocationCount = Long(metric, "markerInvocationCount"),
                    });
                }
            }
        }
    }

    private static void EmitAggregate(
        JsonElement source,
        string stage,
        string category,
        string marker,
        string property,
        string statistic,
        string unit,
        Action<PerformanceMeasurement> emit)
    {
        if (Number(source, property) is not { } value) return;
        emit(new PerformanceMeasurement
        {
            Stage = stage,
            Category = category,
            Marker = marker,
            Statistic = statistic,
            Unit = unit,
            Value = value,
        });
    }

    private static bool EmitMetricAggregate(
        JsonElement source,
        string stage,
        string category,
        string marker,
        string thread,
        string property,
        string statistic,
        string unit,
        Action<PerformanceMeasurement> emit)
    {
        if (Number(source, property) is not { } value) return false;
        emit(new PerformanceMeasurement
        {
            Stage = stage,
            Category = category,
            Marker = marker,
            Thread = thread,
            Statistic = statistic,
            Unit = unit,
            Value = value,
            SampleCount = Long(source, "sampleCount"),
            InvocationCount = Long(source, "markerInvocationCount"),
        });
        return true;
    }

    private static bool EmitMetricAggregates(
        JsonElement source,
        string stage,
        string category,
        string marker,
        string thread,
        bool milliseconds,
        string unit,
        Action<PerformanceMeasurement> emit)
    {
        var emitted = false;
        if (milliseconds)
        {
            emitted |= EmitMetricAggregate(source, stage, category, marker, thread, "averageMilliseconds", PerformanceStatistic.Average, "milliseconds", emit);
            emitted |= EmitMetricAggregate(source, stage, category, marker, thread, "p95Milliseconds", PerformanceStatistic.P95, "milliseconds", emit);
            emitted |= EmitMetricAggregate(source, stage, category, marker, thread, "maximumMilliseconds", PerformanceStatistic.Maximum, "milliseconds", emit);
            emitted |= EmitMetricAggregate(source, stage, category, marker, thread, "totalMilliseconds", PerformanceStatistic.Total, "milliseconds", emit);
        }
        else
        {
            emitted |= EmitMetricAggregate(source, stage, category, marker, thread, "averageValue", PerformanceStatistic.Average, unit, emit);
            emitted |= EmitMetricAggregate(source, stage, category, marker, thread, "p95Value", PerformanceStatistic.P95, unit, emit);
            emitted |= EmitMetricAggregate(source, stage, category, marker, thread, "maximumValue", PerformanceStatistic.Maximum, unit, emit);
            emitted |= EmitMetricAggregate(source, stage, category, marker, thread, "valueSum", PerformanceStatistic.Total, unit, emit);
        }
        return emitted;
    }

    private static bool IsTimeUnit(string unit)
        => unit.Trim().ToLowerInvariant() is
            "ns" or "nanosecond" or "nanoseconds" or "timenanoseconds"
            or "ms" or "millisecond" or "milliseconds" or "timemilliseconds";

    private static PerformanceCapture ImportCsv(PerformanceEvidenceDocument document)
    {
        var rows = ParseCsv(document.Content);
        if (rows.Count < 2)
            throw new ArgumentException("Runtime-evidence CSV requires a header and at least one data row.");
        var headers = rows[0];
        var observed = 0;
        var unavailable = 0;
        var truncated = false;
        var measurements = new List<PerformanceMeasurement>();
        Dictionary<string, string>? first = null;
        foreach (var values in rows.Skip(1))
        {
            if (values.All(string.IsNullOrWhiteSpace)) continue;
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Length; i++)
                row[headers[i].Trim()] = i < values.Length ? values[i].Trim() : "";
            first ??= row;
            var marker = Csv(row, "marker", "name");
            if (marker.Length == 0) continue;
            observed++;
            var declaredAvailable = CsvBool(row, "available") ?? true;
            var value = CsvDouble(row, "value");
            var available = declaredAvailable && value.HasValue;
            if (!available) unavailable++;
            if (measurements.Count >= document.MaximumMeasurements)
            {
                truncated = true;
                continue;
            }
            measurements.Add(new PerformanceMeasurement
            {
                Stage = Csv(row, "stage"),
                Category = Csv(row, "category"),
                Marker = marker,
                Thread = Csv(row, "thread"),
                Statistic = Csv(row, "statistic") is { Length: > 0 } stat ? stat : PerformanceStatistic.Sample,
                Unit = Csv(row, "unit"),
                Value = available ? value : null,
                SampleCount = CsvLong(row, "sampleCount"),
                InvocationCount = CsvLong(row, "invocationCount"),
                FrameIndex = CsvLong(row, "frameIndex"),
                Available = available,
                Error = Csv(row, "error") is { Length: > 0 } error
                    ? error
                    : declaredAvailable && !value.HasValue
                        ? "Measurement did not contain a finite numeric value."
                        : "",
                SymbolHint = Csv(row, "symbolHint"),
            });
        }

        first ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var identity = new PerformanceCaptureIdentity
        {
            ScenarioId = Csv(first, "scenarioId", "scenario"),
            ProductName = Csv(first, "productName"),
            AppVersion = Csv(first, "appVersion"),
            BuildId = Csv(first, "buildId", "buildGuid"),
            GitCommit = Csv(first, "gitCommit"),
            Dirty = CsvBool(first, "dirty"),
            DefineProfiles = SplitList(Csv(first, "defineProfiles")),
            FeatureFlags = SplitList(Csv(first, "featureFlags")),
            CapturedAtUtc = DateTimeOffset.TryParse(Csv(first, "capturedAtUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var captured)
                ? captured
                : null,
        };
        return new PerformanceCapture
        {
            CaptureId = Csv(first, "captureId") is { Length: > 0 } id ? id : StableCaptureId(document.SourceName, document.Content),
            SourceFormat = "GenericCsv",
            SourceSchema = Csv(first, "schema") is { Length: > 0 } schema ? schema : "lifeblood.performance-capture@1",
            Identity = identity,
            Device = new PerformanceDeviceIdentity
            {
                DeviceModel = Csv(first, "deviceModel"),
                DeviceClass = Csv(first, "deviceClass"),
                Platform = Csv(first, "platform"),
                OperatingSystem = Csv(first, "operatingSystem"),
                Processor = Csv(first, "processor"),
                GraphicsDevice = Csv(first, "graphicsDevice"),
                GraphicsApi = Csv(first, "graphicsApi"),
                Width = CsvInt(first, "width"),
                Height = CsvInt(first, "height"),
                RefreshRateHz = CsvDouble(first, "refreshRateHz"),
            },
            Workload = new PerformanceWorkloadIdentity
            {
                Fingerprint = Csv(first, "workloadFingerprint", "fingerprint"),
                CaptureMode = Csv(first, "captureMode"),
                AudioSampleRate = CsvInt(first, "audioSampleRate"),
                AudioBufferFrames = CsvInt(first, "audioBufferFrames", "dspBufferLength"),
                TargetFrameRate = CsvInt(first, "targetFrameRate"),
                Counters = ReadCsvCounters(first),
            },
            Measurements = measurements.ToArray(),
            ImportReceipt = new PerformanceImportReceipt
            {
                SourceName = document.SourceName,
                InputCharacterCount = document.Content.Length,
                ObservedMeasurementCount = observed,
                EmittedMeasurementCount = measurements.Count,
                UnavailableMeasurementCount = unavailable,
                Truncated = truncated,
            },
        };
    }

    private static IReadOnlyDictionary<string, double> ReadCsvCounters(IReadOnlyDictionary<string, string> row)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var pair in row)
        {
            const string prefix = "counter.";
            if (!pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (double.TryParse(pair.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && double.IsFinite(value))
                result[pair.Key[prefix.Length..]] = value;
        }
        return result;
    }

    private static List<string[]> ParseCsv(string content)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < content.Length; i++)
        {
            var ch = content[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < content.Length && content[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (ch == '"') quoted = false;
                else field.Append(ch);
                continue;
            }
            if (ch == '"') quoted = true;
            else if (ch == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < content.Length && content[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row.ToArray());
                row.Clear();
            }
            else field.Append(ch);
        }
        if (quoted) throw new ArgumentException("Runtime-evidence CSV contains an unterminated quoted field.");
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row.ToArray());
        }
        return rows;
    }

    private static string StableCaptureId(string source, string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source + "\n" + content));
        return "perf_" + Convert.ToHexString(bytes).ToLowerInvariant()[..24];
    }

    private static JsonElement? Object(JsonElement source, string name)
        => source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static string? String(JsonElement source, string name)
    {
        if (!source.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null,
        };
    }

    private static double? Number(JsonElement source, string name)
    {
        if (!source.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)) return number;
        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            && double.IsFinite(number)) return number;
        return null;
    }

    private static int? Int(JsonElement source, string name)
        => Number(source, name) is { } value && value is >= int.MinValue and <= int.MaxValue ? (int)value : null;

    private static long? Long(JsonElement source, string name)
        => Number(source, name) is { } value && value is >= long.MinValue and <= long.MaxValue ? (long)value : null;

    private static bool? Bool(JsonElement source, string name)
    {
        if (!source.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        return value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    private static DateTimeOffset? Date(JsonElement source, string name)
        => DateTimeOffset.TryParse(String(source, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;

    private static string[] StringArray(JsonElement source, string name)
    {
        if (!source.TryGetProperty(name, out var value)) return Array.Empty<string>();
        if (value.ValueKind == JsonValueKind.String) return SplitList(value.GetString() ?? "");
        if (value.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, double> NumberMap(JsonElement source, string name)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (!source.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object) return result;
        foreach (var pair in value.EnumerateObject())
        {
            if (pair.Value.ValueKind == JsonValueKind.Number
                && pair.Value.TryGetDouble(out var number)
                && double.IsFinite(number))
                result[pair.Name] = number;
        }
        return result;
    }

    private static string Csv(IReadOnlyDictionary<string, string> row, params string[] names)
    {
        foreach (var name in names)
            if (row.TryGetValue(name, out var value) && value.Length > 0) return value;
        return "";
    }

    private static double? CsvDouble(IReadOnlyDictionary<string, string> row, params string[] names)
        => double.TryParse(Csv(row, names), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : null;

    private static int? CsvInt(IReadOnlyDictionary<string, string> row, params string[] names)
        => int.TryParse(Csv(row, names), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static long? CsvLong(IReadOnlyDictionary<string, string> row, params string[] names)
        => long.TryParse(Csv(row, names), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static bool? CsvBool(IReadOnlyDictionary<string, string> row, params string[] names)
        => bool.TryParse(Csv(row, names), out var value) ? value : null;

    private static string[] SplitList(string value)
        => value.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
}

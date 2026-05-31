using System.Diagnostics;
using System.Diagnostics.Metrics;
using Lifeblood.Application.Ports.Infrastructure;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// ITelemetrySink adapter backed by .NET ActivitySource and Meter.
/// </summary>
public sealed class DotNetDiagnosticsTelemetrySink : ITelemetrySink, IDisposable
{
    public const string ActivitySourceName = "Lifeblood";
    public const string MeterName = "Lifeblood";

    private readonly ActivitySource _activitySource = new(ActivitySourceName);
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _operationStarted;
    private readonly Counter<long> _operationCompleted;
    private readonly Counter<long> _operationFailed;
    private readonly Counter<long> _eventCounter;
    private readonly Histogram<double> _operationDurationMs;
    private readonly ObservableGauge<long> _processWorkingSetBytes;
    private readonly ObservableGauge<long> _processPrivateBytes;
    private readonly ObservableGauge<long> _managedHeapBytes;
    private readonly ObservableGauge<long> _processThreadCount;

    public DotNetDiagnosticsTelemetrySink()
    {
        _operationStarted = _meter.CreateCounter<long>(
            "lifeblood.operation.started",
            unit: "{operation}",
            description: "Number of Lifeblood operations started.");
        _operationCompleted = _meter.CreateCounter<long>(
            "lifeblood.operation.completed",
            unit: "{operation}",
            description: "Number of Lifeblood operations completed without thrown exceptions.");
        _operationFailed = _meter.CreateCounter<long>(
            "lifeblood.operation.failed",
            unit: "{operation}",
            description: "Number of Lifeblood operations that threw exceptions.");
        _eventCounter = _meter.CreateCounter<long>(
            "lifeblood.event",
            unit: "{event}",
            description: "Number of Lifeblood telemetry events.");
        _operationDurationMs = _meter.CreateHistogram<double>(
            "lifeblood.operation.duration",
            unit: "ms",
            description: "Lifeblood operation duration in milliseconds.");
        _processWorkingSetBytes = _meter.CreateObservableGauge<long>(
            "lifeblood.process.working_set",
            observeValue: static () => Environment.WorkingSet,
            unit: "By",
            description: "Current Lifeblood process working set.");
        _processPrivateBytes = _meter.CreateObservableGauge<long>(
            "lifeblood.process.private_bytes",
            observeValue: static () => ReadCurrentProcessPrivateBytes(),
            unit: "By",
            description: "Current Lifeblood process private bytes.");
        _managedHeapBytes = _meter.CreateObservableGauge<long>(
            "lifeblood.gc.managed_heap",
            observeValue: static () => GC.GetTotalMemory(forceFullCollection: false),
            unit: "By",
            description: "Current managed heap bytes visible to the Lifeblood process.");
        _processThreadCount = _meter.CreateObservableGauge<long>(
            "lifeblood.process.thread_count",
            observeValue: static () => ReadCurrentProcessThreadCount(),
            unit: "{thread}",
            description: "Current Lifeblood process thread count.");
    }

    public static ITelemetrySink CreateFromEnvironment(string environmentVariableName)
    {
        var raw = Environment.GetEnvironmentVariable(environmentVariableName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return NoOpTelemetrySink.Instance;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" or "diagnostics" => new DotNetDiagnosticsTelemetrySink(),
            _ => NoOpTelemetrySink.Instance,
        };
    }

    public ITelemetryOperation StartOperation(string name, params TelemetryTag[] tags)
    {
        var operationTags = BuildTags(name, tags);
        var activity = _activitySource.StartActivity(name, ActivityKind.Internal);
        activity?.SetTag("operation.name", name);
        foreach (var tag in tags)
        {
            activity?.SetTag(tag.Name, tag.Value);
        }

        _operationStarted.Add(1, operationTags);
        return new Operation(
            name,
            tags,
            activity,
            _operationCompleted,
            _operationFailed,
            _operationDurationMs);
    }

    public void RecordEvent(string name, params TelemetryTag[] tags)
    {
        var eventTags = BuildLowCardinalityTags(name, tags);
        Activity.Current?.AddEvent(new ActivityEvent(
            name,
            tags: BuildActivityTags(tags)));
        _eventCounter.Add(1, eventTags);
    }

    public void Dispose()
    {
        _activitySource.Dispose();
        _meter.Dispose();
    }

    private static TagList BuildTags(string name, TelemetryTag[] tags)
    {
        var tagList = new TagList
        {
            { "operation.name", name },
        };

        foreach (var tag in tags)
        {
            tagList.Add(tag.Name, tag.Value);
        }

        return tagList;
    }

    private static TagList BuildLowCardinalityTags(string name, TelemetryTag[] tags)
    {
        var tagList = new TagList
        {
            { "operation.name", name },
        };

        foreach (var tag in tags)
        {
            if (tag.Value is string or bool)
            {
                tagList.Add(tag.Name, tag.Value);
            }
        }

        return tagList;
    }

    private static ActivityTagsCollection BuildActivityTags(TelemetryTag[] tags)
    {
        var activityTags = new ActivityTagsCollection();
        foreach (var tag in tags)
        {
            activityTags.Add(tag.Name, tag.Value);
        }
        return activityTags;
    }

    private static long ReadCurrentProcessPrivateBytes()
    {
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    private static long ReadCurrentProcessThreadCount()
    {
        using var process = Process.GetCurrentProcess();
        return process.Threads.Count;
    }

    private sealed class Operation : ITelemetryOperation
    {
        private readonly string _name;
        private readonly TelemetryTag[] _startTags;
        private readonly Activity? _activity;
        private readonly Counter<long> _operationCompleted;
        private readonly Counter<long> _operationFailed;
        private readonly Histogram<double> _operationDurationMs;
        private readonly Stopwatch _stopwatch;
        private readonly List<TelemetryTag> _dynamicTags = new();
        private bool _failed;
        private bool _disposed;

        public Operation(
            string name,
            TelemetryTag[] startTags,
            Activity? activity,
            Counter<long> operationCompleted,
            Counter<long> operationFailed,
            Histogram<double> operationDurationMs)
        {
            _name = name;
            _startTags = startTags;
            _activity = activity;
            _operationCompleted = operationCompleted;
            _operationFailed = operationFailed;
            _operationDurationMs = operationDurationMs;
            _stopwatch = Stopwatch.StartNew();
        }

        public void SetTag(string name, object? value)
        {
            _activity?.SetTag(name, value);
            _dynamicTags.Add(new TelemetryTag(name, value));
        }

        public void SetError(Exception exception)
        {
            _failed = true;
            _activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            _activity?.SetTag("exception.type", exception.GetType().FullName);
            _activity?.SetTag("exception.message", exception.Message);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopwatch.Stop();

            var tags = BuildTags(_name, _startTags);
            foreach (var tag in _dynamicTags)
            {
                tags.Add(tag.Name, tag.Value);
            }
            tags.Add("operation.status", _failed ? "error" : "success");
            _operationDurationMs.Record(_stopwatch.Elapsed.TotalMilliseconds, tags);
            if (_failed)
            {
                _operationFailed.Add(1, tags);
            }
            else
            {
                _operationCompleted.Add(1, tags);
            }

            _activity?.Dispose();
        }
    }
}

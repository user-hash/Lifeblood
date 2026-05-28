namespace Lifeblood.Application.Ports.Infrastructure;

/// <summary>
/// Operational telemetry port. The Application layer owns only the neutral
/// operation/tag shape; concrete adapters decide whether that becomes
/// ActivitySource/Meter data, logs, tests, or nothing.
/// </summary>
public interface ITelemetrySink
{
    ITelemetryOperation StartOperation(string name, params TelemetryTag[] tags);

    void RecordEvent(string name, params TelemetryTag[] tags);
}

public interface ITelemetryOperation : IDisposable
{
    void SetTag(string name, object? value);

    void SetError(Exception exception);
}

public readonly record struct TelemetryTag(string Name, object? Value);

public sealed class NoOpTelemetrySink : ITelemetrySink
{
    public static readonly NoOpTelemetrySink Instance = new();

    private NoOpTelemetrySink()
    {
    }

    public ITelemetryOperation StartOperation(string name, params TelemetryTag[] tags)
        => NoOpTelemetryOperation.Instance;

    public void RecordEvent(string name, params TelemetryTag[] tags)
    {
    }

    private sealed class NoOpTelemetryOperation : ITelemetryOperation
    {
        public static readonly NoOpTelemetryOperation Instance = new();

        private NoOpTelemetryOperation()
        {
        }

        public void SetTag(string name, object? value)
        {
        }

        public void SetError(Exception exception)
        {
        }

        public void Dispose()
        {
        }
    }
}

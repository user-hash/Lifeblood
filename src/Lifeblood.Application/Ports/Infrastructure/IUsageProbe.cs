using Lifeblood.Domain.Results;

namespace Lifeblood.Application.Ports.Infrastructure;

/// <summary>
/// Captures runtime usage (wall time, CPU time, peak memory, GC pressure,
/// per-phase timings) for one analyze run. The port exists so the use case
/// can stay free of System.Diagnostics types. Concrete implementations live
/// in adapter assemblies.
///
/// Usage:
///   using var capture = probe.Start();
///   // ... do work ...
///   capture.MarkPhase("analyze");
///   // ... do more work ...
///   capture.MarkPhase("validate");
///   var usage = capture.Stop();
///
/// INV-USAGE-PORT-001: The port returns a fresh capture on every call to
/// <see cref="Start"/>. Captures are not thread-safe for concurrent phase
/// marking, but the peak-memory sampling runs on a background timer and is
/// safe alongside the owning thread's phase marks.
/// INV-USAGE-PORT-002: <see cref="IUsageCapture.Stop"/> is idempotent. A
/// second call returns the same snapshot. <see cref="IDisposable.Dispose"/>
/// on an already-stopped capture is a no-op.
/// </summary>
public interface IUsageProbe
{
    /// <summary>Begin a new capture. Starts the wall clock and memory sampling.</summary>
    IUsageCapture Start();
}

/// <summary>
/// One active capture. Owns the wall-clock stopwatch, the peak-memory sampler,
/// and the list of phase timings. Call <see cref="MarkPhase"/> at each phase
/// boundary, then <see cref="Stop"/> to finalize.
/// </summary>
public interface IUsageCapture : IDisposable
{
    /// <summary>
    /// Record the end of the current phase under <paramref name="name"/>. The
    /// phase's duration is the time since the previous <c>MarkPhase</c> call,
    /// or since the capture started for the first phase.
    /// </summary>
    void MarkPhase(string name);

    /// <summary>
    /// Finalize the capture. Stops the wall clock, takes a final peak-memory
    /// sample, and returns the usage snapshot. Idempotent.
    /// </summary>
    AnalysisUsage Stop();
}

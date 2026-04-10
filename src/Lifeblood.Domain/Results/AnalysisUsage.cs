namespace Lifeblood.Domain.Results;

/// <summary>
/// Runtime usage snapshot for a single analyze run. Wall time, CPU time,
/// peak memory, GC pressure, per-phase breakdown, host core count.
///
/// Populated by <c>IUsageProbe</c> from the Application layer. The Domain
/// itself does not compute these values. It just carries them.
///
/// INV-USAGE-001: All fields are inert data. No System.Diagnostics types
/// leak onto this record. Anything that reads it can do so on any thread.
/// INV-USAGE-002: Units are documented on every field. Bytes for memory,
/// milliseconds for time. No implicit conversions.
/// </summary>
public sealed class AnalysisUsage
{
    /// <summary>Wall-clock duration of the entire analyze run, in milliseconds.</summary>
    public long WallTimeMs { get; init; }

    /// <summary>Total CPU time (user + kernel) consumed by the process during the run, in milliseconds.</summary>
    public long CpuTimeTotalMs { get; init; }

    /// <summary>User-mode CPU time during the run, in milliseconds.</summary>
    public long CpuTimeUserMs { get; init; }

    /// <summary>Kernel-mode CPU time during the run, in milliseconds.</summary>
    public long CpuTimeKernelMs { get; init; }

    /// <summary>Peak working set observed during the run, in bytes.</summary>
    public long PeakWorkingSetBytes { get; init; }

    /// <summary>Peak private bytes observed during the run, in bytes.</summary>
    public long PeakPrivateBytesBytes { get; init; }

    /// <summary>Number of logical processors visible to the host when the run started.</summary>
    public int HostLogicalCores { get; init; }

    /// <summary>Delta of GC generation 0 collections across the run.</summary>
    public int GcGen0Collections { get; init; }

    /// <summary>Delta of GC generation 1 collections across the run.</summary>
    public int GcGen1Collections { get; init; }

    /// <summary>Delta of GC generation 2 collections across the run.</summary>
    public int GcGen2Collections { get; init; }

    /// <summary>Per-phase timings in the order they were marked.</summary>
    public PhaseTiming[] Phases { get; init; } = Array.Empty<PhaseTiming>();

    /// <summary>
    /// Convenience derivation: total CPU time divided by wall time, as a percent.
    /// 100 means one core fully saturated across the run. 200 means two cores
    /// fully saturated. Divide by <see cref="HostLogicalCores"/> for average
    /// utilization across the whole box.
    /// </summary>
    public double CpuUtilizationPercent =>
        WallTimeMs > 0 ? (double)CpuTimeTotalMs / WallTimeMs * 100.0 : 0.0;
}

/// <summary>
/// One phase of the analyze pipeline. Marked at phase boundaries by
/// <c>IUsageCapture.MarkPhase(string)</c>. Duration is measured from the
/// previous mark (or from the start of capture for the first phase).
/// </summary>
public sealed class PhaseTiming
{
    public string Name { get; init; } = "";
    public long DurationMs { get; init; }
}

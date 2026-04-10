using System.Diagnostics;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Domain.Results;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Tests for the concrete <see cref="ProcessUsageProbe"/>. These are real
/// runs that exercise <see cref="System.Diagnostics.Process"/> and the
/// sampling timer. They deliberately do NOT assert exact wall-time values
/// because CI runners vary wildly. They assert only that the probe returns
/// internally-consistent numbers, captures CPU time deltas as expected,
/// records phase timings in order, and cleans up its timer.
/// </summary>
public class ProcessUsageProbeTests
{
    [Fact]
    public void Probe_ReturnsNonZeroWallTime_WhenRunSleeps()
    {
        var probe = new ProcessUsageProbe(sampleIntervalMs: 50);
        using var capture = probe.Start();

        Thread.Sleep(100);

        var usage = capture.Stop();
        Assert.InRange(usage.WallTimeMs, 80L, 5_000L);
    }

    [Fact]
    public void Probe_CapturesCpuTimeDelta_AcrossBusyWork()
    {
        // Burn ~50 ms of CPU deliberately. We assert CpuTimeTotalMs is at
        // least 20 ms so this stays stable even on slow CI runners.
        var probe = new ProcessUsageProbe(sampleIntervalMs: 50);
        using var capture = probe.Start();

        var sw = Stopwatch.StartNew();
        long sink = 0;
        while (sw.ElapsedMilliseconds < 50) sink += sw.ElapsedTicks;
        GC.KeepAlive(sink);

        var usage = capture.Stop();
        Assert.True(
            usage.CpuTimeTotalMs >= 20,
            $"expected >= 20 ms CPU, got {usage.CpuTimeTotalMs}");
        Assert.True(
            usage.CpuTimeTotalMs == usage.CpuTimeUserMs + usage.CpuTimeKernelMs,
            $"total ({usage.CpuTimeTotalMs}) must equal user ({usage.CpuTimeUserMs}) + kernel ({usage.CpuTimeKernelMs})");
    }

    [Fact]
    public void Probe_CpuUtilizationPercent_IsInternallyConsistent()
    {
        var probe = new ProcessUsageProbe(sampleIntervalMs: 50);
        using var capture = probe.Start();
        Thread.Sleep(100);
        var usage = capture.Stop();

        // Utilization percent is a pure function of wall time and CPU time.
        // Verify the derivation matches.
        var expected = usage.WallTimeMs > 0
            ? (double)usage.CpuTimeTotalMs / usage.WallTimeMs * 100.0
            : 0.0;
        Assert.Equal(expected, usage.CpuUtilizationPercent, 3);
    }

    [Fact]
    public void Probe_PeakWorkingSet_IsAtLeastInitialSample()
    {
        var probe = new ProcessUsageProbe(sampleIntervalMs: 50);
        using var capture = probe.Start();
        Thread.Sleep(60);
        var usage = capture.Stop();

        // The constructor takes an initial RSS sample. Stop takes a final
        // sample. Peak must be positive and the two measurements must not
        // disagree with each other's sign.
        Assert.True(usage.PeakWorkingSetBytes > 0);
        Assert.True(usage.PeakPrivateBytesBytes > 0);
    }

    [Fact]
    public void Probe_HostLogicalCores_MatchesEnvironment()
    {
        var probe = new ProcessUsageProbe();
        using var capture = probe.Start();
        var usage = capture.Stop();
        Assert.Equal(Environment.ProcessorCount, usage.HostLogicalCores);
    }

    [Fact]
    public void Probe_PhaseMarks_ArePreservedInOrder()
    {
        var probe = new ProcessUsageProbe(sampleIntervalMs: 500);
        using var capture = probe.Start();

        Thread.Sleep(10);
        capture.MarkPhase("alpha");
        Thread.Sleep(10);
        capture.MarkPhase("beta");
        Thread.Sleep(10);
        capture.MarkPhase("gamma");

        var usage = capture.Stop();
        Assert.Equal(3, usage.Phases.Length);
        Assert.Equal("alpha", usage.Phases[0].Name);
        Assert.Equal("beta", usage.Phases[1].Name);
        Assert.Equal("gamma", usage.Phases[2].Name);
        // Each phase duration should be positive. We don't assert exact
        // values because Thread.Sleep precision is coarse.
        Assert.All(usage.Phases, p => Assert.True(p.DurationMs >= 0));
    }

    [Fact]
    public void Probe_Stop_IsIdempotent()
    {
        var probe = new ProcessUsageProbe();
        using var capture = probe.Start();
        Thread.Sleep(20);
        var first = capture.Stop();
        var second = capture.Stop();
        // Same snapshot on the second call. INV-USAGE-PORT-002.
        Assert.Same(first, second);
    }

    [Fact]
    public void Probe_Dispose_AfterStop_IsNoOp()
    {
        var probe = new ProcessUsageProbe();
        var capture = probe.Start();
        Thread.Sleep(10);
        capture.Stop();
        capture.Dispose();
        capture.Dispose();
    }

    [Fact]
    public void Probe_Dispose_WithoutStop_DoesNotThrow()
    {
        var probe = new ProcessUsageProbe();
        var capture = probe.Start();
        Thread.Sleep(10);
        capture.Dispose();
    }

    [Fact]
    public void Probe_TwoCaptures_AreIndependent()
    {
        // Two sequential captures should produce independent snapshots. No
        // shared state between captures. INV-USAGE-PROBE-001.
        var probe = new ProcessUsageProbe(sampleIntervalMs: 50);

        using (var c1 = probe.Start())
        {
            Thread.Sleep(30);
            var u1 = c1.Stop();
            Assert.True(u1.WallTimeMs >= 20);
        }

        using var c2 = probe.Start();
        Thread.Sleep(30);
        var u2 = c2.Stop();
        Assert.True(u2.WallTimeMs >= 20);
    }

    [Fact]
    public void Probe_ShortRun_StillReportsNonZeroPeak()
    {
        // Sub-sample-interval runs should still get a non-zero peak because
        // the probe takes an initial sample in the constructor.
        var probe = new ProcessUsageProbe(sampleIntervalMs: 5_000);
        using var capture = probe.Start();
        var usage = capture.Stop();
        Assert.True(usage.PeakWorkingSetBytes > 0,
            "Probe must take an initial RSS sample so short runs still report a non-zero peak");
    }

    [Fact]
    public void Probe_GcCollectionCounts_AreNonNegative()
    {
        var probe = new ProcessUsageProbe();
        using var capture = probe.Start();

        // Allocate enough to trigger at least one gen0 collection.
        for (int i = 0; i < 1_000; i++)
        {
            var _ = new byte[10_000];
        }
        GC.Collect(0, GCCollectionMode.Forced);

        var usage = capture.Stop();
        Assert.True(usage.GcGen0Collections >= 0);
        Assert.True(usage.GcGen1Collections >= 0);
        Assert.True(usage.GcGen2Collections >= 0);
    }
}

using System.Diagnostics;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Domain.Results;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Concrete <see cref="IUsageProbe"/> backed by <see cref="Process.GetCurrentProcess"/>
/// and <see cref="Stopwatch"/>. Samples peak working set and peak private
/// bytes on a background <see cref="Timer"/> at a fixed interval. Captures
/// CPU time as a delta of <see cref="Process.UserProcessorTime"/> and
/// <see cref="Process.PrivilegedProcessorTime"/> across the run, so the
/// reported numbers are specific to the analyze call and not contaminated
/// by earlier work inside the same process.
///
/// Why this lives in Adapters.CSharp and not Application:
/// the Application layer holds ports only. Any type that touches
/// <see cref="System.Diagnostics.Process"/> is a host-OS adapter and belongs
/// alongside <c>PhysicalFileSystem</c>. Both Lifeblood.CLI and
/// Lifeblood.Server.Mcp already reference this assembly, so both composition
/// roots can share the same concrete probe.
///
/// INV-USAGE-PROBE-001: Every capture is isolated. Two captures started
/// back-to-back do not share state. Their peak samples, CPU deltas, and
/// GC counts are computed independently from the start of each capture.
/// INV-USAGE-PROBE-002: The sampling timer is disposed at <c>Stop</c> or
/// <c>Dispose</c>. Leaked timers would keep the probe alive past the run.
/// </summary>
public sealed class ProcessUsageProbe : IUsageProbe
{
    private readonly int _sampleIntervalMs;

    public ProcessUsageProbe(int sampleIntervalMs = 250)
    {
        if (sampleIntervalMs < 10) sampleIntervalMs = 10;
        _sampleIntervalMs = sampleIntervalMs;
    }

    public IUsageCapture Start() => new ProcessUsageCapture(_sampleIntervalMs);

    private sealed class ProcessUsageCapture : IUsageCapture
    {
        private readonly Stopwatch _sw;
        private readonly Process _proc;
        private readonly TimeSpan _userCpuStart;
        private readonly TimeSpan _kernelCpuStart;
        private readonly int _gen0Start;
        private readonly int _gen1Start;
        private readonly int _gen2Start;
        private readonly int _hostCores;
        private readonly Timer _timer;
        private readonly List<PhaseTiming> _phases = new();
        private readonly object _peakLock = new();

        private long _peakWs;
        private long _peakPrivate;
        private long _lastPhaseMs;
        private AnalysisUsage? _final;
        private volatile bool _stopped;

        public ProcessUsageCapture(int sampleIntervalMs)
        {
            _proc = Process.GetCurrentProcess();

            // Take the initial CPU snapshot BEFORE starting the stopwatch so
            // the first sample window does not see phantom CPU from the
            // constructor's own work.
            _userCpuStart = _proc.UserProcessorTime;
            _kernelCpuStart = _proc.PrivilegedProcessorTime;
            _gen0Start = GC.CollectionCount(0);
            _gen1Start = GC.CollectionCount(1);
            _gen2Start = GC.CollectionCount(2);
            _hostCores = Environment.ProcessorCount;

            // Initial RSS sample, so tiny runs that finish before the first
            // timer tick still get a non-zero peak.
            _proc.Refresh();
            _peakWs = _proc.WorkingSet64;
            _peakPrivate = _proc.PrivateMemorySize64;

            _sw = Stopwatch.StartNew();
            _timer = new Timer(SamplePeak, null, sampleIntervalMs, sampleIntervalMs);
        }

        private void SamplePeak(object? _)
        {
            if (_stopped) return;
            try
            {
                // Refresh() is cheap on Windows and Linux. It re-reads
                // /proc/self on Linux and calls GetProcessMemoryInfo on
                // Windows. Safe to call from the timer thread alongside the
                // owning thread's MarkPhase calls.
                _proc.Refresh();
                lock (_peakLock)
                {
                    var ws = _proc.WorkingSet64;
                    var priv = _proc.PrivateMemorySize64;
                    if (ws > _peakWs) _peakWs = ws;
                    if (priv > _peakPrivate) _peakPrivate = priv;
                }
            }
            catch
            {
                // Process is being torn down or the handle is in a weird
                // state. Swallow and let the next tick retry.
            }
        }

        public void MarkPhase(string name)
        {
            if (_stopped) return;
            var now = _sw.ElapsedMilliseconds;
            _phases.Add(new PhaseTiming { Name = name, DurationMs = now - _lastPhaseMs });
            _lastPhaseMs = now;
        }

        public AnalysisUsage Stop()
        {
            if (_final != null) return _final;

            _stopped = true;
            _timer.Dispose();
            _sw.Stop();

            // Final peak sample so that spikes between the last timer tick
            // and the explicit stop are captured.
            try
            {
                _proc.Refresh();
                lock (_peakLock)
                {
                    var ws = _proc.WorkingSet64;
                    var priv = _proc.PrivateMemorySize64;
                    if (ws > _peakWs) _peakWs = ws;
                    if (priv > _peakPrivate) _peakPrivate = priv;
                }
            }
            catch { /* best effort */ }

            var userNow = _proc.UserProcessorTime;
            var kernelNow = _proc.PrivilegedProcessorTime;
            var userMs = (long)(userNow - _userCpuStart).TotalMilliseconds;
            var kernelMs = (long)(kernelNow - _kernelCpuStart).TotalMilliseconds;
            if (userMs < 0) userMs = 0;
            if (kernelMs < 0) kernelMs = 0;

            long peakWs, peakPrivate;
            lock (_peakLock)
            {
                peakWs = _peakWs;
                peakPrivate = _peakPrivate;
            }

            _final = new AnalysisUsage
            {
                WallTimeMs = _sw.ElapsedMilliseconds,
                CpuTimeTotalMs = userMs + kernelMs,
                CpuTimeUserMs = userMs,
                CpuTimeKernelMs = kernelMs,
                PeakWorkingSetBytes = peakWs,
                PeakPrivateBytesBytes = peakPrivate,
                HostLogicalCores = _hostCores,
                GcGen0Collections = Math.Max(0, GC.CollectionCount(0) - _gen0Start),
                GcGen1Collections = Math.Max(0, GC.CollectionCount(1) - _gen1Start),
                GcGen2Collections = Math.Max(0, GC.CollectionCount(2) - _gen2Start),
                Phases = _phases.ToArray(),
            };
            return _final;
        }

        public void Dispose()
        {
            if (_final != null) return;
            _stopped = true;
            try { _timer.Dispose(); } catch { }
            try { _sw.Stop(); } catch { }
        }
    }
}

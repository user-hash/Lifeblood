# Usage-Reporting Invariants

Every analyze run can emit a structured `AnalysisUsage` snapshot
containing wall time, CPU time (total, user, kernel), peak working set,
peak private bytes, GC collection counts per generation, host logical
core count, and per-phase durations. The snapshot is populated by an
optional `Lifeblood.Application.Ports.Infrastructure.IUsageProbe` passed
to `AnalyzeWorkspaceUseCase`. Both the CLI and the MCP server ship the
probe on by default; every `lifeblood_analyze` response carries the
snapshot.

The probe lives in `Lifeblood.Adapters.CSharp` (touches
`System.Diagnostics.Process`); the port lives in
`Application/Ports/Infrastructure/`. Only composition roots
(`CLI.Program`, `Server.Mcp.GraphSession`) construct the concrete probe.

- **INV-USAGE-001. Usage is inert data.** `AnalysisUsage` and `PhaseTiming` hold only primitive fields and arrays of primitive fields. No `System.Diagnostics` types leak onto the records. Consumers read freely on any thread.
- **INV-USAGE-002. Units are documented on every field.** Bytes for memory, milliseconds for time. No implicit conversions. Only derived property: `CpuUtilizationPercent = CpuTimeTotalMs / WallTimeMs * 100`.
- **INV-USAGE-PORT-001. The probe returns a fresh capture on every `Start`.** Two captures started back-to-back do not share peak samples, CPU deltas, phase lists, or GC counters. A capture's lifetime is scoped to the single analyze run.
- **INV-USAGE-PORT-002. `IUsageCapture.Stop` is idempotent.** A second call returns the same `AnalysisUsage` instance; `Dispose` on an already-stopped capture is a no-op. `AnalyzeWorkspaceUseCase` disposes the capture on the error path so the sampling timer cannot outlive a failed run.
- **INV-USAGE-PROBE-001. `ProcessUsageProbe` takes an initial RSS sample in its constructor.** Sub-sample-interval runs still report a non-zero peak working set. Pinned by `ProcessUsageProbeTests`.
- **INV-USAGE-PROBE-002. The sampling timer is disposed at `Stop` or `Dispose`.** Leaked timers would corrupt the next capture's peak samples. Pinned by `Probe_TwoCaptures_AreIndependent`.

# Usage-Reporting Invariants

The probe lives in `Lifeblood.Adapters.CSharp` (touches
`System.Diagnostics.Process`); the port lives in
`Application/Ports/Infrastructure/`. Only composition roots
(`CLI.Program`, `Server.Mcp.GraphSession`) construct the concrete probe.

- **INV-USAGE-001. Usage is inert data.** `AnalysisUsage` and `PhaseTiming` hold only primitive fields and arrays of primitive fields. No `System.Diagnostics` types leak onto the records. Consumers read freely on any thread.

- **INV-USAGE-002. Units are documented on every field.** Bytes for memory, milliseconds for time. No implicit conversions. Only derived property: `CpuUtilizationPercent = CpuTimeTotalMs / WallTimeMs * 100`.

- **INV-USAGE-PORT-001. The probe returns a fresh capture on every `Start`.** Two captures started back-to-back do not share peak samples, CPU deltas, phase lists, or GC counters. A capture's lifetime is scoped to the single analyze run.

- **INV-USAGE-PORT-002. `IUsageCapture.Stop` is idempotent.** A second call returns the same `AnalysisUsage` instance; `Dispose` on an already-stopped capture is a no-op. `AnalyzeWorkspaceUseCase` disposes the capture on the error path so the sampling timer cannot outlive a failed run.

- **INV-USAGE-PROBE-001. `ProcessUsageProbe` takes an initial RSS sample in its constructor.**

- **INV-USAGE-PROBE-002. The sampling timer is disposed at `Stop` or `Dispose`.** Leaked timers would corrupt the next capture's peak samples. Pinned by `Probe_TwoCaptures_AreIndependent`.

- **INV-TELEMETRY-001. Operational telemetry flows through `ITelemetrySink` with a no-op default and a server-side .NET diagnostics adapter.**

- **INV-TELEMETRY-002. The emitted telemetry-event-name surface is a single source of truth that `lifeblood_capabilities` advertises verbatim.** `McpTelemetryEvents` (in `Lifeblood.Server.Mcp`) declares every event name as a constant plus an ordered `All` array; `ServerIdentity.BuildCapabilities` publishes `featureFlags.operationalTelemetryEvents = McpTelemetryEvents.All` verbatim, and the server-edge emit sites (`ToolHandler` tool/analyze events, `GraphSession.analyze.phase`) reference those constants — so an event the server emits cannot drift out of the advertised surface. `lifeblood.cache.lookup` is emitted from `Lifeblood.Connectors.Mcp` (a lower assembly that cannot reference the type); its literal there mirrors `McpTelemetryEvents.CacheLookup` and is covered by the advertised-set check. This closed the gap where `lifeblood.analyze.phase` was emitted and documented but absent from the advertised capability list. Pinned by `McpTelemetryEventsTests` and `ToolHandlerTests`.

# Lifeblood .NET Concurrency Prep Plan

Date: 2026-05-28

This is a preparation plan for future shared Lifeblood hosting. It is not a
daemon implementation plan and it does not retarget production away from
`net8.0`.

## Evidence Baseline

- Lifeblood self analyze, 2026-05-28: 3,939 symbols, 23,380 edges, 11 modules,
  0 violations, 0 cycles.
- `lifeblood_dependants type:Lifeblood.Server.Mcp.GraphSession`: 35 semantic
  dependants. Production dependants are `Program`, `McpDispatcher`,
  `ToolHandler`, and `WriteToolHandler`; the rest are tests.
- `lifeblood_file_impact src/Lifeblood.Server.Mcp/GraphSession.cs`: depends on
  30 files and is depended on by 15 files.
- `lifeblood_dependants type:Lifeblood.Application.UseCases.WorkspaceSession`:
  11 semantic dependants. Production use flows through `GraphSession`.
- `lifeblood_dependants type:Lifeblood.Connectors.Mcp.Internal.InvariantParseCache`:
  10 semantic dependants, including an existing concurrent-reader test.
- `lifeblood_dependants type:Lifeblood.Adapters.CSharp.Internal.SharedMetadataReferenceCache`:
  6 semantic dependants. Production use is inside Roslyn analyze and incremental
  analyze setup.

## Hexagonal Boundary

Keep concurrency policy at the host/session edge.

- Domain stays lock-free except for immutable-data lazy indexes that are already
  local to `SemanticGraph`.
- Application owns neutral session state through `WorkspaceSession`; it should
  not learn about server daemon scheduling, tenants, transports, or runtime
  primitives.
- Server.Mcp or a future host adapter owns request serialization, multi-client
  admission, and any session gate.
- Roslyn adapter caches stay per-analyze unless a measured parallel compilation
  design needs a shared cache.
- Telemetry remains an Application port with host-side adapters; counters and
  spans do not become synchronization policy.

## Current Shared-State Map

| Area | Current state | Current safety | Future shared-host action |
|---|---|---|---|
| `GraphSession` | Holds `_session`, `_roslynAdapter`, last paths, and analyze timestamp. | Safe under today's single stdio request loop; no internal locks. | Serialize analyze/load/auto-refresh against read/write tools before multi-client hosting. |
| `WorkspaceSession` | Mutable graph, analysis, capability, generation, and write-side ports. | Single-writer by composition convention; generation increment is not atomic. | Keep as internal mutable state behind a server-side gate; do not expose as concurrent primitive. |
| `SemanticGraph` | Immutable arrays after construction; lazy indexes published via `Interlocked.CompareExchange`. | Concurrent reads are acceptable; duplicate index builds are possible but not torn. | Keep as the read snapshot object; swap whole graph/session snapshots, not in-place graph mutation. |
| `RoslynCompilationHost` | Retained compilations plus lazy `RoslynWorkspaceManager`. | Treat as read-side service after construction; disposal is owned by session replacement. | Gate disposal/replacement so no write-side query can run while old services are being cleared. |
| `InvariantParseCache<T>` | Private dictionary guarded by `lock`; parse happens outside the lock. | Concurrent readers may duplicate cold parses but cannot observe torn cache state. | Keep current lock until measured contention appears; per-key locks only if invariant parsing becomes a hotspot. |
| `ProcessUsageProbe` | Shared probe creates isolated capture instances; each capture owns its timer and peak lock. | Capture state is isolated; timer writes are guarded. | Keep as adapter-level measurement, not request scheduling. |
| `DotNetDiagnosticsTelemetrySink` | ActivitySource/Meter adapter; operation objects own their stopwatch state. | .NET diagnostics primitives handle concurrent recordings. | Add metrics for queue wait/lock wait only after a real session gate exists. |
| `SharedMetadataReferenceCache` | Per-analyze dictionary used by module compilation builder. | Safe while module compilation remains sequential. | If module compilation becomes parallel, replace with `ConcurrentDictionary` or per-worker caches plus merge. |

## Required Tests Before Shared Hosting

Add these before any daemon or multi-client retained-graph server work:

- Concurrent read-side tools over a stable loaded `GraphSession` return
  consistent summaries and do not throw.
- `lifeblood_analyze` or auto-refresh cannot clear/dispose write-side services
  while `compile_check`, `find_references`, or `execute` is using them.
- Analyze requests for different project roots are serialized or isolated into
  distinct sessions; no cross-project graph bleed.
- Stale-refresh and explicit incremental analyze cannot run concurrently on the
  same retained adapter.
- Telemetry records queue wait, execution duration, success/error, and
  cancellation separately once a session gate exists.
- Existing `InvariantParseCache` concurrent-reader coverage stays green.

## Runtime Primitive Policy

- Do not add `System.Threading.Lock` while production targets `net8.0`.
- Do not add locks to Domain objects to compensate for host scheduling gaps.
- On a future approved target that supports newer primitives, replace ordinary
  lock objects only in real contended state: session gate, invariant cache, and
  any measured shared Roslyn cache.
- Keep the first shared-host implementation simple: one session gate per loaded
  workspace, whole-snapshot replacement, concurrent reads only when no load or
  dispose is in progress.

## Deferred Decisions

- Whether Lifeblood should run as one process per client, one shared daemon per
  workspace, or one daemon with many workspace sessions.
- Whether read-side operations should be allowed during analyze using the last
  completed snapshot, or blocked until the new snapshot is committed.
- Whether write-side Roslyn tools can safely run concurrently with each other.
  Treat as blocked until `RoslynCompilationHost`, `RoslynCodeExecutor`, and
  `RoslynWorkspaceRefactoring` are tested under parallel calls.

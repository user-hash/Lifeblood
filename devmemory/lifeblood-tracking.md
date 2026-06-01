# Lifeblood Tracking Log

Tracking file version: 1.0
Created: 2026-05-14
Scope: Lifeblood product feedback discovered while dogfooding against DAWG.

This is the clean canonical tracker for Lifeblood-only bugs, improvements,
optimizations, and shipped follow-through. DAWG architecture findings belong in
DAWG audit docs unless they expose a Lifeblood product issue.

Closed/shipped history (39 Shipped + 1 Receipt entries through v0.7.11) lives in
[`lifeblood-tracking-archive.md`](lifeblood-tracking-archive.md). This live file
carries only the active backlog and new intake so the working surface stays small.

## Rules From 2026-05-14 Forward

1. Every entry must name the Lifeblood version under test.
2. Every entry must include a concrete date and source session/report.
3. Every entry must declare one type: Bug, Improvement, Optimization, UX, Docs,
   or Shipped.
4. Every open item gets a stable tracking id in this file until it is promoted
   to a Lifeblood id such as `LB-BUG-*`, `LB-FR-*`, `LB-FP-*`, or
   `LB-INBOX-*`.
5. Every shipped item must point to the Lifeblood changelog, tag, or commit that
   closed it.
6. Do not mix DAWG architectural debt with Lifeblood tool feedback. If Lifeblood
   merely measured a DAWG issue, keep it out. If Lifeblood gave a wrong,
   incomplete, too-large, stale, or hard-to-act-on answer, track it here.
7. When an entry ships, MOVE it to `lifeblood-tracking-archive.md` (do not relabel
   in place — `TrackingLedgerTests.ClassifyStatus` has no `Archived` branch) and
   update the count anchors below.

## Required Entry Template

```text
## YYYY-MM-DD - Lifeblood vX.Y.Z - Short title

Status: Open | Candidate | Shipped | Archived
Type: Bug | Improvement | Optimization | UX | Docs | Shipped
Source: report/session/file path
Workspace: DAWG | Lifeblood self | other
Verification: tool output, test, changelog, or commit reference

Summary:
- What happened.

Impact:
- Why it matters.

Fix shape:
- Concrete product change requested or shipped.
```

## Current Snapshot

Latest released Lifeblood tag: **`v0.7.11`** (changelog rewritten human-readable
2026-06-01; GitHub Release cut + tag re-pointed to `5fd1c45`). `main` is
post-release; the `[Unreleased]` changelog section is the next-version intake.

Current verification anchors live in [`docs/STATUS.md`](../docs/STATUS.md) —
self-analyze symbols / edges / modules / types, test discovery count,
`[SkippableFact]` count, typed-invariant audit, MCP tool count, port count,
static-tables defaults. Every anchor is ratcheted against live source by
`DocsTests.Anchor_MatchesLiveSource` on every CI run.

Machine-checked tracking ledger summary (`TrackingLedgerTests` parses this file
as the SSoT; do not hand-edit these counts without making the entry bodies agree):

<!-- trackingStatusShippedCount: 0 --><!-- trackingStatusPartiallyShippedCount: 2 --><!-- trackingStatusReceiptCount: 0 --><!-- trackingStatusOpenCount: 0 -->

New intake — un-started findings/feature requests awaiting prioritization (the
ledger itself holds only Shipped + in-flight per `TrackingLedger_HasNoPlainOpenOrCandidateEntries`):
[`lifeblood-intake.md`](lifeblood-intake.md).

Active non-shipped implementation ledger:
<!-- trackingActiveBacklog:start -->
- 2026-05-28 - Lifeblood .NET feature adoption revised stage order
- 2026-05-28 - Lifeblood .NET runtime/JIT benchmark lane
<!-- trackingActiveBacklog:end -->

Historical close receipts (L-LIM-001..006 multi-define closure, Native-Clang
opt-in lane, gravity-well measurements, hash-truth audit, prior primary-source
reports) are preserved verbatim in `lifeblood-tracking-archive.md`.

## 2026-05-28 - Lifeblood .NET feature adoption revised stage order

Status: Partially shipped
Type: Planning
Source: legacy-repo review of the .NET platform-feature plan, 2026-05-28
Workspace: Lifeblood self
Verification: reconciles the already-landed JSON baseline (`7123200`) and
telemetry baseline (`5cff398`) with the product reality that Lifeblood is a
legacy-compatible dotnet tool, not a greenfield server.

Summary:
- The original direction was sound but missed product gates that matter more
  for a legacy tool repo: support/EOL timing, dotnet tool packaging, schema
  compatibility modes, source-generated JSON/AOT readiness, and measurement
  breadth before retargeting.
- The current code state is: JSON schema snapshots + opt-in strict duplicate
  rejection landed first; telemetry baseline then landed on `net8.0` with a
  no-op default and .NET diagnostics adapter. Future order below supersedes the
  initial brainstorm order.
- 2026-05-31 implementation note: the first architecture-first slice shipped
  the server-edge tool argument contract/binder, `LIFEBLOOD_JSON_COMPAT`
  compatibility modes, analyze phase telemetry, a retained-session gate,
  Runtime Async diagnose/compile-check fixtures, expanded benchmark workloads,
  optional packaging checks, an opt-in Runtime Async benchmark lane that passed
  a local side-by-side .NET 11 preview SDK run with `runtime-async=on`, and a
  hardened .NET 10 experimental target lane with restore/build/test/semantic
  self-analyze/pack receipts. Production projects remain on `net8.0`; .NET 10
  remains experimental until benchmark/package data supports a production
  migration decision.

Priority order:
1. Telemetry on `net8.0`: port + no-op + diagnostics adapter + tool/analyze
   timings.
2. JSON DTO/schema hardening: typed MCP args, schema snapshots, duplicate /
   unknown / missing tests, and `legacy` / `warn` / `strict` compatibility modes.
3. Benchmark harness: `net8.0` vs `net10.0`, identical workloads,
   machine-readable output.
4. .NET 10 experimental target: build/test/package lane, not production.
5. Tool packaging/distribution: `dotnet tool exec`, `dnx`, CLI schema,
   platform-specific / self-contained / AOT experiments where useful.
6. Concurrency prep: only real shared state, no daemon rewrite yet.
7. Production `net10.0` migration: decide before .NET 8 EOL; keep `net8.0` only
   as a compatibility branch if needed.
8. .NET 11 Runtime Async lane: detect/analyze user projects first, opt-in
   Lifeblood benchmark second, production never before stable evidence.

Remaining open work:
- Close the remaining concrete child entries below with evidence receipts, then
  make the production `net10.0` migration decision from
  benchmark/package/schema data.

## 2026-05-28 - Lifeblood .NET runtime/JIT benchmark lane

Status: Partially shipped
Type: Optimization
Source: DAWG/Lifeblood .NET platform-feature planning session, 2026-05-28
Workspace: Lifeblood self and DAWG dogfood workspace
Verification: local inspection: production projects target `net8.0`; `global.json`
pins SDK `8.0.100` with `latestFeature` roll-forward; local machine has 8/9/10
runtimes but no .NET 11 SDK/runtime; baseline harness shipped in
`tools/runtime-benchmarks/run-lifeblood-runtime-benchmark.ps1`; local smoke run
completed `net8.0` self-analyze and captured graph counts, process wall/CPU,
peak memory, GC collections, and analyze/validate phase timings. 2026-05-31
slice expands the workload selector beyond self-analyze to analyze/context,
incremental-noop, and CLI help lanes, adds category metadata plus
`parseDurationMs` for CLI output parsing, and records the measurement
availability caveats in the machine-readable report. Follow-up local slice
extends the MCP GC benchmark beyond memory ceilings: after retained
`lifeblood_analyze`, it dispatches `lifeblood_capabilities`,
`lifeblood_context`, `lifeblood_cycles`, and `lifeblood_dead_code`, recording
per-tool `dispatchLatencyMs`, response bytes, and completion status. Pinned by
`BenchmarkSmokeTests`. Local 2026-05-31 smoke (`Runs=1`, net8 MCP publish,
`benchmarkRunId=codex-smoke-20260531`)
completed all three GC configs and all retained read-side dispatches; workstation
read-side latencies were capabilities 19 ms, context 88 ms, cycles 26 ms, and
dead-code summarize 22 ms. CLI and MCP benchmark reports now carry a shared
`benchmarkRunId`; the MCP harness also passes it into the child process as
`LIFEBLOOD_BENCHMARK_RUN_ID` for future telemetry/report joins.

Summary:
- Newer runtimes may improve JIT, GC, JSON, and async behavior, but Lifeblood
  should not retarget production until identical workloads show real benefit with
  no schema or semantic drift.
- DAWG is the right large-workspace benchmark subject, but benchmark code must
  stay generic and usable on Lifeblood self.

Remaining open work:
- Run comparable `net8.0`/`net10.0` workloads over Lifeblood and DAWG, including
  the retained read-side MCP dispatch lane, then gate retargeting on stable
  semantic counts and measured win/loss data.

Impact:
- A runtime upgrade that looks good in general .NET marketing can still be a loss
  for Roslyn-heavy retained-graph workloads if memory, startup, or compilation
  behavior regresses.
- A reproducible benchmark lane gives the project a factual gate for retargeting
  instead of intuition.

Fix shape:
- Add a benchmark script/project that runs identical workloads on the current
  target and experimental newer targets when SDKs are installed.
- Keep the first harness generic and source-only: it should discover supported
  CLI target frameworks, mark unsupported requested targets as skipped, and emit
  output under `artifacts/runtime-benchmarks/` without committing run products.
- Required workloads: Lifeblood self full analyze, Lifeblood self incremental
  noop, DAWG full analyze when available, DAWG read-only analyze, and the top
  read-side tools on a retained graph.
- Emit machine-readable JSON plus a short human summary with wall time, peak
  working set/private bytes, allocated bytes, Gen0/Gen1/Gen2 counts, JSON
  parse/serialize time, Roslyn load time, graph build time, resolver/index time,
  MCP dispatch latency, graph counts, and tool success/error counts.
- Gate production retargeting on measured wall-time or memory improvement with
  unchanged tests, schema snapshots, and semantic graph results.
- Add an explicit support gate: production `net10.0` migration must be decided
  before .NET 8 EOL. If customers still need `net8.0`, keep it as a compatibility
  branch rather than leaving `main` stranded on an unsupported runtime.

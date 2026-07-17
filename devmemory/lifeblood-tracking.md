# Lifeblood Tracking Log

Tracking file version: 1.0
Created: 2026-05-14
Scope: Lifeblood product feedback discovered while dogfooding against DAWG.

This is the clean canonical tracker for Lifeblood-only bugs, improvements,
optimizations, and shipped follow-through. DAWG architecture findings belong in
DAWG audit docs unless they expose a Lifeblood product issue.

Closed history, including local v0.7.12/v0.7.13-alpha implementation receipts,
lives in
[`lifeblood-tracking-archive.md`](lifeblood-tracking-archive.md). This live file
carries only the active backlog and new intake so the working surface stays small.

## Rules From 2026-05-14 Forward

1. Every entry must name the Lifeblood version under test.
2. Every entry must include a concrete date and source session/report.
3. Every entry must declare one type: Bug, Improvement, Optimization, UX, Docs,
   or Shipped.
4. Unstarted findings stay in `lifeblood-intake.md`. When implementation begins,
   promote the item here as `Partially shipped` with its stable intake/product
   id; do not maintain the same active item in both files.
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

Status: Partially shipped | Shipped | Receipt
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

Latest stable tag represented in the changelog: **`v0.7.12`** at `dbfd871`
(2026-06-22). `CHANGELOG.md` now carries a dated `[0.7.12]` historical section,
its comparison link, and `[Unreleased]` begins at `v0.7.12`. The latest-tag
relationship is ratcheted through the shared source-control adapter
(`INV-CHANGELOG-LATEST-TAG-001`); this maintenance atom did not create, move,
push, or publish a tag.

Current verification anchors live in [`docs/STATUS.md`](../docs/STATUS.md) —
self-analyze symbols / edges / modules / types, test discovery count,
`[SkippableFact]` count, typed-invariant audit, MCP tool count, port count,
static-tables defaults. Every anchor is ratcheted against live source by
`DocsTests.Anchor_MatchesLiveSource` on every CI run.

Machine-checked tracking ledger summary (`TrackingLedgerTests` parses this file
as the SSoT; do not hand-edit these counts without making the entry bodies agree):

<!-- trackingStatusShippedCount: 0 --><!-- trackingStatusPartiallyShippedCount: 8 --><!-- trackingStatusReceiptCount: 0 --><!-- trackingStatusOpenCount: 0 -->

New intake — un-started findings/feature requests awaiting prioritization (the
ledger itself holds only Shipped + in-flight per `TrackingLedger_HasNoPlainOpenOrCandidateEntries`):
[`lifeblood-intake.md`](lifeblood-intake.md).

Active non-shipped implementation ledger:
<!-- trackingActiveBacklog:start -->
- 2026-05-28 - Lifeblood .NET feature adoption revised stage order
- 2026-05-28 - Lifeblood .NET runtime/JIT benchmark lane
- LB-INTAKE-20260629-013 - Generated DSP/math probe recipe
- LB-INTAKE-20260714-029 - Diff-scoped diagnostic ownership report
- LB-INTAKE-20260714-030 - First-class evidence baseline drift check
- LB-INTAKE-20260714-032 - Compact invariant audit without duplicate zero-source ledgers
- LB-INTAKE-20260714-033 - Retained-session memory telemetry start/end/peak delta
- LB-INTAKE-20260716-044 - Static semantic blind spots need explicit unsupported-edge receipts
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

## LB-INTAKE-20260629-013 - Generated DSP/math probe recipe

Status: Partially shipped
Type: Docs
Source: DAWG Burst regression-test dogfood, 2026-06-29 through 2026-07-17
Workspace: DAWG and Lifeblood self
Verification: playbook/skill validation; 46 focused documentation tests; DAWG
generation 1 / `snap_53f23e1f959a4a4d80d33b67b3d0f55b` compile check;
Unity observatory run `513ffc3fafc14ef3920c92297d3d4bcd`

Summary:
- Workflow 8 in `docs/PLAYBOOK_CSHARP.md` now derives parameterized probes from
  one consumer-owned contract/range authority, compile-checks their real test
  files, and leaves measured execution with the consumer's test framework.
- The Lifeblood skill and routing reference point agents at the same workflow.
  No audio runner, generator command, range database, fact cache, graph edge, or
  retained semantic base was added.
- Installed alpha.91 analyzed live DAWG Editor+Player in 65.43 seconds and
  published generation 1 / snapshot
  `snap_53f23e1f959a4a4d80d33b67b3d0f55b`. A bounded compile check resolved both
  `DspSanityTests.cs` and `TuningParamRangeProviderRatchetTests.cs` uniquely in
  `Nebulae.Tests.Editor.Audio` with zero diagnostics.
- DAWG's normal runner selected and completed 184/184 output/range cases with
  zero test failures in 11.02 seconds. Its global receipt correctly ended
  `infra_failed`, however, because unrelated DAWG files changed during the run;
  this is supporting value evidence, not a frozen closure receipt.

Impact:
- Agents have a reusable structural-to-measured test path without Lifeblood
  embedding synth vocabulary or pretending static evidence proves runtime
  output.

Fix shape:
- Keep the documented recipe and skill routing as the permanent product shape.
- Do not add a Lifeblood-owned execution engine unless a future cross-project
  benchmark proves a genuinely shared runtime boundary.

Remaining open work:
- Repeat the same bounded compile check and 184-case DAWG target run from one
  stable source digest; require a terminal passed, non-drifted receipt, then move
  this entry to closed history.

## LB-INTAKE-20260716-044 - Static semantic blind spots need explicit unsupported-edge receipts

Type: Improvement
Priority: Medium
Status: Partially shipped
Source: DAWG first-session field report, 2026-07-16
Workspace: DAWG

What:
- The field report called out semantic blind spots that Lifeblood does not
  currently represent as graph edges, including reflection and
  `Resources.Load` relationships.
- DAWG also has source-text ratchets using `File.ReadAllText` that can protect
  production files while showing zero semantic file impact, because the graph
  models compiled references rather than arbitrary test data dependencies.

Why it matters:
- These are not graph corruption bugs; they are unsupported relationship
  classes. The product should say that clearly so agents do not overstate
  semantic coverage.
- When unsupported edges matter, the tool should offer an explicit extension
  path instead of letting users infer missing relationships from silence.

Fix shape:
- Add an unsupported-edge/heuristic receipt for known blind-spot families:
  reflection strings, Unity resource paths, serialized asset references, and
  source-text/file IO ratchets.
- Keep core semantic edges precise. Heuristic relationships should be separate,
  confidence-tagged, and opt-in for tools like file impact or test impact.
- Add fixtures where normal graph impact is zero but an advisory unsupported
  relationship is reported with confidence and evidence.

Resolution evidence:
- Added Domain `UnsupportedRelationshipReport` / family / hit DTOs.
- `lifeblood_file_impact` now accepts
  `includeUnsupportedRelationships:true`. The normal semantic
  `dependsOnCount` / `dependedOnByCount` fields stay graph-proven; the new
  capped advisory receipt is separate and reports
  `semanticGraphEdgesChanged:false`.
- The first shipped scanner detects source-file IO literal relationships such
  as `File.ReadAllText("Target.cs")`, capped to 25 hits. A follow-up scanner
  detects graph-resolved reflection type strings such as
  `Type.GetType("Namespace.Target")` only when the queried file declares the
  target type. `Resources.Load` paths and Unity serialized asset references are
  explicitly documented as unsupported families in the receipt rather than
  silently modeled as graph edges.
- Focused verification:
  `dotnet test tests\Lifeblood.Tests\Lifeblood.Tests.csproj -c Release --filter FullyQualifiedName~ToolHandlerTests.Handle_FileImpact_UnsupportedRelationships_SourceFileIoLiteral_IsAdvisory`
  passed 1/1.

Remaining open work:
- Add separate opt-in, confidence-tagged adapters for Unity Resources paths and serialized asset references only when their target identity can be resolved without weakening semantic graph edges; keep source-file IO and reflection-string scanning advisory and bounded.

## LB-INTAKE-20260714-029 - Diff-scoped diagnostic ownership report

Status: Partially shipped
Type: Feature request
Source: DAWG ADSR/LFO/genre dogfood, 2026-07-14 through 2026-07-17
Workspace: DAWG and Lifeblood self
Verification: commit `bceb0d5`; current 1,700-case Release suite

Summary:
- `lifeblood_diagnose(diagnosticOwnershipMode)` joins the existing retained
  Roslyn diagnostics to bounded Git working-tree, staged, since-commit, or
  caller-supplied touched-path evidence.
- Results group diagnostic indexes as `introducedByDiff`,
  `preExistingTouchedFile`, `preExistingUnrelated`, or `unknownOwnership`
  without duplicating the diagnostic payload.
- Truncated/failed Git evidence, external paths, explicit files without line
  history, and stale retained diagnostics fail closed to unknown. The Git
  adapter remains the one source-control fact authority; no baseline
  compilation or retained semantic base was added.

Impact:
- Agents can separate current-atom diagnostics from unrelated repository debt
  without laundering uncertain provenance into an owned finding.

Remaining open work:
- Run the installed tool on a stable DAWG publication with a controlled
  working-tree/staged diagnostic fixture and capture all four ownership groups,
  exact snapshot/generation preconditions, and zero source drift; then archive
  the entry.

## LB-INTAKE-20260714-030 - First-class evidence baseline drift check

Status: Partially shipped
Type: Improvement
Source: DAWG evidence-baseline dogfood, 2026-07-14 through 2026-07-17
Workspace: DAWG and Lifeblood self
Verification: commit `ce93db7`; current 1,700-case Release suite

Summary:
- `lifeblood_evidence_drift` parses one bounded workspace-contained Markdown
  stamp and compares it with the exact leased graph plus a live invariant
  audit after recapturing canonical source, descriptor, and rule identity.
- It reports exact deltas, caller-visible tolerance, fixed safety flags,
  commit/content provenance, verdict, and separate analysis/evidence refresh
  guidance. Stale publications and incomplete baselines fail closed.
- The existing per-profile edge projection is shared with analyze; the tool is
  read-only and creates no baseline compilation or retained semantic base.

Impact:
- Generated evidence can be cited or refreshed from a product-owned freshness
  verdict instead of an unverified external count comparison.

Remaining open work:
- On one stable DAWG Editor+Player publication, exercise the default
  `docs/code-maps/EVIDENCE.generated.md` path with exact snapshot/generation
  preconditions and record current/stale guidance plus
  `additionalSemanticBaseCount:0`; then archive the entry.

## LB-INTAKE-20260714-032 - Compact invariant audit without duplicate zero-source ledgers

Status: Partially shipped
Type: Optimization
Source: DAWG evidence-refresh dogfood, 2026-07-11 through 2026-07-17
Workspace: DAWG and Lifeblood self
Verification: commit `b311d7e`; current 1,700-case Release suite

Summary:
- `lifeblood_invariant_check(mode:"audit", summarize:true)` preserves totals,
  category counts, duplicate occurrences, parse warnings, and coverage while
  projecting only nonzero source counts.
- The citation-safe receipt owns that compact source projection once and the
  top level references it. The default full v1 response remains compatible.
- A 56-source zero-heavy fixture proves duplicates and warnings remain visible
  without serializing the same mostly-zero ledger twice.

Impact:
- Large invariant trees retain provenance and coverage warnings without a
  multi-thousand-token duplicate source inventory dominating the response.

Remaining open work:
- Run compact and full audit against the same stable DAWG snapshot, verify the
  compact payload remains bounded while totals/duplicates/warnings agree, and
  archive the entry with the exact generation/snapshot receipt.

## LB-INTAKE-20260714-033 - Retained-session memory telemetry start/end/peak delta

Status: Partially shipped
Type: Optimization
Source: DAWG retained-session dogfood, 2026-07-11 through 2026-07-17
Workspace: DAWG and Lifeblood self
Verification: commit `0afc087`; current 1,700-case Release suite

Summary:
- `AnalysisUsage` remains the one per-request usage authority and now records
  start, end, absolute peak, signed end-minus-start, and nonnegative
  peak-above-start working-set/private-byte values.
- Existing absolute peak fields remain backward-compatible projections. The
  process adapter drains its sampler before the final sample and uses no fixed
  wall-clock completion deadline.
- A retained full/noop/one-file-edit/descriptor-fallback fixture pins semantic
  parity and the additive CLI/MCP wire shape without another telemetry type.

Impact:
- A request can distinguish retained graph cost from transient rebuild growth
  instead of treating a process-wide absolute peak as evidence of a leak.

Remaining open work:
- Run one stable installed DAWG process through full Editor+Player,
  incremental-noop, controlled one-file incremental, and descriptor fallback;
  capture start/end/delta/peak-above-start with unchanged semantic counts, then
  archive the entry.

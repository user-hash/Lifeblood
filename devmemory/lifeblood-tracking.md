# Lifeblood Tracking Log

Tracking file version: 1.0
Created: 2026-05-14
Scope: Lifeblood product feedback discovered while dogfooding against DAWG.

This is the clean canonical tracker for Lifeblood-only bugs, improvements,
optimizations, and shipped follow-through. DAWG architecture findings belong in
DAWG audit docs unless they expose a Lifeblood product issue.

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

Latest released Lifeblood tag: **`v0.7.10`**. `main` is now post-release; the
`[Unreleased]` changelog section is the next-version intake area and is the
canonical place for post-`v0.7.10` release notes until a release cut is made.

Current verification anchors live in [`docs/STATUS.md`](../docs/STATUS.md) —
self-analyze symbols / edges / modules / types, test discovery count,
`[SkippableFact]` count, typed-invariant audit, MCP tool count, port count,
static-tables defaults. Every anchor is ratcheted against the live source by
`DocsTests.Anchor_MatchesLiveSource` on every CI run. The historical
verification-anchor block that used to appear here (point-in-time snapshots)
is retired in favour of the live STATUS.md anchors.

Machine-checked tracking ledger summary (`TrackingLedgerTests` parses this file
as the SSoT; do not hand-edit these counts without making the entry bodies agree):

<!-- trackingStatusShippedCount: 36 --><!-- trackingStatusPartiallyShippedCount: 4 --><!-- trackingStatusReceiptCount: 1 --><!-- trackingStatusOpenCount: 0 -->

Active non-shipped implementation ledger:
<!-- trackingActiveBacklog:start -->
- 2026-05-28 - Lifeblood .NET feature adoption revised stage order
- 2026-05-28 - Lifeblood .NET JSON contract hardening
- 2026-05-28 - Lifeblood .NET runtime/JIT benchmark lane
- 2026-05-28 - Lifeblood .NET tool packaging/distribution lane
<!-- trackingActiveBacklog:end -->

**2026-05-24 Wave 6 close — L-LIM-001 CLOSED**: multi-define union analyze chain
shipped (Wave 6.A → 6.F) across commits `43c1499..dd157af`. Port `IDefineProfileResolver`
+ adapter `UnityDefineProfileResolver` (Editor + Player MVP) + `Edge.Profiles[]`
+ GraphBuilder union dedup + `lifeblood_analyze defineProfiles` input +
`lifeblood_dependants/dependencies` `profiles[]` + `profileFilter` narrowing
+ IOperation `profileScope`. DAWG receipt (point-in-time, at the dist swap
moment): edges 247,350 → 247,460 (delta +110 Player-only edges, invariant);
`AdaptiveBeatGrid.Bootstrap_WireServices` callsite visible on `AudioRuntimeProfilePolicy.Resolve`
+ `AudioRuntimeProfilePersistenceLocator.Current` with `profiles:["Player"]`.
**L-LIM-001..006 all CLOSED**. DAWG `reference_lifeblood_known_limitations.md`
L-LIM-001 marked CLOSED with the full receipt table.

**Reviewer Stage 1 polish (shipped in v0.7.9)**: `INV-MULTI-DEFINE-INCREMENTAL-001`
closes the multi-profile + incremental-analyze parity defect — `AnalysisSnapshot.ActiveProfiles`
is SSoT for "which profiles is this graph under?", `IncrementalAnalyze` replays
the snapshot's profile set over changed files so per-edge `Profiles[]`
provenance survives a file-touch. `DocsTests` refactored to a single
`DocsAnchor[]` table — adding a ratcheted count is one row, not one method.
Hardcoded count citations stripped from README / ARCHITECTURE.md /
architecture.html / TOOLS.md / MCP_SETUP.md / UNITY.md; STATUS.md is the only
visible-prose carrier for the canonical numbers, every other surface links to
it. CI matrix extended to `windows-latest`.

**v0.7.9 release cut**: tag `v0.7.9` lands on commit `3531d37`
(`docs(changelog): cut [0.7.9] - 2026-05-24 release section + link refs`).
The changelog records the 0.7.8 -> 0.7.9 delta: write-side profile-scope
honesty, multi-profile incremental cross-project edge carry, incremental-noop
summary repair, release-surface doc ratchets, and the DAWG live receipts for
L-LIM-001..006 closure.

**Native-Clang opt-in lane**: ships as an opt-in build target under
`adapters/native-clang/` (CMake + libclang). The C# core packages (`Lifeblood`,
`Lifeblood.Server.Mcp`) do NOT carry `lifeblood-native-clang.exe` and do NOT
depend on LLVM. The 11 `[SkippableFact]` ratchets skip silently when the
executable is absent (default suite green) and fail loudly when
`LIFEBLOOD_REQUIRE_NATIVE_CLANG=1` is set on a host where the executable IS
expected. See [`docs/NATIVE_CLANG.md`](../docs/NATIVE_CLANG.md) § "Opt-in
execution lane" for the build recipe.

Gravity-well measurement at plan start (historical Phase-2 targets):
- `src/Lifeblood.Adapters.CSharp/RoslynCompilationHost.cs`: 1,139 LOC.
- `src/Lifeblood.Server.Mcp/ToolHandler.cs`: 1,125 LOC.
- `src/Lifeblood.Connectors.Mcp/LifebloodSymbolResolver.cs`: 1,111 LOC.
- `src/Lifeblood.Adapters.CSharp/RoslynEdgeExtractor.cs`: 833 LOC.
- `src/Lifeblood.Adapters.CSharp/RoslynSymbolExtractor.cs`: 823 LOC.
- Five-file total: 5,031 LOC. Native-Clang benchmark: 113 files / 5,790 LOC /
  largest 249 LOC. Phase-2 target: every adapter file under 400 LOC, one
  concern per file, behind stable Application-layer ports.

Hash-truth audit (2026-05-15): six of the eight original LB-TRACK
entries cited pre-rebase ghost hashes (`fc8ff96` / `a8b7925` / `bcb61aa`
/ `ca1fae0` / `eab3e7b` / `001be0d`) that exist in the Lifeblood object
database but are not ancestors of any tagged release. The mainline
closing commits have been substituted below. Future entries must cite
the hash recorded by `git log --oneline <commit>` against `main` after
the change has actually merged, not the local pre-rebase hash.

Primary source reports:

- `.claude/devmemory/lifeblood-field-report-2026-05-11.md`
- `docs/invariants/eternal-arch-audit-2026-05-12.md`
- `D:/Projekti/Lifeblood/docs/IMPROVEMENT_INBOX.md`
- `D:/Projekti/Lifeblood/docs/DOGFOOD_FINDINGS.md`
- `D:/Projekti/Lifeblood/CHANGELOG.md`

Legacy unversioned source material has been normalized below. Future reports
must not be unversioned.

## 2026-05-30 - Unity new-file discovery misses pre-meta source — DUPLICATE, see LB-TRACK-20260530-028

Collapsed 2026-05-30 reconciliation: this was a second repro of the same bug now
canonically tracked as `LB-TRACK-20260530-028` (below). Distinct evidence from
this session folded into 028's verification block: new file
`Assets/Tests/Editor/Audio/Burst/BurstCombFilterKernelParityTests.cs` returned
`LB0002 file not found in any loaded compilation` from `compile_check(filePath)`,
and both `incremental-noop` and full analyze held `files:3670` until
`refresh_unity` generated the `.meta` (then `files:3671`). Server
`0.7.10+e98a9b7c16030678f826fc9550e02ceb6fd2a577`.

## 2026-05-24 - Lifeblood v0.7.9 - DAWG reconnect/post-update notes

Status: Shipped (in-tree, untagged) - Lifeblood endpoint closed; DAWG workflow
note remains downstream guidance, not a Lifeblood release gate.
Type: UX
Source: DAWG post-update reconnect during ABG top-down comment pass
Workspace: DAWG
Verification: Lifeblood tag `v0.7.9` at commit `3531d37`; DAWG dogfood probes
from the ABG pass and v0.7.9 changelog `[0.7.9] - 2026-05-24`. Lifeblood-side
capability drift fix shipped in `b6d13c7` (`feat(mcp): expose capabilities and
docs evidence`): `lifeblood_capabilities` reports server version/source,
optional git commit/dirty state, tool count with read/write split, feature
flags, schema snapshot path, STATUS.md anchor path, operational telemetry event
names, and session state. Pinned by
`ToolHandlerTests.Handle_Capabilities_WithoutLoad_ReturnsVersionToolCountsAndContractPaths`
and advertised in `schemas/tools/v1/lifeblood_capabilities.schema.json`.

Summary:
- No new correctness blocker surfaced after the v0.7.9 dist swap. The DAWG ABG
  pass used `compile_check`, `file_impact`, `cycles`, and
  `assignment_coverage` without a fresh false-positive class.
- `lifeblood_assignment_coverage` is the standout new daily-workflow tool for
  DAWG-style `*HostBindings`: it verified complete slot assignment on the ABG
  service/MIDI slices without reflection or source-text walking. Example DAWG
  receipts from the pass: `CoreRebuildHostBindings` 45/45,
  `CoreStateSyncHostBindings` 39/39, `SnapshotApplyHostBindings` 14/14,
  `StepShapeEnforcerHostBindings` 10/10, `ClipSyncHostBindings` 11/11,
  `MidiGridHostBindings` 4/4, `MidiCcHostBindings` 16/16; zero absent/null
  slots on the checked construction sites.
- The remaining noticed friction is documentation/capability drift outside
  Lifeblood: DAWG guidance still carried older tool counts/version wording in
  places while the server was already at v0.7.9 / 30 MCP tools.

Impact:
- The product behavior looks healthy, but stale downstream guidance can make
  agents use older workflows, under-use `assignment_coverage`, or distrust
  multi-profile results that v0.7.9 now handles honestly.
- This is the same family as LB-NICE-010, now observed again immediately after
  the v0.7.9 release cut.

Fix shape:
- Promote LB-NICE-010 into a concrete next-version acceptance target:
  `lifeblood_version_info` or `lifeblood_capabilities` should return semver,
  build hash, dirty flag, tool count, feature flags (`multiProfileAnalyze`,
  `assignmentCoverage`, `writeSideProfileScope`, summarize-capable tools), and
  the docs/status anchor path. Callers can then drift-detect on session start
  instead of learning capability state from stale local prose.
- Add a small DAWG-side workflow note once the capability endpoint exists:
  `assignment_coverage` is the first-choice static audit for `*HostBindings`;
  source-text ratchets are fallback only when the contract itself is source
  text.

Non-blocking follow-ups found during 2026-05-29 review:
- `ServerIdentity.RunGit` waits for process exit before reading redirected
  stdout/stderr. A very dirty repo can fill the `git status --porcelain` pipe,
  force the 1500ms timeout, and degrade `dirty`/`state` to unknown. This is
  graceful, not a release blocker. Bulletproof shape: async output/error reads
  or drain-before-wait discipline.
- `featureFlags.summarizeCapableTools` used to be discovered by serializing
  anonymous input schemas and searching for a `"summarize"` property token.
  Closed by the 2026-05-31 local SSoT cleanup: `ServerIdentity` now reads
  `ToolInputContract` metadata, and `ToolHandlerTests` pins the capability list
  against that typed contract source.

## 2026-05-28 - Lifeblood v0.7.9-1-g4a7a63a - Durable documentation receipts for living-doc baselines

Status: Shipped (in-tree, untagged)
Type: UX
Source: DAWG LDF eternal living-doc refresh, top-down architecture/doc pass
Workspace: DAWG
Verification: Lifeblood DAWG MCP session returned
`lifeblood_analyze` summary `68,311` symbols, `252,832` edges, `90` modules,
`3,618` files, `0` violations, `101` cycles; `lifeblood_invariant_check`
audit returned `102` parsed entries, no duplicates, no parse warnings. DAWG
follow-through commits: `742e4e245 docs(invariants): align audit baseline`
and `ed2df3dc6 docs(invariants): remove brittle lifeblood generations`.
Lifeblood-side receipt fix shipped in `b6d13c7` (`feat(mcp): expose
capabilities and docs evidence`): `lifeblood_analyze` and
`lifeblood_invariant_check` audit responses now include docs-safe
`evidenceReceipt` blocks, source-local invariant counts, durable query recipes,
and explicit `doNotCite` lists for volatile envelope freshness fields. Pinned by
`ToolHandlerTests.Handle_Analyze_Response_IncludesDocsSafeEvidenceReceipt`,
`InvariantProviderAndHandlerTests.Provider_Audit_ReportsPerSourceCounts`, and
the invariant-check handler receipt assertions.

Summary:
- Lifeblood was semantically correct enough to drive the DAWG LDF refresh, but
  downstream living docs still had to hand-normalize evidence. Twelve active
  docs carried stale invariant-audit totals (`113` / `114`), and six active
  invariant docs had copied a session-local Lifeblood generation label from an
  older run.
- The friction is not a new analysis false-positive class. It is a documentation
  receipt shape problem: agents need a durable, citation-safe Lifeblood evidence
  block for living docs, and clear guidance about which envelope fields are
  session-local diagnostics rather than stable doc facts.
- This overlaps with the existing LB-NICE-010 capability/version endpoint, but
  it is narrower: capability discovery says "what server am I talking to?";
  a docs receipt says "what exact semantic evidence may I cite in a living doc?"

Impact:
- Without a citation-safe receipt, downstream docs copy whatever field is nearby
  (`analysisGeneration`, old invariant totals, stale profile notes), then future
  LDF passes spend time cleaning evidence drift instead of architecture content.
- The invariant-audit count is especially easy to misquote because active docs
  want a single baseline, while humans still need source-path provenance,
  source-local counts, duplicate IDs, parse warnings, and parse-warning file
  lines to know whether a count is complete enough to cite.

Fix shape:
- Add a docs-safe `citation` / `evidenceReceipt` block to `lifeblood_analyze`
  and `lifeblood_invariant_check` responses, or add a small
  `lifeblood_evidence_summary` / `lifeblood_docs_receipt` tool that composes the
  same data after analyze.
- The receipt should include durable fields: Lifeblood semver/build hash, dirty
  flag, workspace root, requested/active define profiles, graph counts, module
  count, file count, violation count, cycle count, invariant total,
  `sourcePaths[]`, per-source invariant counts, duplicate IDs, parse warnings,
  and the exact query recipe used.
- The receipt should explicitly exclude or mark as "do not cite in docs":
  `analysisGeneration`, `stalenessSeconds`, and other session-local freshness
  diagnostics. Those fields remain valuable for tool joins and stale-read
  detection, but they are not living-doc facts.
- Add a ratchet fixture that builds a docs receipt from a multi-source invariant
  fixture and asserts: `sourcePaths[]` is present, per-source counts are present,
  parse warnings retain file/line provenance, and no session-local generation
  label appears in the citation block.

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

## 2026-05-28 - Lifeblood .NET JSON contract hardening

Status: Partially shipped
Type: Improvement
Source: DAWG/Lifeblood .NET platform-feature planning session, 2026-05-28
Workspace: Lifeblood self
Verification: local inspection: all C# projects target `net8.0`; `ToolRegistry`
owns hand-authored anonymous MCP input schemas; baseline shipped in `7123200`
with `schemas/tools/v1/<tool>.schema.json` snapshots plus opt-in duplicate
property rejection behind `LIFEBLOOD_STRICT_JSON`. 2026-05-31 slice adds
`ToolInputContract` projection, `ToolArgumentBinder`, `LIFEBLOOD_JSON_COMPAT`
(`legacy` / `warn` / `strict`), the `LIFEBLOOD_STRICT_JSON` strict alias,
warn-mode argument telemetry, and strict-mode structured tool errors. Pinned by
`ToolArgumentContractTests` and `ToolHandlerTelemetryTests`. Follow-up local
slice adds enum preservation/enforcement and a typed-contract schema round-trip
ratchet across every registered tool. A second local SSoT cleanup routes
`lifeblood_capabilities.featureFlags.summarizeCapableTools` through the typed
contract metadata instead of schema text searching. A third local SSoT cleanup
makes `ToolInputContractCatalog` the primary authoring source: `ToolRegistry`
now owns tool identity/availability/descriptions only, while schemas exposed by
`tools/list` are generated from typed contract metadata. 2026-05-31 follow-up
adds typed request-record binding for the session-mutating high-risk tools
(`lifeblood_analyze`, `lifeblood_compile_check`) through `ToolRequestBinder`,
preserving back-compatible handler defaults such as
`compile_check.staleRefresh=true`. Source-generated context adoption remains
open because the first attempt proved a real diagnostic-parity constraint:
dotnet build runs the System.Text.Json source generator, but Lifeblood's current
Roslyn diagnose path does not, so production code cannot require generated
`JsonSerializerContext.Default` members until generator/parity support exists.
Follow-up benchmark slice adds
`tools/runtime-benchmarks/Lifeblood.JsonParserBenchmark`, a measurement-only
report harness that compares the current string parser with UTF-8 span and
buffered `PipeReader` parser shapes in legacy and strict modes. The production
MCP transport stays on the current line/string path until the report proves
timing/allocation improvement with equivalent diagnostics. `BenchmarkSmokeTests`
ratchets the harness shape, compiles the benchmark source through Roslyn with
the required .NET + ASP.NET Core reference packs, and preserves the
source-generated-context diagnostic-parity gate. Local 2026-05-31 smoke could
not execute the standalone benchmark project in this session because MSBuild
object-file writes for the new project were denied even after redirecting output
to the scratch tree; this entry therefore records the harness as compile-proven
and ready for a writable build host, not as completed adoption.
Remaining open work: run the JSON parser benchmark report on a writable host,
decide `PipeReader` adoption from evidence, and adopt source-generated contexts
only after diagnostic parity can see the generated surface or production no
longer depends on generator-only members.

Summary:
- Newer `System.Text.Json` capabilities are directly relevant to Lifeblood's
  public MCP wire contracts: schema export/validation, stricter reader behavior,
  duplicate-property rejection, and possible `PipeReader` parsing.
- Lifeblood now has per-tool `tools/list` input-schema snapshots, but schemas
  are still authored as anonymous objects in `ToolRegistry`; the typed contract
  projection and binder now exist at the server edge. The typed projection now
  preserves enum values, enforces them in strict mode, and can regenerate the
  current schema surface, while the deeper typed DTO/schema-builder authoring
  source remains open.

Remaining open work:
- Run the JSON parser benchmark on a host that can build the standalone harness,
  then measure `PipeReader` before adopting it. Adopt source-generated JSON
  contexts only after Lifeblood diagnostic parity can see the generated surface
  or the production code path is otherwise proven not to depend on generator-only
  members.

Impact:
- Schema drift is a high-leverage failure class: clients learn tool arguments
  from `tools/list`, and a silent input-schema change can break agents without
  breaking Lifeblood tests.
- Strict parsing catches malformed or duplicated arguments at the protocol
  boundary instead of letting handlers infer partial intent from ambiguous JSON.
- Compatibility matters: some MCP clients may currently send duplicate or extra
  fields. Strict rejection is correct only after a warn/observe phase proves the
  client population is clean.

Fix shape:
- Create a typed schema source model for MCP tool inputs (DTOs or a
  schema-builder abstraction) while preserving the existing `tools/list` wire
  shape.
- Generate or validate per-tool schema snapshots under `schemas/tools/v1/` and
  ratchet every `ToolRegistry.GetTools()` schema against those snapshots.
- Keep three schema-compatibility modes: `legacy` (default, accept today's wire),
  `warn` (accept but emit telemetry events for duplicate / unknown / malformed
  shape risk), and `strict` (reject duplicate properties, missing required
  fields, unknown strict-mode fields).
- Use the `warn` mode to collect evidence before enabling strict mode in
  CI/dogfood; production strict mode stays a separate decision.
- Add source-generated `System.Text.Json` contexts for hot MCP request/response
  DTOs and compare them against reflection-based serialization for throughput,
  allocation pressure, and future AOT friendliness.
- Investigate `PipeReader` only after strict-mode tests pass and measurement
  shows stdio parsing is a real cost or the implementation becomes cleaner.
- No breaking field rename/removal; any break follows
  `docs/SCHEMA_DEPRECATION_POLICY.md`.

## 2026-05-28 - Lifeblood .NET telemetry surface

Status: Shipped
Type: Improvement
Source: DAWG/Lifeblood .NET platform-feature planning session, 2026-05-28
Workspace: Lifeblood self
Verification: local inspection: `ProcessUsageProbe` and `AnalysisUsage` already
provide analyze receipts; baseline shipped in `5cff398` with `ITelemetrySink`,
no-op default, `DotNetDiagnosticsTelemetrySink`, and `ToolHandler` tool-call
operation telemetry. The release-prep atom for fallback/truncation/cache/JSON
dispatch telemetry shipped in `9e4ce90` (`feat(telemetry): record operational
tool events`): events now cover `lifeblood.tool.success_result`,
`lifeblood.tool.error_result`, `lifeblood.tool.response_json`,
`lifeblood.tool.truncated`, `lifeblood.analyze.result`,
`lifeblood.analyze.fallback`, and `lifeblood.cache.lookup`. Pinned by
`ToolHandlerTelemetryTests` and surfaced by `lifeblood_capabilities`
`featureFlags.operationalTelemetryEvents`. 2026-05-31 slice adds
`lifeblood.tool.arguments`, real `lifeblood.analyze.phase` scopes/events from
`GraphSession` phase boundaries, and `allocation.bytes` deltas. Invariant cache
lookup telemetry now emits after releasing the cache lock. Follow-up local
slice adds broader runtime counters where the host supports them:
`lifeblood.process.working_set`, `lifeblood.process.private_bytes`,
`lifeblood.gc.managed_heap`, and `lifeblood.process.thread_count` observable
gauges on the opt-in diagnostics sink, pinned by a real `MeterListener`
measurement test. Cross-process benchmark correlation is covered by the shared
`benchmarkRunId` emitted by the CLI and MCP benchmark reports and injected into
the MCP benchmark child process as `LIFEBLOOD_BENCHMARK_RUN_ID`.

Summary:
- Lifeblood has good user-facing analyze receipts, but not a general operational
  telemetry surface for a future shared server or multi-client host.
- .NET diagnostics primitives map cleanly to the current architecture if they are
  introduced behind an Application-layer port with a no-op default.

Closure:
- Telemetry remains opt-in, Domain/Application still see only the neutral
  `ITelemetrySink` port, and `AnalysisUsage` remains the user-facing evidence
  receipt. Runtime counters are diagnostics-only process gauges.

Impact:
- Without telemetry, performance regressions and multi-user contention will be
  diagnosed from ad hoc wall-clock logs instead of comparable tool/analyze spans
  and counters.
- DAWG-scale workspaces make this especially important because result truncation,
  graph size, fallback mode, profile count, GC deltas, and cache behavior matter
  more than a single success/failure bit.

Fix shape:
- Keep the internal telemetry port with no-op default and the diagnostics
  adapter using `ActivitySource` and `Meter`.
- Instrument analyze wall time, per-phase timing, tool latency, success/error
  counts, fallback mode, graph size, profile count, GC deltas, JSON parse/dispatch
  cost, and truncation events.
- 2026-05-29 closure note: the top-level tool/analyze/cache/truncation atom is
  closed; phase-boundary spans and allocation deltas are still intentionally
  open so the next pass does not overclaim per-phase coverage from handler-level
  events.
- Add phase-level telemetry at the actual phase boundaries; do not claim
  per-phase coverage from the top-level tool seam alone.
- Keep `AnalysisUsage` as the user-facing receipt; telemetry is operational
  signal, not a replacement for response evidence.
- Add sink tests proving every MCP tool emits start/stop/error events without
  requiring external OpenTelemetry infrastructure.

2026-05-31 closure note:
- The `InvariantParseCache<T>` lock/telemetry follow-up is closed: the lookup
  outcome is computed under the private lock, and `lifeblood.cache.lookup` emits
  after the lock is released.

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

## 2026-05-28 - Lifeblood .NET 10 experimental target lane

Status: Shipped
Type: Improvement
Source: DAWG/Lifeblood .NET platform-feature planning session, 2026-05-28
Workspace: Lifeblood self
Verification: baseline lane shipped in
`tools/dotnet-lanes/run-lifeblood-experimental-target.ps1`; local smoke run on
2026-05-28 skipped honestly because only the .NET 8 SDK was installed locally.
2026-05-29 release-prep fix shipped in `b6d13c7`: native `dotnet` stderr is
demoted under Windows PowerShell 5.1 so successful commands are governed by exit
code instead of `NativeCommandError` wrapping. 2026-05-31 implementation replaced
the fragile solution-level `TargetFramework` global-property override with a
temporary copied source tree: the copied solution projects are retargeted to
`net10.0`, checked-in project files remain `net8.0`, root `global.json` is
omitted from the copy, and packages are emitted under `artifacts/` for CI
collection. Verified locally with SDK `10.0.300`: restore, build, full test
suite, CLI pack, and MCP pack all passed. `DotNetLaneScriptTests` pin the honest
skip report, temp-copy retargeting, and no `-p:TargetFramework=$TargetFramework`
override posture. Follow-up local slice adds target-lane evidence receipts:
schema snapshot inventory, parsed experimental test totals, experimental CLI
self-analyze counts, and comparisons against the production `docs/STATUS.md`
test/semantic anchors. The same slice hardens the lane for repeatable local/CI
runs by serializing restore, adding an explicit cached/offline restore switch
(`-RestoreIgnoreFailedSources`), and falling forward to a fresh temp work
directory when the previous experimental tree is locked. Local 2026-05-31 smoke
with SDK `10.0.300` reached restore and emitted host/schema/status receipts, but
could not complete in that session because NuGet source access was refused and
required Roslyn/xUnit packages were not present in the local cache. Follow-up
hardening adds explicit `-PackageSources`, `-DotnetExe`, `-DotnetCliHome`, and
`-WorkDirRoot` controls so the lane can run against local SDK/package/cache
state without mutating production project files. Local net10 receipt
`D:\Projekti\DAWG\codex_tmp\lifeblood-net10-experimental-target.json` passed
with SDK `10.0.300`: restore, build, full test suite
(`1307 passed / 11 skipped / 1318 total`), semantic self-analysis matching the
production anchors (`4316` symbols, `24876` edges, 11 modules, 455 types), CLI
pack, and MCP pack all completed with exit code 0.

Summary:
- The production solution remains pinned to `net8.0`; the experimental lane is
  an external build/test/package probe that retargets a temporary source copy
  when a matching SDK is installed.
- The copied tree omits root `global.json` so the repo SDK pin cannot force the
  experimental lane back to the production SDK.

Closure:
- Current-pass .NET 10 experimental target evidence is complete: the lane uses a
  copied tree, omits root `global.json`, retargets only copied projects, records
  schema/test/semantic receipts, restores from an explicit package source,
  builds/tests/self-analyzes, and packs both tool entry points without changing
  checked-in TFMs or publishing packages. Production migration remains a
  separate runtime/packaging benchmark decision.

Fix shape:
- Keep this lane report-driven and non-production: no project TFM edits, no
  package publishing, and no production migration until tests, schema snapshots,
  semantic graph counts, and benchmark measurements stay stable.
- Build/test/package the solution and the two tool entry points when the SDK is
  present; emit a machine-readable skip report when it is not.
- Use this lane as the prerequisite for packaging experiments and runtime/JIT
  benchmarks on newer SDKs.

## 2026-05-28 - Lifeblood .NET tool packaging/distribution lane

Status: Partially shipped
Type: Improvement
Source: legacy-repo review of the .NET platform-feature plan, 2026-05-28
Workspace: Lifeblood self and released dotnet tool packages
Verification: Lifeblood ships as a dotnet tool; platform-specific tools,
`dotnet tool exec`, `dnx`, CLI schema, self-contained publishing, and AOT
experiments are product-relevant upgrade surfaces independent of language syntax
features. Baseline local packaging smoke shipped in
`tools/dotnet-lanes/run-lifeblood-tool-packaging.ps1`; local run packed both
tool entry points, installed them from the local package folder, verified
`lifeblood --help`, and verified `lifeblood-mcp` starts and exits cleanly when
stdin closes. 2026-05-31 slice extends the packaging report with optional
`dotnet tool exec` and `dnx` help smokes when the local SDK/tooling supports
those entrypoints; unsupported hosts record honest skipped steps instead of
failing or pretending coverage. Follow-up local slice adds a CLI help-contract
validation step plus opt-in report-only publish experiments for
framework-dependent, self-contained, trimmed, and AOT publish shapes under
`artifacts/tool-packaging/<tfm>/publish-experiments`; MCP trim/AOT paths are
recorded as intentional skips until Roslyn compatibility evidence says
otherwise. Pinned by `DotNetLaneScriptTests`.

Summary:
- Lifeblood is a tool product. Runtime retargeting is not enough; packaging,
  install, execution, and cross-platform startup behavior are part of the
  product surface.

Remaining open work:
- Run the report-only publish experiments on supported RID/SDK hosts, compare
  outputs across platforms, and decide whether any platform-specific package
  shape graduates from experiment to supported lane.

Fix shape:
- Add an experimental packaging lane that builds and smoke-tests the current
  global tool package, platform-specific tool packages when SDK support is
  available, and `dotnet tool exec` / `dnx` invocation forms.
- Keep the baseline lane local-only: pack, local install, command smoke, report;
  never publish from this script.
- Include CLI schema/help output checks so packaging changes cannot drift the
  user-facing command surface.
- Compare framework-dependent, self-contained, trimmed, and AOT-friendly builds
  only where they make sense for the MCP server and CLI; do not sacrifice Roslyn
  compatibility for theoretical startup wins.

## 2026-05-28 - Lifeblood .NET concurrency prep for shared server

Status: Shipped
Type: Improvement
Source: DAWG/Lifeblood .NET platform-feature planning session, 2026-05-28
Workspace: Lifeblood self
Verification: local inspection: current MCP server is a stdio process with a
retained `GraphSession`; no `System.Threading.Lock` adoption exists; shared
multi-client daemon/server work is not yet implemented. Baseline audit shipped
in `docs/plans/dotnet-concurrency-prep-2026-05-28.md`, backed by Lifeblood
self analyze plus targeted dependency/file-impact queries on `GraphSession`,
`WorkspaceSession`, invariant parse cache, metadata reference cache, usage
probe, and telemetry sink. 2026-05-31 slice adds `GraphSessionGate` /
`ISessionGate` in `Lifeblood.Server.Mcp`: read-side calls share a read gate,
while `lifeblood_analyze` and `lifeblood_compile_check` take the write gate
because they can replace or refresh the retained session. Pinned by
`GraphSessionGateTests` and `ToolHandlerTelemetryTests`. Follow-up local slice
adds concurrent gate stress for reader-before-writer, writer-before-reader,
writer serialization, and explicit `lifeblood_compile_check` write-gate routing.

Summary:
- Newer locking primitives are potentially useful, but only around real shared
  state: retained graph/session access, compilation host refresh, incremental
  cache transitions, telemetry counters, and future multi-client scheduling.
- This is preparation work, not permission to build a shared daemon prematurely.

Closure:
- Current-pass concurrency prep is complete: retained-session replacement is
  gated at `Lifeblood.Server.Mcp`, read-side calls share the read gate,
  session-mutating calls use the write gate, and stress ratchets pin
  reader/writer exclusion plus writer serialization. Future shared-server
  transport work gets a new entry when that transport exists; no shared daemon is
  part of this pass.

Impact:
- If Lifeblood grows from per-client stdio processes into a shared server, lock
  placement and session isolation will determine whether concurrent agents can
  analyze/query safely.
- Premature lock rewrites on `net8.0` add complexity without benefit because
  newer primitives are not available on the current production target.

Fix shape:
- Audit current mutable shared-state sites and document which are per-process,
  per-session, per-workspace, or future shared-server concerns.
- Keep concurrency policy at the host/session edge: Server.Mcp or a future host
  adapter serializes session replacement; Domain and Application remain free of
  daemon scheduling concerns.
- Keep ordinary locks on production `net8.0`; adopt `System.Threading.Lock` only
  in an experimental newer-target lane or after a measured retarget is approved.
- Add focused tests around graph replacement, incremental refresh, and concurrent
  read-side tool calls before any daemon/shared-host work starts.
- Do not build a shared Lifeblood daemon as part of this entry; remove obvious
  lock-risk and document the future shape.

2026-05-31 closure note:
- First gate is intentionally at `Lifeblood.Server.Mcp`, not Domain/Application.
  The daemon remains out of scope. Future work can widen the policy only after
  a concrete shared-host transport exists.

## 2026-05-28 - Lifeblood .NET Runtime Async compatibility

Status: Shipped
Type: Improvement
Source: DAWG/Lifeblood .NET platform-feature planning session, 2026-05-28
Workspace: Lifeblood self and user-analyzed projects
Verification: Lifeblood production target remains `net8.0`; compatibility
awareness shipped with csproj `<Features>` discovery,
`ModuleInfo.CompilerFeatures`, `CSharpParseOptions.WithFeatures` thread-through,
profile-clone preservation, and focused fixtures in
`CsprojCompilationFactsTests`. 2026-05-31 slice adds feature-bearing fixtures
for `diagnose`, compile-check file mode, and compile-check snippet mode in
`CompileCheckParseOptionsParityTests`. Remaining local slice adds
`tools/runtime-benchmarks/run-lifeblood-runtime-async-benchmark.ps1`: it injects
`<Features>runtime-async=on</Features>` only into the temporary copied
experimental tree via the `run-lifeblood-experimental-target.ps1`
`-CompilerFeatures` hook, measures CLI workloads, and delegates retained MCP
read-side measurement to the MCP GC benchmark harness when a supporting SDK is
available. Local side-by-side .NET 11 preview SDK
`11.0.100-preview.4.26230.115` was installed under
`D:\Projekti\DAWG\codex_tmp\dotnet-11`; the Runtime Async lane then restored,
built, and ran the copied `net11.0` tree with `runtime-async=on`, producing
`1307 passed / 11 skipped / 1318 total`, semantic self-analysis counts matching
the production anchors (`4316` symbols, `24876` edges, 11 modules, 455 types),
and CLI workload receipts for self-analyze and help. The retained MCP read-side
measurement also completed on the `net11.0` Runtime Async server: all three GC
configs analyzed successfully, all retained read-side dispatches completed, and
workstation read-side latencies were capabilities 139 ms, context 53 ms, cycles
34 ms, and dead-code summarize 20 ms. The real compatibility defect found by
this lane was fixed: synthetic implicit-global-usings trees now parse with the
module's feature-bearing `CSharpParseOptions`; stdio tests use the current
`DOTNET_HOST_PATH`, and the JSON parser benchmark compile probe locates the
active SDK reference pack. Production Runtime Async adoption remains a future
runtime/platform decision, not part of this shipped compatibility entry.

Summary:
- Runtime Async is preview/experimental until the SDK/runtime is available and
  stable. Lifeblood should treat it as compatibility awareness first, not as a
  production-server feature.
- The first Lifeblood requirement is Roslyn parity for projects that opt in:
  analyze, diagnose, and compile-check should not drift because a project carries
  a new `<Features>` marker. Analyze parse-option preservation was already
  pinned; diagnose and compile-check compatibility fixtures are now covered.

Closure:
- Current-pass Runtime Async compatibility is complete: Lifeblood preserves
  feature-bearing parse options through analyze, diagnose, compile-check, and
  synthetic module trees; the opt-in benchmark lane runs against a local
  side-by-side .NET 11 SDK and records test, semantic, CLI, and retained MCP
  evidence. Future production adoption gets a new entry when the platform
  feature is stable enough to consider for Lifeblood itself.

Impact:
- Users may analyze projects that enable Runtime Async before Lifeblood itself
  should run that way. Lifeblood needs to preserve project parse/compilation
  options accurately even when the feature is only metadata to the current host.
- If Runtime Async eventually reduces allocation pressure, the benefit must be
  proven on MCP request loops and Roslyn-heavy Lifeblood workloads, not assumed.

Fix shape:
- Keep project-option awareness for `<Features>runtime-async=on</Features>`
  on the csproj-driven compilation-facts seam: discover once, store on
  `ModuleInfo.CompilerFeatures`, preserve through define-profile cloning, and
  pass into `CSharpParseOptions.WithFeatures`.
- Diagnose and compile-check fixtures are now present so replacement/snippet
  trees also prove feature-bearing project compatibility.
- Promote those compile-parity traps ahead of enabling Runtime Async in
  Lifeblood itself: user-project analysis compatibility is earlier than server
  runtime experimentation.
- Add an opt-in experimental Lifeblood server build lane with Runtime Async only
  when a supporting SDK/runtime is installed.
- Benchmark async-heavy MCP request loops, JSON dispatch, and retained-graph tool
  calls for latency and allocation pressure before any production adoption.
- Do not ship production Runtime Async until the platform feature is stable and
  the benchmark lane shows real improvement without semantic or schema drift.

## Shipped - Lifeblood v0.7.3

Status: Shipped
Type: Shipped
Source: 2026-05-11 DAWG field report, Lifeblood changelog v0.7.3
Workspace: DAWG and Lifeblood self
Verification: `D:/Projekti/Lifeblood/CHANGELOG.md` entry `[0.7.3] - 2026-05-12`

Summary:
- `v0.7.3` closed the top P1 asks from the 2026-05-11 DAWG field report and
  one high-severity correctness bug discovered during the same dogfood wave.

Shipped items:

- `LB-BUG-020` / `INV-INCREMENTAL-XREF-001`: fixed silent cross-module edge
  loss under incremental analyze. This was dangerous because it could make live
  symbols appear dead after an incremental run.
- `INV-EDGE-CALLSITE-001`: added structured `CallSite` provenance to
  expression-derived edges.
- Dependency/dependant MCP responses now expose edge kind plus nullable
  `callSite`.
- `INV-RESOLVE-MEMBER-001`: added `lifeblood_resolve_member` for type-scoped
  member lookup and overload disambiguation.
- `INV-BLAST-RADIUS-GROUP-001`: added `lifeblood_blast_radius groupBy` for
  production/test/editor/generated buckets and per-module counts.
- Added CLI `verify --incremental`, CLI `export --out`, and honest rejection for
  single-shot CLI `analyze --incremental`.
- Generalized shipped docs and examples so Lifeblood no longer reads as coupled
  to DAWG-specific symbols or paths.

Measured v0.7.3 state from Lifeblood docs:

- Tests: 751 -> 776.
- MCP tools: 25 -> 26.
- Read-side tools: 15 -> 16.
- CallSite provenance observed on 133,523 / 219,548 edges (60.8 percent) in a
  real Unity workspace; remaining edges were graph-derived and had no single
  source authoring location by design.

## Open - Lifeblood v0.7.3 Follow-Ups

### LB-TRACK-20260514-001 - `lifeblood_diagnose` misresolves `System.Math`

Status: Shipped v0.7.4 - **DAWG verified 2026-05-14**
Type: Bug
Source: `docs/invariants/eternal-arch-audit-2026-05-12.md`,
`D:/Projekti/Lifeblood/docs/IMPROVEMENT_INBOX.md` (`LB-INBOX-007`)
Workspace: DAWG, Lifeblood self
Verification: Lifeblood `e1acbe3` (`fix(refs): mirror csproj-tool closure
semantics per module (LB-TRACK-001)`), shipped in v0.7.4 and carried
into v0.7.5; 893 / 893 green on Lifeblood self post-v0.7.3 audit.
**DAWG roundtrip verified 2026-05-14 by external pre-release audit:
`lifeblood_diagnose` against DAWG returned 0 errors / 0 `System.Math`
/ 0 CS0234 namespace-collision findings** — the fix shipped end-to-end
through the post-redeploy MCP and survives. INV-INBOX-007 marked
SHIPPED in `D:/Projekti/Lifeblood/docs/IMPROVEMENT_INBOX.md` with
Resolution paragraph preserving original Observed body for
regression-trace.

Root cause (different from the original framing):
- Not Roslyn binding precedence — the binding under Lifeblood was always
  spec-correct (C# §11.7.2 namespace-or-type lookup: a sibling-namespace
  child of the parent namespace shadows an outer-using BCL type). The actual
  fault was Lifeblood's reference graph leaking a sibling-namespace
  assembly onto a Unity-asmdef module's compile classpath even when the
  asmdef declared no such reference. Once `Nebulae.Math.dll` was visible to
  the Adapter compilation, bare `Math.X` legitimately bound to the
  workspace namespace, producing CS0234 that Unity itself ships clean.

Fix shape (shipped on `main`):
- Compilation reference closure is now a discovered module fact
  (`ReferenceClosureMode` + `ModuleInfo.ReferenceClosure`). Old-format
  MSBuild 2003-schema csprojs (Unity asmdef generators) → `DirectOnly`;
  SDK-style → `Transitive` (default, preserves pre-fix behavior for
  Lifeblood self + NuGet workspaces).
- `RoslynModuleDiscovery.ParseProject` reads root xmlns + `Sdk` attribute;
  `ModuleCompilationBuilder.ProcessInOrder` branches dep-ref resolution
  accordingly.
- `INV-MODULE-REFS-001` added in `docs/invariants/csharp-adapter.md`;
  `INV-CANONICAL-001` tightened to scope-to-Transitive.
- Eternal, dynamic: any future BCL-vs-workspace-namespace collision
  (Color, Time, Random, ...) is covered by the same closure-mode
  branch — no per-name special casing.

### LB-TRACK-20260514-002 - Diagnostic envelope needs preprocessor context

Status: Shipped v0.7.4 - **DAWG verified 2026-05-14**
Type: Improvement
Source: `D:/Projekti/Lifeblood/docs/IMPROVEMENT_INBOX.md` (`LB-INBOX-008`)
Workspace: DAWG, Lifeblood self
Verification: Lifeblood `b520df8` (`feat(diagnose+compile_check):
preprocessor scope on envelopes (LB-TRACK-002)`), shipped in v0.7.4
and carried into v0.7.5; 893 / 893 green on Lifeblood self
post-v0.7.3 audit. DAWG roundtrip verified 2026-05-14: `definesActive`
+ `resolvedModule` surface on `lifeblood_diagnose` / `lifeblood_compile_check`
responses against DAWG (130+ Unity defines including UNITY_EDITOR
correctly bound). INV closed-out via `INV-DIAGNOSTIC-ENVELOPE-DEFINES-001`
in Lifeblood `docs/invariants/tools.md`.

Summary:
- Diagnostic and compile-check responses did not report which preprocessor
  symbols were active when Lifeblood bound the file.

Impact:
- A caller could not tell an `UNITY_EDITOR`-gated finding apart from a
  release-build risk without re-running the host under a different define
  set.

Fix shape (shipped on `main`):
- New domain type `DiagnosticsReport { Diagnostics, DefinesActive,
  ResolvedModule }` + `DefinesActive` on `CompileCheckResult`. Port adds
  `ICompilationHost.GetDiagnosticsReport(DiagnosticsRequest)`. Adapter's
  private `CollectDefines(string? moduleName)` walks each compilation's
  first-tree `CSharpParseOptions.PreprocessorSymbolNames`, sorted
  ASCII-ordinal and deduplicated. Project-wide scope returns the union
  across every loaded compilation.
- `lifeblood_diagnose` + `lifeblood_compile_check` wire shape gains
  `definesActive[]` + `resolvedModule` (additive — no field removals).
- Eternal-seam cleanup folded in: `ResolveCompilation` promoted to return
  `(string? Module, CSharpCompilation? Compilation)` so
  `CompileCheckSnippet` no longer carries an inline
  `ContainsKey`/`Keys.FirstOrDefault` duplication; `GetDiagnosticsReport`
  reuses the existing `FindOwningCompilation` seam instead of an inline
  `goto resolved` walker. One contract, one resolver.
- `INV-DIAGNOSTIC-ENVELOPE-DEFINES-001` pinned in
  `docs/invariants/tools.md`; pinned by `DiagnosticEnvelopeDefinesTests`
  (7-fact harness: file-scope owning-module defines, module-scope
  defines, project-wide union, sort+dedup invariant, unknown-module
  empty-not-crash, compile-check file owning-module defines,
  compile-check snippet resolved-module defines).

### LB-TRACK-20260514-003 - Enum-member reference coverage is inconclusive

Status: Shipped v0.7.4 - **DAWG shape reverified 2026-05-19 prerelease check**
Type: Improvement
Source: `D:/Projekti/Lifeblood/docs/IMPROVEMENT_INBOX.md` (`LB-INBOX-009`)
Workspace: DAWG, Lifeblood self
Verification: Lifeblood `f288b7c` (`feat(enum_coverage): per-member
reference coverage tool (LB-TRACK-003)`), shipped in v0.7.4 and
carried into v0.7.5; 821 / 821 green (+8 over the post-LB-TRACK-008
baseline of 813); `lifeblood verify --incremental` self-passes
(2686 / 13356 after the wave, identical full vs incremental). DAWG
shape reverified 2026-05-19 against `FeatureId`: 33 members,
`unproducedCount: 0`, `unreferencedCount: 0`, and the later
`dispatchTableReferenceCount` S4 field correctly distinguishes static-table
routing for the formerly confusing members.

Summary:
- Enum members are first-class symbols after earlier fixes, but
  reference queries did not classify useful coverage shapes
  (production vs comparison vs switch-pattern consumption).

Impact:
- State-machine and telemetry enums could drift silently: a value
  exists on the type, is checked-for, but is never produced — and
  `find_references` returned hits for consumer sites alongside
  production sites without distinguishing them.

Fix shape (shipped on `main`):
- New `lifeblood_enum_coverage` MCP tool — per-member reference
  coverage with `producedCount` / `consumedComparisonCount` /
  `consumedSwitchCount` plus convenience flags `isUnproduced` and
  `isUnreferenced`. Top-level `unproducedCount` + `unreferencedCount`
  summarize the response.
- Classifier walks the parent-syntax chain from each enum-member
  reference site. Production: assignment RHS / EqualsValueClause /
  ReturnStatement / YieldStatement / ArrowExpressionClause /
  ArgumentSyntax. Comparison: BinaryExpression with
  EqualsExpression / NotEqualsExpression / LessThan / LessThanOrEqual
  / GreaterThan / GreaterThanOrEqual kinds. Switch: IsExpression
  (legacy parse), IsPatternExpression, ConstantPattern,
  CaseSwitchLabel, CasePatternSwitchLabel, SwitchExpressionArm.
- Eternal-fix folded in: skip-inner-of-qualified-reference guard
  covers both `MemberAccessExpressionSyntax.Name` and
  `QualifiedNameSyntax.Right` so `m is Mode.A` does not double-count
  (Roslyn parses `Mode.A` in `is`-RHS position as a QualifiedNameSyntax
  whose inner identifier also binds to the field).
- Single O(total_nodes) pass per compilation — cheaper than calling
  FindReferences per-member on big enums.
- Tool count ratchet bumped 26 → 27; docs/STATUS.md banner +
  per-server detail line + `<!-- toolCount: 27 -->` anchor updated;
  `ToolRegistry_Returns27Tools` fact asserts
  `Contains("lifeblood_enum_coverage")` so the schema can't drift.
- `INV-ENUM-COVERAGE-001` pinned in `docs/invariants/tools.md`;
  pinned by `EnumCoverageTests` (8 facts: non-enum returns null,
  unknown returns null, empty-enum empty-members, production-sites
  counted, comparison counted, switch+pattern counted,
  unproduced-flagged-and-counted dogfood scenario,
  members-in-declaration-order wire stability).

### LB-TRACK-20260514-004 - `dead_code` findings need first-response triage fields

Status: Shipped v0.7.4 - **DAWG shape reverified 2026-05-19 prerelease check**
Type: Improvement
Source: `docs/invariants/eternal-arch-audit-2026-05-12.md`
Workspace: DAWG, Lifeblood self
Verification: Lifeblood `68fd4a2` (`feat(dead_code): triage fields —
directDependants/bucket/declarationOnly (LB-TRACK-004)`), shipped in
v0.7.4 and carried into v0.7.5; 798 / 798 green (+16 over the v0.7.3
baseline of 776 + 6 already-landed inter-stage tests). DAWG shape
reverified 2026-05-19: property-only `dead_code` summary carries
`directDependants`, `bucket`, `declarationOnly`, and
`sameClassConsumerCount`; `bucketBreakdown` sums to 62 Production findings
after F1d closes the property-read graph gap.

Summary:
- `lifeblood_dead_code` is useful, but the first response did not carry enough
  triage shape for large Unity workspaces.

Impact:
- The user had to pair every candidate with `find_references`,
  `blast_radius`, source inspection, and tests before it became actionable.

Fix shape (shipped on `main`):
- New `DeadCodeBucket` enum (Production / Test / Editor / Generated)
  on a segment-aware path classifier — splits the normalized POSIX
  path on `/` and matches whole segments, so a root-level `obj/`
  classifies identically to nested `/obj/` and a filename containing
  the word "test" does not accidentally trigger the Test bucket.
- Precedence Generated → Test → Editor → Production (most-authoritative
  signal wins). Test beats Editor by design: a fixture under
  `Tests/Editor/Foo.cs` is a test fixture (Tests root + filename
  convention) not an Editor utility — the Editor subfolder there is
  just NUnit PlayMode assembly placement. Generated wins over
  everything because build artifacts (`obj/`, `bin/`) and codegen
  (`*.Generated.*`, `/generated/`) are never a refactor target.
- `DirectDependants` on every `DeadCodeResult` — 0 for every classic
  finding (analyzer drops symbols with non-Contains incoming edges
  via `HasIncomingReference`) but kept on the wire as forward-compatible
  signal for future relaxed-criteria modes.
- `DeclarationOnly` true iff `Symbol.IsAbstract` is set (interface
  members, abstract methods, abstract types).
- `bucketBreakdown` count map on the response alongside the existing
  `kindBreakdown` so a caller can fold tails in one pass.
- `INV-DEADCODE-TRIAGE-001` pinned in `docs/invariants/tools.md`;
  pinned by `DeadCodeTriageFieldsTests` (12-case path-classification
  theory + DeclarationOnly + DirectDependants + per-bucket FindDeadCode
  invariant).

### LB-TRACK-20260514-005 - Static table extraction for declarative architecture

Status: Shipped v0.7.5 - **DAWG dogfood verified 2026-05-14**
Type: Improvement
Source: `.claude/devmemory/lifeblood-field-report-2026-05-11.md`
Workspace: DAWG, Lifeblood self
Verification: Lifeblood `e267504` (`feat(mcp): lifeblood_static_tables tool —
registry + handler + ratchet sync`) closes the 14-commit feature chain
(`61d39ce` Domain DTOs → `51531ce` port stub → `dd8cbfc` real extractor →
`4cb1a6d` literal cells → `d3eaec7` constructor row cells → `4291374` enum
cells → `42b5679` method-group cells → `f56704b` field-reference + nested
array → `e6a9b53` Computed + truncation envelope → `e267504` MCP registry +
handler → `99959c6` INV body → `dbef2d6` doc sweep → `dfa7a46` default-arg
provenance fix → `210c38c` INV closure ref + drift ratchets); v1.1 add-on
shipped at Lifeblood `df4f91c` (`feat(static-tables): MethodGroup cells
carry MethodReturnFlagIds[] union`). DAWG-side live-MCP dogfood against
`KernelCapabilityTable.Features` end-to-end: 32 rows, 9 `MethodGroup` cells
with `MethodReturnFlagIds` populated for 6 direct-return delegates, null
for 3 helper-delegating delegates per design.

Summary:
- Lifeblood sees symbols and edges, but it does not yet extract structured facts
  from static declarative tables.

Impact:
- Table-driven systems can drift between row claims, writeback fields, comments,
  and predicates without a direct Lifeblood query exposing the mismatch.

Fix shape (shipped on `main`):
- v1.0: `lifeblood_static_tables` MCP tool + Domain DTOs (`StaticTableReport`
  / `StaticTable` / `StaticTableRow` / `StaticTableCell` / `StaticTableValue`)
  + port `ICompilationHost.GetStaticTables` + Roslyn adapter
  `RoslynStaticTableExtractor` + `WriteToolHandler.HandleStaticTables` +
  name-leakage ratchet `StaticTableNameLeakageTests`. Walks every `static`
  field / property whose initializer Roslyn surfaces as `IArrayCreationOperation`
  / `ICollectionExpressionOperation` / single `IObjectCreationOperation`
  and emits one table per match with row + cell facts sourced from
  `SemanticModel.GetOperation` only — no regex, no syntax-text parsing, no
  static-constructor execution. Cell-kind taxonomy: `Null` / `Bool` /
  `String` / `Number` / `EnumMember` / `EnumFlags` / `MethodGroup` /
  `FieldReference` / `Array` / `Computed`. Generic by contract — the
  name-leakage ratchet scans extractor + DTOs for consumer-domain identifier
  leakage and fails the build on any match. Pinned by 33 facts in
  `StaticTableExtractorTests` + `INV-EXTRACT-STATIC-TABLES-001`.
- v1.1: `MethodGroup` cells additively carry `MethodReturnFlagIds[]?` — the
  deduplicated ordinal-sorted union of enum-flag member ids reachable in any
  `return` position of the delegate target's body when the target has a
  source decl in the same compilation. Null in four load-bearing cases
  (compiled-metadata BCL target, cross-compilation target, body has no
  `IOperation`, every return classifies as `Computed`). `ReturnFlagCollector`
  overrides `VisitAnonymousFunction` / `VisitLocalFunction` so nested
  lambdas / local functions don't bleed. Wire-shape additive — existing
  `MethodGroupId`-only callers stay byte-stable. 7 new facts + 1 augmented
  null-pin on the existing MethodGroup-cell test. Pinned by
  `INV-METHOD-FLAG-SUMMARY-001`.

### LB-TRACK-20260514-006 - Table-to-predicate drift checks

Status: Shipped v0.7.5 (doc atom) - **closed by INV pin, zero new tool code**
Type: Improvement → Docs (re-scoped during design)
Source: `.claude/devmemory/lifeblood-field-report-2026-05-11.md`
Workspace: DAWG, Lifeblood self
Verification: Lifeblood `4f227c7` (`docs(static-tables):
INV-FLAG-COVERAGE-COMPOSITION-001 — recipe pin, no new tool`). DAWG-side
live-MCP dogfood: 9/9 identical flag-member-id sets on both surfaces
(`MethodReturnFlagIds` v1.1 field vs `lifeblood_dependencies` outbound
`References` edges) for a directly-returning method; for a helper-delegating
method `MethodReturnFlagIds` nulled correctly (body has no direct-return
enum-flag literal) while `lifeblood_dependencies` surfaced 1 partial
flag-ref + 1 `Calls` edge to the helper — strictly more signal, not less.
Build clean (0 warnings, 0 errors). StaticTable test filter green (47/47).

Summary:
- A repeated bug pattern is: a capability row becomes implemented and writeback
  backed, but a downstream predicate or waterfall still rejects it.

Impact:
- Grep and basic symbol edges do not catch this contract asymmetry.

Re-scope during design (no-yes-man pushback):
- The initial 006-A memo proposed `lifeblood_table_predicate_admission` with
  three verdicts (`Admitted` / `Unreachable` / `Inconclusive`) and ~300 LOC.
  Honest re-read flagged the proposal as DAWG-vocabulary leak into a generic
  Roslyn tool: "predicate" / "admission" / "Admitted" are consumer-domain
  semantics, not Lifeblood-domain semantics. The user's "no yes-man, eternal
  solution, no hardcoding" cadence applied — re-cast 006 to the eternal shape.

Fix shape (shipped on `main`):
- `INV-FLAG-COVERAGE-COMPOSITION-001` pinned in `docs/invariants/tools.md`
  under "Static Table Extraction (post-v0.7.4)". The "does method M reference
  every enum-flag in row R's flag-cell?" join composes from
  `lifeblood_static_tables` (row side, `EnumFlagMemberIds` on `EnumFlags`
  cells or `MethodReturnFlagIds` on `MethodGroup` cells) +
  `lifeblood_dependencies` (method side, outbound `References` edges to
  enum-flag member fields) with client-side set-relation. The three coverage
  relations (subset / partial / disjoint) are pure set-arithmetic over two
  flag-id sets the existing tools already emit; what those relations *mean*
  in a caller's domain (admission gate, authority report, rule-check) is a
  CONSUMER concern, not a Lifeblood concern. Lifeblood MUST NOT ship a
  verdict-shaped wire tool on this question — explicitly forbids `admitted`
  / `admission` / `inconclusive` / `denied` names in tool-shape position.
- `lifeblood_dependencies` strictly supersets `MethodReturnFlagIds` for
  three cases the v1.1 field nulls: cross-compilation targets (graph edges
  cross module boundaries while the v1.1 walker bails), body-position
  references beyond `return` (inspection guards, switch arms, ternary
  conditions), transitive walks through helper-call delegation (caller
  follows the `Calls` edge one level deeper for closure).
- Doc atom only: `<remarks>` see-also on `StaticTableValue.MethodReturnFlagIds`
  xmldoc + new INV body + CHANGELOG `[Unreleased]` entry. No new tool, no
  new port, no new test file — composition rests on already-pinned surfaces
  (`INV-METHOD-FLAG-SUMMARY-001` row-side emission +
  `EdgeCallSiteTests.Extract_FieldReference_AttachesCallSite` method-side
  `References`-edge emission for field access).

### LB-TRACK-20260514-007 - Test impact suggestions

Status: Shipped v0.7.4 - **Lifeblood self ratcheted; DAWG recall heuristic intentionally unshipped**
Type: Improvement
Source: `.claude/devmemory/lifeblood-field-report-2026-05-11.md`
Workspace: DAWG, Lifeblood self
Verification: Lifeblood `f204032` (`feat(test_impact): which tests
transitively depend on a target (LB-TRACK-007)`), shipped in v0.7.4
and carried into v0.7.5; 830 / 830 green (+9 over the post-LB-TRACK-003
baseline of 821); `lifeblood verify --incremental` self-passes
(2686 / 13356, identical full vs incremental). Lifeblood self recall
ratchet landed in `4ff06bd` (`TestImpactRecallAnchorTests`) after the
LB-TRACK-009/010 graph-edge fixes. The DAWG-side Advisory heuristic layer
was intentionally not shipped per the 2026-05-15 decision: measurement first,
design only if recall drops below 95% after redeploy.

Summary:
- Lifeblood could identify affected symbols via `blast_radius` but
  did not yet bridge from "what changed" to "which tests prove it" —
  callers paired blast-radius with manual test discovery, walked back
  to containing types, and composed `dotnet test --filter` strings by
  hand.

Impact:
- Test-selection overhead on every refactor / extraction wave; high
  enough cost that callers sometimes skip the verification step
  entirely.

Fix shape (shipped on `main`):
- New `lifeblood_test_impact` MCP tool — BFS over incoming
  non-Contains edges with per-symbol minimum-distance tracking;
  classifies each visited Method symbol via the extractor-recorded
  `Properties["attributes"]` string. Test-case attribute set:
  `Test` / `TestCase` / `TestCaseSource` / `Theory` / `UnityTest` /
  `Fact` (NUnit + Unity Test Framework + xUnit). Lifecycle attributes
  (`SetUp` / `OneTimeSetUp` / `TearDown` / `OneTimeTearDown` /
  `UnitySetUp` / `UnityTearDown`) intentionally EXCLUDED.
- Affected test methods folded by containing type; per-class
  `minDistance` is the smallest distance across the class's affected
  methods, mapped to confidence Direct (1) / OneHop (2) /
  Transitive (3+).
- Wire shape: `target`, `targetKind` (Symbol or File),
  `totalTestMethodCount`, `directTestClassCount`,
  `affectedTestClassCount`, `affectedTestClasses[]` sorted by
  ascending distance then by qualified name for byte-stable output,
  plus `recommendedFilters[]` — pre-composed
  `FullyQualifiedName~<class>` strings the caller pastes directly
  into `dotnet test --filter`.
- Disambiguation: `target` starting with a canonical-id prefix
  routes through `ISymbolResolver`; otherwise treated as a file path
  (multi-source BFS over every symbol declared in the file).
- Analyzer lives in `Lifeblood.Analysis/TestImpactAnalyzer.cs` —
  pure graph read (INV-ANALYSIS-002), no Roslyn compilation needed,
  same layer as `BlastRadiusAnalyzer` + `CircularDependencyDetector`.
- Test-case attribute set is duplicated relative to
  `UnityReachabilityAdapter`'s entry-point set (intentional —
  UnityReachability uses the broader set for dead-code
  dispatch-entrypoint detection; this analyzer needs only
  assertion-bearing methods). Consolidation would be its own atom if
  the two policies ever diverge.
- Tool count ratchet bumped 27 → 28; docs/STATUS.md banner +
  per-server detail line + `<!-- toolCount: 28 -->` anchor updated;
  `ToolRegistry_Returns28Tools` fact asserts
  `Contains("lifeblood_test_impact")`.
- `INV-TEST-IMPACT-001` pinned in `docs/invariants/tools.md`; pinned
  by `TestImpactAnalyzerTests` (9 facts: no-tests empty report,
  direct-caller Direct, one-hop OneHop, tests-grouped-by-containing-
  type, non-tests excluded, lifecycle attributes excluded, UnityTest
  + xUnit Fact recognized, recommended-filters-in-distance-order,
  file-mode multi-source BFS).

### LB-TRACK-20260514-008 - Dependency cycle taxonomy for large Unity workspaces

Status: Shipped v0.7.4 - **DAWG reverified 2026-05-19 prerelease check**
Type: UX
Source: `docs/invariants/eternal-arch-audit-2026-05-12.md`
Workspace: DAWG, Lifeblood self
Verification: Lifeblood `d5482a3` (`feat(cycles): taxonomy buckets on
lifeblood_cycles (LB-TRACK-008)`), shipped in v0.7.4 and carried into
v0.7.5; 813 / 813 green (+8 over the post-LB-TRACK-002 baseline of
805); `lifeblood verify --incremental` self-passes (2686 / 13356
across the wave). DAWG reverified 2026-05-19: `lifeblood_cycles`
returns classified `descriptors[]`, `previewClassified[]`, and
`bucketBreakdown` (`PartialClassCluster: 65`, `LikelyRealLoop: 26` on the
current 91-cycle graph).

Summary:
- Pre-fix `lifeblood_cycles` returned a flat `string[][]` of every
  Tarjan SCC. On real Unity workspaces (DAWG: 123 cycles across 809
  symbols) the lion's share are not architectural loops at all —
  build artifacts, source-generator output, and intra-type mutual
  recursion drown the signal.

Impact:
- Large cycle reports became backlog mass instead of a prioritized
  action list. Caller had to walk each cycle's members and inspect
  file paths / containing types by hand to triage.

Fix shape (shipped on `main`):
- `CircularDependencyDetector.DetectClassified(graph)` reuses the
  existing Tarjan SCC pass via `Detect(graph)` so cycle membership
  is byte-identical to the legacy shape — bucket is additive
  metadata, never a member-set change. Legacy `Detect → string[][]`
  overload kept for its five existing call-sites (`AnalysisPipeline`,
  three test fixtures, the context-pack assembler).
- Three buckets, precedence (most authoritative wins):
  1. `GeneratedOrStaticAnalysisArtifact` — any participating
     symbol's file path matches `*.g.cs`, `*.Generated.*`, or a
     path segment named `obj` / `bin` / `generated`.
  2. `PartialClassCluster` — every participating method / property
     / field walks up the Contains reverse-chain to the same
     enclosing Type symbol. Captures intra-type mutual recursion /
     method-pair cycles (partial classes manifest the same way at
     SCC level because Roslyn surfaces them as one type with
     members spread across files).
  3. `LikelyRealLoop` — everything else; the actual
     cross-type / cross-module architectural-backlog cycles.
- Wire-shape additive: response now carries `descriptors[]
  { symbols, bucket }` and `bucketBreakdown` alongside legacy
  `cycles[][]`; summarize mode adds `previewClassified[]` alongside
  `preview[]`.
- Domain: `CycleDescriptor { Symbols, Bucket }` + `CycleBucket` enum
  in `Lifeblood.Domain.Results`.
- Named follow-up: three slightly drifted "is this a generated /
  test / editor path?" classifiers in-tree today
  (`LifebloodDeadCodeAnalyzer.ClassifyBucket` segment-aware,
  `LifebloodMcpProvider.ClassifyBucket` substring-based with `.g.cs`
  support, this detector's Generated-tier subset). Consolidating
  them into `Lifeblood.Analysis.PathBucketClassifier` is its own
  atom.
- `INV-CYCLE-TAXONOMY-001` pinned in `docs/invariants/tools.md`;
  pinned by `CycleTaxonomyTests` (8 facts: cross-type LikelyReal
  default, obj-segment Generated, `.Generated.` filename Generated,
  `.g.cs` filename Generated, two-methods-same-type
  PartialClassCluster, Generated-beats-Partial precedence,
  membership-matches-legacy-Detect back-compat, empty-graph
  empty-result).

## Open - Lifeblood v0.7.6 Prep (post-v0.7.5)

### LB-TRACK-20260515-009 - `dead_code` / `dependants` miss method-group refs through target-typed `new(...)` and generic-method calls

Status: Shipped v0.7.6 — Lifeblood self recall ratcheted; DAWG heuristic intentionally unshipped
Type: Bug
Source: `D:/Projekti/Lifeblood/docs/IMPROVEMENT_INBOX.md` (`LB-INBOX-010`)
Workspace: Lifeblood self, then DAWG verify
Verification: Shipped across two commits on Lifeblood `main`:
`d20a3b0` `feat(extractor): INV-EXTRACT-METHOD-GROUP-CANDIDATE-001 —
target-typed new(MethodGroup) edges (W2-A)` + `6084a55`
`feat(extractor): INV-EXTRACT-METHOD-ORIGINAL-DEFINITION-001 —
generic-call canonical id parity (W2-B)`. The previously-skipped
LB-INBOX-010 regression pin is now a live ratchet
(`ExtractEdges_StaticFieldInitializerMethodGroup_TargetTypedNew_AttributedToCctor`),
and the second fixture
(`ExtractEdges_TargetTypedNewMethodGroup_OverloadedMethod_PicksFirstCandidate`)
pins overload disambiguation. `4ff06bd` adds the W2-F recall anchor for
`test_impact` across the LB-TRACK-009/010 graph-edge fixes; the Advisory
heuristic layer remains intentionally unshipped unless a future DAWG recall
measurement proves it necessary.

Summary:
- Three real graph-level false positives observed in Lifeblood self-dogfood
  (2026-05-14, post-v0.7.5): target-typed `new(MethodGroup)`
  (`BclReferenceLoader.cs:20`, `RoslynCodeExecutor.cs:90`) emits the
  ctor edge but not the method-group reference; instantiated
  generic-method calls (`ToolHandler.cs:162`) bind under the
  instantiated symbol-id and miss the source-declared generic id.

Impact:
- `find_references` (Roslyn semantic) sees the usage; `dependants` /
  `dead_code` / `blast_radius` (graph BFS) miss it. Method-group / generic
  call-site reach is the exact class the truth envelope must bound to keep
  the "AI agent decides a live method is dead" hazard sub-threshold.

Fix shape:
- W2-A: handle `CandidateReason.OverloadResolutionFailure` in the
  argument-position handler — re-query `GetSymbolInfo` on the outer
  `BaseObjectCreationExpressionSyntax` to force target-type resolution,
  then re-query the inner identifier (no per-class special case).
- W2-B: route every `IMethodSymbol` through `OriginalDefinition` before
  `GetMethodId` so instantiated-generic edges land on the canonical
  source-declared id — mirrors `INV-CANONICAL-001` discipline.
- Pin via `INV-EXTRACT-TARGET-TYPED-NEW-001` + tighten the open
  `INV-CANONICAL-001` to cover the instantiated-vs-original-definition
  distinction explicitly.

### LB-TRACK-20260515-010 - Static-table facts do not feed graph liveness; implicit array tables can be missed

Status: Shipped v0.7.6 — DAWG roundtrip closed by explicit static-ctor follow-up
Type: Bug + Improvement (compound)
Source: `D:/Projekti/Lifeblood/docs/IMPROVEMENT_INBOX.md` (`LB-INBOX-011`)
Workspace: Lifeblood self, then DAWG verify (90-module Unity workspace)
Verification: Shipped across three commits on Lifeblood `main`.
`b984da1` `feat(extractor): INV-EXTRACT-STATIC-IMPLICIT-ARRAY-001 —
implicit array initializers classify as Array (W2-C)` closes part 1.
`538f202` `docs(extractor): INV-EXTRACT-DISPATCH-TABLE-COVERAGE-001
+ LB-INBOX-011 SHIPPED (W2-D)` documents that part 2 reduces to
INV-EXTRACT-METHOD-GROUP-CANDIDATE-001 + the existing identifier /
member-access walkers — no separate synthesizer needed. `d243259`
`feat(extractor): INV-EXTRACT-SYNTHESIZED-CTOR-001 — surface
synthesized .cctor / .ctor for initializer edges (W2-E)` corrected
W2-D's initial "no new code" claim by surfacing the synthesized ctors
so GraphBuilder's dangling-edge filter no longer drops the
initializer-derived edges. Pinned end-to-end by
`DispatchTableLivenessRatchetTests.DispatchTableDelegateTargets_AreLiveAcrossGraphWalkSurfaces`
(graph dependants + dead_code axes). `b950fc5` closed the DAWG roundtrip's
explicit static-constructor half: explicit `.cctor` declarations surface with
`.cctor` ids, initializer-owned fields/properties carry declarative
`References` edges, and `.cctor` methods are excluded from dead-code findings.

Summary:
- `lifeblood_static_tables` extracts dispatch-table rows with
  `MethodGroup` / `FieldReference` / `EnumMember` / `EnumFlags` cells
  carrying stable symbol ids, but the graph receives no corresponding
  `References` edges from the containing static field/property to those
  referenced symbols. Separately, implicit primitive arrays authored as
  `private static readonly float[] X = { ... }` are missed by the
  extractor itself — different Roslyn operation tree shape than
  `IArrayCreationOperation` / `ICollectionExpressionOperation`.

Impact:
- Read-side tools split reality in two. A method live only through a
  dispatch-table delegate row shows zero dependants; `dead_code` and
  `port_health` can mark it dead. This is exactly the class of false
  positive the truth envelope is supposed to bound.

Fix shape:
- W2-C: extend `RoslynStaticTableExtractor` to match the implicit
  array initializer operation shape (likely
  `IFieldInitializerOperation` wrapping `IArrayInitializerOperation` /
  `IConvertedExpressionOperation`). Operation-tree only; ban regex /
  syntax-text fallback (already INV-pinned).
- W2-D: emit graph `References` edges (not `Calls` — data ref, not
  invocation) from the containing static field/property symbol to every
  cell-resolved symbol id, with the existing `Edge.CallSite` provenance
  shape (`INV-EDGE-CALLSITE-001`) so the synthesized edges are
  byte-stable with expression-derived edges.
- W2-E: 4-axis verify ratchet — pin a static-table fixture and assert
  `dead_code` / `dependants` / `port_health` / `blast_radius` all read
  the synthesized edges. Regression-catches any future tool that
  bypasses the graph for table-driven liveness.

Cross-reference: W2-F (test_impact recall re-measurement) gates whether
LB-TRACK-007's Advisory heuristic layer ships in v0.7.6 or stays
deferred — once table-driven test fixtures appear as semantic edges,
test_impact recall is expected to climb above 95% without a heuristic.

### LB-TRACK-20260515-011 - `lifeblood_diagnose` produces 7,772 spurious diagnostics on Lifeblood.Tests vs MSBuild's 0 errors / 0 warnings

Status: Shipped v0.7.6 — release gate cleared before tag
Type: Bug (product-truth mismatch)
Source: 2026-05-15 dogfood session against Lifeblood `main` post-v0.7.5
Workspace: Lifeblood self (Lifeblood.Tests module)
Verification: Shipped across four commits on Lifeblood `main` (Wave W1
of the v0.7.6 prep masterplan). `f0939ff`
`INV-DIAGNOSTIC-NUGET-BINDING-PARITY-001` (top-level reference dedup,
later widened at `198af1e` to bucket by `(name, culture, public-key)`
so distinct strong-named identities survive). `f0b56a0`
`INV-DIAGNOSTIC-MSBUILD-IMPLICIT-NOWARN-001` (csc-default `1701`/`1702`
NoWarn baseline unioned into every discovered module). `fea89f9`
`INV-DIAGNOSTIC-IVT-PARITY-001` (`<InternalsVisibleTo>` items
synthesize `[assembly: IVT("X")]` syntax trees at compile time so
producer PEs carry the friend-assembly attribute). `7baee98`
`INV-DIAGNOSTIC-PARITY-001` — ratchet wall asserting Lifeblood self
emits zero diagnostics in the canonical parity class
{CS0122, CS0117, CS0234, CS1503, CS1701, CS1702, CS1705, CS1729}.
Empirical: 7,772 spurious findings pre-wave → 0 post-wave on source.
The v0.7.6 release commit `76389e1` was cut after the redeploy/tag gate
cleared; the earlier MCP-binary warning is superseded by that tag.

Summary:
- `lifeblood_diagnose moduleName=Lifeblood.Tests` returns 7,772
  diagnostics: CS0122 × 223 (inaccessible due to protection level),
  CS1503 × 6, CS0117 × 5, CS1729 × 1, CS1701 × 7537 (assembly
  reference version mismatch). `dotnet build Lifeblood.sln --no-restore`
  is clean: 0 warnings / 0 errors. The 7,772 spurious findings sort
  into two distinct families with two different root causes:
    1. CS1701 (× 7537): NuGet binding-redirect / version-mismatch
       diagnostics that MSBuild silently resolves through its
       binding-redirect lattice. Lifeblood compile host does not
       honor the same `TreatWarningsAsErrors` / `NoWarn` /
       `WarningsNotAsErrors` shape MSBuild applies.
    2. CS0122 / CS0117 / CS1503 / CS1729 (× 235 combined):
       `InternalsVisibleTo("Lifeblood.Tests")` declared on
       `Lifeblood.Adapters.CSharp.csproj:10` is not surviving the
       PE-image downgrade as a friend-assembly relation honored by the
       Tests-module compilation. MSBuild project-reference IVT
       semantics not mirrored in `ModuleCompilationBuilder`.

Impact:
- Users cannot trust `lifeblood_diagnose` as a build-parity gate when
  Lifeblood itself fails the parity ratchet on its own test assembly.
  Every "Lifeblood says X is broken" finding requires manual MSBuild
  cross-check before action — exactly the trust budget the v0.7.x
  truth-envelope work is supposed to eliminate.

Fix shape (per W1 plan, 2026-05-15):
- W1-A: `INV-DIAGNOSTIC-NUGET-BINDING-PARITY-001` — read MSBuild's
  effective `TreatWarningsAsErrors` / `NoWarn` / `WarningsNotAsErrors`
  lattice from each csproj at discovery time as a discovered module
  fact (mirrors `DefineConstants` / `LangVersion` already-present
  pattern). Thread into Roslyn `CSharpCompilationOptions
  .WithSpecificDiagnosticOptions`. No per-name special case for CS1701
  — the suppression list comes from MSBuild, not from a Lifeblood
  hardcode.
- W1-B: `INV-DIAGNOSTIC-IVT-PARITY-001` — read `InternalsVisibleTo`
  attributes during module discovery; emit
  `ModuleInfo.InternalsVisibleTo: string[]` (canonical strings only —
  no Roslyn primitives leak into Domain per `INV-DOMAIN-PURE`). Set
  `CSharpCompilation.AssemblyName` to match the IVT target name when
  consuming, and assert the friend-assembly bridge explicitly in
  `ModuleCompilationBuilder`. Pin with `DiagnosticIvtParityTests`
  (producer/consumer fixture asserting CS0122 count = 0).
- W1-C: `INV-DIAGNOSTIC-PARITY-001` — `BuildDiagnosticParityTests`
  ratchet wall. Runs `dotnet build` on a fixture solution + Lifeblood
  `diagnose moduleName=...` against the same compilation; asserts
  error-severity diagnostic count from Lifeblood ≤ MSBuild within
  tolerance 0. Catches every future "Lifeblood claims errors MSBuild
  doesn't" regression at one chokepoint.

## Open - Lifeblood v0.7.7 Prep (post-v0.7.6, surfaced 2026-05-15 DAWG dogfood)

### LB-TRACK-20260515-012 - `find_references` / `dependants` / `dead_code` miss cross-partial private-method invocations on heavily-partial classes

Status: Shipped (in-tree, untagged) — F1b atom of the 2026-05-19 plan.
Closing commit: `b0b5eb5` `test(extract): F1b pin cross-partial private method/field references (INV-EXTRACT-CROSS-PARTIAL-RESOLUTION-001)`. The Roslyn semantic model already resolved cross-partial calls correctly because both partial trees join the same Compilation — F1b authored regression-pin fixtures (`ExtractEdges_CrossPartialPrivateMethodCall_EmitsCallsEdgeWithCanonicalId` + `ExtractEdges_CrossPartialPrivateFieldReference_EmitsReferencesEdge`) so a future walker rewrite cannot silently scope resolution to the current syntax tree. **DAWG re-verified 2026-05-19 post-F1d redeploy**: `lifeblood_find_references(method:Nebulae.BeatGrid.AdaptiveBeatGrid.EnableAutoRotationDelayed())` returns `count: 2` — declaration at `AdaptiveBeatGrid.cs:282` + Usage at `AdaptiveBeatGrid.Bootstrap.Events.cs:109` with `containingSymbolId: AdaptiveBeatGrid.Bootstrap_BindEvents()`. Original entry preserved below for regression-trace.
Type: Bug
Source: DAWG dogfood 2026-05-15 (this tracking document), Lifeblood v0.7.6
Workspace: DAWG (200+ ABG partial files)
Verification: Reproduction shape verified end-to-end on Lifeblood v0.7.6
live MCP: `lifeblood_find_references(method:Nebulae.BeatGrid.AdaptiveBeatGrid.EnableAutoRotationDelayed())`
returns `count: 1` with `kind: Declaration` only. Source-text grep on the
same workspace finds the call at
`Assets/_Project/Scripts/BeatGrid/AdaptiveBeatGrid.Bootstrap.Events.cs:109`
in the form `StartCoroutine(EnableAutoRotationDelayed())` — a direct
invocation across two partial declarations of the same `AdaptiveBeatGrid`
class. Same gap affects `lifeblood_dead_code` (the method appears in the
preview as a Production-bucket finding despite being live) and
`lifeblood_dependants` (count 0). Confirmed by adjacent fixture: a similar
shape — `_pageNav?.TogglePage()` private forwarder called by
`indicatorBtn.onClick.AddListener(() => TogglePage())` — also returned a
single-declaration result, masking a real wiring question.

Summary:
- Roslyn binds the cross-partial private method call correctly at the
  language level, but Lifeblood's extractor either drops the `Calls` edge
  or attributes it to a containing symbol the resolver doesn't recognise.
  Heavily-partial classes (200+ partials in DAWG; ABG alone is the host
  with 140 implemented interfaces and 2,134 methods scattered across
  partials) hit this pattern thousands of times.

Impact:
- Real Lifeblood false-negative class for the "is this dead code?" decision
  — exactly the hazard `INV-DEADCODE-001` is supposed to bound. A reviewer
  taking `dead_code` output at face value would have proposed deleting
  `EnableAutoRotationDelayed` (used every app start via StartCoroutine),
  `SyncAllPatternsToNetworkBulk` (live MP-sync entry point ratcheted by
  `PatternSeamTests`), and several other coroutine / private-handler
  methods. The tool's truth envelope advertises `Semantic` confidence on
  `find_references`; that claim does not hold for this class.

Fix shape (proposed):
- Reproduce the gap inside Lifeblood's self-test suite using a two-partial
  fixture with one partial declaring `private void Foo() { ... }` and the
  sibling partial calling `Foo()`. Pin the expected `find_references` count
  ≥ 2 (one declaration + at least one call).
- Re-investigate `RoslynEdgeExtractor.ExtractCallEdge` /
  `FindContainingMethodOrLocal` / `GetMethodId` against the cross-partial
  case. Most likely cause: the containing-method walk inside the sibling
  partial returns an `IMethodSymbol` whose `OriginalDefinition` -> canonical
  id resolves to a partial-decl Lifeblood didn't surface as the primary
  symbol. The W2-B fix (`INV-EXTRACT-METHOD-ORIGINAL-DEFINITION-001`)
  closed the generic-method case; partial-class private-method may need a
  parallel `PrimaryDeclaration` normalisation before `GetMethodId`.
- Truth envelope: until fixed, every `find_references` /
  `dependants` / `dead_code` response should carry a `limitations[]`
  entry whenever the target's `ContainingType.DeclaringSyntaxReferences.Length >= 2`
  (partial class), so consumers see the precision drop. INV-FREEZE-PARTIAL-PRIVATE-001
  candidate name.

### LB-TRACK-20260515-013 - `lifeblood_port_health` returns `verdict: empty` on composite ports that inherit real contracts

Status: Shipped (in-tree, untagged) — F3a + F3b atoms of the 2026-05-19 plan.
Closing commits: `fe50998` `refactor(port-health): F3a extract HandlePortHealth body to IPortHealthAnalyzer seam (INV-PORT-HEALTH-ANALYZER-SEAM-001)` (extracts the algorithm behind an Application-layer port) and `8df96c6` `feat(port-health): F3b composite + inherited interface surface (INV-PORT-HEALTH-COMPOSITE-001)` (walks `IfaceInherits` edges, emits `directMemberCount` / `inheritedMemberCount` / `aggregateMemberCount` / `inheritedInterfaces[]` / `isCompositeInterface` + interface-extends-interface emits `Inherits` not `Implements` via `bf23480` F3c). **DAWG re-verified 2026-05-19**: `lifeblood_port_health(type:Nebulae.BeatGrid.Transport.ITransportTimelineHost)` returns `memberCount: 24, liveMembers: 24, deadMembers: 0, livenessPct: 1, verdict: "healthy"` with `directMemberCount: 0, inheritedMemberCount: 24, aggregateMemberCount: 24, isCompositeInterface: true, inheritedInterfaces: [ITransportTimelineAnchor, ITransportTimelineClock, ITransportTimelineLoop, ITransportTimelineState, ITransportTimelineSubsystems]`. Was previously `verdict: empty`. Original entry preserved below for regression-trace.
Type: Improvement
Source: DAWG dogfood 2026-05-15, Lifeblood v0.7.6
Workspace: DAWG (three Wave 4F composite ports)
Verification: `lifeblood_port_health` on `IPianoRollRefreshCoordinatorHost`,
`ITransportTimelineHost`, `IMelodicGridRenderHost` (all DAWG composite
ports per `INV-ABG-HOST-WIDTH-001` Wave 4F Phase 1) returns
`memberCount: 0, liveMembers: 0, deadMembers: 0, verdict: "empty"`. Source
inspection shows each interface declares zero own members but extends 5-6
concern-pure sub-ports (e.g. `interface ITransportTimelineHost :
ITransportTimelineState, ITransportTimelineClock,
ITransportTimelineAnchor, ITransportTimelineSubsystems,
ITransportTimelineLoop {}`). The composite is the contract — its members
are inherited.

Summary:
- `port_health` measures direct member declarations only. A composite-port
  pattern (an interface that bundles N sub-ports via inheritance with 0
  own members) is the canonical DI ergonomics shape for the "≤ N members
  per port" host-width invariant — and the tool reports it as vestigial.

Impact:
- Misleads architectural review. An AI agent following the tool output
  would propose deleting three live ABG host contracts whose composite
  function is exactly the invariant Wave 4F was designed to enforce.
- Same advisory-truth-envelope class as `dead_code`: the verdict
  ("empty") sounds definitive but is missing a load-bearing distinction.

Fix shape (proposed):
- Walk `ITypeSymbol.AllInterfaces` (Roslyn) when an interface's own member
  count is zero. If the inherited member set is non-empty, return
  `verdict: "composite"` with `inheritedMemberCount`, `inheritedFromCount`,
  and `liveMembers` / `deadMembers` measured against the inherited set
  (the same liveness check the tool already performs on direct members).
- Wire-shape additive: existing fields stay byte-stable, new fields
  default to absent on non-composite cases.
- INV-PORT-HEALTH-COMPOSITE-001 candidate name. Pin with two-fact fixture:
  empty-marker interface (no own members, no base interfaces) → still
  reports `empty`; composite interface (no own members, ≥1 base
  interface contributing members) → reports `composite` with correct
  inherited counts.

### LB-TRACK-20260515-014 - `lifeblood_enum_coverage` "unconsumed" pattern misses static-dispatch-table routing

Status: Shipped (in-tree, untagged) — S4 atom of the 2026-05-19 plan.
Closing commit: `c3ee6dd` `feat(enum-coverage): S4 dispatch-table reference counter (INV-ENUM-COVERAGE-DISPATCH-TABLE-001)`. Every enum-coverage member row now carries `dispatchTableReferenceCount` — additive, recognised via the same classifier `lifeblood_static_tables` uses so dispatch-table-routed values are triage-able from one row. `producedCount == dispatchTableReferenceCount` means "only a routing key, never genuinely produced in app code". **DAWG re-verified 2026-05-19**: `lifeblood_enum_coverage(type:Nebulae.BeatGrid.Audio.DSP.Burst.FeatureId)` returns 33 members; the 8 the tracking entry called out (`Waveform`, `FM`, `PWM`, `Formant`, `CrossMod`, `PitchEnvelope`, `Glide`, `Unison`) now show `dispatchTableReferenceCount: 1` (was missing) and `unproducedCount: 0` / `unreferencedCount: 0` at the top level. Original entry preserved below for regression-trace.
Type: Improvement / Documentation
Source: DAWG dogfood 2026-05-15, Lifeblood v0.7.6
Workspace: DAWG (`Nebulae.BeatGrid.Audio.DSP.Burst.FeatureId` 33 members)
Verification: `lifeblood_enum_coverage` on `FeatureId` reports 8 members
with `consumedSwitchCount: 0, consumedComparisonCount: 0`
(`Waveform`, `FM`, `PWM`, `Formant`, `CrossMod`, `PitchEnvelope`, `Glide`,
`Unison`) despite being produced ≥ 2 times each. Reviewer initial reading
was "potential drift / dead signal". Drill-down with `lifeblood_dependants`
showed each surfaces as a `References` edge from
`KernelCapabilityTable..cctor()` (the post-Stage-0 initializer-owner edge
landed by `INV-EXTRACT-DISPATCH-TABLE-COVERAGE-001`) into the static
`KernelCapabilityTable.Features[]` dispatch table where the FeatureId is
the row key, not a comparison subject.

Summary:
- `enum_coverage`'s `consumedComparison` + `consumedSwitch` are
  syntax-recognised at the call site (`==`, `case`, `is`-pattern). Static
  dispatch-table consumption — where the enum value indexes a row in a
  `static readonly T[] = { new T(EnumId.X, ...), ... }` shape — produces
  graph-edge consumption (post-Stage-0) but no `==` / `case` syntax.
  The honest signal is "produced but never compared", which is correct
  syntactically but easy to misread as drift.

Impact:
- A reviewer following `enum_coverage` output at face value would propose
  deleting dispatch-table-routed enum values. The Stage 0 dispatch-table
  edges now make the routing visible via `dependants`, but the
  enum-coverage tool doesn't compose against them — the consumer has to
  cross-tool manually.

Fix shape (proposed):
- Add `consumedDispatchTableCount` to each member row in `enum_coverage`,
  populated when the member appears as a cell value in a static-table
  initializer detected by `lifeblood_static_tables` machinery. Pure
  composition against the already-pinned dispatch-table-edge surface;
  no new extractor work.
- `isUnreferenced` semantics tighten: `isUnreferenced = true` iff
  `(totalReferences == 0)` AND `(consumedDispatchTableCount == 0)`.
  Backward-compatible — the existing `totalReferences` field already
  catches the edges, so the bool only changes for members that previously
  appeared mis-flagged.
- Doc / xmldoc on `enum_coverage` adds an explicit note: "`consumedSwitch`
  / `consumedComparison` measure syntactic consumption — see
  `consumedDispatchTableCount` for static-init dispatch routing surfaced
  by `INV-EXTRACT-DISPATCH-TABLE-COVERAGE-001`."
- INV-ENUM-COVERAGE-DISPATCH-001 candidate name.

### LB-TRACK-20260515-015 - `dead_code` produces high false-positive noise on private members read by the same class

Status: Shipped (in-tree, untagged) — F2 wire-shape + F1d underlying-graph fix of the 2026-05-19 plan.
Closing commits: `8134337` `feat(dead-code): F2 SameClassConsumerCount triage + IncludeSameClassOnlyConsumers (INV-DEADCODE-TRIAGE-002)` (added the wire-shape `sameClassConsumerCount` / `directDependants` / `bucket` / `declarationOnly` fields on every finding) AND `7a45ff0` `fix(extract): F1d bare-identifier property/event reads emit References edge (INV-EXTRACT-PROPERTY-READ-001)` (closes the algorithm gap behind F2 — bare-identifier sibling property reads were not emitting graph edges so F2's same-class field always reported 0 on the empirical class). **DAWG dogfood 2026-05-19 surfaced F1d**: workspace-wide non-public property zero-incoming was 88.7% (1099/1239) vs field 1.5% — the 88.7% delta isolated the bare-identifier-property bug. Post-F1d redeploy: zero-incoming property rate dropped to 67.2% (-21.5 pp, 266 properties recovered); `lifeblood_dependants(property:Nebulae.BeatGrid.Audio.FX.BeatGridFXBusManager.DspTime)` returns `count: 3` (was 0) with full callSite metadata at lines 250/634/691; `lifeblood_dependants(property:Nebulae.BeatGrid.MidiLearnManager.OverridesPath)` returns `count: 2` (was 0); `lifeblood_dead_code(includeKinds:[Property], excludePublic:true)` finding count dropped 247 → 62 (-75%) and DspTime / OverridesPath are no longer in the FP set. Remaining 62 are Unity SerializeField-backed Inspector-bound properties (different runtime-discovery class). Original entry preserved below for regression-trace.
Type: Improvement
Source: DAWG dogfood 2026-05-15 round 2 (Property+Field scan, 397 Production
findings), Lifeblood v0.7.6
Workspace: DAWG (53k+ symbol Unity workspace)
Verification: `lifeblood_dead_code includeKinds:["Property","Field"]
excludePublic:true` returned 410 advisory findings (162 fields + 248
properties; 397 Production-bucket). Two drilled candidates were both same-class
read FPs:
- `property:Nebulae.BeatGrid.Audio.FX.BeatGridFXBusManager.DspTime`
  (private getter at `BeatGridFXBusManager.cs:110`) consumed three times
  in the same class at lines 250 / 634 / 691.
- `property:Nebulae.BeatGrid.MidiLearnManager.OverridesPath`
  (private getter at `MidiLearnManager.cs:317`) consumed four times in
  the same class at lines 337 / 340 / 341 / 355.

Both match Lifeblood's documented FP class #3 ("private fields read via
same-class access when the enclosing type has no external references" —
truth-envelope `limitations[]` calls this out). The class is acknowledged
but the FP is unfilterable from the wire shape — a caller has to cross-tool
with `find_references` (which carries the same gap on partial classes —
see LB-TRACK-012) or grep each candidate to triage.

Summary:
- `dead_code` flags private property / field accessed only via implicit
  `this.X` from sibling methods of the same containing type. The graph
  drops the edge because the containing-method walk in `RoslynEdgeExtractor`
  records the access as a property/field read but the dead-code analyzer's
  `HasIncomingReference` walk doesn't credit "incoming from a sibling
  member of the same containing type" with the same weight as cross-type
  references. The truth envelope flags the class but doesn't surface it
  per-finding, so the caller cannot fold the noise.

Impact:
- High-noise output on real-world Unity workspaces where private getter
  patterns (cached `DspTime`, computed `OverridesPath`, derived
  `IsAvailable`) are idiomatic for keeping public surface narrow. On DAWG
  this class likely accounts for most of the 397 Production findings —
  reviewer triage cost is ~30s of grep per candidate × 397 ≈ multi-hour
  manual sweep, which collapses the practical value of running the tool
  on properties / fields at all.

Fix shape (proposed):
- Add `sameClassConsumerCount` field on every `DeadCodeResult`. Computed
  by walking the symbol's incoming edges (the analyzer already iterates
  them in `HasIncomingReference`) and counting any edge whose `SourceId`
  resolves to a symbol with the same `ContainingType` as the finding.
  Zero on the wire when the symbol is not a Property / Field / Method
  with a containing type (no false floor for top-level types). Distinct
  from `directDependants` (which counts ALL incoming non-Contains edges,
  but only when the finding itself has zero — and `directDependants` is
  always 0 for classic findings by construction since the analyzer drops
  symbols where `HasIncomingReference == true`).
- Re-walk semantics: `dead_code` keeps the same flagging criterion
  (`!HasIncomingReference`) for backwards compatibility, but the
  same-class graph edges that today block surfacing via
  `HasIncomingReference` now appear on the wire so the caller can filter
  them with one boolean check instead of cross-tool grep.
- Symmetric proposal alternative: introduce an `IncludeSameClassConsumed:
  bool` option on `DeadCodeOptions` (default `true` — preserves current
  behavior; `false` excludes the FP class from the output entirely).
  Either field-on-wire OR option-on-input closes the gap; the
  field-on-wire path is recommended because it lets the caller decide per
  finding rather than per query.
- INV-DEADCODE-SAMECLASS-001 candidate name. Pin via two-fact fixture: a
  type with a private property read by a sibling method of the same
  class (expect `sameClassConsumerCount == 1`); a type with the same
  shape PLUS one external reference (expect the finding to be absent
  from the result since `HasIncomingReference` already blocks it — this
  fixture asserts the field doesn't accidentally surface previously-live
  symbols).

Cross-reference: this finding is in the same family as LB-TRACK-012
(cross-partial private-method invocation extractor gap) — both are
"private member, real consumer, false-negative on the dead-code /
find_references wire." LB-TRACK-012 is the extractor gap (edge missing
from the graph); this entry is the wire-shape gap (edge present but the
caller cannot filter the FP class one-shot). Closing both shrinks the
"dead-code precision tail" the truth envelope advertises as Advisory.

### LB-TRACK-20260515-016 - Holistic dogfood rating + qualitative assessment

Status: Shipped (re-rated 2026-05-19 post-F1d redeploy) — qualitative entry, no per-feature fix; closes by re-rating after the LB-TRACK-012/013/014/015 family closes.
Closing context: all four LB-TRACK entries cited as "what costs points" are now Shipped:
- 012 (cross-partial private-method invocation) → `b0b5eb5` F1b
- 013 (port_health composite reads as empty) → `fe50998` F3a + `8df96c6` F3b + `bf23480` F3c
- 014 (enum_coverage dispatch-table routing) → `c3ee6dd` S4 `dispatchTableReferenceCount`
- 015 (dead_code same-class FPs) → `8134337` F2 wire-shape + `7a45ff0` F1d underlying-graph fix
Updated re-rating once those land in a release: trustworthiness-after-verification should push from 8.5 → 9+ because the four "trust but verify" overhead drivers are now wire-visible (composite surface on authority + port_health, dispatch-table refs on enum_coverage, triage fields on dead_code, cross-partial Calls edges proven via fixture). MCP-reconnect-on-close polish observation remains valid; not filed as its own product entry. Original qualitative entry preserved below for context.
Type: UX / Trustworthiness assessment
Source: DAWG dogfood close-out 2026-05-15, Lifeblood v0.7.6
Workspace: DAWG (full v1.2.368.4 → v1.2.368.5-mp-tick-arch architectural
session — 13 commits across MP transport cycle break, sub-port
segregation, WavetableBank cycle pass, Voice CS0282, gamepad haptic wire)

Summary:
- Overall rating: **8.5 / 10** for DAWG architectural dogfooding at v0.7.6.
- Genuinely changes how safely the team works. Already prevents chasing
  architecture-ghost rewrites that would have started from grep-only
  evidence.

What earned the rating (concrete wins from this session):
- Found the real dispatch-table edge bug end-to-end (the Stage 0 fix that
  shipped in Lifeblood v0.7.6 itself was DAWG-derived; close-the-loop wins
  for both repos).
- Proved the WavetableBank ↔ Validator loop was a real LikelyRealLoop, not
  partial-class noise — let us break it confidently with a -2 SCC delta
  (LikelyRealLoop bucket 59 → 57).
- Quantified the MP transport cycle shrink honestly (44 → 32 files,
  measured by jq-filtering cycles output for `Multiplayer/Transport`).
- Caught dead-code false-positive classes that grep alone would have
  surfaced as confident "delete this" recommendations (see
  EnableAutoRotationDelayed cross-partial gap below).
- Gave better confidence than grep on every refactor — before/after edge
  counts + per-symbol dependant resolution removed the "did this break
  anything" guesswork.

What costs points (already filed as LB-TRACK entries — fix-shape
proposed):
- `dead_code` carries noisy private same-class read FPs (~390-ish
  Production findings on DAWG, drilled candidates 100% same-class FPs in
  the sample). Filed as **LB-TRACK-015**.
- `find_references` / graph edges have a known cross-partial private
  invocation gap (cost a real verification pass on this session —
  EnableAutoRotationDelayed was reported as dead but actually called via
  StartCoroutine from a sibling partial). Filed as **LB-TRACK-012**.
- `port_health` misreads composite inherited ports as empty (cost an
  initial mis-verdict on F3 — three Wave 4F ABG composite host ports
  flagged as vestigial when they're the canonical INV-ABG-HOST-WIDTH-001
  pattern). Filed as **LB-TRACK-013**.
- `enum_coverage` needs better wording for static dispatch-table
  consumption — `consumedSwitch=0 AND consumedComparison=0` on a
  dispatch-routed enum reads as drift signal when it's actually correct
  syntactic accounting. Filed as **LB-TRACK-014**.
- MCP session transport closed on the caller side during one mid-session
  redeploy, though the dist stdio probe itself worked when re-launched.
  Watcher pattern (preserved in `D:/Projekti/Lifeblood/redeploy-watcher.ps1`)
  handles this — but auto-reconnect would be a smoother UX. Not filed as
  its own entry; intermittent enough that observation matters more than
  a fix proposal at this point.

Five-axis rating:
- **Architectural value: 9.5 / 10.** The single biggest gain. Lifeblood
  let us write commit messages with measured SCC deltas instead of "this
  should help cycles." That changes review quality.
- **Bug-finding value: 9 / 10.** Real bugs surfaced (CS0282 layout,
  AutomationTargetId 14/15 unreferenced, dispatch-table edge missing,
  WavetableBank cycle, gamepad denied-haptic unwired). One unit off
  because the bug-finding fires through the same advisory truth envelope
  the false-positive tail rides on — a real bug + a false positive look
  identical until you grep-cross-check.
- **False-positive discipline: 7.5 / 10.** Truth envelope flags the
  Advisory tier honestly but each Advisory finding still needs the
  consumer to read the docstring (or this tracking file) to know which
  FP class they're staring at. The LB-TRACK-012..015 fix-shape proposals
  exist for a reason — the discipline isn't there yet, but the contract
  to get there IS.
- **Release polish: 8 / 10.** v0.7.6 shipped end-to-end (commit + tag +
  GitHub Release + NuGet × 2) without drama. Watcher redeploy script
  works. MCP-reconnect-on-close is the polish gap.
- **Trustworthiness after verification: 8.5 / 10.** The "verify before
  acting on Advisory output" overhead is the price; the price is small
  compared to wrong-rewrites-prevented, but it's non-zero. v0.7.7 closing
  out LB-TRACK-012/013/014/015 would push this past 9.

Short version: Lifeblood is now genuinely changing how safely we work.
Not perfect yet, but already good enough to prevent chasing wrong
architecture ghosts — that's the big win. The remaining cost is the
"trust but verify" overhead on Advisory findings, which v0.7.7's
proposed fixes (per LB-TRACK-012..015) target directly.

This entry is qualitative and does not propose a specific fix — the
specific fixes already exist in the per-finding LB-TRACK entries above.
Its purpose is to give the Lifeblood maintainer a calibrated sense of
where the product sits today against a real architectural workload, so
the v0.7.7 roadmap can prioritize against rating-affecting axes.

## Open - Lifeblood v0.7.8 Prep (surfaced 2026-05-18 DAWG ABG-final dogfood)

### LB-TRACK-20260518-017 - `authority_report` needs inherited-interface and composite surface

Status: Shipped (in-tree, untagged) — F3e atom of the 2026-05-19 plan.
Closing commit: `bd4c59f` `feat(authority): F3e composite + inherited interface surface in InterfaceUsage (INV-AUTHORITY-COMPOSITE-001)`. Every `perInterface` row now carries `directMemberCount`, `inheritedMemberCount`, `aggregateMemberCount`, `memberCount` (back-compat alias = aggregate), `inheritedInterfaces[]`, `isCompositeInterface`. **DAWG re-verified 2026-05-19**: `lifeblood_authority_report(type:Nebulae.BeatGrid.AdaptiveBeatGrid)` returns 7 implementedInterfaces / 142 ownedPublicSurface / 1202 totalMethods / 426 pureForwarders / forwarderRatio 0.354 — EXACT match to Polish-1 P0.c baseline. Composite ifaces show their inherited contract surface: `IWheelMenuActions` direct=4 inherited=39 aggregate=43 (9 sub-ports), `IPianoRollRefreshCoordinatorHost` direct=0 inherited=35 (6 sub-ports), `IMelodicGridRenderHost` direct=0 inherited=21 (6 sub-ports), `ITransportTimelineHost` direct=0 inherited=24 (5 sub-ports). Original entry preserved below for regression-trace.
Type: Improvement
Source: DAWG ABG final-phase review, 2026-05-18, Lifeblood v0.7.7 live MCP
Workspace: DAWG
Verification:
- `lifeblood_analyze(projectPath:"D:/Projekti/DAWG", incremental:true, allowFullFallback:true)` returned `incremental-noop`, 65,693 symbols, 237,066 edges, 0 violations.
- `lifeblood_authority_report(type:Nebulae.BeatGrid.AdaptiveBeatGrid)` reported 12 implemented interfaces, 142 owned public surface, and forwarderRatio 0.359.
- The same report showed direct `memberCount:0` for `IPianoRollRefreshCoordinatorHost`, `IMelodicGridRenderHost`, and `ITransportTimelineHost`, and `memberCount:4` for `IWheelMenuActions`.
- Source review showed those are composite surfaces, not empty/trivial contracts: PR refresh is 0 own + 6 sub-ports (~35 aggregate members), melodic render is 0 own + 6 sub-ports (~21 aggregate members), transport timeline is 0 own + 5 sub-ports (~24 aggregate members), and wheel menu is 4 own + 9 sub-ports (~43 aggregate members).

Summary:
`authority_report` is excellent for ABG thinning, but direct-member reporting makes composite interfaces look smaller or deader than they are. This is the same product family as `port_health` inherited-interface blindness (LB-TRACK-20260515-013), but it now affects the top-level authority planning tool too.

Impact:
An agent can incorrectly rank a 0-own-member composite as a simple dead interface, when retiring it would actually collapse several deliberately narrow sub-ports into a mega-Bindings object. In the DAWG ABG final-phase review, this would have pushed the plan toward metric-chasing instead of preserving earned boundary shape.

Fix shape:
- Add `directMemberCount`, `inheritedMemberCount`, `inheritedInterfaceCount`, and `aggregateMemberCount` to each `perInterface` row.
- Add `isCompositeInterface` and a short `compositeShape` field when an interface inherits other interfaces.
- For implemented composite interfaces, include the inherited interface names and their member counts.
- Keep current direct-member fields for backward compatibility, but make aggregate surface visible enough that agents do not treat 0-own-member composites as dead by default.

### LB-TRACK-20260518-018 - Project-wide diagnose can retain stale errors contradicted by file compile checks

Status: Shipped (in-tree, untagged) — S5 + S5b atoms of the 2026-05-19 plan.
Closing commits: `09c8924` `feat(envelope): S5 analysisGeneration monotonic counter (INV-DIAGNOSE-FRESHNESS-001)` + `28ed311` `feat(diagnose): S5b scope-aware possiblyStale flag on response (INV-DIAGNOSE-FRESHNESS-002)`. `lifeblood_diagnose` / `lifeblood_compile_check` envelopes now carry `analysisGeneration` (monotonic counter incremented per full or incremental re-analyze) and `possiblyStale` (scope-aware: file scope checks the one file's mtime vs graph timestamp; module scope walks files parented to the module; project scope walks every tracked File symbol). **DAWG re-verified 2026-05-19**: `lifeblood_diagnose(filePath:Assets/_Project/Scripts/BeatGrid/AdaptiveBeatGrid.cs)` returns `count: 0, possiblyStale: false, resolvedModule: "Nebulae.BeatGrid.Runtime", analysisGeneration: 1, definesActive: [124 symbols incl. UNITY_EDITOR / EDITION_NEON / ENABLE_AUDIO / UNITY_2023_1_OR_NEWER / NETSTANDARD2_1]`. Original entry preserved below for regression-trace.
Type: Bug
Source: DAWG ABG final-phase review, 2026-05-18, Lifeblood v0.7.7 live MCP
Workspace: DAWG
Verification:
- Project-wide `lifeblood_diagnose` reported CS0246 errors for `StoryVisualHostBindings` in `UnityStoryVisualAdapter.cs`.
- In the same session, `lifeblood_compile_check(filePath:".../StoryVisualHostBindings.cs")` succeeded.
- `lifeblood_compile_check(filePath:".../UnityStoryVisualAdapter.cs")` also succeeded.
- `lifeblood_find_references(type:Nebulae.BeatGrid.Hosts.StoryVisualHostBindings, includeDeclarations:true)` resolved the type and returned live declaration/reference data.
- Unity MCP `read_console(types:["error"])` returned 0 compiler errors.
- Re-running incremental analysis as a no-op did not clear the stale project-wide diagnostics.

Summary:
The file-scoped compiler path and semantic reference path agreed that `StoryVisualHostBindings` exists and compiles, while project-wide diagnostics still surfaced old CS0246 failures.

Impact:
This is a trust issue during large architectural waves. Agents may treat stale project-level diagnostics as current blockers, or worse, design a fix around an already-resolved compile error.

Fix shape:
- Ensure project-wide `diagnose` and file-scoped `compile_check` share the same invalidation generation.
- If `compile_check` refreshes a module successfully, invalidate or recompute cached project diagnostics for that module.
- Add diagnostic envelope fields such as `analysisGeneration`, `diagnosticGeneration`, and `possiblyStale` when the diagnostic cache predates the latest successful compile check.
- Consider a consistency warning when project diagnostics contain errors for a symbol/file that `find_references` and file compile checks resolve successfully in the same loaded graph.

### LB-TRACK-20260518-019 - Add final-boundary planning verdicts to reduce manual joining

Status: Shipped (in-tree, untagged) — S7 atom of the 2026-05-19 plan.
Closing commit: `243dd28` `feat(authority): S7 planning-verdict evidence fields (INV-AUTHORITY-PLANNING-COMPOSITION-001)`. `authority_report` now returns `crossAssemblyConsumerCount` (distinct other modules with incoming edges into the type or its members — boundary-contract evidence), `sameAssemblyConsumerCount` (distinct same-module consumer symbols — adapter-shim evidence), `hasSingleImplementer` (true/false for interface targets with exactly one source-defined implementer; null for non-interface targets — adapter-shim candidate when paired with high cross-assembly use). Composite-aware: `aggregateCompositeMemberCount` is the same as `aggregateMemberCount` from F3e. Verdict composition (`EvictableDebt` / `BoundaryContract` / `SceneDiscoveryContract` / `CompositeFacade` / `AdapterShimOnly` / `NeedsAudit`) is caller-owned per design — the tool exposes the evidence axes; the architecture plan stays in the agent's hands. **DAWG re-verified 2026-05-19**: `lifeblood_authority_report(type:Nebulae.BeatGrid.AdaptiveBeatGrid)` returns `crossAssemblyConsumerCount: 4, sameAssemblyConsumerCount: 1581, hasSingleImplementer: null`. Original entry preserved below for regression-trace.
Type: Optimization
Source: DAWG ABG final-phase review, 2026-05-18, Lifeblood v0.7.7 live MCP
Workspace: DAWG
Verification:
- `lifeblood_authority_report(type:Nebulae.BeatGrid.AdaptiveBeatGrid)` supplied the key counts for the final 12 ABG interfaces.
- Human source review still had to join member counts, consumer shape, asmdef boundary, scene-discovery requirements, composite inherited surface, and prior narrowing intent.
- That manual join produced the final useful classification: 5 likely eviction-debt interfaces (`ITabSessionHost`, `ITimelineLoopLengthPort`, `IPianoRollAudioPreviewPort`, `IPianoRollSnapPort`, `IPianoRollViewportPort`) and 7 earned contracts/composites that should probably remain as documented boundary ports.

Summary:
Lifeblood provided the raw facts needed to avoid over-thinning ABG, but the final architectural verdict still required a hand-built planning layer. The tool could make this easier without making the decision for the agent.

Impact:
At the end of a thinning campaign, raw counts become less important than shape. A 1-member cross-asmdef contract can be earned architecture, while a 6-member same-asmdef callback can be debt. Without a planning verdict layer, agents may keep chasing interface count after the remaining interfaces have already earned their existence.

Fix shape:
- Add an optional `planning` mode to `authority_report`, or a separate `authority_plan` tool.
- Return candidate categories such as `EvictableDebt`, `BoundaryContract`, `SceneDiscoveryContract`, `CompositeFacade`, `AdapterShimOnly`, and `NeedsAtom0Audit`.
- Include short evidence fields: `crossAssemblyConsumerCount`, `sameAssemblyConsumerCount`, `isSceneDiscoveryContract`, `aggregateCompositeMemberCount`, `singleImplementation`, and `stateAuthorityRisk`.
- Keep the verdict advisory only. Lifeblood should expose the shape and likely tradeoff, while the architecture plan remains caller-owned.

### LB-TRACK-20260518-020 - Remaining trust holes after ABG-final dogfood

Status: Shipped (in-tree, untagged) — composite closure across S5/S5b/S6/S7 + F3a-F3f + F1b + F1d atoms of the 2026-05-19 plan.
Closing commits: composite — the five named holes track to the closed entries:
- "Project-wide diagnostics can go stale" → LB-TRACK-018 / S5 `09c8924` + S5b `28ed311`
- "Composite/inherited interface surface needs better reporting" → LB-TRACK-013 / F3a-F3f (port_health) + LB-TRACK-017 / F3e (authority_report)
- "Some advisory results still require manual verification" → S6 `8fcff9f` `feat(envelope): S6 advisory limitations + uniform write-side envelope (INV-ADVISORY-LIMITATIONS-001)` — every read-side response now carries per-tool `limitations[]` describing what static analysis cannot prove (Unity runtime dispatch, UnityEvent YAML bindings, prefab/scene serialized refs, etc.)
- "Endgame architecture needs better planning verdicts" → LB-TRACK-019 / S7 `243dd28`
- "Unity serialized/runtime discovery is not fully modeled" — explicitly surfaced in the per-tool `limitations[]` (port_health / authority_report / dead_code / enum_coverage / blast_radius all carry an Advisory limitation block when the workspace is Unity-shaped); deeper modeling (serialized-field/prefab/scene walk) is out of scope for this release wall — that would be a UnityReachabilityProvider v2 in a future wave, not a trust hole that blocks release. **DAWG re-verified 2026-05-19**: every read-side response in this conversation carries an `envelope` with `truthTier` / `confidence` / `evidenceSource` / `stalenessSeconds` / `filesChangedSinceAnalyze` / `limitations[]` / `analysisGeneration`. Original entry preserved below for regression-trace.
Type: UX
Source: DAWG ABG final-phase review, 2026-05-18, Lifeblood v0.7.7 live MCP
Workspace: DAWG
Verification:
- The final ABG review needed Lifeblood plus source reads, file compile checks, Unity console checks, and architecture receipts before deciding what to evict or keep.
- Three holes are already filed as concrete entries: stale project diagnostics (LB-TRACK-20260518-018), composite/inherited surface reporting (LB-TRACK-20260518-017), and missing endgame planning verdicts (LB-TRACK-20260518-019).
- Two broader holes remain as product trust-envelope issues: advisory results still need manual verification before destructive action, and Unity serialized/runtime discovery is not fully modeled.

Summary:
Remaining holes to keep visible:
- Project-wide diagnostics can go stale.
- Composite/inherited interface surface needs better reporting.
- Some advisory results still require manual verification.
- Unity serialized/runtime discovery is not fully modeled.
- Endgame architecture needs better planning verdicts.

Impact:
These holes do not invalidate Lifeblood as the first-pass C# architecture authority, but they define where agents must keep doing paired verification. They matter most near destructive actions: deleting members, retiring interfaces, declaring code dead, or deciding that a Unity-facing contract is only architectural debt.

Fix shape:
- Keep project-wide diagnostics and file compile checks on a shared invalidation generation.
- Expose inherited/composite surface in authority and port-health tools.
- Make advisory outputs carry stronger trust-envelope fields that say what was proven semantically and what still needs source/Unity verification.
- Add Unity-aware blind-spot warnings for scene discovery, serialized fields, prefab wiring, Unity magic methods, editor/runtime split, and domain reload/cache behavior.
- Add planning verdicts for late-stage architecture work so the tool can distinguish eviction debt from earned contracts without forcing the caller to hand-join every signal.

## Open - Lifeblood v0.7.8 Prep (surfaced 2026-05-19 inspection for two-phase hardening plan)

### LB-TRACK-20260519-021 - Extension-method calls canonicalize without `ReducedFrom`, live methods may appear dead

Status: Shipped (in-tree, untagged) — F1a atom of the 2026-05-19 plan.
Closing commit: `d4ccfa5` `fix(extract): F1a route extension-method ids through ReducedFrom (INV-EXTRACT-EXTENSION-REDUCED-001)`; both `RoslynEdgeExtractor.GetMethodId`
and `RoslynCompilationHost.BuildSymbolId` normalize via `ReducedFrom` before
`OriginalDefinition`. Pinned by `INV-EXTRACT-EXTENSION-REDUCED-001` and four
fixtures in `RoslynExtractorTests` (instance-style, static-style, generic-
extension, chained-extension). Debug suite 1011 → 1015. STATUS test-count
anchor refreshed.
Type: Bug
Source: `D:/Projekti/lifeblood_plan.txt` Stage 1 (2026-05-18), confirmed by 2026-05-19 source inspection of `RoslynEdgeExtractor.GetMethodId`.
Workspace: Lifeblood self
Verification:
- Grep across `src/` and `tests/` for `ReducedFrom` / `IsExtensionMethod` returns zero matches (excluding Roslyn binary DLLs).
- `RoslynEdgeExtractor.cs:725` only routes through `method.OriginalDefinition`, not `(method.ReducedFrom ?? method).OriginalDefinition`.
- No regression fixture exists for extension-method canonical-id parity between declaration and invocation paths.

Summary:
For an instance-style extension invocation `x.Foo()`, Roslyn binds `IMethodSymbol` to the reduced form (without the explicit `this` parameter). The declaration path (`RoslynSymbolExtractor`) emits the unreduced form. Without `ReducedFrom` normalization in `GetMethodId`, every reduced-form call-site lands on a non-matching symbol id, so the declared method shows `directDependants:0` and `dead_code` may flag it.

Impact:
This is the same defect class that `INV-EXTRACT-METHOD-ORIGINAL-DEFINITION-001` closed for generic methods (LB-INBOX-010 Wave W2-B) but specialized to extension methods. Empirical class likely present in any workspace that uses extension methods on user types — DAWG has several (`MinisExtensions`, OKLab helpers, etc.).

Fix shape:
- In `RoslynEdgeExtractor.GetMethodId`, prepend `if (method.ReducedFrom != null) method = method.ReducedFrom;` before the `OriginalDefinition` route, matching how `RoslynCompilationHost.FindReferences` already normalizes on the consumer side.
- Add `ExtensionMethodCanonicalIdTests` covering: (a) instance-style invocation `x.Foo()`, (b) static-style invocation `Helper.Foo(x)`, (c) recursive extension chain, (d) generic extension method.
- Pin `INV-EXTRACT-EXTENSION-REDUCED-001`.

### LB-TRACK-20260519-022 - `port_health` algorithm lives inline in `ToolHandler`, no `IPortHealthAnalyzer` seam

Status: Shipped (in-tree, untagged) — F3a + F3b + F3c + F3d + F3e + F3f atoms of the 2026-05-19 plan.
Closing commits: `fe50998` F3a (extract seam) + `8df96c6` F3b (composite + inherited surface, `INV-PORT-HEALTH-COMPOSITE-001`) + `bf23480` F3c (interface-extends-interface emits `Inherits` not `Implements`, `INV-EXTRACT-IFACE-INHERIT-001`) + `9cc4e91` F3d (real-graph composite-interface fixtures closing F3b synthetic-graph blind spot) + `bd4c59f` F3e (composite/inherited surface on `authority_report` `InterfaceUsage`, `INV-AUTHORITY-COMPOSITE-001`) + `33d2fc1` F3f (docs/tools description sync). F3a extracted the algorithm behind `IPortHealthAnalyzer` in `Lifeblood.Application.Ports.Right`; F3b added the composite-aware traversal; F3c-F3f closed the iface-edge-kind, fixture-blind-spot, authority-side parity, and doc-sync follow-ups. `ToolHandler` routes through the injected port (default ctor falls back to a new instance). Pinned by 9+ `PortHealthAnalyzerTests` fixtures plus real-graph composite tests + a seam-discipline scan that refuses to find the inline algorithm tokens in `ToolHandler.cs`. Debug suite 1028 → 1037 → … → 1097 over the chain; port count 26 → 27. STATUS testCount + portCount anchors refreshed. **DAWG re-verified 2026-05-19**: see LB-TRACK-013 closure for the live-MCP composite verdict on `ITransportTimelineHost`.
Type: Improvement
Source: `D:/Projekti/lifeblood_plan.txt` Stage 3 + `docs/plans/lifeblood-correctness-masterplan-2026-05-15.md` Stage 3, confirmed by 2026-05-19 source inspection.
Workspace: Lifeblood self
Verification:
- `grep -rn "IPortHealthAnalyzer\|PortHealth" src/` returns only two hits, both in `ToolHandler.cs` (the dispatch arm at line 88 and the inline 75-line implementation at lines 812-886).
- No port interface for port-health logic exists in `Lifeblood.Application/Ports/`.
- The inline algorithm walks only direct `Contains` edges; `EdgeKind.IfaceInherits` / inherited-interface traversal is absent.

Summary:
Two related drifts: (a) the algorithm lives in `ToolHandler` instead of behind an Application-layer port, breaking the hexagonal pattern that the other 26 port interfaces already follow; (b) the algorithm itself is composite-blind — an interface whose surface comes entirely from inherited sub-ports reports `memberCount:0` and verdict `empty`, matching the LB-TRACK-20260518-017 family on `authority_report`.

Impact:
Same architectural drift class as LB-TRACK-20260518-017 but on a different tool. Together they form a coherent "composite-interface blindness" problem visible across port_health, authority_report, and any future analyzer that walks direct members only.

Fix shape:
- Define `IPortHealthAnalyzer` in `Lifeblood.Application/Ports/Right/`.
- Implement `LifebloodPortHealthAnalyzer` in `Lifeblood.Connectors.Mcp/` (or `Lifeblood.Analysis/` — choose by dependency direction).
- Move the algorithm body. ToolHandler routes to the port. No behavior change in this atom (F3a).
- Follow-up atom (F3b) adds composite/inherited surface: walk `IfaceInherits` edges, emit `directMemberCount` / `inheritedMemberCount` / `aggregateMemberCount` / `inheritedInterfaces[]` / `isCompositeInterface`.
- Pin `INV-PORT-HEALTH-ANALYZER-SEAM-001` (the seam exists) and `INV-PORT-HEALTH-COMPOSITE-001` (composite traversal).

### LB-TRACK-20260519-023 - Multiple symbol-id generation paths can drift

Status: Shipped (in-tree, untagged) — F1c plus final canonical-id SSoT polish of the 2026-05-19 plan.
Closing commits: `f7c66ad` `fix(symbol-id): F1c .ctor/.cctor parser ambiguity + INamedTypeSymbol.Constructors lookup (INV-CANONICAL-ID-PARITY-001)` plus the final polish commit. `ParseSymbolId` now preserves the literal `.ctor` / `.cctor` IL names, and `FindInCompilation` looks up constructors via `INamedTypeSymbol.Constructors` / `.StaticConstructors`. `CanonicalSymbolFormat` now owns Roslyn symbol-id construction for type / method / field / property / event symbols; `RoslynSymbolExtractor`, `RoslynEdgeExtractor`, and `RoslynCompilationHost.BuildSymbolId` route through it. `ArchitectureInvariantTests.CSharpAdapter_RoslynSymbolIds_HaveSingleSourceOfTruth` refuses direct `SymbolIds.Type` / `Method` / `Field` / `Property` calls outside `CanonicalSymbolFormat`, so future extractor or lookup work cannot quietly fork the grammar again. Pinned by `INV-CANONICAL-ID-PARITY-001`, `SymbolIdCanonicalParityTests`, and the C# adapter SSoT architecture ratchet. Debug suite anchor is 1098/1098 for the prerelease wall.
Type: Bug
Source: `docs/plans/lifeblood-correctness-masterplan-2026-05-15.md` Stage 1 observed-risk note ("Lifeblood has multiple symbol-id paths with subtly different behavior"); re-surfaced as F1c in the 2026-05-19 plan.
Workspace: Lifeblood self
Verification:
- `rg "SymbolIds\.(Type|Method|Field|Property)\(" src/Lifeblood.Adapters.CSharp` reports only `Internal/CanonicalSymbolFormat.cs`.
- Focused SSoT lane: `SymbolIdCanonicalParity`, `ArchitectureInvariant`, `RoslynExtractor`, and `CanonicalSymbolFormat` filters pass 104/104.
- Full prerelease wall target: 1098/1098 tests, 0 skipped.
- `RoslynWorkspaceManager.ParseSymbolId` remains the parser side of the contract; it preserves `.ctor` / `.cctor` and round-trips through lookup.

Summary:
Symbol-id generation is reproduced in at least four places. Each path has its own subtle handling for constructors, static constructors, accessors, generic methods, and (post LB-TRACK-021) extension methods. Drift between them surfaces as "find_references finds the symbol but dependants returns 0" or vice versa.

Impact:
Reference-side correctness depends on every read tool answering against the same canonical id. Stage 2 (dead-code triage) and Stage 3 (port-health composite) both assume reference data is trustworthy, so this is a load-bearing dependency for the rest of the plan.

Fix shape:
- Audit the four paths against a single fixture (method, ctor, cctor, property/event accessor, generic method, extension method).
- Promote `CanonicalSymbolFormat` to be the only Roslyn-symbol id-building primitive in the C# adapter; route every declaration, edge, and lookup construction path through it.
- Keep `RoslynWorkspaceManager.ParseSymbolId` as the parser side of the contract, including `.ctor` and `.cctor` canonical ids without dot-splitting ambiguity.
- Pin `INV-CANONICAL-ID-PARITY-001` with parity fixtures plus the architecture ratchet that refuses duplicate builders.

### LB-TRACK-20260519-024 - Bare-identifier sibling-member property/event reads emit no graph edge (read-tool reality split)

Status: Shipped (in-tree, untagged) — F1d atom of the 2026-05-19 plan.
Closing commit: `7a45ff0` `fix(extract): F1d bare-identifier property/event reads emit References edge (INV-EXTRACT-PROPERTY-READ-001)`.
Type: Bug
Source: DAWG dogfood 2026-05-19, post-F2/F3 redeploy verification session, Lifeblood v0.7.7 head + uncommitted F1d.
Workspace: DAWG (65,940 symbols / 90 modules / 238,242 edges pre-fix → 242,233 post-fix = +3,991 newly-recovered property/event-read edges)
Verification:
- Pre-fix: `lifeblood_find_references(property:Nebulae.BeatGrid.Audio.FX.BeatGridFXBusManager.DspTime)` returned 4 hits (decl + 3 sibling reads with proper `containingSymbolId`); `lifeblood_dependants(...)` on the same symbol returned `count: 0`. Source-text grep confirmed the 3 sibling reads at `BeatGridFXBusManager.cs:250/634/691` (`double now = DspTime;`).
- Workspace-wide audit via `lifeblood_execute`: non-public Property zero-incoming-non-`Contains`-edges = 88.7% (1099/1239) vs Field 1.5% (174/11765) vs Type 0.8% (4/482). The Property/Field delta isolated the bug to `ExtractReferenceEdge`'s bare-identifier path.
- Root cause: `RoslynEdgeExtractor.ExtractReferenceEdge` for `IdentifierNameSyntax` carried arms for `INamedTypeSymbol` / `IFieldSymbol` / `IMethodSymbol` but no `IPropertySymbol` / `IEventSymbol` arm. The member-access form (`this.X`, `obj.X`) was already handled by `ExtractMemberAccessEdge` via `EmitSymbolLevelEdge`. C# style convention drops `this.` overwhelmingly, so the bare-identifier path is the common case and the gap silently swallowed ~89% of private-property incoming edges across the workspace. Roslyn's `SymbolFinder.FindReferencesAsync` walks the semantic model directly and ignores the graph, so `find_references` kept working while `dependants` / `dead_code` / `blast_radius` / `port_health` (all edge-graph walkers) systematically missed every bare-identifier property read. F2's `sameClassConsumerCount` triage field consequently always reported 0 on private property findings, defeating the FP-folding contract.
- Post-fix re-verification: `lifeblood_dependants(...DspTime)` returns `count: 3` with full `callSite` metadata (lines 250/634/691, per-source-method dedup intact); `lifeblood_dependants(...OverridesPath)` returns `count: 2` (3 grep reads in `SaveOverrides` dedup to one + 1 read in `LoadOverrides`); workspace-wide Property zero-incoming rate dropped 88.7% → 67.2% (-21.5 pp, 266 properties recovered); `lifeblood_dead_code(includeKinds:[Property], excludePublic:true)` finding count dropped 247 → 62 (-75%) and DspTime / OverridesPath dropped from the FP set entirely.

Summary:
Bare-identifier `IPropertySymbol` and `IEventSymbol` reads inside sibling-member bodies emitted no graph edge. The walker handled types/fields/method-groups but had no property/event arm — the member-access form was already covered by `ExtractMemberAccessEdge` through `EmitSymbolLevelEdge`, so the wire-shape contract was intact but the AST-entry point was incomplete. The covered cases are read AND write (LHS of `=` is also `IdentifierNameSyntax`) AND event subscription (`Changed += h;`).

Impact:
Same family as LB-TRACK-012 (cross-partial private-method invocation) but specialized to non-partial private properties. F2's same-class triage field, every dead_code property-bucket scan, every dependants / blast_radius / port_health query against a private property was wrong by default until F1d. The 88.7% workspace-wide hit rate made this the single highest-impact extractor gap discovered across the 2026-05-19 plan.

Fix shape:
- `RoslynEdgeExtractor.ExtractReferenceEdge`: add `if (referencedSymbol is IPropertySymbol or IEventSymbol) { if (ContainingType tracked) EmitSymbolLevelEdge(...); return; }` arm between the field arm and the method-group arm. Route through `EmitSymbolLevelEdge` (existing helper) so the wire shape is byte-stable across the two AST entry points.
- Three new fixtures in `RoslynExtractorTests`: `ExtractEdges_BareIdentifierPropertyRead_EmitsReferencesEdge` (private expression-bodied property read), `ExtractEdges_BareIdentifierPropertyWrite_EmitsReferencesEdge` (auto-prop write through bare identifier), `ExtractEdges_BareIdentifierEventReference_EmitsReferencesEdge` (private event `+=` handler wire).
- Pin `INV-EXTRACT-PROPERTY-READ-001` in `docs/invariants/csharp-adapter.md`.

Debug suite 1094 → 1097 green, zero skipped. STATUS testCount anchor refreshed.

## Shipped - Lifeblood v0.7.8 Prep (Stage 0 dogfood + Wave 5 cleanup, 2026-05-24)

Stage 0 of a multi-stage dogfood plan: exercise all 30 MCP tools end-to-end against DAWG, re-verify every shipped LB-TRACK claim through -024, file fresh gaps with proper architectural fix shape (eternal solutions, no hotpatches). Wave 5 closed -025 / -026 / -027 with closing commits `3b664a0` / `c87862e` / `2ae4266` plus eternal-shape ratchet `INV-LIST-SHAPE-UNIFORM-001` (`cdb7691`). All three entries below carry Shipped status with closing-commit references; original Open bodies preserved for regression-trace. Wave 6 = L-LIM-001 multi-define union analyze — implementation plan landed at `docs/plans/multi-define-union-l-lim-001-plan-2026-05-24.md` (commit `938893b`); 6-phase phased rollout (port + default resolver, multi-profile compile, Unity adapter, Edge.Profiles[] wire, per-IOperation tool policy, DAWG dogfood closure). Estimated 2–4 weeks focused work + 1 week dogfood. Wave 6 implementation deferred from the Wave 5 session per scope honesty; Phase 2 entry point is `IDefineProfileResolver` port (Wave 6.A).

### LB-TRACK-20260524-025 - `lifeblood_rename` returns whole-file replacement and misses cross-partial usage sites

Status: Shipped (in-tree, untagged) — Wave 5 Stage 0 cleanup pass.
Closing commit: `3b664a0` `fix(rename): per-TextChange wire shape + cross-partial coverage (LB-TRACK-025)`. Two compound defects closed in one atom: (a) `Rename` checked `mgr.Solution == null` BEFORE any operation triggered `EnsureWorkspace`, so first-call returns on a fresh `RoslynWorkspaceRefactoring` instance returned empty — fix moves `ResolveSymbol` BEFORE the Solution null check so `EnsureWorkspace` fires; (b) `SourceText.GetTextChanges(oldText)` did a brute text diff between SourceText instances from different TextLoader containers, degenerating to a single whole-file TextChange — fix switches to `Document.GetTextChangesAsync(oldDoc)` for Roslyn's Document-level granular diff. Cross-partial coverage falls out for free because `Renamer.RenameSymbolAsync` already runs at Solution scope. Pinned by `RenameWireShapeTests` (6 facts: diagnostic single-type probe, cross-partial method, cross-partial field, same-file multi-use property + method, NewText length budget). `WriteSideIntegrationTests.Rename_GreeterType_ReturnsRealEdits` relaxed to match Roslyn's minimal-diff contract. INV-RENAME-POINT-EDITS-001 + INV-RENAME-CROSS-PARTIAL-001 pinned in `docs/invariants/tools.md`. Original entry preserved below for regression-trace.
Type: Bug (correctness + wire-shape, compound)
Source: DAWG Stage 0 dogfood 2026-05-24, Lifeblood v0.7.8-alpha.0.31 (post-dist-swap)
Workspace: DAWG
Verification:
- Probe 1 — cross-partial method: `lifeblood_rename symbolId:"method:Nebulae.BeatGrid.AdaptiveBeatGrid.EnableAutoRotationDelayed()" newName:"EnableAutoRotationDelayedRenamed"`. `lifeblood_find_references` on the same symbol returns `count: 2` (decl at `AdaptiveBeatGrid.cs:291`, usage at `AdaptiveBeatGrid.Bootstrap.Events.cs:109` with `containingSymbolId: AdaptiveBeatGrid.Bootstrap_BindEvents()`). Rename response: `editCount: 1`. The single edit covers `AdaptiveBeatGrid.cs` lines 1–337 with the FULL file as `newText`. The sibling-partial usage in `AdaptiveBeatGrid.Bootstrap.Events.cs` is **absent from the edit list**. Applying the returned edits as-given would rename the declaration but leave the cross-partial call site referring to the old name — instant CS0103 / CS1061 on the next build.
- Probe 2 — same-file property: `lifeblood_rename symbolId:"property:Nebulae.BeatGrid.Infrastructure.Audio.BeatGridAudioPool.DspTime" newName:"DspTimeNow"`. `lifeblood_find_references` returns 6 usages all in `BeatGridAudioPool.cs` (lines 226 / 600 / 622 / 649 / 694 / 915). Rename response: `editCount: 1`, single edit covering lines 1–940 with the full 940-line file as `newText`. All 6 usages ARE renamed inside that newText — same-file rename is functionally correct, but the wire shape collapses 7 logical TextChanges (decl + 6 usages) into one whole-document blob.

Summary:
Two distinct defects in the same tool, exposed by the same operation.
1. **Wire-shape defect.** Roslyn's `Renamer.RenameSymbolAsync` returns per-document `TextChange[]` (one TextSpan-bounded change per renamed site). Lifeblood is collapsing every document's full set of changes into a single `Edit { startLine:1, endLine:<lastLine>, newText:<entire file body> }`. A caller cannot diff which lines changed without local-side diffing newText against the on-disk file. Multi-rename atomic application becomes risky because the whole-file blob will overwrite ANY concurrent edits the caller has in flight, not just the renamed sites.
2. **Correctness defect.** Cross-partial / cross-file rename emits edits ONLY for the document containing the symbol's primary declaration. Sibling partials of the same type that reference the symbol via bare-identifier are not included in `edits[]`, even though `find_references` correctly surfaces those call sites in the same session. Applying the returned rename mechanically breaks the build whenever the symbol has any cross-partial / cross-file consumer.

Impact:
- Mechanically applying `lifeblood_rename` output on any non-leaf private symbol in a heavily-partial codebase (DAWG ABG = 159 partial files for one type) produces a broken build. Truth envelope advertises `Semantic` / `Proven` on the response; that claim does not hold for the editCount when partials exist.
- Wire-shape defect makes "apply only the rename hunks" impossible from response shape alone — caller MUST diff. Composing rename with concurrent local edits (the normal Claude Code workflow) becomes unsafe by construction.
- Same family as the now-closed extractor gaps LB-TRACK-012 (cross-partial private method invocation) and LB-TRACK-024 (bare-identifier property reads): "Lifeblood underweights cross-partial / cross-file edges." LB-TRACK-012's regression-pin fixture (`ExtractEdges_CrossPartialPrivateMethodCall_EmitsCallsEdgeWithCanonicalId`) proves the extractor sees the edge; the rename tool nonetheless misses it on the write side. The Renamer must consume those edges, not just the analyzer.

Fix shape (proposed — eternal, no per-symbol guard):
- **Wire shape (`INV-RENAME-POINT-EDITS-001` candidate name).** Replace the whole-document edit emission with per-`TextChange` projection from Roslyn's `Renamer.RenameSymbolAsync` solution. For each touched `Document`, iterate `originalSolution.GetDocument(id).GetTextChangesAsync(newDocument)` and emit one wire-level `Edit { filePath, startLine, startColumn, endLine, endColumn, newText }` per `TextChange`. The `startLine/endLine/newText` already exist on the wire — just bind them to the narrow TextSpan, not the document span. Caller can stop diffing and start applying edits as-is.
- **Cross-partial coverage (`INV-RENAME-CROSS-PARTIAL-001` candidate name).** Investigate why `Renamer.RenameSymbolAsync(solution, symbol, ...)` is returning a solution whose changed-document set excludes the sibling partial. Likely candidates: (a) Lifeblood is passing a single-document scope instead of `Solution` scope; (b) the SymbolFinder.FindReferences pass that drives the rename is bounded to the symbol's `DeclaringSyntaxReferences[0].SyntaxTree` instead of walking the workspace; (c) bare-identifier sibling-partial references resolve to a `CandidateReason.OverloadResolutionFailure` shape that the renamer drops (same family as the now-closed `INV-EXTRACT-METHOD-GROUP-CANDIDATE-001` extractor gap). Root-cause one, ratchet all three with explicit fixtures.
- **Eternal regression-pin (`RenameCrossPartialFixtureTests` candidate name).** Author a two-partial-file fixture in Lifeblood self-tests:
  - File A: `partial class Host { private void Foo() { } }`
  - File B: `partial class Host { void Caller() { Foo(); } }`
  - Probe `lifeblood_rename` on `method:NS.Host.Foo()` → `Bar`. Assert: (a) `editCount >= 2`; (b) edits cover BOTH files; (c) each edit's TextSpan is narrower than its containing document; (d) `newText.Length` for each edit is bounded by `(endLine - startLine + 1) * maxLineWidth` not the full file size. Catches both the wire-shape and the cross-partial regression at one chokepoint.
- **Cross-cross-asmdef coverage variant.** Once cross-partial works, add a sibling fixture across two asmdef-bounded modules to prove the renamer walks the Solution, not the Project. Mirrors the closure-mode work from `LB-TRACK-20260514-001` / `INV-MODULE-REFS-001`.

Anti-goals (per INV-AUTONOMY-003):
- No per-symbol-kind hardcoded path. The rename machinery is one cycle: feed Roslyn's `Renamer.RenameSymbolAsync` the right scope, iterate ALL changed documents, project TextChanges to wire shape. Not "if method then look at partials else don't."
- No "set `editCount >= someThreshold` and call it done" sentinel. The fixture above asserts SHAPE, not count.

### LB-TRACK-20260524-026 - `lifeblood_file_impact` lacks `summarize` / `maxResults` controls; overflows tool-result cap on god-type files

Status: Shipped (in-tree, untagged) — Wave 5 Stage 0 cleanup pass.
Closing commit: `c87862e` `feat(file-impact): summarize flag + maxResults cap (LB-TRACK-026)`. Wire-shape parity with the `dead_code` / `cycles` / `blast_radius` / `test_impact` summarize trio. New input options: `maxResults` (default 500 normal / 25 summarize mode) clips each direction's array independently; `summarize:bool` (default false) forces `maxResults=25` regardless of caller-passed value. Per-direction `dependsOnTruncated` / `dependedOnByTruncated` flags + composite `truncated` bool for one-field checks. Counts (`dependsOnCount` / `dependedOnByCount`) stay full so summarize callers see real magnitude. Fix is purely additive at the handler layer (`Lifeblood.Server.Mcp.ToolHandler.HandleFileImpact`) — Domain port shape unchanged. Pinned by `ToolHandlerTests.Handle_FileImpact_*` (4 facts: default invocation reports counts + truncation shape, explicit maxResults clips + fires flags, summarize:true forces 25 over caller-passed 100, summarize:false honors explicit caller cap as regression guard). Uniform-shape ratchet `INV-LIST-SHAPE-UNIFORM-001` (closing commit `cdb7691`) closes the silent-drift class — future list-shape tools shipping without the trio fail at build time. INV-FILE-IMPACT-SUMMARIZE-001 pinned in `docs/invariants/tools.md`. Original entry preserved below for regression-trace.
Type: Improvement (wire-shape uniformity)
Source: DAWG Stage 0 dogfood 2026-05-24, Lifeblood v0.7.8-alpha.0.31
Workspace: DAWG
Verification:
- `lifeblood_file_impact filePath:"Assets/_Project/Scripts/BeatGrid/AdaptiveBeatGrid.cs"` returns 185,034 characters across 4,089 lines, exceeding the downstream tool-result cap. Response saved to disk by the runtime; caller must `jq` it back. `AdaptiveBeatGrid.cs` is the primary partial of a 159-partial-file MonoBehaviour with `directDependants:4` but transitive blast radius across 410 symbols / 5 modules — file_impact aggregates the same cross-file reach but with no shortcut to a compact shape.
- `lifeblood_blast_radius` on the same symbol already exposes the right shape (`summarize:true` + `maxResults:N` + `groupBy:both` → ~5 KB response with all key signal). file_impact carries no equivalent flags.
- Asymmetry survey across read-side tools: `dead_code`, `cycles`, `blast_radius`, `test_impact` ALL expose `summarize` + `maxResults`. `file_impact` and `static_tables` (see LB-TRACK-20260524-027) do NOT. Pinned wire-shape inconsistency.

Summary:
`lifeblood_file_impact` returns full `incomingFiles[]` and `outgoingFiles[]` arrays without caps or summary mode. On god-type primary partials in a real Unity workspace, the response routinely overflows the 30k-character soft cap downstream tools enforce. The signal a caller needs (count + small preview + bucket histogram) is structurally already present; the wire shape just doesn't expose it.

Impact:
- Tool unusable on the exact codebase shape where it's most valuable (large workspaces with high-fan-out files). The cap-overflow path forces callers to read the saved file in chunks before they can act, which collapses the "one MCP call, one decision" workflow that the truth envelope advertises.
- Wire-shape inconsistency across the read-side tool set means callers can't reason uniformly about response budgets. Mental cost grows linearly with tool count.

Fix shape (proposed — uniform with already-shipped list-shape tools):
- Add `summarize:bool` (default false), `maxResults:int` (default 500 normal mode / 25 summarize mode), and `previewPerSection:int` (default 5) to `lifeblood_file_impact` input schema. When `summarize:true`: response carries `incomingFileCount`, `outgoingFileCount`, `incomingPreview[]` (≤ `maxResults`), `outgoingPreview[]` (≤ `maxResults`), and a `truncated:bool` flag. When `summarize:false` + `maxResults` set: array clipped + `truncated:true`.
- Optional follow-on (`INV-FILE-IMPACT-BUCKET-001` candidate name): mirror `blast_radius`'s `groupBy:"bucket"|"module"|"both"` so god-type files surface their Production/Test/Editor/Generated split + per-module count without a full enumeration.
- **Eternal-shape ratchet (`UniformListShapeRatchetTests` candidate name).** Add a registry-driven test on Lifeblood self that walks every read-side tool returning a `list[]` shape and asserts EACH carries the trio `summarize:bool` + `maxResults:int` + `truncated:bool`. Catches future tools that ship without the cap shortcut. This is the eternal version of the per-tool fix — the goal is uniform list-shape discipline, not papering one tool.

Cross-reference: same family as `INV-CYCLE-TAXONOMY-001` (cycles got summarize via `LB-TRACK-20260514-008`) + `INV-BLAST-RADIUS-GROUP-001` (blast_radius got groupBy via `LB-TRACK-20260514-003` follow-up). Both prior fixes establish the wire pattern; file_impact is the only same-shape tool that lags.

### LB-TRACK-20260524-027 - `lifeblood_static_tables` lacks `summarize` shortcut; default `maxRows:1024` too high for dispatch-table-heavy types

Status: Shipped (in-tree, untagged) — Wave 5 Stage 0 cleanup pass.
Closing commit: `2ae4266` `feat(static-tables): summarize flag + drop maxRows default 1024→32 (LB-TRACK-027)`. Default tuning: `DefaultMaxRows` 1024 → 32 (triage workflow floor, INV-STATIC-TABLES-DEFAULT-MAXROWS-001). New `Summarize:bool?` option on `StaticTablesOptions` forces hard caps `maxRows=3` + `maxTables=16` regardless of caller-passed values (INV-STATIC-TABLES-SUMMARIZE-001). Wire shape preserved — same `tables[]` / `rows[]` / `cells[]` / `truncated` flags, just smaller. No new DTOs / no new ports. Pinned by `StaticTableExtractorTests` (4 new facts: default-32 truncation, summarize forces rows + tables hard caps, summarize:false honors explicit caller-passed maxRows). Uniform-shape ratchet `INV-LIST-SHAPE-UNIFORM-001` (closing commit `cdb7691`) closes the silent-drift class. Both new INVs pinned as standalone bullets in `docs/invariants/tools.md` Static Table Extraction section (post-cleanup audit count). Original entry preserved below for regression-trace.
Type: Improvement (wire-shape uniformity + default tuning)
Source: DAWG Stage 0 dogfood 2026-05-24, Lifeblood v0.7.8-alpha.0.31
Workspace: DAWG
Verification:
- `lifeblood_static_tables typeId:"KernelCapabilityTable"` (no caps): 466,662 characters / 9,749 lines — overflows downstream tool-result cap. DAWG's `KernelCapabilityTable` carries multiple dispatch-table fields with hundreds of rows each (capability matrix + sub-reason names per axis).
- Re-probe with explicit caps `maxRows:5, maxTables:2`: returns 2 tables × 5 rows each, with `rowsTruncated:true` per table + top-level `tablesTruncated:true`. **The caps work correctly.** The issue is only that the default `maxRows:1024` is two orders of magnitude above what triage callers want.
- `lifeblood_static_tables` carries no `summarize:bool` shortcut. Caller must know `maxRows`+`maxTables` upfront. Inconsistent with `dead_code` / `cycles` / `blast_radius` / `test_impact` summarize convention.

Summary:
Two compound defects in default tuning + wire-shape uniformity. The existing `maxRows:1024` default + `maxTables:64` default were chosen to "fit everything" but fit nothing on real DAWG dispatch tables. There's no compact `summarize` mode. Together they make the tool unusable by default on the codebases where dispatch-table extraction matters most.

Impact:
- Identical UX hit to LB-TRACK-20260524-026 — every default invocation on a god-type table overflows downstream tool-result budget, forcing the `jq`-on-disk fallback path.
- Triage workflows ("does this table reference enum X?", "which method is wired into slot 7?") need ~5 rows visible + truncation flag; they don't need 1024 rows.
- Same wire-shape gap as `file_impact` — no `summarize` shortcut means the caller must remember tool-specific cap parameters instead of a uniform `summarize:true` pattern.

Fix shape (proposed — uniform with already-shipped list-shape tools):
- **Wire-shape (`INV-STATIC-TABLES-SUMMARIZE-001` candidate name).** Add `summarize:bool` (default false). When `summarize:true`: drop `rows[]` and `cells[]` content, return per-table `{ memberId, memberName, filePath, line, containerKind, elementTypeId, rowCount, rowsTruncated, sampleRow[] }` where `sampleRow` is capped at 3 rows for context. Top-level `tableCount` + `tablesTruncated`.
- **Default tuning (`INV-STATIC-TABLES-DEFAULT-MAXROWS-001` candidate name).** Drop default `maxRows` from 1024 → 32. The 1024 ceiling was a fence against accidentally-truncated extraction; the empirical floor for triage workflows is ~5–20 rows. Callers that need full extraction explicitly pass `maxRows:1024` (or `0` for unlimited if added).
- **Eternal-shape ratchet — same one as LB-TRACK-20260524-026.** The `UniformListShapeRatchetTests` proposal above closes both gaps in one fixture: walk every list-shaped read tool, assert summarize/maxResults/truncated trio. Pin once, the next list-shape tool can't ship without it.

Cross-reference: `LB-TRACK-20260514-005` shipped `lifeblood_static_tables` v1.0 + v1.1 with `MethodReturnFlagIds[]`; the tool's correctness story is settled. This entry is purely the UX wire-shape gap surfaced by DAWG-sized payloads — no semantics change.

### Stage 0 verification receipt (2026-05-24)

Status: Receipt (no action)
Type: Verification log
Source: DAWG Stage 0 dogfood 2026-05-24
Workspace: DAWG @ HEAD `31bb6e4bb`
Coverage: 30/30 MCP tools probed end-to-end on a live 90-module / 67,068-symbol / 247,350-edge Unity workspace.

Re-verified shipped claims (all hold):
- **LB-TRACK-20260515-012** (cross-partial private invocation, F1b). `find_references method:Nebulae.BeatGrid.AdaptiveBeatGrid.EnableAutoRotationDelayed() includeDeclarations:true` → `count: 2` (decl + Usage at `AdaptiveBeatGrid.Bootstrap.Events.cs:109` with proper `containingSymbolId`). EXACT match to original closure receipt.
- **LB-TRACK-20260515-013 / F3a–F3c** (port_health composite). `port_health type:Nebulae.BeatGrid.Transport.ITransportTimelineHost` → `memberCount: 24, liveMembers: 24, deadMembers: 0, verdict: "healthy", isCompositeInterface: true, inheritedInterfaces: [ITransportTimelineAnchor, ITransportTimelineClock, ITransportTimelineLoop, ITransportTimelineState, ITransportTimelineSubsystems]`. EXACT match.
- **LB-TRACK-20260515-014 / S4** (enum_coverage `dispatchTableReferenceCount`). `enum_coverage type:Nebulae.BeatGrid.Audio.DSP.Burst.FeatureId` returns 33 members; the 8 dispatch-routed members (`Waveform`, `FM`, `PWM`, `Formant`, `CrossMod`, `PitchEnvelope`, `Glide`, `Unison`) all carry `dispatchTableReferenceCount: 1`. `unproducedCount: 0, unreferencedCount: 0`. EXACT match.
- **LB-TRACK-20260515-015 / F2 + F1d** (dead_code `sameClassConsumerCount` + bare-identifier property/event reads). Re-probe target `BeatGridFXBusManager.DspTime` was renamed/removed in DAWG since the closure receipt; substituted `property:Nebulae.BeatGrid.Infrastructure.Audio.BeatGridAudioPool.DspTime` (current bare-identifier sibling-method property read): `dependants` returns `count: 3` with full `callSite` provenance (lines 226/600/649/694/915 — graph dedups 600+622 to one edge per INV-STREAM-005). `find_references` returns 6 source-text usages. F1d wire intact.
- **LB-TRACK-20260518-017 / F3e** (authority_report composite/inherited surface). `authority_report type:Nebulae.BeatGrid.AdaptiveBeatGrid` returns `implementedInterfaceCount: 7, ownedPublicSurface: 144, forwarderRatio: 0.354`. Drift +2 ownedPublicSurface from Polish-1 baseline 142 — within expected DAWG-side commit drift; matches the 2026-05-24 Wave 1 baseline re-probe receipt. All 4 composite ifaces (`IWheelMenuActions` direct=4 inherited=39, `IPianoRollRefreshCoordinatorHost` direct=0 inherited=35, `IMelodicGridRenderHost` direct=0 inherited=21, `ITransportTimelineHost` direct=0 inherited=24) surface aggregateMemberCount correctly.
- **LB-TRACK-20260518-019 / S7** (planning verdicts). `authority_report` carries `crossAssemblyConsumerCount: 4, sameAssemblyConsumerCount: 1628, hasSingleImplementer: null` on ABG. crossAssembly EXACT match; sameAssembly +47 vs prior baseline (DAWG drift).
- **LB-TRACK-20260519-021 / F1a** (extension-method ReducedFrom canonical-id). Validated indirectly via no-error find_references / dependants / authority on extension-using DAWG callers. No `directDependants:0` false positives surfaced on extension methods this session.
- **LB-TRACK-20260519-024 / F1d** (bare-identifier property/event reads). Covered by LB-TRACK-015 receipt above — `BeatGridAudioPool.DspTime` has 5 distinct semantic dependants with full callSite metadata, post-dedup; pre-fix would have shown 0.

Re-verified L-LIM closures (5/6 hold; L-LIM-001 still Wave 2):
- **L-LIM-002 incremental edge parity.** `analyze incremental:true allowFullFallback:true` (no-op) returns `mode: "incremental-noop", edges: 247350` — EXACT match to full baseline 247350.
- **L-LIM-003 incremental authority staleness.** `authority_report` returns fresh data with composite-aware fields; no stale interface-count or member-count surfaced.
- **L-LIM-004 execute IL2CPP + BCL.** `execute` ran trivial `Console.WriteLine(1+1)` AND complex multi-statement `Graph.SymbolsOfKind(...)` queries cleanly — no CS0009 (`GameAssembly.dll` PE-no-managed-metadata error), no CS0518 (`System.Object` missing). Output: `edges=247350 types=4098 methods=26929 props=5512 calls=36421 refs=138131 impls=4109 compilations=90`. BCL fully bound; IL2CPP native PE silently filtered per `INV-EXECUTE-001`.
- **L-LIM-005 test_impact reflection heuristic.** `test_impact target:"type:Nebulae.BeatGrid.AdaptiveBeatGrid" includeReflectionHeuristic:true` returns `affectedTestClassCount: 227, semanticEdgeHits: 2, reflectionHeuristicHits: 225, totalTestMethodCount: 1613`. The 225 ratchet/reflection tests previously invisible to pure BFS are now surfaced with `kind: ReflectionHeuristic`. `limitations[]` correctly names the heuristic boundary (`Type.GetType(computedString)` still invisible).
- **L-LIM-006 assignment_coverage.** `assignment_coverage targetTypeId:"type:Nebulae.BeatGrid.Tick.BeatGridTickHostBindings"` (resolver routes to `Nebulae.BeatGrid.Runtime.BeatGridTickHostBindings`): single `Proven` site at `AdaptiveBeatGrid.cs:138-181`, all 33 slots `Assigned`, zero `siteLimitations[]`. Wire shape solid.
- **L-LIM-001 multi-define.** NOT re-probed in this Stage 0 session (deliberately out of scope per the user's stage-split directive). **CLOSED later in the same day under Wave 6** (commits `43c1499..dd157af`) — see top-of-file Wave 6 close snapshot and DAWG `reference_lifeblood_known_limitations.md` L-LIM-001 closure receipt for the live multi-profile evidence.

Tools fully exercised (30/30):
| Tool | Probe target | Verdict |
|---|---|---|
| analyze (full) | DAWG | ✅ 67068/247350/90 |
| analyze (incremental noop) | DAWG | ✅ matches full edge count |
| compile_check (snippet+module) | `Nebulae.BeatGrid.Runtime` | ✅ 0 diagnostics, 142 definesActive |
| diagnose (file scope) | `AdaptiveBeatGrid.cs` | ✅ 0 diagnostics, possiblyStale:false |
| authority_report | ABG | ✅ composite surface OK |
| blast_radius (groupBy both + summarize) | `BeatGridTickHostBindings` | ✅ Production:399 / Test:9 / Editor:2 |
| context (summarize) | DAWG | ✅ 18 pure modules, 97 cycles |
| cycles (summarize) | DAWG | ✅ 97 cycles / LikelyRealLoop:30 / PartialClassCluster:67 |
| dead_code (Type kind, summarize) | DAWG | ✅ 1 finding (`DawgToolsPackageAssert`), bucket:Production |
| dependants | `BeatGridAudioPool.DspTime` | ✅ 5 deps w/ callSite |
| dependencies | `EnsureTickOrchestrator` | ✅ 79 deps w/ callSite |
| documentation | `BeatGridTickHostBindings` | ✅ xmldoc round-trip OK |
| enum_coverage | `FeatureId` | ✅ 33 members w/ dispatchTableReferenceCount |
| execute | DAWG globals | ✅ BCL bound, IL2CPP filtered |
| **file_impact** | `AdaptiveBeatGrid.cs` | ⚠️ overflow → LB-TRACK-026 |
| find_definition | `EnsureTickOrchestrator` | ✅ |
| find_implementations | `ITransportTimelineHost` | ✅ 2 implementers (ABG + RecordingHost test fixture) |
| find_references | `EnableAutoRotationDelayed()` | ✅ cross-partial OK |
| format | code roundtrip | ✅ |
| invariant_check (audit) | DAWG | ✅ 214 INV / 124 cat / 0 dup / 1 parseWarning (DAWG-side, low) |
| lookup | ABG | ✅ 159 partial filePaths surfaced |
| partial_view | `BeatGridTickHostBindings` | ✅ source + combined view OK |
| port_health | `ITransportTimelineHost` | ✅ composite verdict OK |
| **rename** | cross-partial + same-file | ❌ → LB-TRACK-025 |
| resolve_member | `AdaptiveBeatGrid.EnsureTickOrchestrator` | ✅ Unique |
| resolve_short_name | `DspTime` | ✅ 4 candidates surfaced |
| search | `canonicalize symbol id` | ✅ ranked w/ matchKind |
| **static_tables** | `KernelCapabilityTable` | ⚠️ overflow → LB-TRACK-027 |
| symbol_at_position | `AdaptiveBeatGrid.cs:138:35` | ✅ |
| test_impact (semantic+heuristic) | ABG | ✅ 227 affected (2 semantic + 225 reflection) |
| assignment_coverage | `BeatGridTickHostBindings` | ✅ 33/33 Proven |

DAWG-side observations (not Lifeblood bugs, surfaced incidentally):
- `invariant_check` parser warning: `D:/Projekti/DAWG/docs/invariants/oversample-stage-policy.md:29` — id `INV-OVS-ENGAGEMENT-7` (the full id is `INV-OVS-ENGAGEMENT-7-AXIS-001`) didn't match shape A/B/D/E; fell back to bullet-prefix title extraction. Lifeblood parser regex apparently rejects embedded digit inside the category segment. Could be tightened parser-side (separate atom) or DAWG-side (rename to `INV-OVS-7AXIS-ENGAGEMENT-001`). Filed here as observation only.
- DAWG-side `dead_code` finding: `type:Nebulae.Systems.DawgToolsPackageAssert` shows no incoming semantic references. Bucket=Production, declarationOnly:false. Advisory — likely Unity reflection-bound (`[InitializeOnLoadMethod]` or similar). DAWG triage, not Lifeblood.
- `BeatGridFXBusManager` (cited in LB-TRACK-015 closure receipt) was renamed/removed in DAWG between 2026-05-19 and 2026-05-24. Lifeblood resolver correctly refused substitution per INV-RESOLVER-007 — not a Lifeblood bug. Memory file `feedback_label_precision_post_block_or_undrained.md` could be refreshed in DAWG memory.

## Legacy Source Map

These files remain useful background, but this tracker is the source of truth
from 2026-05-14 forward.

### 2026-05-11 DAWG field report -> Lifeblood v0.7.3 input

File: `.claude/devmemory/lifeblood-field-report-2026-05-11.md`

High-value asks:

- Member resolver: shipped in v0.7.3 as `lifeblood_resolve_member`.
- Dependency call-site provenance: shipped in v0.7.3 as `Edge.CallSite` and
  dependency/dependant wire fields.
- Blast-radius grouping: shipped in v0.7.3 as `groupBy`.
- Static table extraction: open as `LB-TRACK-20260514-005`.
- Stale graph warnings: partially addressed by existing staleness behavior, but
  not tracked here until a fresh v0.7.3+ repro proves remaining friction.
- Test impact suggestions: open as `LB-TRACK-20260514-007`.
- Table-to-predicate drift checks: open as `LB-TRACK-20260514-006`.

### 2026-05-13 DAWG post-reconnect sweep -> Lifeblood v0.7.3 follow-up

File: `docs/invariants/eternal-arch-audit-2026-05-12.md`

Useful Lifeblood-only follow-ups:

- `System.Math` diagnostic collision: closed via `LB-TRACK-20260514-001`
  (committed to Lifeblood `main` `e1acbe3`, DAWG-verified 2026-05-14).
- `dead_code` triage fields: closed via `LB-TRACK-20260514-004`
  (committed to Lifeblood `main` `68fd4a2`, DAWG-verified 2026-05-14).
- Dependency cycle taxonomy: closed via `LB-TRACK-20260514-008`
  (committed to Lifeblood `main` `d5482a3`, DAWG-verified 2026-05-14).

DAWG-only architecture numbers from that report are intentionally not repeated
here unless they expose a Lifeblood product issue.

### 2026-04-10 DSP audit feedback -> historical pre-v0.7 input

Historical file in DAWG git history:
`.claude/plans/lifeblood-feedback-2026-04-10.md`

Important historical asks that later informed the product:

- `find_references.containingSymbolId` / call-site context.
- Primitive type aliasing in symbol ids.
- Auto-refresh stale workspace behavior for compile/check loops.
- Dead-code scan.
- Partial-type combined view.
- Semantic invariant checks.
- Blast-radius break kind.
- Containing-type filtering on references.

Do not add new work to the legacy file. Promote any still-current item here with
a Lifeblood version and a fresh repro.

### LB-TRACK-20260530-028 - `lifeblood_analyze` can miss newly-created Unity test files before Unity import creates `.meta`

Status: Shipped (in-tree, untagged) — [Unreleased] cheap bug-first pass 2026-05-30
Type: Bug / stale-workspace correctness
Resolution: Two-part close. (1) `compile_check` now distinguishes "path not
found" from "on disk but not in any loaded compilation" via the typed
`CompileCheckResult.FileResolution` enum + a handler-composed `staleDescriptorHint`
(`INV-COMPILE-CHECK-FILE-RESOLUTION-001`, `CompileCheckFileResolutionTests`).
(2) The `lifeblood_analyze` tool description no longer over-promises pre-meta
disk pickup — discovery is descriptor-driven and the honest contract points at
the `compile_check` signal (`INV-UNITY-NEWFILE-DISCOVERY-HONESTY-001`,
`AnalyzeDescriptionHonestyTests`). The raw pre-meta disk sweep stays deferred.
Source: DAWG Burst-first dogfood, 2026-05-30, Lifeblood v0.7.10+e98a9b7c
Workspace: DAWG (`D:/Projekti/DAWG`, Unity project, multi-profile Editor+Player)

Observed:
- During the S2 Burst event-boundary slice, a new file `Assets/Tests/Editor/Audio/Burst/BurstEventBoundaryOracleTests.cs` was created on disk before Unity had imported it / generated the sibling `.meta`.
- A full Lifeblood analyze on the DAWG project did not include the new file in the graph; the file count stayed at 3671.
- After `refresh_unity` generated `BurstEventBoundaryOracleTests.cs.meta`, `lifeblood_compile_check(filePath, staleRefresh:true)` resolved the file in module `Nebulae.Tests.Audio.Burst`, auto-refreshed the workspace, and the graph file count moved to 3672.
- This contradicts the current tool description claim that a `.cs` file added to disk but not yet seen by Unity (no `.meta` sibling) will be picked up by Lifeblood's incremental walker on the next analyze.

Impact:
- AI agents can add a new Unity test/source file, run Lifeblood analyze immediately, and receive a Proven-looking graph that omits the new file until Unity import catches up. That can produce stale test-impact, dependency, and compile-planning decisions in the exact edit-then-analyze loop Lifeblood is meant to support.
- The failure mode is subtle because `compile_check(filePath, staleRefresh:true)` later succeeds, making the earlier analyze result look like user error rather than a graph refresh gap.

Suggested fix shape:
- Reproduce in a Unity-shaped fixture: create a new `.cs` under an asmdef-owned folder without a `.meta`, run full analyze and incremental analyze before Unity import, assert the file is discovered and assigned to the owning module.
- If the current module discovery intentionally filters on Unity `.meta` or generated csproj membership, update the tool description and truth-envelope limitations to state that new Unity files may require Unity import before graph analysis, while `compile_check(filePath)` can still bind the file after stale refresh.
- Eternal preference: make analyze and compile_check share the same source-file discovery policy so file counts, module resolution, and compile diagnostics converge before Unity import. If Unity `.meta` GUID stability is the blocker, surface a limitation field naming the temporary identity.

Acceptance:
- Regression test covering new-file/no-meta discovery in a Unity-shaped workspace.
- Tool description and STATUS/known-limitations updated to match the implemented behavior.

### LB-TRACK-20260530-029 - MCP transport closure after parallel compile-check leaves no reconnect path

Status: Shipped (in-tree, untagged) — [Unreleased] cheap bug-first pass 2026-05-30
Type: Bug / session resilience / diagnostic envelope
Resolution: Root cause was NOT a server-side concurrency race — the stdio loop is
serial by construction, so an MCP client's parallel batch is serialized
server-side. The real faults: a broken-pipe IOException in the loop's own
error-path write escaped and killed the process (permanent transport close), and
error responses dropped the request id. The loop is extracted to a testable
`McpServerLoop` (`INV-MCP-TRANSPORT-RESILIENCE-001`): dispatch faults → id-
correlated `-32603` with a structured recovery `data` envelope; serialize faults
→ id-correlated error not silence; broken-pipe writes logged + swallowed. No
single-flight guard (would be theater on a serial loop, INV-WORK-005/007). Pinned
by `McpServerLoopTests` (4 facts). The proposed structured fatal envelope shipped
as the JSON-RPC 2.0 `data` member on `JsonRpcError`.
Source: DAWG Burst-first dogfood, 2026-05-30, Lifeblood v0.7.10 session
Workspace: DAWG (`D:/Projekti/DAWG`, Unity project, multi-profile Editor+Player)

Observed:
- During the L4a bound-patch pass, one `lifeblood_compile_check(filePath)`
  succeeded, while sibling compile-check calls issued in the same parallel tool
  batch returned only `Transport closed`.
- After that point every direct Lifeblood MCP call retried in the session
  (`lifeblood_capabilities`, `lifeblood_analyze`) failed immediately with the
  same `Transport closed` message.
- The result carried no structured envelope naming whether the Lifeblood server
  process crashed, the MCP client transport closed, a request payload overflowed,
  or a concurrent request violated a server-side single-flight assumption.
- Unity/Roslyn compile and targeted NUnit tests remained healthy, so the DAWG
  work could continue, but the mandatory Lifeblood lane was unavailable for the
  rest of the checkpoint.

Impact:
- Agent workflows treat Lifeblood as the semantic trust source. A bare transport
  close during parallel compile-check leaves the agent unable to decide whether
  to retry serially, force a fresh analyze, restart the MCP server, reduce
  payload size, or file a product bug.
- The failure is especially expensive in Unity sessions where compile checks
  are often batched after source edits. A single transport collapse turns the
  semantic lane from Proven to unavailable with no recovery receipt.

Suggested fix shape:
- Add a structured top-level failure envelope for MCP-side fatal errors:
  `phase`, `activeTool`, `requestId`, `workspaceRoot`, `profile`, `isConcurrent`,
  `serverPid` if known, and a bounded exception/stack excerpt when available.
- If Lifeblood is intentionally single-flight for write-side/Roslyn compilation
  tools, reject concurrent compile-check calls explicitly with a recoverable
  `RetrySerially` outcome instead of letting the transport die.
- Add a smoke regression that runs two compile-check requests concurrently
  against a large Unity-shaped workspace snapshot. Acceptance is either both
  succeed, or one returns a structured recoverable rejection while subsequent
  `lifeblood_capabilities` and `lifeblood_analyze` calls still work.
- Document the recovery posture in the tool descriptions: whether agents should
  retry serially, run full analyze, or reconnect the MCP server.

Acceptance:
- Parallel compile-check stress no longer closes the MCP transport.
- If a fatal exception still occurs, the wire result is structured enough to
  route the bug without external logs.
- Subsequent read-side calls after a failed compile-check still succeed, or the
  client receives an explicit server-restart-required status.

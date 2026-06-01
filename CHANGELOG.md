# Changelog

All notable changes to Lifeblood are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.7.11] - 2026-06-01

**Final release state:** 1323 discovered / **1312 passed / 0 failed**
/ 11 native-clang skips; self-analyze **0 violations / 0 cycles, 4394 symbols / 25106 edges**;
31 MCP tools, 30 ports, 160 invariants. Authoritative live counts live in `docs/STATUS.md`
(truth-receipt SSoT) — the per-pass deltas below are historical within this release span,
not the final-state numbers.

DAWG-dogfood cheap bug-first pass (2026-05-30). Six trust-and-robustness fixes
surfaced during a DAWG Burst session, reconciled from `IMPROVEMENT_INBOX.md` +
`devmemory/lifeblood-tracking.md`. Full suite 1258 passed / 0 failed / 11
native-clang skips. Self-analyze 0 violations / 0 cycles. Tests 1228 → 1318;
invariants 150 → 160; self symbols 3834 → 4309 / edges 23020 → 24854.

### Added

- **MCP argument contracts + JSON compatibility modes** (`INV-MCP-TOOL-ARG-CONTRACT-001`, `INV-MCP-STRICT-JSON-001`). `ToolInputContract.FromSchema` projects registered input schemas into typed contract metadata at the handler/composition edge; `ToolArgumentBinder` validates tool arguments under `LIFEBLOOD_JSON_COMPAT=legacy|warn|strict`. Legacy remains default, warn accepts with `lifeblood.tool.arguments` telemetry, strict rejects invalid arguments with structured diagnostics, and `LIFEBLOOD_STRICT_JSON` remains the strict alias.
- **Analyze phase telemetry and session gate** (`INV-TELEMETRY-001`, `INV-MCP-SESSION-GATE-001`). `GraphSession` records real `lifeblood.analyze.phase` scopes/events with allocation deltas; invariant cache lookup telemetry now emits after the cache lock is released. `GraphSessionGate` keeps retained-session mutation at the MCP host boundary: read-side calls share read access, while analyze and compile-check stale refresh use the write gate.
- **Runtime diagnostics gauges.** The opt-in .NET diagnostics sink now exports process working-set bytes, private bytes, managed heap bytes, and process thread count as observable gauges, pinned by a real `MeterListener` measurement test.
- **Session-gate stress ratchets** (`INV-MCP-SESSION-GATE-001`). `GraphSessionGateTests` now pin reader/writer exclusion in both directions plus writer serialization, and `ToolHandlerTelemetryTests` verifies `lifeblood_compile_check` uses the write gate because stale refresh can replace the retained session.
- **.NET adoption lanes**. Runtime benchmark script now reports expanded workloads (`analyze`, `context`, `incremental-noop`, `cli-help`) with category metadata and parse-duration measurements. The CLI and MCP benchmark reports share a `benchmarkRunId`; the MCP GC benchmark passes it into the child process as `LIFEBLOOD_BENCHMARK_RUN_ID`, then runs retained read-side tools after analyze and records per-tool dispatch latency/response-size completion evidence. A benchmark-only JSON parser harness now compares the current string parser with UTF-8 span and buffered `PipeReader` parser shapes in legacy and strict modes; production MCP transport remains unchanged until the report proves a real win. Tool-packaging smoke records optional `dotnet tool exec` / `dnx` help checks when available and honest skips otherwise. Runtime Async fixtures now cover diagnose, compile-check file mode, and compile-check snippet mode for `<Features>runtime-async=on</Features>` projects; the opt-in Runtime Async benchmark lane injects that compiler feature only into a copied experimental tree. Local .NET 11 preview evidence (`11.0.100-preview.4.26230.115`) restores/builds/tests the copied Runtime Async tree, matches production semantic counts, and completes retained MCP read-side dispatch. The lane also closed the synthetic implicit-global-usings feature-parity defect found by that run.
- **JSON parser benchmark evidence.** The standalone parser harness now has a local execution receipt, and strict JSON mode reuses the UTF-8 bytes already required for duplicate-property detection when deserializing the envelope. Buffered `PipeReader` remains measurement-only because it is semantically equivalent but allocates more per operation under the current newline-framed stdio transport.
- **Source-generated JSON parity.** The C# adapter now discovers framework-reference source-generator analyzers from the target framework ref pack and runs them before extraction/diagnostics, so generator-created members are visible to Lifeblood. MCP request parsing adopts a generated `JsonRpcRequest` context without mutating the shared serializer options; dynamic response payloads remain on the existing serializer path.
- **.NET 10 experimental target evidence.** The experimental target lane now accepts explicit dotnet host, package-source, CLI-home, and workdir controls. Local `net10.0` receipt with SDK `10.0.300` restored, built, ran the full test suite, matched production self-analysis anchors, and packed both CLI and MCP tools from the copied tree without changing production TFMs.
- **Packaging/distribution evidence.** Local Windows `win-x64` tool-packaging receipt packed and installed both tool entry points, validated CLI help output, smoke-tested MCP closed-stdin startup/shutdown, and ran report-only publish experiments. Framework-dependent and self-contained publish shapes completed where supported; CLI AOT records an honest native-linker-prerequisite skip, and MCP trim/AOT remain intentionally skipped until Roslyn compatibility evidence supports them.
- **.NET 10 experimental evidence receipts.** `run-lifeblood-experimental-target.ps1` now records schema snapshot inventory, parses the experimental test summary, runs an experimental CLI self-analyze, and compares test/semantic counts against the production `docs/STATUS.md` anchors before any production TFM migration is considered. The lane also serializes restore, can opt into cached/offline restore via `-RestoreIgnoreFailedSources`, and falls forward to a fresh temp work directory when a previous experimental tree is locked.
- **Tracking ledger SSoT ratchet** (`INV-TRACKING-SSOT-001`). `TrackingLedgerTests` parses `devmemory/lifeblood-tracking.md` entry bodies, keeps status summary anchors honest, pins the active backlog to the `Partially shipped` entries, and requires every partial entry to declare its remaining open work.
- **Enum-aware tool argument contracts.** `ToolInputContract` now preserves declared schema enum values, can regenerate every registered tool input schema byte-stably through the existing canonicalizer, and `ToolArgumentBinder` rejects enum values outside the schema in strict mode.
- **Typed contract-backed capability flags.** `lifeblood_capabilities.featureFlags.summarizeCapableTools` now derives from `ToolInputContract` argument metadata instead of serializing input schemas and searching for a `"summarize"` token. The existing capabilities wire shape is unchanged; `ToolHandlerTests` pins the flag list against the typed contract SSoT.
- **Authoritative MCP input contract catalog.** `ToolInputContractCatalog` now owns registered MCP tool argument names, types, required flags, enum values, and descriptions. `ToolDefinition.InputSchema` is generated from the typed contract, and `ToolRegistry` no longer authors anonymous schema objects. Snapshot tests still pin the `tools/list` wire shape byte-stably.
- **High-risk MCP request-record binding.** `ToolRequestBinder` binds `lifeblood_analyze` and `lifeblood_compile_check` through typed request records, preserving back-compatible handler defaults such as `compile_check.staleRefresh=true` while keeping MCP field names in `Lifeblood.Server.Mcp`. Source-generated request contexts stay in the remaining .NET adoption lane until Lifeblood diagnostic parity supports generator output.
- **Report-only packaging experiments.** `run-lifeblood-tool-packaging.ps1` now validates CLI help output, records opt-in framework-dependent / self-contained / trim / AOT publish experiments under `artifacts/tool-packaging`, and marks unsupported Roslyn-heavy trim/AOT paths as honest skips instead of release gates.
- **Telemetry event-name SSoT** (`INV-TELEMETRY-002`). `McpTelemetryEvents` centralizes every emitted server-edge event name; `ServerIdentity` advertises it verbatim and the emit sites reference the constants, closing the gap where `lifeblood.analyze.phase` was emitted and documented but missing from the advertised capability list. Pinned by `McpTelemetryEventsTests` and `ToolHandlerTests`.

### Fixed

- **`execute` workspace-type runtime-load boundary** (`INV-EXECUTE-WORKSPACE-LOAD-BOUNDARY-001`, LB-TRACK-20260530-031). Scripts compile against workspace/engine types (injected as Roslyn metadata references) but those assemblies are not loaded into the host runtime, so instantiation / `Unsafe.SizeOf<T>` / reflection over a workspace type threw `FileLoadException` and the executor leaked the raw "Could not load file or assembly" message. `RoslynCodeExecutor` now classifies the exception chain against the known workspace module set and returns a structured compile-against-not-run `targetRuntimeWarnings` boundary. Pinned by `ExecuteRobustnessTests`.
- **`compile_check` file-resolution states** (`INV-COMPILE-CHECK-FILE-RESOLUTION-001`, LB-TRACK-20260530-028). LB0002 collapsed "pinned-module miss", "matched no loaded compilation", and "resolved" into one opaque message. New typed `CompileCheckResult.FileResolution` enum; the file-mode handler (which proves on-disk presence) surfaces a `staleDescriptorHint` for the on-disk-but-not-in-compilation case. Pinned by `CompileCheckFileResolutionTests`.
- **full `analyze` structured failure** (`INV-ANALYZE-STRUCTURED-FAILURE-001`, LB-INBOX-012). A pipeline fault (e.g. NullReference after Unity asset-import churn) surfaced as a bare "Object reference not set to an instance of an object.". `RoslynWorkspaceAnalyzer` tracks a phase/module/file/profile cursor and wraps unexpected faults in a new `WorkspaceAnalysisException`; `ToolHandler.HandleAnalyze` serializes the structured context. Validation exceptions (`ArgumentException`) propagate unchanged. Pinned by `WorkspaceAnalysisFailureTests`.
- **MCP stdio transport resilience** (`INV-MCP-TRANSPORT-RESILIENCE-001`, LB-TRACK-20260530-029). A broken-pipe `IOException` in the loop's own error-path write escaped and terminated the process, closing the transport permanently; error responses also dropped the request id. The read-dispatch-write loop is extracted to a testable `McpServerLoop`: dispatch faults become id-correlated `-32603` responses with a structured recovery `data` envelope, serialization faults still emit an id-correlated error, and broken-pipe writes are logged and swallowed. `JsonRpcError` gains the JSON-RPC 2.0 `data` member. Pinned by `McpServerLoopTests`.

### Changed

- **`compile_check` / `diagnose` compact verbosity** (`INV-DIAGNOSTIC-ENVELOPE-VERBOSITY-001`, LB-TRACK-20260530-030). Additive `verbosity:"compact"` drops the full `definesActive[]` list (150+ entries on a Unity profile) while keeping `definesActiveCount`; default verbose wire shape is byte-stable. `v1` input-schema snapshots updated.
- **`analyze` tool description honesty** (`INV-UNITY-NEWFILE-DISCOVERY-HONESTY-001`, LB-TRACK-20260530-028). Removed the false "a `.cs` file ... WILL be picked up by the incremental walker" claim — discovery is descriptor-driven (`.csproj` / asmdef membership); a pre-import file needs project-file regeneration. Points at the `compile_check` `staleDescriptorHint` as the pre-import path. Pinned by `AnalyzeDescriptionHonestyTests` (source-text ratchet).

## [0.7.10] - 2026-05-29

### Added

- `lifeblood_capabilities` read-side MCP tool (tool 31, read-side 18) reports the live server version + version source, optional git commit / dirty state when running from a repo checkout, tool count with read/write split, feature flags (including the active operational-telemetry event names and `summarizeCapableTools`), `schemas/tools/v1` snapshot path, `STATUS.md` anchor path, and current session state. Call it at session start to detect local-server / local-doc drift before relying on stale prose.
- Docs-safe `evidenceReceipt` blocks on `lifeblood_analyze` and `lifeblood_invariant_check` audit responses. Each receipt separates durable citation facts (server identity, source-control state, query recipe, counts) from session-local envelope freshness via an explicit `doNotCite[]` list, and `invariant_check` adds per-source invariant counts (`SourceCounts[]`).
- Opt-in operational telemetry (`LIFEBLOOD_TELEMETRY`) via the `ITelemetrySink` port. Events: `lifeblood.tool.success_result` / `error_result` / `exception` / `response_json` / `truncated`, `lifeblood.analyze.result` / `fallback`, and `lifeblood.cache.lookup` (`hit` / `miss` / `stale` / `missing` / `error`). The default sink is no-op; the `DotNetDiagnosticsTelemetrySink` maps to .NET `ActivitySource` / `Meter` only when opted in.

### Fixed

- `lifeblood_invariant_check` audit now detects **cross-file** duplicate invariant ids (the same `INV-X` declared in both `CLAUDE.md` and `AGENTS.md`, for example), not just within-file repeats. The prior aggregation deduped cross-file ids silently, so a repo that mirrored invariants across sources produced an unreconcilable receipt — per-source counts summed to more than the unique total with an empty `duplicates[]`. Each duplicate now carries a per-site `occurrences[]` ledger (`sourcePath` + `line` + `title`), and the audit reports `declaredCount` (Σ of per-source declaration sites) and `duplicateDeclarationCount` (= `declaredCount` − `totalCount`) so the math reconciles. `DuplicateInvariantId.SourceLines` is retained for v1 compatibility but marked deprecated in favour of `Occurrences`. `sourceCounts` now counts declaration sites (within-file repeats included) so it sums to `declaredCount`.
- `tools/dotnet-lanes/run-lifeblood-experimental-target.ps1` demotes native `dotnet` stderr handling under Windows PowerShell 5.1 so successful commands that write warnings to stderr are governed by exit code rather than `NativeCommandError` wrapping, and serializes the experimental build step (`-maxcpucount:1`) to avoid duplicate project-reference writes under a solution-level `TargetFramework` override.

### Documentation

- Brought the public doc surface to 1:1 with code for release prep: tool count corrected to 31 across `README.md`, `docs/MCP_SETUP.md`, `docs/TOOLS.md` (added the missing `lifeblood_assignment_coverage` row + the `Capabilities` / `Assignment Coverage` entries in the README tool table); documented the four server environment variables (`LIFEBLOOD_TELEMETRY`, `LIFEBLOOD_STALENESS_SECONDS_THRESHOLD` default `3600`, `LIFEBLOOD_FILES_CHANGED_THRESHOLD` default `10`, `LIFEBLOOD_STRICT_JSON`) in `docs/MCP_SETUP.md`; completed the `docs/ARCHITECTURE.md` port-interface list (now all 30, adding `IDefineProfileResolver`, `ITelemetryOperation`, `IPortHealthAnalyzer`); and removed a drifted read-side tool count from `INV-PORT-HEALTH-ANALYZER-SEAM-001`.

## [0.7.9] - 2026-05-24

External-review-driven release. Closes one second-pass blocker (write-side
profile scope honesty, `INV-MULTI-DEFINE-WRITESIDE-001`), fixes the
multi-profile incremental cross-project edge drop the first-pass review
caught (`fix(adapter)`: per-profile `DowngradedRefsByProfile` carry), fixes
the incremental-noop summary zeroing, and refreshes the public-surface
truth contract (RELEASE.md skip policy split, NATIVE_CLANG.md local/manual
audit-gate wording, ci.yml + build.yml overlap collapsed). Two new
ratchet files pin the contracts (`MultiProfileCrossModuleIncrementalTests`
+ `MultiProfileWriteSideScopeTests`, 9 new facts). The 0.7.8 → 0.7.9
delta lands ten commits between tag `v0.7.8` (`f8e50bb`) and tag `v0.7.9`
(this release).

Tests 1191 → 1228 (+37 across the chain: write-side scope ratchet +
multi-profile cross-module incremental ratchet + the docs-ratchet
refreshes). Skipped: 11 (native-clang opt-in `[SkippableFact]` lane —
unchanged). INVs 149 → 150 across 98 → 99 categories (one new:
`INV-MULTI-DEFINE-WRITESIDE-001`). Self-analyze: 3,834 symbols /
23,020 edges / 11 modules / 399 types / 0 violations / 0 cycles. All
public-surface anchors aligned to live source via the existing
`DocsTests.Anchor_MatchesLiveSource` ratchet.

The 2026-05-24 masterplan (`docs/plans/MASTERPLAN-2026-05-24.md`) lands Waves 1 / 3 / 4 (re-probe + drift-ratchet, test-impact reflection heuristic, assignment-coverage) closing five of six DAWG-side limitations (L-LIM-002 / -003 / -004 / -005 / -006). A second-pass DAWG Stage 0 dogfood surfaced and fixed three wire-shape gaps (LB-TRACK-20260524-025 / -026 / -027) under Wave 5. **Wave 6 (`docs/plans/multi-define-union-l-lim-001-plan-2026-05-24.md`) ships multi-define union analyze in six phases (6.A → 6.F) closing the last open DAWG limitation L-LIM-001** — `IDefineProfileResolver` port + `UnityDefineProfileResolver` 2-profile MVP (Editor + Player) + `Edge.Profiles[]` per-edge provenance + GraphBuilder union dedup + `profileFilter` narrowing + IOperation `profileScope` discipline. Live DAWG dogfood receipt: edges 247,350 (single-profile) → 247,460 (Editor+Player union) = +110 Player-only edges recovered, canonical L-LIM-001 trap edges restored. Lifeblood self-analysis snapshot (symbols, edges, modules, types, violations, cycles), test discovery, and invariant audit are anchored in [`docs/STATUS.md`](docs/STATUS.md) and ratcheted against the live source on every CI run via `Anchor_MatchesLiveSource` in [`tests/Lifeblood.Tests/DocsTests.cs`](tests/Lifeblood.Tests/DocsTests.cs).

### Added

- **`lifeblood_assignment_coverage` MCP tool** (Wave 4 / `INV-ASSIGNMENT-COVERAGE-001..004`). Per-construction-site slot coverage for a target type: for each `new TargetType { ... }` or `new TargetType()` + statement-level assignment site, walks the containing method's `IOperation` tree and reports which of the target's public mutable slot members are assigned at that site and which are absent. Sister tool to `lifeblood_static_tables` — same Compilation-required convention, same `WriteToolHandler` placement, same operation-tree-only contract. Per-site `confidence` reflects construction shape: inline object-initializer OR single-method statement-level chain on non-aliased local before escape is `Proven`; factory-constructed, aliased, or branched MAY-assign sites are `Advisory` with the bumping shape named in `siteLimitations[]` (`FactoryConstruction` / `AliasedLocal` / `BranchedMayAssign` / `PostEscapeAssignment`). Per-slot `status` is `Assigned` / `Absent` / `AssignedNull` (null-literal assignment is distinct from absent so a caller can tell 'forgot to wire' from 'deliberately wired null'). Per-slot `expressionKind` classifies the assignment (`Lambda` / `MethodGroup` / `FieldReference` / `PropertyAccess` / `NullLiteral` / `Other`). Default slot enumeration is "delegate-typed public mutable fields + properties" (the Bindings shape that motivated the tool); optional flags toggle non-delegate mutable surface. Domain DTOs (`AssignmentCoverageReport` + `AssignmentCoverageSite` + `AssignmentCoverageSlot` + canonical string sets) carry zero Roslyn dep. Adapter `RoslynAssignmentCoverageExtractor` lives in its own file per `INV-ADAPTER-THIN-001`. Port: extension of EXISTING `ICompilationHost.GetAssignmentCoverage` (no new port — corrected from v1 plan). Closes DAWG L-LIM-006; unblocks DAWG Polish-1 P4 `BindingsClosureCoverageRatchetTests`. Pinned by `AssignmentCoverageExtractorTests` (11 facts: inline / statement / mixed / partial / lambda / method-group / field-reference / null-literal / post-escape / branched / aliased).

- **`lifeblood_test_impact` reflection heuristic** (Wave 3 / `INV-TEST-IMPACT-REFLECTION-001..003`). Opt-in `includeReflectionHeuristic: true` enables a post-BFS source-text scan for ratchet / reflection tests that reach the target via `typeof(T)` / `nameof(T)` / `Type.GetType("FQN")` / qualified-name string literals — the BFS over `Calls` / `References` incoming edges cannot see these patterns. Heuristic scans each test method's containing file once for the target's FQN as a source-text substring, AND for the bare short name only when the file also contains the target's namespace as a substring OR the short name is globally unique. Hits surface as `kind: ReflectionHeuristic` rows alongside semantic-edge `kind: Semantic` rows. Top-level `semanticEdgeHits` + `reflectionHeuristicHits` totals make the dual-source-of-truth visible per-call. Wire-shape change is purely additive — back-compat callers reading only v0.7.8 fields keep working byte-stable when the heuristic is omitted (default false). Honestly approximate: every heuristic-active response carries a `limitations[]` entry naming the source-text scan's known gaps (`Type.GetType(computedString)` remains invisible; comments / identifier names containing the FQN can false-positive). Implementation lives in `Lifeblood.Analysis` as a pure Domain-dependent algorithm; source-text reader passed as `Func<string, string?>` delegate so the analyzer stays free of `IFileSystem` dependency. Handler-side `ToolHandler.ReadFileSafe` wraps `_session.FileSystem` with try/catch + null-on-failure, keeping file I/O policy at the connector boundary. Closes DAWG L-LIM-005. Pinned by `TestImpactReflectionHeuristicTests` (5 facts: FQN-literal match, nameof source-text match, short-name without namespace context rejected, wire-shape preserved when disabled, Limitations[] populated when active).

- **`INV-DOCS-005` + `INV-DOCS-006` + `INV-DOCS-007` invariant + skipped-count ratchets** (Wave 1 Atom 1.5 + reviewer pass). Closes the drift class where `docs/STATUS.md` prose ("122 typed invariants across 83 categories", "zero skipped") silently outlived the live invariant tree + test discovery. Sibling to the existing `portCount` / `toolCount` / `testCount` ratchets. New STATUS.md HTML anchors: `<!-- invariantCount: N -->`, `<!-- invariantCategoryCount: N -->`, `<!-- skippedCount: N -->`. `DocsTests` adds `StatusDoc_InvariantCount_MatchesLiveAudit` + `StatusDoc_InvariantCategoryCount_MatchesLiveAudit` + `StatusDoc_SkippedCount_MatchesLiveDiscovery` + `StatusDoc_VisibleSkippedCount_MatchesHiddenAnchor` — any future rename / addition / removal of an invariant or `[SkippableFact]` fails the ratchet unless the STATUS.md anchor moves in the same commit.

### Added

- **`INV-MULTI-DEFINE-WRITESIDE-001` — write-side Roslyn tools surface profile scope on every response** (external review pass 2026-05-24, second-pass blocker). `lifeblood_find_references`, `lifeblood_find_definition`, `lifeblood_find_implementations`, `lifeblood_rename` now carry `analyzedUnderProfile` + an inline `limitations[]` entry on multi-profile snapshots naming the retained profile + the other-profile gap + switch instructions. All four also accept optional `profileScope:string?` that fails loudly when mismatched against the retained profile (parallels `INV-MULTI-DEFINE-IOP-001` on the IOperation tools). Closes the contract gap the second-pass review caught: the original Wave 6 changelog claimed `find_references` was closed end-to-end for L-LIM-001 because graph `dependants` got `profileFilter` over the union graph, but the live Roslyn write-side handlers still queried only the retained compilation host. The honesty fix is additive (existing callers ignore the new fields) and re-aligns the Wave 6 doc surface with the actual scope of the fix. Pinned by `MultiProfileWriteSideScopeTests` (7 facts: analyzedUnderProfile + limitations on all four tools under multi-profile; profileScope mismatch errors; profileScope match succeeds; single-profile graphs emit empty limitations). Tests 1221 → 1228. INV count 149 → 150 across 98 → 99 categories.

### Fixed

- **Multi-profile incremental drops cross-project edges on caller-touch** (`INV-MULTI-DEFINE-INCREMENTAL-001` + `INV-INCREMENTAL-XREF-001` cross-product, external review pass 2026-05-24). Pre-fix `AnalysisSnapshot.DowngradedRefs` was a single dict keyed by module — only the first-profile (Editor) PE images were carried across calls; non-first-profile (Player) incremental passes received `carryDowngraded:null` AND only recompiled changed modules. Under that combination, a changed caller in module B with a `#if PLAYER_ONLY` reference to module A had no Player PE image for A available during recompile; the cross-project Player edge bound to a Roslyn error symbol and was silently dropped by `GraphBuilder`'s dangling-edge filter. Scratch repro: edge count 10 → 7 on caller-touch, `perProfileEdgeCounts.Player` → 0. Fix replaces `DowngradedRefs` with `DowngradedRefsByProfile` (outer dict keyed by profile name, inner by module name); both `AnalyzeWorkspace` and `IncrementalAnalyze` now retrieve / write the carry under the SAME profile they are compiling, so each profile's PE images survive across incrementals. Pinned by `MultiProfileCrossModuleIncrementalTests` (2 facts: cross-project Player edge survives caller-touch with provenance intact; per-profile edge counts stable across 3 repeated touches).

- **`incremental-noop` response zeros out modules/types/files/violations/cycles** (external review pass 2026-05-24). `GraphSession.LoadIncremental` passed `analysis:null` into `BuildLoadResult` on the no-change incremental path; the summary block fell back to 0 for every analysis-derived metric because the `analysis?.X ?? 0` chains had nothing to read. Graph is unchanged on noop so the prior `_session.Analysis` is still valid — noop now reuses it and surfaces real counts. The wire-shape difference between "no changes" and "no graph loaded" stops being indistinguishable to summary-readers.

- **DAWG L-LIM-002 / L-LIM-003 / L-LIM-004 confirmed CLOSED under v0.7.8** (Wave 1 Atoms 1.1 / 1.2 / 1.3 re-probe receipts; DAWG `reference_lifeblood_known_limitations.md` resolution section appended 2026-05-24). L-LIM-002 (incremental edge drop): five edge-count samples (full / no-change incremental / leaf-touch incremental / hub-touch incremental / ground-truth full) all match at 247350 against DAWG @ HEAD `5b513ff`. L-LIM-003 (authority stale post-incremental): `authority_report type:Nebulae.BeatGrid.AdaptiveBeatGrid` returns fresh data post-incremental (7 ifaces / forwarderRatio 0.354 — matches Polish-1 baseline exactly), `analysisGeneration` counter observably advances. L-LIM-004 (`lifeblood_execute` IL2CPP): probe runs clean with `GameAssembly.dll` confirmed present at canonical trap site, semantic globals reachable (`Graph.Symbols.Count` = 67068), `targetProfile` honesty contract working.

- **Stale self-analyze numbers across public docs** (reviewer pass 2026-05-24). `docs/STATUS.md` line 45 self-analysis block + `docs/architecture.html` line 378 footer cited 3,506 symbols / 21,113 edges — the pre-Wave-3-4 baseline. Refreshed to live 3,628 symbols / 21,663 edges / 377 types alongside the existing 0 violations / 0 cycles.

- **"Zero skipped" prose lies** across STATUS.md line 3 + README.md line 149 + architecture.html line 207. Actual test suite carries 37 `[SkippableFact]` declarations (native-clang lane, etc.) — 11 of those skip at runtime under default CI env, the rest pass when their gate is satisfied. Refreshed prose to surface the DECLARED `[SkippableFact]` count (mechanical, env-independent) rather than the runtime outcome (env-dependent on `LIFEBLOOD_REQUIRE_NATIVE_CLANG` etc.). The new `INV-DOCS-007` ratchet pins the declared count.

### Wave 6.E: IOperation tool multi-profile scope (2026-05-24)

- **`lifeblood_enum_coverage` / `lifeblood_static_tables` / `lifeblood_assignment_coverage` carry `analyzedUnderProfile` + accept `profileScope:string?`** (`INV-MULTI-DEFINE-IOP-001`). Tools operate against the retained profile's compilations only (memory: peak RAM = single-profile baseline regardless of profile count, because subsequent passes force `RetainCompilations=false` and downgrade compilations after edge extraction). `RoslynWorkspaceAnalyzer.RetainedProfileName` exposes the retained profile name; default = first in caller's `defineProfiles` list. `profileScope` is honored when it matches; mismatched values fail loudly with switch-instructions (re-analyze with the requested profile FIRST in `defineProfiles`). Cross-profile IOperation comparison is v2 (would require multi-profile compilation retention, doubles RAM). The MVP solves the immediate L-LIM-001 trap by being explicit about what's covered: every response answers "which profile are these counts from?". Pinned by `MultiProfileIOpScopeTests` (4 facts). Tests 1178 → 1182. INVs 141 → 142 (95 → 96 categories).

### Wave 6.D: Edge.Profiles[] wire shape + UnityDefineProfileResolver default injection (2026-05-24)

- **`lifeblood_analyze` `defineProfiles` input + `profileCount`/`activeProfiles`/`perProfileEdgeCounts` on response summary** (`INV-MULTI-DEFINE-WIRE-001`). Caller opts into multi-profile analyze by passing `defineProfiles:["Editor","Player"]` (Unity workspace) or any subset of the resolver's vocabulary. Unknown names throw eagerly. Response summary surfaces the active profile set + per-profile edge counts so the caller can confirm shape without re-querying.
- **`lifeblood_dependants` / `lifeblood_dependencies` surface `profiles[]` per edge + accept `profileFilter:string[]?`** (`INV-MULTI-DEFINE-EDGE-PROFILES-001`). `EdgeDetail.Profiles` plumbs `Edge.Profiles` through the read-side port to wire shape. `ApplyProfileFilter` is a pure post-query narrow; null filter short-circuits to identity; edges with `Profiles=null` (single-profile back-compat) pass every filter so pre-multi-define graph.json files remain accessible.
- **`UnityDefineProfileResolver` injected at composition root**. `GraphSession.Load` constructs `RoslynWorkspaceAnalyzer` with `UnityDefineProfileResolver` by default — `Library/` auto-detection makes it safe injection everywhere (returns 2 profiles on Unity, single Editor identity on non-Unity).
- Pinned by `MultiProfileWireShapeTests` (6 facts: dependants surfaces profiles, dependencies surfaces profiles, profileFilter narrows, unmatched filter returns empty, no filter keeps all, single-profile-null edges pass filter as back-compat). Tests 1172 → 1178. INVs 140 → 141 across 94 → 95 categories.

### Wave 6.C: UnityDefineProfileResolver 2-profile MVP (2026-05-24)

- **`UnityDefineProfileResolver` sibling adapter** (`INV-MULTI-DEFINE-UNITY-RESOLVER-001`). Workspace-shape-aware (detects `Library/` at root) `IDefineProfileResolver` implementation that returns the canonical 2-profile MVP for Unity workspaces: `Editor` identity + `Player` minus the Unity Editor discriminator family (`UNITY_EDITOR`, `UNITY_EDITOR_WIN`, `UNITY_EDITOR_64`, `UNITY_EDITOR_OSX`, `UNITY_EDITOR_LINUX`). Non-Unity workspaces fall back to single Editor identity, so the resolver is safe to inject into any composition root regardless of workspace flavor. Closes L-LIM-001's load-bearing root cause when wired into `RoslynWorkspaceAnalyzer` — `#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR` canonical witnesses flip from inactive to active under Player. Pinned by `UnityDefineProfileResolverTests` (8 facts). Tests 1164 → 1172. INVs 139 → 140 (93 → 94 categories).

### Wave 6.B: Multi-profile compile pipeline + Edge.Profiles[] (2026-05-24)

- **`AnalysisConfig.DefineProfiles` + per-profile compile orchestration** (`INV-MULTI-DEFINE-ANALYZE-001`). `RoslynWorkspaceAnalyzer.AnalyzeWorkspace` now runs N sequential per-profile passes when `DefineProfiles` is set. Each pass clones modules via `DefineProfileApplier.WithProfileDefines`, compiles + extracts, tags emitted edges with the active profile name via `EdgeProfileTagger.Tag`, and appends to the snapshot through `AnalysisSnapshot.AppendProfileEdges`. First pass uses `ReplaceFile` to set baseline symbols + file timestamps; subsequent passes only add profile-tagged edges. Unknown profile names throw eagerly. Single-profile back-compat (null/empty `DefineProfiles`) executes ONE pass against the resolver's first profile; edges keep `Profiles=null`; wire shape byte-stable with pre-Wave-6 behavior.

- **`Edge.Profiles: IReadOnlyList<string>?` field + GraphBuilder union dedup** (`INV-MULTI-DEFINE-EDGE-PROFILES-001`). New optional provenance field on every `Edge`. Null on single-profile back-compat (preserves pre-Wave-6 wire shape). Populated under multi-profile with the sorted set of profile names that observed the edge. `EdgeProfileMerger.MergeProfiles` unions Profile sets at the GraphBuilder dedup seam (`(SourceId, TargetId, Kind)` key); first-write-wins for every other field. JSON graph round-trip via `JsonGraphExporter` + `JsonGraphImporter` preserves the field (additive — pre-existing graph.json files load with `Profiles=null`).

- **`DefineProfileApplier` pure transform** (`INV-MULTI-DEFINE-APPLIER-001`). Single function `Apply(baseSymbols, profile)` derives the active preprocessor-symbol set as `(BASE - RemoveDefines) ∪ AddDefines`, ordinal-sorted + distinct. `WithProfileDefines(module, profile)` returns a shallow `ModuleInfo` clone with `PreprocessorSymbols` replaced; every other csproj-discovered field stays identity-equal. Never mutates input.

Pinned by `MultiProfileAnalyzeTests` (11 facts: applier add/remove ordinal + identity, tagger null/named, merger union/identity/double-null, GraphBuilder union dedup, single-profile back-compat keeps Profiles=null, AnalysisConfig defaults, unknown-profile throws). Tests 1153 → 1164. INVs 136 → 139 across 90 → 93 categories.

### Wave 6.A: IDefineProfileResolver port + default resolver (2026-05-24)

- **`IDefineProfileResolver` port + `DefaultDefineProfileResolver` adapter** (`INV-MULTI-DEFINE-RESOLVER-001`). Wave 6.A of L-LIM-001 multi-define union analyze. New Left-side port at `Lifeblood.Application.Ports.Left.IDefineProfileResolver` returns the set of preprocessor-symbol profiles a project should be analyzed under. Default adapter returns a single identity Editor profile — every existing single-profile callsite keeps byte-stable wire shape. Per-profile active define set computed as `(BASE - RemoveDefines) ∪ AddDefines`; ordinal-sorted for byte-stable provenance. Pinned by `DefineProfileResolverTests` (4 facts). Port count 27 → 28. Test count 1149 → 1153. INV count 135 → 136 across 89 → 90 categories. Wave 6.B (multi-profile compile pipeline) + Wave 6.C (UnityDefineProfileResolver 2-profile MVP) follow.

### Wave 6 chain — L-LIM-001 CLOSED end-to-end (2026-05-24)

L-LIM-001 (preprocessor-guarded callsites invisible to graph reference tools) **CLOSED for graph-side tools** (`lifeblood_dependants`, `lifeblood_dependencies` over the union graph with `profileFilter`) with live DAWG verification under the 5-wave Wave 6 chain (`43c1499` → `dd157af`). **Live Roslyn write-side tools** (`lifeblood_find_references`, `lifeblood_find_definition`, `lifeblood_find_implementations`, `lifeblood_rename`) remain retained-profile scoped (memory: cross-profile retention would multiply peak RAM per profile retained) and now surface their scope on every response via `analyzedUnderProfile` + a `limitations[]` entry — see `INV-MULTI-DEFINE-WRITESIDE-001` (closed in the `[Unreleased]` section after the external review pass 2026-05-24 caught the prior overbroad wording):

- **6.A** `IDefineProfileResolver` Application-layer port + default single-Editor identity adapter (`INV-MULTI-DEFINE-RESOLVER-001`)
- **6.B** Per-profile compile orchestration + `Edge.Profiles[]` + graph-builder union dedup + `EdgeProfileMerger` + `EdgeProfileTagger` + `DefineProfileApplier` pure transform (`INV-MULTI-DEFINE-ANALYZE-001` / `-EDGE-PROFILES-001` / `-APPLIER-001`)
- **6.C** `UnityDefineProfileResolver` 2-profile MVP: Editor identity + Player (= baseline minus UNITY_EDITOR family). Workspace-shape-aware via `Library/` detection (`INV-MULTI-DEFINE-UNITY-RESOLVER-001`)
- **6.D** `lifeblood_analyze defineProfiles:string[]?` input + response surface + `lifeblood_dependants` / `dependencies` `profiles[]` per-edge + `profileFilter:string[]?` (`INV-MULTI-DEFINE-WIRE-001`)
- **6.E** IOperation-walking tools (enum_coverage / static_tables / assignment_coverage) surface `analyzedUnderProfile` + `profileScope`; peak RAM stays at single-profile baseline (`INV-MULTI-DEFINE-IOP-001`)

Total scope: **7 new INVs** pinned in `docs/invariants/architecture.md` + **29 new tests** across 4 test files (`MultiProfileAnalyzeTests`, `UnityDefineProfileResolverTests`, `MultiProfileWireShapeTests`, `MultiProfileIOpScopeTests`) + 2 new Adapter files + 2 new Domain helpers + 1 new Application port + 1 new Adapter resolver. Wire shape additions are 100% additive — single-profile back-compat is byte-stable.

Tests 1155 / 1155 Debug green (filter `!~NativeClang`); zero skipped. STATUS anchors: 142 invariants across 96 categories, 28 ports, 30 MCP tools.

**Live re-probe receipt (post-dist-swap, 2026-05-24):**
- `lifeblood_analyze projectPath:DAWG defineProfiles:["Editor","Player"]`: `profileCount:2`, `activeProfiles:["Editor","Player"]`, `perProfileEdgeCounts:{Editor:161548, Player:157889}`. Edges 247,350 (single-profile) → 247,460 (union) = **+110 Player-only edges recovered**. Wall 71.9s / Peak RSS 4.58GB = 1.8× / 1.35× single-profile baseline (within plan budget).
- `lifeblood_dependants(AudioRuntimeProfilePolicy.Resolve)`: 19 dependants — `AdaptiveBeatGrid.Bootstrap_WireServices` at `AdaptiveBeatGrid.Bootstrap.Services.cs:64` visible with `profiles:["Player"]` ← **L-LIM-001 canonical trap edge restored**.
- `lifeblood_dependants(AudioRuntimeProfilePersistenceLocator.Current)`: 12 dependants — `Bootstrap_WireServices` at line 59 visible with `profiles:["Player"]`.
- `profileFilter` wire shape validated: filter `["Player"]` → 19 dependants on `Resolve`, filter `["Editor"]` → 18 (the Player-only bootstrap edge drops as the spec requires). Delta = 1 = exactly the L-LIM-001 trap edge.
- DAWG-side `reference_lifeblood_known_limitations.md` L-LIM-001 marked CLOSED with the full receipt table.

### Release readiness note: native-clang opt-in lane

The native-clang adapter (`adapters/native-clang/`) ships as an **opt-in build target**, not a bundled artifact. The C# core packages (`Lifeblood`, `Lifeblood.Server.Mcp`) do NOT carry `lifeblood-native-clang.exe` and do NOT depend on LLVM. Consumers who need C-language analysis build the executable from `adapters/native-clang/` via CMake + libclang per `docs/TOOLCHAIN.md`. The 11 `[SkippableFact]` ratchets in `tests/Lifeblood.Tests/NativeClangExecutableRatchetTests*.cs` skip silently when the executable is absent (default suite green at `1180 passed + 11 skipped / 1191 total`) and fail loudly when `LIFEBLOOD_REQUIRE_NATIVE_CLANG=1` is set on a host where the executable IS expected to be present — this is a LOCAL / MANUAL audit gate (no GitHub workflow sets the env var; LLVM toolchain on the runner is opt-in consumer work), not a release-blocker. Default suite live count `1217 passed + 11 skipped / 1228 total`. Full opt-in execution requires the consumer's own LLVM toolchain. See `docs/NATIVE_CLANG.md` § "Opt-in execution lane" for the build recipe; `docs/RELEASE.md` step 5 documents both skip categories the release gate accepts.

### Wave 6 plan reference (2026-05-24)

The Wave 6 architectural plan that drove the chain landed at `docs/plans/multi-define-union-l-lim-001-plan-2026-05-24.md` — six-phase rollout (6.A port + default resolver, 6.B multi-profile compile, 6.C Unity-aware adapter, 6.D wire shape, 6.E per-IOperation policy, 6.F live dogfood closure). The plan was tightened during 6.A–6.C: the original 5-profile vocabulary collapsed to the 2-profile MVP (Editor + Player) after empirical proof that platform-target defines are already in the Editor baseline; the Editor-discriminator axis (UNITY_EDITOR / UNITY_EDITOR_WIN / etc.) is the only load-bearing distinguisher for the L-LIM-001 trap. `Edge.Profiles[]` is omitted on single-profile back-compat (not empty-array) so the wire shape stays byte-stable for callers who don't opt into `defineProfiles`.

### Wave 5: Stage 0 dogfood follow-through (2026-05-24)

DAWG-side Stage 0 pass against the post-Wave-1/3/4 dist exercised all 30 MCP tools and surfaced three wire-shape gaps not covered by the masterplan. Each closes with eternal-shape posture: not a per-tool patch, but a default-tuning + uniform-shape upgrade that hardens the class of trap for any future tool.

- **`lifeblood_rename` per-TextChange wire shape + cross-partial coverage** (`INV-RENAME-POINT-EDITS-001` + `INV-RENAME-CROSS-PARTIAL-001`, LB-TRACK-20260524-025). Two compound defects shipped together:
  - **Workspace-warming bug.** `Rename` checked `mgr.Solution == null` BEFORE any operation that would trigger `EnsureWorkspace`, so the very first call on a fresh `RoslynWorkspaceRefactoring` instance returned an empty edit array. Long-running MCP sessions hid the bug because a prior `find_references` / `format` had already warmed the workspace. Fix: call `ResolveSymbol` (which triggers `EnsureWorkspace`) before reading `mgr.Solution`. The eternal posture is "let the manager own its lifecycle"; the null check now follows symbol resolution, not precedes it.
  - **Whole-file edit wire shape.** `SourceText.GetTextChanges(oldText)` did a brute text diff between pre/post Roslyn documents whose `SourceText` instances came from different `TextLoader` containers; the diff degenerated to a single change-everything `TextChange` even when only a handful of identifier spans actually moved. Result: one `editCount: 1` covering `startLine=1, endLine=lastLine`, `newText=full file body` — diff/selective-apply impossible, mechanical application would overwrite concurrent local edits. Fix: switch to `Document.GetTextChangesAsync(oldDocument)` — Roslyn's Document-level diff that surfaces the granular `TextChange`s the Renamer authored, not a brute text diff. Each `TextChange` is a narrow identifier-span replacement; one wire-level `TextEdit` per change is point-edit by construction.
  - **Cross-partial coverage** falls out of the workspace fix for free: `Renamer.RenameSymbolAsync` already runs at Solution scope (walks every project for incoming references), and the per-Document iteration in the response projection visits every changed document. Pre-fix dogfood saw cross-partial usages missing because the `Solution == null` early return short-circuited the response entirely.
  - Tests: 6 new facts in `RenameWireShapeTests` (diagnostic single-type-rename probe, cross-partial method, cross-partial field, same-file multi-use property, same-file multi-use method, point-edit NewText length budget). Pin in-memory fixtures with no golden-repo dependency. `WriteSideIntegrationTests.Rename_GreeterType_ReturnsRealEdits` relaxed to match the new minimal-diff contract (NewText is a substring of the new identifier, not the full identifier). Closes DAWG LB-TRACK-20260524-025.

- **`lifeblood_file_impact` `summarize` flag + `maxResults` cap** (`INV-FILE-IMPACT-SUMMARIZE-001`, LB-TRACK-20260524-026). Pre-fix the tool returned full `dependsOn[]` / `dependedOnBy[]` arrays with no caps; god-type primary partials in real-world Unity workspaces (DAWG `AdaptiveBeatGrid.cs` = 159 partials) returned 185 KB / 4089 lines — overflowed downstream tool-result budgets every default invocation. Fix is purely additive at the handler layer (Domain port shape unchanged): `maxResults` clips each direction's array independently (default 500, summarize mode 25) and fires `dependsOnTruncated` / `dependedOnByTruncated` flags plus a composite `truncated` bool. `summarize:true` forces `maxResults=25` regardless of caller-passed value — mirrors the summarize shortcut already shipped on `dead_code`, `cycles`, `blast_radius`, `test_impact`. Counts (`dependsOnCount` / `dependedOnByCount`) stay full so a summarize caller still sees real magnitude before deciding whether to paginate. `ToolHandlerTests` adds 4 facts (default truncation shape, explicit maxResults clips + fires truncated, summarize forces 25 over caller-passed 100, summarize:false honors explicit caller cap as regression guard). Closes DAWG LB-TRACK-20260524-026.

- **Uniform list-shape contract eternal ratchet** (`INV-LIST-SHAPE-UNIFORM-001`). Pinned by `UniformListShapeRatchetTests` (3 theories × 6 list-shape tools = 18 cases): asserts every read-side tool whose response is an unbounded list exposes `summarize:bool` + at least one cap argument on its input schema, AND that the tool description references `summarize` or `truncat*` so the catalog surfaces the wire-shape shortcut. Closes the silent-drift class — future list-shape tools that ship without the trio fail at build time, not at dogfood time. Per-tool cap vocabulary stays owned by the tool (rows vs results vs tables vs files vs ...) — uniformity is the wire-shape contract, not behavior identity.

- **`lifeblood_static_tables` default `maxRows` 1024 → 32; new `summarize` flag** (`INV-STATIC-TABLES-DEFAULT-MAXROWS-001` + `INV-STATIC-TABLES-SUMMARIZE-001`, LB-TRACK-20260524-027). Pre-fix default of 1024 rows was a fence against accidentally-truncated extraction; empirically overflowed downstream tool-result budgets on real dispatch-table god-types (DAWG `KernelCapabilityTable` returned 466 KB / 9749 lines on default invocation). Default tightened to 32 (matches the triage workflow floor of ~5–20 rows visible) — callers needing full extraction pass `maxRows` explicitly. New `summarize:bool` flag forces hard caps `maxRows=3` + `maxTables=16` regardless of caller-passed values; mirrors the `summarize` shortcut already shipped on `dead_code`, `cycles`, `blast_radius`, `test_impact`. Wire shape preserved — same `tables[]` / `rows[]` / `truncated` flags, just smaller. `StaticTableExtractorTests` adds 4 facts (default-32 truncation, summarize forces rows + tables, summarize:false honors explicit). Closes DAWG LB-TRACK-20260524-027.

## [0.7.8] - 2026-05-19

The 2026-05-19 two-phase hardening plan (`<plan-file>`) lands its Phase-1 trust-hardening atoms (F0..F3f + S4..S8a), four native-clang adapter refactors, three execute-robustness fixes surfaced by Unity IL2CPP dogfood, and one dogfood-derived extractor fix (F1d) that closed an 88.7%-workspace-wide property-edge gap. Twelve new typed invariants land in `docs/invariants/csharp-adapter.md` + `docs/invariants/tools.md`; the canonical-symbol-identity chain (F1a/b/c), the composite-port intelligence chain (F3a..f), the diagnose-freshness envelope (S5/S5b), the advisory-limitations envelope (S6), the planning-verdict evidence (S7), and the adapter-thinning seam (S8a) all ship together. Lifeblood self-analysis: **3,506 symbols, 21,113 edges, 11 modules, 363 types, 0 violations, 0 cycles. 1,098 tests, zero skipped. 122 typed invariants across 83 categories.**

### Fixed

- **`INV-EXTRACT-PROPERTY-READ-001` — bare-identifier sibling-member property and event reads emit a symbol-level `References` edge.** Pre-fix, `RoslynEdgeExtractor.ExtractReferenceEdge`'s `IdentifierNameSyntax` walker carried arms for `INamedTypeSymbol` / `IFieldSymbol` / `IMethodSymbol` but no `IPropertySymbol` / `IEventSymbol` arm. The member-access form (`this.X`, `obj.X`) was already handled by `ExtractMemberAccessEdge` through the shared `EmitSymbolLevelEdge` helper, so the wire-shape contract was intact but the AST-entry point was incomplete. C# style convention overwhelmingly drops the `this.` prefix, so the bare-identifier path is the common case and the gap silently swallowed ~89% of private-property incoming edges across a real workspace. DAWG dogfood 2026-05-19 (LB-TRACK-20260519-024) measurement: workspace-wide non-public property zero-incoming-non-`Contains`-edges = 88.7% (1099/1239) vs field 1.5% (174/11765) vs type 0.8% (4/482). `lifeblood_find_references` resolved the same reads correctly because Roslyn's `SymbolFinder.FindReferencesAsync` walks the semantic model directly, bypassing the edge graph; `dependants` / `dead_code` / `blast_radius` / `port_health` (all edge-graph walkers) systematically missed them, splitting Lifeblood's read-tool reality. F2's `sameClassConsumerCount` triage field consequently always reported 0 on private property findings, defeating the FP-folding contract. Fix adds an `IPropertySymbol or IEventSymbol` arm to `ExtractReferenceEdge` that routes through `EmitSymbolLevelEdge` — same helper member-access uses, so the wire shape is byte-stable across both AST entry points. Covers reads AND writes (LHS of `=` is also `IdentifierNameSyntax`) AND event subscription (`Changed += h;`). Post-fix DAWG re-verification: 238,242 → 242,233 edges (+3,991 newly-recovered property/event-read edges); workspace property zero-incoming rate 88.7% → 67.2% (-21.5 pp, 266 properties recovered); `lifeblood_dead_code(includeKinds:[Property], excludePublic:true)` finding count dropped 247 → 62 (-75%). Pinned by `ExtractEdges_BareIdentifierPropertyRead_EmitsReferencesEdge`, `ExtractEdges_BareIdentifierPropertyWrite_EmitsReferencesEdge`, `ExtractEdges_BareIdentifierEventReference_EmitsReferencesEdge`.

- **`INV-EXTRACT-EXTENSION-REDUCED-001` — extension-method canonical ids walk through `ReducedFrom` before `OriginalDefinition`.** Pre-fix, instance-style extension invocation `x.Foo()` bound to the REDUCED `IMethodSymbol` whose parameter list drops the explicit `this` receiver; the declaration path emitted the unreduced form. Every reduced-form call-site landed on a non-matching canonical id, so the declared extension method showed `directDependants:0` and could surface as a `dead_code` finding even when the workspace called it heavily — same defect class LB-INBOX-010 closed for generic methods, specialized to extension methods. Both producer-side (`GetMethodId`) and consumer-side (`BuildSymbolId`) now route through `if (method.ReducedFrom != null) method = method.ReducedFrom;` before `OriginalDefinition` canonicalization. Fix order matters: `ReducedFrom` first (recover explicit `this` receiver in `T` form), then `OriginalDefinition` (recover open-generic form for generic extensions). Pinned by four `RoslynExtractorTests` fixtures covering instance-style, static-style, generic, and chained-extension shapes. LB-TRACK-20260519-021 / F1a atom of the 2026-05-19 plan.

- **`INV-CANONICAL-ID-PARITY-001` — symbol-id parsing preserves the literal IL constructor names `.ctor` and `.cctor`, and Roslyn symbol-id construction has a single source of truth.** Producer side emits `method:NS.T..ctor()` and `method:NS.T..cctor()` for instance and static constructors — the leading dot is baked into `IMethodSymbol.Name`. Pre-fix, `RoslynWorkspaceManager.ParseSymbolId` called `nameOnly.Split('.')` on `"App.Service..ctor"` and produced `["App", "Service", "", "ctor"]`; the empty middle segment made `FindInCompilation` fail at `GetMembers("")` so `FindReferences("method:App.Service..ctor()")` returned zero refs. Fix: `SplitPreservingCtorNames` detects the `..cctor` / `..ctor` suffix (order matters — `.cctor` ends in `.ctor`), strips the suffix, splits the remaining container path on dots, and appends the literal `.cctor` / `.ctor` as the last part. `FindInCompilation`'s method arm uses `INamedTypeSymbol.Constructors` / `.StaticConstructors` for the special names. Final polish closes the deferred SSoT drift: `CanonicalSymbolFormat` now builds every Roslyn type / method / field / property / event ID, `RoslynSymbolExtractor`, `RoslynEdgeExtractor`, and `RoslynCompilationHost.BuildSymbolId` route through it, and `ArchitectureInvariantTests.CSharpAdapter_RoslynSymbolIds_HaveSingleSourceOfTruth` refuses direct `SymbolIds.Type` / `Method` / `Field` / `Property` calls anywhere else in the C# adapter. Pinned by `SymbolIdCanonicalParityTests` — four invocation-site fixtures plus three white-box parser ratchets plus one end-to-end resolve ratchet. LB-TRACK-20260519-023 / F1c plus final SSoT polish of the 2026-05-19 plan.

- **`INV-EXTRACT-IFACE-INHERIT-001` — interface-extends-interface emits `Inherits` not `Implements`.** Pre-fix, `RoslynSymbolExtractor` emitted `Implements` edges for both class-implements-interface and interface-extends-interface, conflating the two relationships. The composite-port intelligence (`port_health` / `authority_report`) needs to traverse interface inheritance closures to surface inherited member counts on composite-facade interfaces; conflating the two edge kinds made that traversal incorrect. Fix: dedicated `Inherits` edge kind for interface-to-interface inheritance; `Implements` reserved for concrete-class-implements-interface. Composite-aware `port_health` and `authority_report` (F3b + F3e) traverse the `Inherits` closure to surface inherited surface. LB-TRACK-20260519-022 (composite port-health) / F3c atom of the 2026-05-19 plan.

- **`lifeblood_execute` managed-PE gate on runtime-assembly probe (`INV-EXECUTE-001` upgrade — LB-LIM-004 a).** When the executor is wired with an `IRuntimeAssemblyResolver` (Unity workspaces), each candidate path is now validated via `System.Reflection.AssemblyName.GetAssemblyName(path)` before becoming a `MetadataReference`. Native PEs — Unity IL2CPP `GameAssembly.dll`, C++/CLI without managed surface, and other DOS-magic-but-not-managed binaries — throw `BadImageFormatException` and are filtered at the reference-graph boundary instead of surfacing `CS0009 PE image doesn't contain managed metadata` at compile time on every subsequent script run. Skipped count + first three filenames surface on `runtimeAssemblyWarnings`. First-observed in a Unity IL2CPP workspace (2026-05-19): `GameAssembly.dll` was wrongly injected as a managed ref and broke every `execute` call against the workspace. Pinned by `Executor_RuntimeAssemblyProbe_SkipsNativePE_AndSurfacesDiagnostic`.

- **`lifeblood_execute` script-reference-set centralized seam (LB-LIM-004 b).** Reference admission for execute calls is now isolated in `Internal.ScriptReferenceSetBuilder` — the single seam that admits host scripting BCL, retained workspace `CSharpCompilation` metadata references, admitted runtime probe DLLs, and the assemblies that define `RoslynSemanticView` globals. Pre-fix, reference policy was scattered across `RoslynCodeExecutor` with each caller rediscovering native-PE filtering, runtime-BCL exclusion, and duplicate-identity handling. The builder uses an explicit file-backed `HostBclReferences` lazy because `ScriptOptions.Default` only carries "Unresolved" assembly names, so execute supplies real `MetadataReference.CreateFromFile` entries for `System.Runtime`, `System.Console`, `System.Collections`, `System.Linq`, `netstandard`, and more. Native-PE filtering, runtime BCL/contract filtering (`mscorlib`, `netstandard`, `System.*`), and duplicate-identity collapsing all happen at the seam.

- **`lifeblood_execute` `targetProfile` honesty.** `host` is now the only execution profile; non-host values (e.g. `net-standard-2.1`, `net-6.0`) are accepted for backward compatibility but run against the host scripting BCL and surface an explicit `targetRuntimeWarnings` limitation instead of pretending the executor swapped to a different reference pack. Closes the silent-mismatch class where a caller passed `targetProfile: "net-standard-2.1"` and got results that secretly ran against the host BCL anyway.

### Added

- **`INV-EXTRACT-CROSS-PARTIAL-RESOLUTION-001` — regression-guard fixtures pin cross-partial private method and field reference resolution.** The 2026-05-15 correctness masterplan Stage 1 named cross-partial private invocation as a suspected gap. Empirical verification: the current extractor already resolves cross-partial calls correctly because both partial trees join the same Roslyn Compilation. The Roslyn semantic model walks the shared declaration set natively — no extractor change required. F1b authors two regression-guard fixtures (`ExtractEdges_CrossPartialPrivateMethodCall_EmitsCallsEdgeWithCanonicalId`, `ExtractEdges_CrossPartialPrivateFieldReference_EmitsReferencesEdge`) so a future walker rewrite cannot silently scope resolution to the current syntax tree. LB-TRACK-20260519-021-adjacent / F1b atom of the 2026-05-19 plan.

- **`INV-DEADCODE-TRIAGE-002` — `sameClassConsumerCount` field on every `dead_code` finding plus `IncludeSameClassOnlyConsumers` query option.** F2 adds the wire-shape contract that lets a caller fold the same-class-private-member-read FP class with one bool check instead of cross-tool grep. Computed by walking the symbol's incoming edges and counting any edge whose `SourceId` resolves to a symbol with the same `ContainingType` as the finding. The underlying graph edges this field reads from are emitted by F1d (`INV-EXTRACT-PROPERTY-READ-001`) for properties/events; pre-F1d the field always reported 0 on private property findings. Pinned by `DeadCodeSameClassTests`.

- **`INV-PORT-HEALTH-ANALYZER-SEAM-001` — `IPortHealthAnalyzer` Application-layer port.** F3a extracts the inline algorithm body from `ToolHandler.HandlePortHealth` (75 LOC) into `Lifeblood.Application.Ports.Right.IPortHealthAnalyzer` + `Lifeblood.Connectors.Mcp.LifebloodPortHealthAnalyzer`. ToolHandler routes through the injected port. Behavior is byte-equal to the pre-F3a inline body in this atom — composite/inherited traversal lands in F3b. Pinned by nine `PortHealthAnalyzerTests` fixtures plus a seam-discipline scan that refuses to find the inline algorithm tokens (`"vestigial"`, `liveCount++`, `var memberIds = new List<string>`) anywhere in `ToolHandler.cs`. Port count 26 → 27. LB-TRACK-20260519-022 / F3a atom of the 2026-05-19 plan.

- **`INV-PORT-HEALTH-COMPOSITE-001` — `lifeblood_port_health` surfaces composite + inherited interface contract surface.** F3b walks `EdgeKind.Inherits` (the dedicated interface-extends-interface edge kind landed by F3c) when an interface's own member count is zero or it carries inherited contracts. Response shape adds `directMemberCount` / `inheritedMemberCount` / `aggregateMemberCount` / `memberCount` (back-compat alias = aggregate) / `inheritedInterfaces[]` / `isCompositeInterface`. Pre-fix, a composite-port pattern (an interface that bundles N sub-ports via inheritance with 0 own members) reported `verdict: "empty"` — the exact pattern the host-width invariant Wave 4F was designed to enforce. Post-fix DAWG re-verification: `lifeblood_port_health(type:ITransportTimelineHost)` returns `memberCount: 24, liveMembers: 24, livenessPct: 1, verdict: "healthy"` with `directMemberCount: 0, inheritedMemberCount: 24, inheritedInterfaces[5], isCompositeInterface: true`. Pinned by composite-fixture additions including real-graph composites via F3d. LB-TRACK-20260515-013 / F3b atom of the 2026-05-19 plan.

- **`INV-AUTHORITY-COMPOSITE-001` — `lifeblood_authority_report` per-interface composite surface.** F3e parallels F3b on the top-level authority planning tool. Each `perInterface` row in `authority_report` now carries `directMemberCount` / `inheritedMemberCount` / `aggregateMemberCount` / `memberCount` (alias) / `inheritedInterfaces[]` / `isCompositeInterface`. Pre-fix, an agent could mis-rank a 0-own-member composite as a simple dead interface, when retiring it would actually collapse several deliberately narrow sub-ports into a mega-Bindings object. Post-fix DAWG re-verification on `Nebulae.BeatGrid.AdaptiveBeatGrid`: 7 implemented interfaces / 142 owned public surface / forwarderRatio 0.354 — EXACT match to Polish-1 baseline; composite ifaces show their inherited contract surface (`IWheelMenuActions` direct=4 inherited=39 aggregate=43, `IPianoRollRefreshCoordinatorHost` direct=0 inherited=35, etc.). LB-TRACK-20260518-017 / F3e atom of the 2026-05-19 plan.

- **`INV-AUTHORITY-PLANNING-COMPOSITION-001` — planning-verdict evidence fields on `authority_report`.** S7 adds `crossAssemblyConsumerCount` (distinct other modules with incoming edges into the type or its members — boundary-contract evidence), `sameAssemblyConsumerCount` (distinct same-module consumer symbols — adapter-shim evidence), and `hasSingleImplementer` (true/false for interface targets with exactly one source-defined implementer; null for non-interface targets — adapter-shim candidate when paired with high cross-assembly use). Verdict composition (`EvictableDebt` / `BoundaryContract` / `SceneDiscoveryContract` / `CompositeFacade` / `AdapterShimOnly` / `NeedsAudit`) is caller-owned per design — the tool exposes the evidence axes; the architecture plan stays in the agent's hands. LB-TRACK-20260518-019 / S7 atom of the 2026-05-19 plan.

- **`INV-ENUM-COVERAGE-DISPATCH-TABLE-001` — `dispatchTableReferenceCount` field on every `enum_coverage` member row.** S4 adds a per-member reference counter populated when the member appears as a cell value in a static-table initializer detected by the same classifier `lifeblood_static_tables` uses. `producedCount == dispatchTableReferenceCount` reads as "only a routing key, never genuinely produced in app code" — collapses the dogfood audit ("how many values in this state-machine enum are produced only through dispatch tables?") to a single tool call. `isUnreferenced` semantics tighten: `isUnreferenced = true` iff `totalReferences == 0`. Pinned by `EnumCoverageDispatchTableTests`. LB-TRACK-20260515-014 / S4 atom of the 2026-05-19 plan.

- **`INV-DIAGNOSE-FRESHNESS-001` — `analysisGeneration` monotonic counter on every read-side envelope.** S5 adds a project-wide counter incremented per full or incremental re-analyze, surfaced on every read-side response envelope so callers can detect cache/graph drift across multi-tool sessions. Pinned by `AnalysisGenerationCounterTests`.

- **`INV-DIAGNOSE-FRESHNESS-002` — scope-aware `possiblyStale` flag on `diagnose` / `compile_check` responses.** S5b adds a per-response staleness flag: file scope checks the one file's mtime vs graph timestamp; module scope walks files parented to that module; project scope walks every tracked File symbol. A project-wide diagnostic that contradicts a fresher file-level compile_check now flags `possiblyStale: true` instead of silently presenting stale errors as current. Pinned by `DiagnoseFreshnessTests`. LB-TRACK-20260518-018 / S5b atom of the 2026-05-19 plan.

- **`INV-ADVISORY-LIMITATIONS-001` — per-tool `limitations[]` array on every advisory-tier response + uniform write-side envelope.** S6 adds structured `limitations[]` strings to every read-side response describing what static analysis cannot prove — Unity runtime dispatch (UnityEvent YAML bindings, prefab/scene serialized refs, ScriptableObject delegates, AnimationEvent callbacks, SendMessage/Invoke string-named dispatch, reflection-driven entry points), enum production via Inspector / serialized fields / Resources / Addressables, etc. The advisory tier (`dead_code`, `enum_coverage`, `test_impact`, planning verdicts) carries the strongest limitations; the Semantic and Derived tiers carry the lighter Unity-runtime-discovery limitation when the workspace is Unity-shaped. Write-side envelope uniformized across `execute` / `rename` / `format` / `compile_check`. LB-TRACK-20260518-020 / S6 atom of the 2026-05-19 plan.

- **`INV-ADAPTER-THIN-001` — adapter services that own a self-contained Roslyn walk live in their own files; `RoslynCompilationHost` is the orchestrator + composition root, not the implementation.** S8a moves the `GetEnumCoverage` walker (~115 LOC method body + 65 LOC helpers) into `RoslynEnumCoverageService.cs` (own file, internal sealed class, takes the host by ctor for `Compilations` + `ResolveFromSource` + `BuildSymbolId` access). Host shrinks 1155 → 952 LOC; service is 247 LOC isolated. Plan acceptance: "Largest C# files trend below 600 LOC over time" — S8a is the first cut. The 16 `EnumCoverageTests` fixtures pin the behavior across the move; identical wire shape. Follow-up `S8a-fix` (`INV-ADAPTER-LOOKUP-SEAM-001`) introduces an `IRoslynLookup` interface so the host-service handshake doesn't depend on the host concrete type. Plus a separate `find_references` service extraction atom thins the host further. LB-TRACK-20260519-028 / S8a atom of the 2026-05-19 plan.

- **Native-clang adapter boundary-envelope hardening (Phase 9 of the plan).** Four refactors lift adapter-shape concerns behind explicit facts: type-member emission, global emission, function emission, and overall adapter boundary envelopes. Continues the Clang-style "small services, one concern per file" architecture without changing the external JSON contract.

### Changed

- **Status doc + DocsTests anchors refreshed.** `<!-- testCount: 1098 -->` (was 1011), invariant count 122 across 83 categories (was 101/63), self-analyze 3507/21115/363/0/0 (symbols/edges/types/violations/cycles). Tools / ports unchanged at 29 / 27.

- **Tracker sweep (LB-TRACK 012-024 → Shipped).** Every open atom from the 2026-05-19 plan now carries a closing reference plus DAWG live-MCP or focused Lifeblood self-verification body. LB-TRACK-023 is closed in-tree: F1c fixed the empirical `.ctor` / `.cctor` parser bug, and the final polish atom consolidates Roslyn symbol-id construction behind `CanonicalSymbolFormat` with an architecture ratchet.

## [0.7.7] - 2026-05-16

First non-C# adapter ships as beta. `adapters/native-clang/` is a libclang-based C extractor that emits `graph.json` through the same `JsonGraphImporter` boundary the TypeScript and Python adapters already use, so `Lifeblood.Domain`, `Lifeblood.Application`, `Lifeblood.Analysis`, and every connector stay free of LLVM, Clang, and CMake dependencies. Plus one graph-layer correctness fix and the `INV-CHANGELOG-001` ratchet that caught the v0.7.6 release-tag drift.

### Added

- **Native Clang adapter (beta)** at `adapters/native-clang/`. C extractor built on `libclang`. Reads `compile_commands.json`, emits a Lifeblood-shape `graph.json`. Surfaces translation units, functions, globals, fields, type shells, enum members, macros, includes, callback-table rows and cells, build-profile and command-line macro facts. Per-module, per-file, and per-symbol pressure metrics cover visibility, callback target, include, field access, global access, type shape, return type, parameter type, same-file direct calls, cross-file calls. Partial-parse tolerant: emits diagnostic health counts instead of failing closed. Pinned by `NativeClangAdapterContractTests` and `NativeClangExecutableRatchetTests` over nine fixture families (`tiny-c`, `direct-refs-c`, `multi-tu-c`, `cross-tu-c`, `callback-table-c`, `profile-c`, `partial-parse-c`, `warning-c`, `return-type-c`).

- **FFmpeg scout workflow** at `adapters/native-clang/tools/ffmpeg-scout/`. Repeatable PowerShell harness (`Prepare-FfmpegScout.ps1` plus module scripts) that clones FFmpeg, configures a minimal LLVM-clang build, generates a focused `compile_commands.json` for a representative file slice, and runs the extractor end-to-end. First real-world result on 2026-05-16 against a 5-file slice (`libavfilter/allfilters.c`, `libavcodec/allcodecs.c`, `libavformat/allformats.c`, `libavutil/pixdesc.c`, `libswscale/utils.c`): 9264 symbols, 1067 methods, 14494 Lifeblood-imported edges, 0 architecture violations, 1 likely real cycle in libswscale context initialization. Workflow and limits documented in `adapters/native-clang/FFMPEG_SCOUT.md`. The scout is explicit reconnaissance, not whole-build coverage. Full-build paths (WSL with bear, MSYS2 with bear, project-specific compile-database generator) are named as the next maturity step.

- **`docs/NATIVE_CLANG.md` dedicated capability page.** Scope, build, fixtures, scout workflow, what works today, what is deferred. Single entry point for downstream readers asking how the C support actually works.

- **`docs/RELEASE.md` eternal pre-tag checklist.** Closes the operator gap that shipped v0.7.6 with three red CI workflows. `dotnet test -c Release` exit 0, every `## [X.Y.Z]` heading paired with a matching `[X.Y.Z]:` link reference, and `[Unreleased]` rebased to the new tag are non-negotiable pre-tag steps. Force-moving a published tag to recover from a missed step is explicitly refused. The recovery is always a next-patch release.

### Fixed

- **`INV-CHANGELOG-001` link-reference drift caught at v0.7.6.** `CHANGELOG.md` shipped a `## [0.7.6]` heading without a matching `[0.7.6]: https://.../compare/v0.7.5...v0.7.6` link reference. `DocsTests.Changelog_EveryHeadingHasLinkReference` failed at the release commit, the tag was pushed anyway, and all three tag-triggered CI workflows (`Build & Test`, `CI`, `Release Verification`) went red on origin. The v0.7.6 tag is left as-is on origin because force-moving a published tag is a destructive operation on shared history. The missing link reference is restored as part of this release. The eternal mechanism lives in `docs/RELEASE.md`. The ratchet was already in place and did its job.

- **Role-distinct reference edges preserved through graph synthesis** (`c74517c`). Edges that share `(sourceId, targetId, kind)` but differ on role payload were being collapsed during graph construction. The de-dup key now incorporates the role-identity component so semantically different references survive into the imported graph. Sibling refactor (`dca0b1a`) extracts file-edge derivation from `GraphBuilder` into `Lifeblood.Domain/Graph/FileEdgeDeriver.cs` so the file-edge contract is testable in isolation.

### Changed

- **Test discovery refresh: 1011 tests, zero skipped.** Native-clang track adds contract and executable-ratchet coverage. Graph-layer changes add `GraphBuilderTests` and `GraphValidatorTests` for role-edge identity. `docs/STATUS.md` `<!-- testCount: 1011 -->` anchor and visible prose re-ratcheted by `DocsTests`.

- **`docs/ADAPTERS.md` native-clang section** moved from "planned" to current beta scope (libclang extractor, fixtures, FFmpeg scout, external JSON boundary preserved).

- **`README.md`, `docs/ARCHITECTURE.md`, `docs/architecture.html`, and `CLAUDE.md`** updated to list the native-clang adapter alongside the existing C#, TypeScript, and Python surfaces. Language coverage prose, ASCII diagrams, assembly tables, and project trees all carry the new adapter.


## [0.7.6] - 2026-05-15

### Fixed

- **`INV-EXTRACT-STATIC-CTOR-ID-001` - explicit static constructors use `.cctor` canonical IDs** (`LB-TRACK-20260515-010` Stage 0 gate). DAWG dogfood exposed the missing half of W2-E: `RoslynEdgeExtractor` correctly attributed static field-initializer method-group cells to Roslyn's `.cctor`, but `RoslynSymbolExtractor.ExtractConstructor` emitted explicit `static TypeName()` declarations as `method:NS.T..ctor()` instead of `method:NS.T..cctor()`. `GraphBuilder` then dropped every dispatch-table edge whose source was the unsurfaced `.cctor`, so `find_references` and `static_tables` saw the method group while `dependants` / `dependencies` / `dead_code` did not. Fix: explicit static constructors now surface as `.cctor`; initializer-owned field/property symbols also receive cycle-neutral declarative `References` edges to method-group delegate targets, so `dependencies(field:...Features)` answers the dispatch-table row shape directly while `.cctor` keeps the executable `Calls` edge; `dead_code` treats all `.cctor` methods (explicit and synthesized) as runtime-invoked; the warning text no longer lists method-group delegate arguments as a known false-positive class. Pinned by a DAWG-shaped wide constructor-row ratchet with an explicit static constructor plus a symbol-extractor `.cctor` pin.

### Changed

- **`smoke-mcp-analyze.ps1` server-DLL auto-discovery** (`LB-INBOX-006` Wave W6). Pre-fix, the default `-ServerDll` parameter was a hardcoded `Debug/net8.0/` path that failed at first run for any operator who had only built Release (or had not built yet). The single biggest first-run friction point external reviewers hit on the v0.6.3 round-trip script. New `Resolve-ServerDll` helper walks Debug first then Release under `$PSScriptRoot/src/Lifeblood.Server.Mcp/bin/<Config>/net8.0/`; if neither exists, the one-line diagnostic names every path tried and the exact `dotnet build` command the operator can run to fix the gap. Passing an explicit `-ServerDll` short-circuits discovery but still validates the file exists before launching. Closes the credibility multiplier the v0.6.3 external review flagged at small surface area.

### Added

- **`docs/PLAYBOOK_CSHARP.md` + first anonymized case study** (`LB-INBOX-004` + `LB-INBOX-005` Wave W5). Closes the wedge-proof half of the post-v0.6.3 roadmap. The playbook documents seven concrete workflows for large C# / Roslyn / Unity workspaces (triage a breakage, audit module boundaries, safe rename, inspect blast radius, validate a snippet, recover from a merge conflict, find a dead method) with named tool calls, argument shapes, expected outputs, and an envelope cheat sheet mapping `confidence` bands to ship-or-investigate decisions. First case study `docs/case-studies/unity-daw-parity-2026-05.md` documents the end-to-end resolution of the 7,772-diagnostic gap between Lifeblood `diagnose` and `dotnet build` on a 90-module Unity workspace through Wave W1-A / Ab / B / C — written with the empirical counts that drove each INV, anonymized to "Project A", and explicit about what is deliberately not claimed (dedup alone did NOT close CS1701; MSBuild NoWarn baseline is fixed-list, not introspected). Closes the credibility-multiplier asks from the v0.6.3 external review.

- **`INV-WIRE-CONTRACT-001` — wire contract pin + schema deprecation policy** (`LB-INBOX-003` Wave W4). First concrete pin: `ResponseEnvelopeWireShapeContractTests` reflects on `Lifeblood.Domain.Results.ResponseEnvelope` and asserts the v1 field set is exactly the six canonical fields with stable type names AND every field is `init`-only (mutation after construction is structurally blocked via reflection check for the `IsExternalInit` modreq). New `docs/SCHEMA_DEPRECATION_POLICY.md` names the rules every future contract change must follow: additive changes update the snapshot without a version bump; renames / removals / type changes are breaking and require shipping v2 alongside v1 with at least one minor release of overlap during which v1 responses carry a deprecation `Limitations[]` notice. Surfaces covered by the policy: `ResponseEnvelope`, `tools/list` input schemas, per-tool output JSON, graph JSON schema, canonical id format, parity diagnostic ID set. Per-tool `schemas/tools/v1/<tool>.json` snapshot files are explicitly named as a follow-up atom — the current ratchets pin via reflection / inline JSON assertions until those files land. INV body in `docs/invariants/mcp-protocol.md` ("Wire Contract" section). 

- **`INV-ANALYZE-SKIPPED-PROMINENCE-001` — staleness signals promote to envelope Limitations[] on threshold breach** (`LB-TRACK-20260515-x` Wave W3-A). Pre-fix, `ResponseEnvelope.StalenessSeconds` and `FilesChangedSinceAnalyze` were always on the wire but consumers had to compare them against unwritten thresholds to know whether to re-analyze. A 30-day-stale graph emitted the same envelope as a fresh one. New `StalenessPolicy` (`Application/Ports/Right/IResponseDecorator.cs`) carries two configurable thresholds — `StalenessSecondsWarnThreshold` (default 3600 s), `FilesChangedWarnThreshold` (default 10). `LifebloodResponseDecorator` appends explicit `Limitations[]` entries when either is exceeded, ordered AFTER the static per-tool limitations so consumers reading from index 0 still see tool-specific caveats. Composition root (`Server.Mcp.Program`) reads `LIFEBLOOD_STALENESS_SECONDS_THRESHOLD` / `LIFEBLOOD_FILES_CHANGED_THRESHOLD` from the environment at startup; missing or malformed values fall back to `StalenessPolicy.Default`. The unregistered-tool path also honors the policy. Pinned by `StalenessPolicyEnvelopeTests` (5 facts: fresh graph emits no extra limitation, staleness-seconds threshold appends entry, files-changed threshold appends entry, custom thresholds override defaults, unregistered tool still surfaces staleness limitation).

- **`TestImpactRecallAnchorTests` — Lifeblood-self anchor for `lifeblood_test_impact` recall** (`LB-TRACK-20260515-009`/`010` Wave W2-F). Closes the W2-F gate of the v0.7.6 prep masterplan: with the graph now carrying target-typed-`new(MethodGroup)` edges (`INV-EXTRACT-METHOD-GROUP-CANDIDATE-001`), generic-call canonical-id parity (`INV-EXTRACT-METHOD-ORIGINAL-DEFINITION-001`), and synthesized-ctor surfaces (`INV-EXTRACT-SYNTHESIZED-CTOR-001`), `test_impact` resolves every recently-shipped Wave W1+W2 fixture to its proper test class. Three anchor pins exercise the canonical id shapes: a type target (`type:MetadataReferenceDeduplicator` → `MetadataReferenceDeduplicationTests`), a field target (`field:RoslynModuleDiscovery.MsbuildImplicitNoWarnBaseline` → `MsbuildImplicitNoWarnBaselineTests`), and another type target (`type:RoslynStaticTableExtractor` → `StaticTableExtractorTests`). DAWG-side recall measurement is deferred to the v0.7.6 fresh-MCP redeploy gate per the masterplan; the Advisory heuristic layer on `TestImpactAnalyzer` stays unshipped per the 2026-05-15 user decision — measurement first, design only if recall drops below 95% after redeploy.

- **`INV-EXTRACT-SYNTHESIZED-CTOR-001` — synthesized `.cctor` / parameterless `.ctor()` surface as graph Symbols when types carry initializers** (`LB-TRACK-20260515-010` Wave W2-E). Honest correction of the W2-D "no new code needed" claim: end-to-end ratchet caught a real graph-build gap. Pre-fix, `RoslynEdgeExtractor` attributed field/property initializer edges to `ContainingType.StaticConstructors.FirstOrDefault()` (the synthesized `.cctor` IMethodSymbol). `GraphBuilder.Build` drops every edge whose source isn't a Symbol (line ~89, dangling-edge filter for external refs), and the symbol extractor never surfaced synthesized ctors — so every dispatch-table delegate target, every static-initializer enum/field reference silently disappeared from the graph despite correct extraction. New helper `RoslynSymbolExtractor.SurfaceSynthesizedInitializerConstructors` walks fields + properties for initializer syntax and emits Method-kind Symbols (`method:NS.T..cctor()` / `method:NS.T..ctor()`) for the matching IMethodSymbol whose `DeclaringSyntaxReferences` is empty. Symbols carry `Properties["synthesized"] = "true"`; `LifebloodDeadCodeAnalyzer` skips them after the regular `HasIncomingReference` check (CLR runtime always invokes `.cctor` on type init — inherently live regardless of static reachability, otherwise the surfacing would just shift the false-positive class one symbol over). Symmetric: instance `.ctor()` only surfaces when no user-declared parameterless ctor exists (matches Roslyn's synthesizer policy). Pinned by `DispatchTableLivenessRatchetTests` end-to-end ratchet asserting (a) delegate row method has ≥1 incoming graph edge, (b) `dead_code` does NOT flag it, (c) `dead_code` does NOT flag the synthesized `.cctor` itself. W1-C wall ratchet stays green — synthesized symbols carry no diagnostic surface.

- **`INV-EXTRACT-DISPATCH-TABLE-COVERAGE-001` — dispatch-table cells emit graph edges through existing identifier walkers** (`LB-TRACK-20260515-010` Wave W2-D, closes the second half of `LB-INBOX-011`). Re-investigated as a no-yes-man pushback: the original LB-INBOX-011 proposal asked for a separate "static-table → graph-edge synthesizer" that would emit `References` from each static-table field to its cell-resolved symbols. Empirical re-probe showed the gap was already an instance of LB-INBOX-010 part 1 (target-typed `new(MethodGroup)` candidate handling) — once `INV-EXTRACT-METHOD-GROUP-CANDIDATE-001` ships in `RoslynEdgeExtractor`, every dispatch-table cell (method-group, enum-member, field-reference) emits its canonical edge through the same walker that handles non-table contexts. Method-group cells emit `Calls` from the synthesized `.cctor` (preserving the long-standing v0.6.4 semantic for the explicit `new Lazy<T>(Load)` case); enum-member and field-reference cells emit `References` via `ExtractMemberAccessEdge`. Wire-color choice is observationally identical at the live-ness layer because `dead_code` / `dependants` / `port_health` / `blast_radius` all consume both edge kinds. **No new synthesizer code** — INV body documents the contract so future audits do not re-propose a redundant layer. Pinned by `ExtractEdges_DispatchTableWithMethodGroupAndEnumCells_FullCoverage` (mixed-cell-kind dispatch-table fixture asserting all three cell classes emit their canonical edges from the `.cctor`).

- **`INV-EXTRACT-STATIC-IMPLICIT-ARRAY-001` — implicit array initializers classify as Array container** (`LB-TRACK-20260515-010` Wave W2-C, closes the first half of `LB-INBOX-011`). Pre-fix the extractor's `ClassifyContainer` only matched `IArrayCreationOperation`; the implicit form `static T[] X = { ... }` surfaces in Roslyn as `IArrayInitializerOperation` (no `Type`, element values directly on `.ElementValues`), so a 90-module Unity workspace's recipe arrays (`static readonly float[] Weights = { 0.1f, 0.2f }`) were silently skipped. New explicit `IArrayInitializerOperation` branch in `ClassifyContainer` pulls element values from `op.ElementValues` and resolves the element type from the declaring member's `ITypeSymbol` (threaded into `ClassifyContainer` because the op itself carries no type metadata for the array variant). Container kind stays `Array` — same authoring intent. Operation-tree only; never regex, never syntax-text. Pinned by `GetStaticTables_StaticImplicitArrayField_DetectedAsArrayContainer` + `GetStaticTables_StaticImplicitArrayProperty_DetectedAsArrayContainer` (field + auto-property forms, primitive element-type round-trip).

- **`INV-EXTRACT-METHOD-ORIGINAL-DEFINITION-001` — extractor edges always target the source-declared open-generic form** (`LB-TRACK-20260515-009` Wave W2-B, closes the second half of `LB-INBOX-010`). Pre-fix, `RoslynEdgeExtractor.GetMethodId` built the canonical id directly off whichever `IMethodSymbol` the caller passed in; for a call to a generic method via type-inferred arguments (e.g. `Helper.Pick(items, 5)` binding to `Pick<string>(string[], int)`), the resulting target id used the constructed parameter signature instead of the source-declared open-generic form, so `dependants()` / `dead_code` / `blast_radius` against the declared method returned zero. Empirical class: `ToolHandler.ApplyCap` was called 5× yet showed `directDependants=0`. Fix: `GetMethodId` routes through `(IMethodSymbol)method.OriginalDefinition` at entry; `ExtractCallEdge` is refactored to use `GetMethodId(target)` directly instead of an inline `BuildParamSignature(target)` that bypassed the discipline. Mirrors the same `OriginalDefinition` routing `RoslynCompilationHost.FindReferences` already applies on the consumer side so producer-side and consumer-side ids are byte-identical for the same logical method. Pinned by `ExtractEdges_GenericMethodCall_AttributesToOriginalDefinitionId`.

- **`INV-EXTRACT-METHOD-GROUP-CANDIDATE-001` — target-typed `new(MethodGroup)` and other `CandidateSymbols`-bound method-group references now emit `Calls` edges** (`LB-TRACK-20260515-009` Wave W2-A, closes the first half of `LB-INBOX-010`). Pre-fix, `RoslynEdgeExtractor.ExtractReferenceEdge` early-returned whenever `SymbolInfo.Symbol == null` — Roslyn binds method-group identifiers via `CandidateSymbols` until the outer type-inference context narrows the choice, so target-typed `new(MethodGroup)`, delegate-ctor arguments, and similar shapes silently produced zero edges. Empirical class: `BclReferenceLoader.References = new(Load)` and `RoslynCodeExecutor._cache = new(LoadHostBclReferences)` in Lifeblood self showed `find_references` hits but `dependants=0` on the target methods. New helper `RoslynEdgeExtractor.ResolveCandidateMethodGroup` accepts `CandidateReason.MemberGroup` and `CandidateReason.OverloadResolutionFailure` (the two shapes Roslyn surfaces while target-type inference is incomplete) and returns the first candidate; downstream extraction routes that symbol through the same emit paths as fully-bound symbols. The prior `[Fact(Skip="LB-INBOX-010 ...")]` regression pin (`ExtractEdges_StaticFieldInitializerMethodGroup_TargetTypedNew_AttributedToCctor`) is converted to a live ratchet; new fixture `ExtractEdges_TargetTypedNewMethodGroup_OverloadedMethod_PicksFirstCandidate` pins the overload-disambiguation contract.

### Changed

- **Skipped regression pin retired**: the `LB-INBOX-010` skip on `ExtractEdges_StaticFieldInitializerMethodGroup_TargetTypedNew_AttributedToCctor` is gone now that the extractor fix shipped (`INV-EXTRACT-METHOD-GROUP-CANDIDATE-001`). Test count moves from 963 + 1 skipped to 964 + 0 skipped; STATUS.md prose + the Lifeblood.Tests component-table line updated to match.

- **`INV-DIAGNOSTIC-PARITY-001` — Lifeblood-self diagnose ratchet wall** (`LB-TRACK-20260515-011` Wave W1-C). Single chokepoint pinning every prior parity INV in this wave: any future regression that re-opens IVT propagation (`INV-DIAGNOSTIC-IVT-PARITY-001`), reference closure (`INV-MODULE-REFS-001`), top-level identity dedup (`INV-DIAGNOSTIC-NUGET-BINDING-PARITY-001`), or MSBuild-implicit NoWarn baseline (`INV-DIAGNOSTIC-MSBUILD-IMPLICIT-NOWARN-001`) fails the same test. Canonical parity diagnostic ID set `{ CS0122, CS0117, CS0234, CS1503, CS1701, CS1702, CS1705, CS1729 }` — every member corresponds to one historical Lifeblood-side regression class. Lifeblood's own source tree is the fixture (every commit on `main` must pass `dotnet build` before reaching the test suite, so any parity-class diagnostic firing under Lifeblood's discovery → `ModuleCompilationBuilder` → `RoslynCompilationHost` pipeline against Lifeblood's own modules is by definition a Lifeblood-side false positive). Test runs the full pipeline with `RetainCompilations:true` and asserts an empty parity-class subset of `host.GetDiagnostics()`; surfaces a per-module-per-ID breakdown with sample message text when the wall fires so a future regression names the right INV pointer immediately. Pinned by `BuildDiagnosticParityTests.LifebloodSelfDiagnose_NeverFiresParityClassDiagnostics`. Closes Wave W1 of the v0.7.6 prep masterplan (LB-TRACK-011).

- **`INV-DIAGNOSTIC-MSBUILD-IMPLICIT-NOWARN-001` — MSBuild csc-default `NoWarn` baseline (`CS1701`, `CS1702`) unioned into every discovered module** (`LB-TRACK-20260515-011` Wave W1-A correction + the missing half of W1-A). Pre-fix, `lifeblood_diagnose` against `Lifeblood.Tests` emitted 7,642 spurious `CS1701` findings — cross-module TypeRef binding-redirect warnings fired per consuming type-ref whenever an upstream PE's recorded version of a transitively-shared assembly disagreed with the version currently loaded (xunit.core baked against `System.Runtime 4.0.0.0` vs BCL ref pack `8.0.0.0`). The earlier `MetadataReferenceDeduplicator` (W1-A) handles the front-loaded duplicate-identity case at the top-level reference set but never reaches TypeRefs baked into dependency PE images — that class is what MSBuild's `Microsoft.CSharp.CurrentVersion.targets` silently suppresses via `<NoWarn>$(NoWarn);1701;1702</NoWarn>` on every csc invocation. Lifeblood now mirrors the documented MSBuild baseline: `RoslynModuleDiscovery.MsbuildImplicitNoWarnBaseline` (private static, one source of truth) unioned into `noWarnIds` during `ParseProject` before the field lands on `ModuleInfo.NoWarnDiagnosticIds`, then threaded into `CSharpCompilationOptions.WithSpecificDiagnosticOptions` via the existing INV-COMPFACT-001..003 wire. User-declared `<NoWarn>` entries union with the baseline (no replacement); a module that genuinely needs `CS1701`/`CS1702` back uses MSBuild's own escape hatch (`<WarningsNotAsErrors>` or per-finding `#pragma warning restore`). **Corrects W1-A's `INV-DIAGNOSTIC-NUGET-BINDING-PARITY-001`** which originally claimed dedup alone closed the parity gap and discouraged `<NoWarn>`-based suppression — the dedup is necessary hygiene for the top-level reference graph but the cross-module-TypeRef class is structurally unreachable from dedup, so the baseline mirror is the complementary mechanism. Pinned by `MsbuildImplicitNoWarnBaselineTests` (4 facts: plain csproj carries baseline, user `<NoWarn>` unions with baseline, user-declared `CS1701` stays deduped, end-to-end thread-through to `SpecificDiagnosticOptions`); `CsprojCompilationFactsTests.Discovery_ReadsNoWarn_SemicolonList` updated to assert the union semantics.

- **`INV-DIAGNOSTIC-IVT-PARITY-001` — `<InternalsVisibleTo>` items synthesize friend-assembly attributes onto producer compilations** (`LB-TRACK-20260515-011` Wave W1-B). Pre-fix, a producer csproj declaring `<InternalsVisibleTo Include="Friend" />` emitted no friend-assembly metadata onto its Lifeblood-built PE — the SDK-style source scan skips `obj/`, so MSBuild's `GenerateAssemblyInfo`-generated `*.AssemblyInfo.cs` file never entered the compilation. Friend modules consuming the downgraded reference saw a PE with no IVT, and every internal access fired CS0122 (empirically 223 spurious findings on Lifeblood.Tests against the Adapters.CSharp surface while `dotnet build` was clean). New discovered module fact `ModuleInfo.InternalsVisibleTo: string[]` (Domain-layer port, zero Roslyn dep, default empty preserves pre-fix behavior). `RoslynModuleDiscovery.ParseProject` reads every `<InternalsVisibleTo Include="X" />` item-group entry (trim, distinct by ordinal, empty Includes drop). `Internal.ModuleCompilationBuilder.CreateCompilation` prepends a synthetic syntax tree at path `<InternalsVisibleTo>.cs` carrying one `[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("X")]` per declared target, parsed with the module's own `CSharpParseOptions` so downstream `AddSyntaxTrees` / `ReplaceSyntaxTree` calls do not throw "Inconsistent language versions" against modules declaring a non-default `<LangVersion>`. Modules that surface friend access through hand-authored `[assembly: InternalsVisibleTo]` attributes in source still compile correctly — their IVT flows through normal source discovery. No `obj/` widening (those artifacts are owned by `dotnet build`, not Lifeblood). Pinned by `InternalsVisibleToParityTests` (5 facts: single-item discovery, multi-item union + dedupe + empty-drop, no-items default empty, IVT attribute round-trips on producer compilation's `Assembly.GetAttributes()`, friend-named consumer reaching producer internals emits zero CS0122).

- **`INV-DIAGNOSTIC-NUGET-BINDING-PARITY-001` — assembly identity unification on every compilation reference set** (`LB-TRACK-20260515-011` Wave W1-A). Pre-fix, a workspace whose NuGet closure overlapped simple-names with the SDK BCL ref pack made Roslyn emit `CS1701` / `CS1702` / `CS1705` once per consuming type-ref — empirically 7,537 spurious findings on Lifeblood's own test assembly while `dotnet build` was clean on the same compilation. New seam `Internal.MetadataReferenceDeduplicator.Deduplicate` collapses the reference set to one `MetadataReference` per assembly simple-name keeping the highest `AssemblyIdentity.Version`, mirroring MSBuild's `<AutoUnify>true</AutoUnify>` default. Called by `ModuleCompilationBuilder.CreateCompilation` immediately before `CSharpCompilation.Create`. Identity read via `AssemblyMetadata.GetModules()[0].GetMetadataReader().GetAssemblyDefinition()` — Roslyn's public primitive. Refs whose metadata cannot be parsed as an assembly (modules, native DLLs that escape the loader filter, in-memory refs without an emitted identity) pass through unchanged so the loader can still surface load failures as regular diagnostics. No per-name special case; no `CS1701` suppression via `<NoWarn>` (that would be a hotpatch under `INV-AUTONOMY-003`). Pinned by `MetadataReferenceDeduplicationTests` (6 facts: highest-version wins, order-independent, distinct names survive, empty input, unreadable identity passes through, end-to-end zero `CS1701` on a synthesized duplicate-identity compilation).

## [0.7.5] - 2026-05-14

Post-v0.7.4 wave landing one new MCP tool (`lifeblood_static_tables`) with v1.1 method-return-flag enrichment, one new doc-honesty INV pin, one new live-counting test-count ratchet, and a pre-tag counter audit closing four invariant-count callsites the v0.7.4 sweep missed. **Tests 896 → 946 + 1 skipped regression pin (+50, skipped pin unchanged at `LB-INBOX-010`). MCP tools 28 → 29 (read-side 17, write-side 12 — `lifeblood_static_tables` lives on the write-side handler under the Compilation-required convention). Invariants 87 / 50 categories → 90 / 52 categories.** Three new INVs pinned (`INV-EXTRACT-STATIC-TABLES-001`, `INV-METHOD-FLAG-SUMMARY-001`, `INV-FLAG-COVERAGE-COMPOSITION-001`) plus `INV-DOCS-004` (testCount ratchet). Hexagonal as ever — the new tool ships as Domain DTOs (zero Roslyn dep) + `ICompilationHost.GetStaticTables` port + `RoslynStaticTableExtractor` adapter + `WriteToolHandler` dispatch trio.

### Added

- **`lifeblood_static_tables` MCP tool** (`INV-EXTRACT-STATIC-TABLES-001`). Generic Roslyn extraction of `static` field / property collection-shaped initializers as typed row + cell facts. Sourced from `SemanticModel.GetOperation` only — no regex, no syntax-text parsing, no static-constructor execution. Detects `IArrayCreationOperation`, `ICollectionExpressionOperation`, and single-object `IObjectCreationOperation` container shapes; classifies each cell into `Null` / `Bool` / `String` / `Number` / `EnumMember` / `EnumFlags` (`Bits.A | Bits.B` flattened to a leaf id list in authoring order, including nested `(A | B) | C` shapes) / `MethodGroup` (delegate-target method id only — body content is dataflow and a separate truth tier) / `FieldReference` (non-enum static field) / `Array` (nested literal arrays recursively classified) / `Computed` (eternal fallback with `rawText` source-span provenance). Constructor rows bind cells by parameter name + ordinal via `IObjectCreationOperation.Arguments`; `argumentKind` mirrors `IArgumentOperation.ArgumentKind` (`Explicit` / `DefaultValue` / `ParamArray`). Literal-array rows carry their value on `row.value`. Optional caps: `memberName` narrows to one field/property, `maxRows` (default 1024) caps rows per table, `maxTables` (default 64) caps tables per type — fires `rowsTruncated` / `tablesTruncated` flags; zero / negative caps clamp to the default. **Generic by contract**: pinned by `StaticTableNameLeakageTests` which scans `RoslynStaticTableExtractor.cs` + `StaticTableResults.cs` for a curated list of consumer-domain identifiers — any match fails the build. Tool placement: `WriteToolHandler` (Compilation-required convention, not mutation). Domain: `StaticTableReport` + `StaticTable` + `StaticTableRow` + `StaticTableCell` + `StaticTableValue` records (zero Roslyn dep). Port: `ICompilationHost.GetStaticTables`. Adapter: `RoslynStaticTableExtractor` (separate internal class so the leakage ratchet scopes cleanly). Pinned by `StaticTableExtractorTests` (33 facts).

- **`lifeblood_static_tables` v1.1: `MethodReturnFlagIds[]?` on `MethodGroup` cells** (`INV-METHOD-FLAG-SUMMARY-001`, `LB-TRACK-20260514-005-v1.1`). Additive enrichment on the `MethodGroup` cell kind. When the delegate target carries a source declaration in the cell site's compilation AND at least one return position resolves to an enum-flag value (single enum-const reference or `|`-composed binary-op tree of enum-const leaves), the cell carries the deduplicated, ordinal-sorted union of those flag-member ids on `Value.MethodReturnFlagIds`. Multi-return-path methods union across paths. Computed returns silently skip for that path — mixed bodies surface the classified subset rather than dropping all information. Null in four load-bearing cases: target has no `DeclaringSyntaxReferences` (compiled-metadata BCL), target's syntax tree not in site compilation (cross-compilation enrichment deferred), body has no walkable `IOperation` tree, every return-position classification falls back to `Computed`. `ReturnFlagCollector` overrides `VisitAnonymousFunction` / `VisitLocalFunction` to prevent nested-lambda returns bleeding through. Wire-shape additive — existing callers reading only `MethodGroupId` stay byte-stable. Pinned by `StaticTableExtractorTests` (7 new facts + 1 augmented null-pin on the existing MethodGroup-cell test).

- **`INV-FLAG-COVERAGE-COMPOSITION-001` pinned** (`LB-TRACK-20260514-006`, doc atom — zero new tool code). The "does method M reference every enum-flag in row R's flag-cell?" join composes from `lifeblood_static_tables` (row side) + `lifeblood_dependencies` (method side) with client-side set-relation. Lifeblood MUST NOT ship a one-classification-per-(row,method) wire-shape using consumer-interpretation names (`admitted` / `admission` / `inconclusive` / `denied`) — those bake the consumer's *meaning* of "coverage" into Lifeblood's contract, when the relations (subset / partial / disjoint) are pure set-arithmetic over two flag-id sets the existing tools already emit. `lifeblood_dependencies` supersets `MethodReturnFlagIds` for three cases the v1.1 field nulls: cross-compilation targets (graph edges cross module boundaries), body-position references beyond `return` (inspection guards, switch arms, ternaries), transitive walks through helper-call delegation (caller follows the `Calls` edge if they need closure). Doc atom only: xmldoc `<remarks>` see-also on `StaticTableValue.MethodReturnFlagIds`, INV body in `docs/invariants/tools.md`. No new tool, no new port, no new test — composition rests on existing `INV-METHOD-FLAG-SUMMARY-001` (row side) + `EdgeCallSiteTests.Extract_FieldReference_AttachesCallSite` (method side `References`-edge pin).

- **`StatusDoc_TestCount_MatchesDiscoveredCases` live-counting ratchet** (`INV-DOCS-004`). Sibling to the existing `portCount` / `toolCount` ratchets in `DocsTests`. Reflects on the `Lifeblood.Tests` assembly, counts every concrete-class method carrying a `FactAttribute`-derived attribute (`[Fact]`, `[Theory]`, `[SkippableFact]`); each `[Theory]` is multiplied by its `[InlineData]` row count (fallback 1 when only `[MemberData]` / `[ClassData]` is present, since those expand at runtime via test-case generators). Matches xUnit discovery semantics — same number `dotnet test --list-tests` produces. Closes the silent-drift hole on the `<!-- testCount: N -->` STATUS.md anchor (pre-this commit the anchor was hint-only — it had drifted from 937 to live 946 unnoticed because no ratchet enforced it). Note: this ratchet is anchored to STATUS.md only; visible "N tests + 1 skipped" prose across README / DOGFOOD / architecture.html is still manually maintained — a cross-doc visible-prose ratchet for those surfaces is open work.

### Changed

- **Pre-tag doc-truth audit** (`de8217a`, `8d3a58c`, `d897650`). Eight live-truth counter callsites refreshed across `README.md`, `docs/STATUS.md`, `docs/ARCHITECTURE.md`, `docs/TOOLS.md`, `docs/architecture.html`, `docs/DOGFOOD_FINDINGS.md` — invariant counts (87 / 50 categories → 90 / 52 categories, including four older callsites at 80 / 65 that prior sweeps walked past), self-analysis numbers (2,755 / 13,778 → 2,926 / 14,502), MCP tool count discrepancy in DOGFOOD_FINDINGS (28 / 17R+11W → 29 / 17R+12W), version label ("preparing v0.7.4" → "post-v0.7.4"). CHANGELOG historical entries, IMPROVEMENT_INBOX dated SHIPPED snapshots, and DOGFOOD_FINDINGS pre-v0.7.4 session blocks preserved verbatim — regression-trace catalogue.
- **Eternal-comment policy enforcement** (`8d3a58c`). `INV-WORK-009` — stripped a `(v0.6.7)` version parenthetical from `ToolHandler.cs:592` dead-code-warning code comment. Load-bearing `INV-DEADCODE-001` + `INV-ENVELOPE-001` references preserved. Tool-description version-history narrative in `ToolRegistry.cs` left intact — that text ships to MCP clients and helps callers set expectations on which FP classes are already closed (different audience than internal comments, different policy).

## [0.7.4] - 2026-05-14

Post-v0.7.3 wave + pre-release audit closure. Five new tool-facing capabilities (`test_impact`, `enum_coverage`, `cycles` taxonomy, `dead_code` triage fields, preprocessor-scope on `diagnose` / `compile_check` envelopes), four follow-on bug fixes from a 2026-05-14 fake-stuff audit (BFS seeding for File-target test impact, file-SCC partial-class cycle classification, `DefineConstants` threaded into `CSharpParseOptions`, semantic test-fixture detection in the tier classifier), a five-part Roslyn-faithfulness FOLLOWUP series shifting csproj-driven compilation facts from "parsed but ignored" to "parsed and threaded into Roslyn options" (`LangVersion` / `Nullable` / `NoWarn`) plus a Domain-layer path-classifier SSoT extraction collapsing three drifted bucket classifiers into one. **One release blocker caught by the pre-tag audit and fixed in the same wave: `compile_check` threw `ArgumentException: Inconsistent language versions (Parameter 'syntaxTrees')` on every workspace whose modules declared a non-default `<LangVersion>` — regression introduced by the FOLLOWUP-001 thread-through. Closed by threading module `CSharpParseOptions` through `RoslynCompilationHost.CompileCheckFile` + `CompileCheckSnippet` + `SnippetWrapper.Prepare`; pinned by `CompileCheckParseOptionsParityTests` (3 facts).** Plus a pre-release doc + comment sweep: doc tree rolled across `README.md` / `STATUS.md` / `ARCHITECTURE.md` / `TOOLS.md` / `DOGFOOD_FINDINGS.md` / `architecture.html` / `MCP_SETUP.md` / `UNITY.md` to reflect the new counts, `INV-PATHBUCKET-SHARED-001` authored (was referenced inline + across source + tests but had no own bullet), six-lego eternal-comment sweep across `src/` + `tests/` stripping Phase-X / Stage-N / dated "Added 2026-04-11" / finding-ID journey prose (~33 files, net ~70 LOC trimmed, every INV-ID + architectural rationale preserved), three Phase-named test files renamed (`AnalysisToolsPhase6Tests` → `AnalysisToolsTests`, `FindReferencesPhase4Tests` → `FindReferencesTests`, `ResolverPhase3Tests` → `ResolverCapabilityTests`). **Tests 776 → 896 + 1 skipped regression pin (+120, +1 skip). MCP tools 26 → 28** (+2: `lifeblood_enum_coverage`, `lifeblood_test_impact`). **Read-side tools 16 → 17, write-side tools 10 → 11. Invariants 80 / 43 categories → 87 / 50 categories.** Six new INVs pinned (`INV-DEADCODE-TRIAGE-001`, `INV-DIAGNOSTIC-ENVELOPE-DEFINES-001`, `INV-CYCLE-TAXONOMY-001`, `INV-ENUM-COVERAGE-001`, `INV-TEST-IMPACT-001`, `INV-PATHBUCKET-SHARED-001`). One open backlog item authored on this wave with a skipped regression pin ready to ratchet on close: `LB-INBOX-010` (target-typed `new(MethodGroup)` + generic-method canonical-id drift — `dead_code` / `dependants` miss usages that `find_references` correctly sees). Hexagonal as ever — every feature lands as port + adapter + handler trio with regression tests.

### Added

- **`lifeblood_test_impact` MCP tool** (`INV-TEST-IMPACT-001`, `LB-TRACK-20260514-007`). Which test classes transitively depend on a target symbol or file. BFS over incoming non-Contains edges with per-symbol minimum-distance tracking; affected methods are classified test-vs-non-test via the extractor-recorded `Properties["attributes"]` set (`Test`, `TestCase`, `TestCaseSource`, `Theory`, `UnityTest`, `Fact`). Lifecycle attributes (`SetUp` / `OneTimeSetUp` / `TearDown` / `UnityTearDown`) are excluded — they participate in test execution but are not the assertion-bearing methods a caller wants enumerated. Test methods are folded by containing type; per-class `minDistance` mapped to confidence `Direct` (1) / `OneHop` (2) / `Transitive` (3+). Response carries `target`, `targetKind`, `totalTestMethodCount`, `directTestClassCount`, `affectedTestClassCount`, `affectedTestClasses[]` sorted by ascending distance then qualified name, plus `recommendedFilters[]` — pre-composed `FullyQualifiedName~<class>` strings the caller pastes into `dotnet test --filter`. Target disambiguation: a `target` starting with a canonical-id prefix routes through `ISymbolResolver`; otherwise treated as a file path with every symbol declared in the file becoming a multi-source BFS start. Lives in `Lifeblood.Analysis/TestImpactAnalyzer.cs` (pure graph read, no Roslyn compilation needed — Analysis layer reads Domain only per the hexagonal dependency rules, called directly from the composition root). Handler: `ToolHandler.HandleTestImpact`. Pinned by `TestImpactAnalyzerTests` (9 facts).
- **`lifeblood_enum_coverage` MCP tool** (`INV-ENUM-COVERAGE-001`, `LB-TRACK-20260514-003`). Per-member reference coverage for an enum type — `produced` / `consumedComparison` / `consumedSwitch` / `other` counts classified by parent syntax, plus convenience flags `isUnproduced` (declared + referenced as a consumer but never assigned) and `isUnreferenced` (zero references). Walks every loaded compilation once and classifies each enum-member reference site via the parent chain: assignment RHS / return / yield / arrow body / argument / initializer → Produced; `==` / `!=` / `<` / `<=` / `>` / `>=` → ConsumedComparison; `case` label / `is`-pattern / constant-pattern / switch-expression arm → ConsumedSwitch. Top-level `unproducedCount` + `unreferencedCount` summarize the dogfood case ("which values in this state-machine enum are checked-for but never assigned?") off the top of the wire. `enumTypeId` accepts canonical, fully-qualified, or bare short name through the same resolver as every other type-id-taking tool. Port: `ICompilationHost.GetEnumCoverage`. Adapter: `RoslynCompilationHost.GetEnumCoverage` + private `EnumRefClass` taxonomy. Handler: `WriteToolHandler.HandleEnumCoverage`. Pinned by `EnumCoverageTests` (8 facts).
- **`lifeblood_cycles` taxonomy** (`INV-CYCLE-TAXONOMY-001`, `LB-TRACK-20260514-008`). Every detected SCC is classified into one of three triage buckets: `GeneratedOrStaticAnalysisArtifact` (build artifacts / source-generator output, never a refactor target — short-circuits ahead of every other signal), `PartialClassCluster` (every participating member resolves to the same enclosing type — intra-type mutual-recursion or partial-class spread), or `LikelyRealLoop` (cross-type / cross-module architectural-backlog cycles, the actual refactor surface). Implementation reuses the existing Tarjan SCC pass via `CircularDependencyDetector.DetectClassified` so cycle membership is byte-identical to the legacy `string[][]` shape — bucket is additive metadata. Wire-shape additive: response carries `descriptors[] { symbols, bucket }` and `bucketBreakdown { GeneratedOrStaticAnalysisArtifact, PartialClassCluster, LikelyRealLoop }` alongside the legacy `cycles[][]`; summarize mode adds `previewClassified[]`. Pre-fix the dogfood Unity workspace shipped 123 SCCs as a flat `string[][]` — the lion's share were partial-class clusters, not architectural loops. Pinned by `CycleTaxonomyTests` (8 facts).
- **`lifeblood_dead_code` triage fields** (`INV-DEADCODE-TRIAGE-001`, `LB-TRACK-20260514-004`). Every finding now carries `directDependants` (incoming non-Contains edge count — always 0 for classic findings but on-wire as a forward-compatible signal for future relaxed-criteria modes), `bucket` (`Production` / `Test` / `Editor` / `Generated` — segment-aware path classification mirroring the `blast_radius groupBy=bucket` taxonomy), and `declarationOnly` (true iff `Symbol.IsAbstract` — deleting an abstract member is a public-contract change, not a routine cleanup). Response additionally carries `bucketBreakdown` alongside the existing `kindBreakdown` so a caller can fold the giant Editor / Generated tail without re-walking `findings[]`. Bucket precedence Generated → Test → Editor → Production: a fixture under `Tests/Editor/Foo.cs` classifies Test (defined by `Tests` root + filename) rather than Editor. Pinned by `DeadCodeTriageFieldsTests`.
- **Preprocessor scope on `lifeblood_diagnose` and `lifeblood_compile_check` envelopes** (`INV-DIAGNOSTIC-ENVELOPE-DEFINES-001`, `LB-TRACK-20260514-002`, `LB-INBOX-008`). Every response surfaces `definesActive` (sorted, deduplicated active preprocessor symbols) plus `resolvedModule` (the compilation the scope resolved to; empty for project-wide). Pre-fix a caller could not distinguish an Editor-only finding from a release-build risk without re-running under a different define set; `UNITY_EDITOR`-gated code emitted the same wire shape as `UNITY_STANDALONE` code. Scope rules mirror legacy diagnostics: file-scope routes through `FindOwningCompilation`; module-scope uses the request's module name; project-wide returns the sorted-deduped union across every loaded compilation. Domain: `DiagnosticsReport { Diagnostics, DefinesActive, ResolvedModule }` + `DefinesActive` on `CompileCheckResult`. Port surface: `ICompilationHost.GetDiagnosticsReport`. Pinned by `DiagnosticEnvelopeDefinesTests` (7 facts).
- **`PathBucketClassifier` Domain-layer SSoT** (`INV-PATHBUCKET-SHARED-001`, `LB-FOLLOWUP-20260514-005`). Three drifted bucket classifiers — `LifebloodDeadCodeAnalyzer.ClassifyBucket`, `LifebloodMcpProvider.ClassifyBucket`, `CircularDependencyDetector.IsGeneratedOrStaticAnalysisPath` — collapsed to one `Lifeblood.Domain.PathClassification.PathBucketClassifier` (zero-dependency leaf type). `PathBucket` enum mirrors `DeadCodeBucket` integer values; parity pinned by `PathBucketParityTests`. The next consumer that needs the same Production / Test / Editor / Generated taxonomy reuses the SSoT without re-deriving the path-segment rules.
- **`SymbolPropertyKeys` writer↔reader parity test** (`LB-FOLLOWUP-20260514-004`). Pins the `Properties["attributes"]` / `Properties["baseType"]` / `Properties["classification"]` string-key contract between the writer (`RoslynSymbolExtractor`) and every reader (`UnityReachabilityAdapter`, `RoslynEdgeExtractor`, `LifebloodAuthorityReporter`, `TestImpactAnalyzer`). A typo on either side previously failed silently — the test asserts every writer key has at least one consumer and every consumer key matches a writer.
- **`docs/audit/fake-stuff-2026-05-14.md`** — 353-line audit doc capturing the dogfood pass that produced the BUG-1..4 + FOLLOWUP-001..005 backlog. Eternal history of the audit.

### Fixed

- **`LangVersion` / `Nullable` / `NoWarn` / `DefineConstants` now threaded into Roslyn `CSharpParseOptions` / `CSharpCompilationOptions`** (`LB-FOLLOWUP-20260514-001..003`, BUG-2). Pre-fix a csproj declaring `<LangVersion>13</LangVersion>` was silently ignored: `ModuleCompilationBuilder.CreateCompilation` bound the host's `CSharpParseOptions.Default.LanguageVersion`, so a module asking for C# 12 / 13 features compiled under C# 11. Same drift for `<Nullable>enable</Nullable>` (warning level — `nullable enable` projects compiled as if `nullable disable`), `<NoWarn>` (declared suppressions ignored — every CSproj noise warning surfaced anyway), and `<DefineConstants>` (preprocessor symbols ignored — `#if MY_FLAG` blocks dead-code-eliminated regardless of declaration). FOLLOWUP-001 threads `LangVersion`, FOLLOWUP-002 threads `Nullable` warning level, FOLLOWUP-003 threads `NoWarn`, BUG-2 closes `DefineConstants` (was a separately discovered class). All four parse the csproj string with documented fallback rules; unknown values surface a discovery-time warning instead of a hard failure.
- **`lifeblood_test_impact` File-target BFS seeding** (BUG-1). Type symbols in a file-scope target now correctly seed the BFS via their member symbols (`GetMembersByContains(typeId)`). Pre-fix File-target requests where the file contained only Type-level declarations (no method bodies in the file itself, e.g. a partial-class declaration file) surfaced zero affected tests because the BFS started from a Type seed with no inbound edges.
- **`lifeblood_cycles` partial-class clustering** (BUG-3). File-SCC partial-class clusters classify as `PartialClassCluster` instead of falling through to `LikelyRealLoop`. Pre-fix the file-level cycles surfaced by the Tarjan pass — when partial-class spread put two `Calls` edges in different files pointing at each other — were misclassified as architectural backlog. Fix walks the Contains reverse-chain on every participating symbol and asserts every member resolves to the same enclosing Type before classifying.
- **`TierClassifier` semantic test-fixture detection** (BUG-4). The tier classifier now uses the same `[Test]` / `[TestCase]` / `[Theory]` / `[Fact]` / `[UnityTest]` attribute scan as `TestImpactAnalyzer` (via `Properties["attributes"]`). Pre-fix it filename-sniffed (`*Tests.cs` / `*Test.cs`) and missed fixtures with non-conventional names. Cross-tool semantic-test-detection now flows through one rule.

### Changed

- **`docs/STATUS.md` counts**: 776 → 893 tests; tool count stable at 28, port count stable at 26 (descriptions updated to mention the new tools + new INVs).
- **`docs/invariants/csharp-adapter.md`**: `LangVersion` / `Nullable` / `NoWarn` / `DefineConstants` moved from `INV-COMPFACT-001`'s "future additions" example list to a "shipped via FOLLOWUP-001..003 + BUG-2" cross-reference. The compilation-facts pattern is now exemplified by five live facts, not two.
- **`docs/invariants/tools.md`**: six new INV bodies authored (DEADCODE-TRIAGE, ENUM-COVERAGE, TEST-IMPACT, CYCLE-TAXONOMY, DIAGNOSTIC-ENVELOPE-DEFINES, PATHBUCKET-SHARED).
- **Eternal-prose scrub across generic docs** (polish). Customer-specific references removed from docs that should read as generic — the 0.7.3 wave did this for source + tests; this wave finishes the doc surface. CHANGELOG historical entries, `docs/DOGFOOD_FINDINGS.md` empirical event log, and `docs/plans/dawg-dogfood-polish-2026-04-26.md` historical plan preserved verbatim — those are immutable records of what happened.
- **CI**: NuGet push step dropped from `publish.yml`; release-verify gates retained. Publishing is now opt-in manual instead of tag-driven, matching the "Claude commits, user tags + pushes" cadence.

## [0.7.3] - 2026-05-12

Post-v0.7.2 polish wave from a field-report dogfood pass on a real-world Unity workspace (2026-05-11). One high-severity silent-data-loss bug fixed (cross-module edge drop under incremental analyze), three structured-wire-shape improvements landed (CallSite provenance on every expression-derived edge, type-scoped member resolution, `blast_radius` bucket / per-module grouping), plus an eternal-prose cleanup that generalized consumer-project examples across src + tests + shipped docs so Lifeblood reads as a generic Roslyn-semantic tool rather than coupled to one consumer. **Tests 751 → 776 (+25). MCP tools 25 → 26. Read-side tools 15 → 16. Eleven commits land the wave** (six feature/fix + one CLI + four polish — README lede, masterpolish contract truth, schema CallSite, final docs sweep). Every fix lands as a port + adapter + handler trio with regression tests; `INV-INCREMENTAL-XREF-001` + `INV-EDGE-CALLSITE-001` + `INV-RESOLVE-MEMBER-001` + `INV-BLAST-RADIUS-GROUP-001` pinned in the invariant tree. Hexagonal as ever.

### Fixed

- **Cross-module edge drop under incremental analyze** (`LB-BUG-020`, `INV-INCREMENTAL-XREF-001`). Previously `DowngradedRefs` lived as a local `Dictionary<string, MetadataReference>` inside `ModuleCompilationBuilder.ProcessInOrder` and was discarded at end-of-call. Full analyze populated it for every module; incremental analyze called `ProcessInOrder` with only the `modulesToRecompile` subset, so unchanged dependencies had no metadata reference, every cross-module symbol bound to a Roslyn error symbol, and the corresponding edges were silently dropped by `GraphBuilder`'s dangling-edge filter. Empirical repro at diagnosis time on a multi-module Unity workspace: cross-module edges silently dropped in proportion to the unchanged-module fan-in on a single-file touch (~99 edges per file on a 90-module project; 16,545 edges / 7.6% loss across a 657-file change set; `Symbol.ToRequest()` `dependants` count `0` vs full-mode `3`, with `confidence: Proven` claimed on both). False-positive class: any `lifeblood_dead_code` / `lifeblood_blast_radius` / `lifeblood_dependants` query run after incremental analyze could declare a live symbol "dead". Fix (eternal hexagonal): `AnalysisSnapshot` owns `DowngradedRefs` (per-snapshot persistence). `ModuleCompilationBuilder.ProcessInOrder` accepts a `carryDowngraded` seed + merges working dict back into snapshot-owned carry at end-of-call. `RoslynWorkspaceAnalyzer` clears on full analyze, threads carry on incremental. Acceptance criterion (now pinned): two consecutive analyze calls (incremental then full) on the same source tree produce identical `summary.edges`. Pinned by `IncrementalAnalyzeTests.IncrementalAnalyze_CrossModuleEdges_IdenticalAfterContentlessTouch` + `IncrementalAnalyze_CrossModuleEdges_PreservedAfterContentChangeInDependent`. **Note on numbering**: the source-comment / commit-message references to `LB-BUG-017` from the DAWG-side authoring backlog are the same bug renumbered here to `LB-BUG-020` to resolve a collision with the already-shipped invariant parser fix from v0.7.2.

### Added

- **`Edge.CallSite` structured provenance** (field-report 2026-05-11 P1). New domain type `Lifeblood.Domain.Graph.CallSite` with `FilePath` / `Line` / `Column` / `EndLine` / `EndColumn` / `ContainingSymbolId`, nullable on `Edge.CallSite` for edges with no single authoring location (module→module DependsOn, type→type Inherits without a surfaced clause node). `RoslynEdgeExtractor.BuildCallSite` populates from the originating Roslyn syntax node during extraction. `JsonGraphExporter` / `JsonGraphImporter` round-trip the new field so cached graphs preserve provenance across save/load. Pinned by `EdgeCallSiteTests.Extract_CallEdge_AttachesCallSiteWithFileLineColumn`, `Extract_FieldReference_AttachesCallSite`, `JsonGraph_RoundTripsCallSite`.
- **`lifeblood_dependencies` / `lifeblood_dependants` wire surface** (field-report 2026-05-11 P1 follow-on). New `IMcpGraphProvider.GetDependencyEdges` / `GetDependantEdges` return `EdgeDetail[]` (`OtherEndId`, `Kind`, nullable `CallSite`) alongside the legacy string-id `GetDependencies` / `GetDependants` methods. MCP handler `BuildEdgeWire` lifts each edge into `{ otherEndId, kind, callSite }` so a single call answers "where in source does X depend on Y?". Live-MCP dogfood proof: 133,523 of 219,548 edges (60.8 %) carry CallSite on a real-world Unity workspace export — the remainder are graph-derived edges with no authoring location, as designed.
- **`lifeblood_resolve_member` MCP tool** (field-report 2026-05-11 P1). Type-scoped member resolution with overload disambiguation. `typeName` accepts canonical `type:NS.T`, fully-qualified `NS.T`, or bare short name `T`; bare names dispatch through the short-name index and surface `AmbiguousContainingType` when more than one type carries the short name. `memberName` matches Method / Property / Field / Event members; `paramTypes[]` filters method overloads by parameter signature. Returns typed `outcome` (`Unique` / `MultipleMatches` / `NotFound` / `TypeNotFound` / `AmbiguousContainingType`), resolved containing-type id, every matching member with `kind` / `filePath` / `line` / `paramDisplay`. Use this instead of `lifeblood_resolve_short_name` when you know the containing type — scopes to ONE specific type instead of flattening every member sharing a global short name. Port: `ISymbolResolver.ResolveMember` + `MemberResolutionResult`. Adapter: `LifebloodSymbolResolver.ResolveMember` (mirrors Rule 4 short-name dispatch for bare type-name inputs). Pinned by `ResolveMemberTests` (12 cases covering every `ResolveMemberOutcome`).
- **`lifeblood_blast_radius groupBy`** (field-report 2026-05-11 P1). Optional `groupBy=bucket|module|both` switches the response shape from a flat `affected[]` list to `byBucket` (Production / Test / Editor / Generated) and/or `byModule` (per-asmdef counts), each with optional per-group `preview[]` capped by `previewPerGroup` (default 5). Path-prefix bucketing (`Editor/`, `Tests/`, `.Generated.`) — no separate symbol-side metadata. Mutually exclusive with `summarize:true`. Closes the multi-megabyte-flat-list problem for transitive blasts on popular types: a caller asking "how much production code breaks if I touch X?" reads one number, not a megabyte. Pinned by `BlastRadiusGroupingTests`.
- **CLI: `lifeblood verify --incremental --project <path>`** — full-then-incremental analyze in one process, asserts identical `summary.edges` against `INV-INCREMENTAL-XREF-001`. Non-zero exit on drift so it's CI-wireable. Live dogfood proof: passes on both Lifeblood self (2,513 / 12,446) and a real-world 90-module Unity workspace (62,134 / 219,548).
- **CLI: `lifeblood export --project <path> --out <file>`** — writes the graph JSON to a file instead of stdout. Avoids the v0.7.2 PowerShell-redirect / UTF-16-BOM pitfall (`INV-JSON-IMPORT-BOM-001` was the pre-fix; `--out` removes the need to redirect at all). File writes route through the new `IFileSystem.OpenWrite` port for consistency with the existing read surface.
- **CLI: `lifeblood analyze --incremental` rejects with guidance** — the CLI is single-shot, so there is no persistent snapshot for an incremental call to consult. The flag refuses with a clear message pointing the caller at `lifeblood-mcp` (for interactive incremental) or `lifeblood verify --incremental` (for a one-shot drift check). Silent no-op was hostile UX; refusing with guidance is honest.

### Changed

- **Eternal-prose cleanup across src + tests + shipped docs** (polish, 36 files / +141 −130 lines). Generalized illustrative examples that referenced a specific dogfood Unity workspace (DAWG) and its domain symbols by name (`Nebulae.BeatGrid.Audio.DSP.VoicePatchAdapter`, `MixerScreenAdapter`, `FieldMask.ShimmerPhase`, `AdaptiveBeatGrid`, etc.) into eternal placeholders (`Acme.Module.Storage.Repository`, `WidgetSurfaceAdapter`, `Flags.Active`, `MultiPartialHost`) that demonstrate the same patterns without coupling Lifeblood to one consumer. Tool descriptions that cited scale numbers as `"DAWG: 53k symbols → 286KB+ payload"` rewritten as `"large workspaces: 53k+ symbols may produce 286KB+ payloads"`. Hardcoded example paths (`<workspace>/Assets/.../Foo.cs`) replaced with `/path/to/your/project` / `<project-root>/...`. Preserved: `CHANGELOG.md` historical entries, `docs/DOGFOOD_FINDINGS.md` empirical event log, `docs/plans/dawg-dogfood-polish-2026-04-26.md` historical plan — those are immutable records of what happened, eternal as history. Behavior unchanged; 776/776 tests stay green. `.gitignore` catches up to ignore the redeploy-watcher staging directory + machine-local hot-swap tooling.
- **`lifeblood_dependencies` / `lifeblood_dependants` tool descriptions** updated to declare the new `callSite` wire field so callers can discover the shape from the schema without inspecting a response.
- **`docs/STATUS.md`** counts: 751 → 776 tests, 25 → 26 MCP tools, 15 → 16 read-side tools.
- **`docs/TOOLS.md`** adds the `Resolve member` row, expands `Dependencies` / `Dependants` / `Blast radius` rows with the new wire fields.

## [0.7.2] - 2026-05-08

DAWG-dogfood wave covering the full G1+G2+G4+R2-3 finding set from a Unity-shaped audit session, plus three pre-tag-review release-blocker fixes (UTF-16 graph import crash, `global.json` invalid sdk version, MCP-layer no-prior incremental contract violation), four pre-tag-review polish items, and one parser fix discovered via broader-class investigation (multi-segment INV ids silently dropped — a 10-invariant repo-wide silent gap). **Tests 664 → 751 (+87). Invariants reported by the parser 66 → 76 (+10) — six newly authored, plus four pre-existing multi-segment invariants that the parser was silently dropping.** Six commits land the wave + one for verification suite + one for doc sweep + one for pre-tag review fixes + one for the multi-segment parser fix. The dangerous silent-wrong-answer class around enum-member resolution closes both-sided (extractor + resolver), the analyze pipeline gains a fail-loud caller-owned scope policy, search hits are structurally typed by source bucket, and the authority report stops sounding like an ABG-only tool. Hexagonal as ever — every fix lands as a port + adapter + handler trio with regression tests.

### Pre-tag review fixes (release-blockers)

A pre-tag review found three release-grade holes after the initial wave landed. All fixed before tagging.

- **`JsonGraphImporter` crashed on Windows PowerShell-redirected files** (B1, `INV-JSON-IMPORT-BOM-001`). The README documents `lifeblood export --project ... > graph.json`, but PowerShell `>` writes UTF-16-LE-with-BOM by default; `System.Text.Json.JsonSerializer.Deserialize<T>(Stream)` requires UTF-8 and throws an unhandled exception on any other BOM. Affects both CLI (`lifeblood analyze --graph ...`) and MCP (`graphPath` argument). Fix: read through `StreamReader(stream, UTF8, detectEncodingFromByteOrderMarks: true)` and pass the resulting string to the JSON deserializer. All five standard BOMs (UTF-8 no-BOM, UTF-8-BOM, UTF-16-LE-BOM, UTF-16-BE-BOM, UTF-32 variants) round-trip cleanly. Pinned by `JsonRoundTripTests.ImportDocument_AcceptsAllStandardBomEncodings` (4-case theory) and end-to-end-verified via `lifeblood analyze --graph <utf16-le-bom-file>` against a real on-disk transcoded file.
- **MCP stdio defaulted to host-codepage instead of UTF-8** (broader-class self-finding from B1, `INV-MCP-STDIO-UTF8-001`). The MCP / JSON-RPC stdio transport mandates UTF-8 per the protocol spec, but the server's `Console.InputEncoding` / `Console.OutputEncoding` were not pinned, so on a Windows console with a non-UTF-8 ANSI codepage multi-byte characters in JSON args (Unicode identifiers, accented characters in search queries) could silently mangle. Fix: pin both to `Encoding.UTF8` at `Server.Mcp.Program.Main` entry. Defensive — no test failure observed pre-fix, but the broader class is the same as B1.
- **`global.json` declared an invalid SDK version** (B2). `"version": "8.0.0"` is rejected by `dotnet --info` ("Version '8.0.0' is not valid for the 'sdk/version' value; SDK feature bands need values like 8.0.100"). Builds passed locally only because `rollForward: latestFeature` saved it; bad public-release hygiene. Fix: `8.0.0` → `8.0.100`.
- **MCP `incremental:true` on a fresh session silently fell through to full analyze** (B3 — contract violation between docs and behavior). The `INV-ANALYZE-FALLBACK-001` contract says "NoPriorAnalysis is always Rejected"; the adapter's `IncrementalAnalyze` honored that, but `GraphSession.Load`'s `CanIncremental` gate routed the call past `LoadIncremental` entirely, so the typed `FallbackReason.NoPriorAnalysis` from the adapter was never reached through MCP. Caller's `incremental:true` "be cheap" intent was silently overridden by a slow full re-analyze. Fix: `GraphSession.Load` synthesizes the Rejected wire response directly when `incremental:true` is requested without a usable prior snapshot AND `allowFullFallback:false`. The same fix covers two cases: no prior analysis at all, AND a prior analysis for a different `projectPath`. Both surface as `mode:"rejected"` + `fallbackReason:"noPriorAnalysis"` + `canRetryFull:true` + `suggestedRetry`. Pinned by `AnalyzeWireShapeTests.Load_IncrementalOnFreshSession_AllowFallbackFalse_RejectsWithNoPriorAnalysis`.

### Pre-tag review broader-class find — `ClaudeMdInvariantParser` silently dropped every multi-segment INV id

While verifying the new INV-EXTRACT-ENUMMEMBER-001 / INV-ANALYZE-FALLBACK-001 / etc. claims showed up in `lifeblood_invariant_check`, the diagnostic surfaced 5 of 6 new invariants as `MISSING`. Broader-class probe found the same silent-drop hitting four pre-existing multi-segment invariants too: `INV-FILE-EDGE-001`, `INV-USAGE-PORT-001/002`, `INV-USAGE-PROBE-001/002`. Total impact: **10 invariants quietly missing from the audit repo-wide.**

Root cause: the parser's regex `INV-[A-Z][A-Z0-9]*-\d+` (used by all five shape detectors A/B/C/D/E) only captures single-segment categories. The `[A-Z0-9]*` character class doesn't include `-`, so as soon as the id has a second dashed segment (`USAGE-PORT-001`, `EXTRACT-ENUMMEMBER-001`) the regex bails. The downstream `ExtractCategoryFromId` helper DOES support multi-segment categories — its tests covered that — but the upstream regex never invokes it with multi-segment input, leaving the bug hidden behind a passing helper-level theory.

Fix: regex updated in all five places (`InvariantBulletStart`, `InvariantBareBoldStart`, `BareBoldTitleCapture`, `BoldTitleCapture`, `ShapeAColonBody`, `ShapeDColonBody`, `ShapeEColonBody`) to `INV-[A-Z][A-Z0-9]*(?:-[A-Z][A-Z0-9]*)*-\d+` — accepts arbitrary-depth multi-segment categories while still requiring a numeric tail. Six new ratchet tests in `ClaudeMdInvariantParserTests.Parse_*MultiSegmentId_Parses` cover all five shapes plus a three-segment-category case (`INV-MCP-STDIO-UTF8-001`). Self-audit jumped from 66 to 76 invariants and from 31 to 39 categories — same source files, same authoring shapes, just a parser that finally sees them all.

The deeper class lesson: per-component coverage (helper test + regex test in isolation) doesn't catch wiring drift. End-to-end `lifeblood_invariant_check` smoke against the live tree now diagnostically lists every wave / pre-tag id with FOUND/MISSING so a doc count claim can never silently diverge from parser truth.

### Pre-tag review polish

- **Stale `mode:'full-fallback'` references in 5 sites** (FBT1). The wire mode for the widened-to-full path was refactored to `mode:"full"` + `requestedMode:"incremental"` mid-wave, but the doc/comment sweep missed five sites: `ToolRegistry.cs:123` analyze tool description, `docs/invariants/pipeline.md` (the INV body), `docs/STATUS.md`, `docs/DOGFOOD_FINDINGS.md`, and a doc-comment in `IncrementalAnalyzeResult.cs`. All swept.
- **`docs/STATUS.md` self-analysis code-block had stale counts** (FBT2). Lines 44-50 quoted `Symbols: 2,191 / Edges: 10,194 / Files: 150` — pre-wave numbers, plus a `Files :` row that the CLI doesn't actually print (CLI emits Symbols/Edges/Modules/Types only). Refreshed to current numbers and removed the phantom row.
- **`lifeblood --help` returned "Unknown command"** (FBT3). The CLI switch only recognized `analyze` / `context` / `export`; help arms (`--help`, `-h`, `/?`, `help`) hit the unknown-command branch and exited 1 with a misleading error. Fix: added a `RunHelp` arm that prints the banner + usage and exits 0.
- **`LICENSE` was a 137-byte pointer** (FBT4). NuGet `PackageLicenseExpression` is fine, but repo convention for AGPL-3.0 ships the full license body. Replaced with the canonical 661-line GNU AGPL-3.0 text.

### Headline changes

- **Enum-member extraction + resolver type-aware fallback** (`R2-3` dogfood). Pre-fix `RoslynSymbolExtractor.ExtractEnum` emitted only the enum type — every enum member like `field:NS.FieldMask.ShimmerPhase` was missing from the graph. The resolver's Rule 4 short-name fallback then silently substituted `field:NS.BurstVoiceState.ShimmerPhase` (different containing type, same member name), and `find_references` walked the wrong target. Closed both-sided: extractor emits enum members as `Field`-kind children with `fieldKind=enumMember` + `constantValue` properties (`INV-EXTRACT-ENUMMEMBER-001`); resolver Rule 4 refuses cross-type and cross-kind silent substitution for member-kind inputs (`INV-RESOLVER-007`). Latent dup fix surfaced by enum-member emission: nested types/enums were extracted twice (file-parent + type-parent) and silently deduped — now top-level scan only handles top-level decls. DAWG impact: edge count +18% (180,818 → 214,097) — that's enum-member references the dangling-edge filter was silently dropping pre-fix.
- **Caller-owned incremental scope policy** (`G1` dogfood / `INV-ANALYZE-FALLBACK-001`). `RoslynWorkspaceAnalyzer.IncrementalAnalyze` returned `(graph, count)` and silently widened to a full re-analyze when it detected drift it could not honor cheaply. Replaced with typed `IncrementalAnalyzeResult { Mode, Graph?, ChangedFileCount, Reason?, Detail? }` + `AnalysisConfig.AllowFullFallback` flag (default `false`). When the cheap path can't be honored cleanly the adapter now Rejects with a typed `FallbackReason` (`NoPriorAnalysis` / `ModuleSetChanged` / `ModuleDescriptorChanged`) and the caller decides whether to retry with widened scope. Wire shape: `mode` reports what the adapter DID (`full` / `incremental` / `incremental-noop` / `rejected`), `requestedMode` separately reports what the caller ASKED, `fallbackReason` + `fallbackDetail` populate alongside whenever the cheap path could not be honored. Rejection responses additionally carry `canRetryFull: true` and a `suggestedRetry: { incremental: true, allowFullFallback: true }` block so the agent's next move is self-documenting. Rejection is a NORMAL structured result, not a transport error. Internal best-effort callers (`MaybeRefreshIfStale`) opt into `AllowFullFallback=true` because their contract is "make state fresh."
- **`SearchResult.MatchKind`** (`G2` dogfood / `INV-SEARCH-MATCHKIND-001`). The per-bucket scoring signal previously lived only in human-rendered `MatchSnippets` strings (`"name: …"` / `"qualifiedName: …"` / `"xmlDoc: …"`); callers wanting to filter by source had to parse those strings. Lifted to a typed `MatchKind` enum on `SearchResult` (`Name` / `QualifiedName` / `XmlDoc` / `Multiple`). Adapter-agnostic — future TS / Python search adapters reuse the same taxonomy. Snippet strings unchanged so existing renderers keep working; the structured field is purely additive.
- **`authority_report` description broadened** (`G4` dogfood). The DAWG dogfood case for the tool happened to be an ABG partial-class wave, and the description / docs picked up that framing in three places. A reviewer almost skipped using the tool for filter-dispatch widening because the wording made it sound ABG-specific. Description now names the use cases up front: partial-class hosts, dispatchers routing across row paths (filter family selectors, kernel routers, capability dispatchers), facade types, ports with many implementations. STATUS.md + DOGFOOD_FINDINGS.md prose extended for the same reason.

### Verification

- All 5 commits + verification suite verified bottom-up:
  - Clean rebuild from `dotnet clean`, zero warnings under `-warnaserror`.
  - **Full unit suite: 739 / 739 passing, 0 regressions** across the existing 664-test baseline.
  - Lifeblood self-analysis: exit 0, 0 architecture violations, 0 cycles. All architecture invariants Lifeblood enforces on itself stayed green through the wave.
  - DAWG end-to-end: 60,775 symbols, 214,097 edges, 90 modules, 122 cycles, 0 violations, 47s wall.
  - All 25 MCP tools dispatch + return valid JSON via `All25ToolsSmokeTests` (24 + analyze covered by setup), including `lifeblood_execute` running real C# script against the loaded graph.
  - The R2-3 dogfood scenario reproduced as a permanent regression ratchet: a graph containing both `FieldMask.ShimmerPhase` (enum member) AND `BurstVoiceState.ShimmerPhase` (struct field) — exact-ID lookup returns the right one, cross-type query returns NotFound + both candidates as Did-you-mean.

### Added. Enum-member extraction (R2-3 Part A / INV-EXTRACT-ENUMMEMBER-001)

`RoslynSymbolExtractor.ExtractEnum` walks `EnumDeclarationSyntax.Members` and emits one `Symbol` per member: `Kind = Field`, `Id = SymbolIds.Field(enumFqn, memberName)`, `ParentId = type:enumFqn`, `IsStatic = true`, `Properties[fieldKind] = "enumMember"`, `Properties[fieldType] = enumFqn`, `Properties[constantValue] = "<int-or-Flags-bitfield>"`. Pre-fix three failure modes followed: (1) exact-ID lookup `field:NS.Color.Red` missed in the resolver's Rule 1 and fell through to Rule 4 silent cross-type substitution; (2) References to enum members were dropped by `GraphBuilder`'s dangling-edge filter (line 89), so `find_references` / `dependants` / `blast_radius` returned 0 hits for valid usages; (3) dead-code analysis could never observe enum-member usage. Roslyn models enum members as `IFieldSymbol`, so `RoslynEdgeExtractor`'s existing `IFieldSymbol` arm already emits `References` edges to the field-shape ID — the symbols just had to exist.

Latent dup fix surfaced by enum-member emission: `RoslynSymbolExtractor.Extract`'s outer `DescendantNodes()` scan visited nested types/enums TWICE (once with parentId=file, once via the type-member walker). `GraphBuilder`'s first-write-wins dedup hid the duplicate symbol emission, but enum-member synthesis multiplied the dup into duplicate child symbols. Fixed by guarding the outer scan to top-level declarations (parented to `CompilationUnit` / `Namespace` / `FileScopedNamespace`); nested decls reach extraction via `ExtractType`'s member loop with the correct ParentId.

Six new ratchet tests (`RoslynExtractorTests.ExtractSymbols_EnumMembers_*`): emitted-as-Field with `constantValue`, ID round-trip through `SymbolIds.Field` grammar, implicit autoincrement constants, explicit values, `[Flags]` bitfield resolution, nested enum parented to nested enum, xmldoc summary attached to member.

### Fixed. Resolver Rule 4 type-aware tightening (R2-3 Part B / INV-RESOLVER-007)

`LifebloodSymbolResolver.cs:196-237` — when input parses as a member-kind id (`field:` / `property:` / `method:`), Rule 4 short-name fallback substitutions MUST stay on the same containing-type short name AND the same `Symbol.Kind`. Pre-fix, an exact-prefixed query like `field:NS.FieldMask.ShimmerPhase` that missed Rule 1 fell through to `FindByShortName("ShimmerPhase")` and silently returned `field:NS.BurstVoiceState.ShimmerPhase` — a different containing type, returned as a successful resolution. The R2-3 dogfood case from a Unity workspace closes here. Cross-type / cross-kind hits become `ResolveOutcome.NotFound` with the unfiltered short-name candidates surfaced as `Did-you-mean`. Documented intent of `INV-RESOLVER-005` ("namespace was wrong but the symbol uniquely identified") is preserved: same-short-type-different-namespace substitutions still resolve cleanly.

Four new internal helpers exposed for testability: `TryParseMemberInput`, `ShortNameOf`, `StripTypePrefix`, `SymbolKindForPrefix`. Pinned by `ResolverTypeAwareFallbackTests` (28 tests covering parser theories, the dogfood case, same-short-type accept, cross-kind refuse, method cross-type refuse, exact-ID Rule 1 fast-path under enum-member presence, type-input passthrough, multi-candidate cross-type NotFound).

### Added. Typed IncrementalAnalyzeResult + caller-owned scope policy (G1 / INV-ANALYZE-FALLBACK-001)

New record `IncrementalAnalyzeResult { Mode (Incremental | FullFallback | Rejected), Graph?, ChangedFileCount, Reason? (NoPriorAnalysis | ModuleSetChanged | ModuleDescriptorChanged), Detail? }` in `Lifeblood.Application.Ports.Left`. Adapter-agnostic taxonomy — future Python / TS adapters reuse the same record + enums; adapter-specific descriptor names (asmdef, csproj, pyproject) live in `Detail`, not in the taxonomy.

`AnalysisConfig.AllowFullFallback` flag, default `false`. Adapter does not own the scope-widening policy; caller does. `RoslynWorkspaceAnalyzer.IncrementalAnalyze` now branches every fallback site on the flag via a new `HandleFallback` helper. NoPriorAnalysis is always Rejected (no `projectRoot` to widen against without a snapshot). Pre-fix `InvalidOperationException` throw replaced with the structured Rejected result.

`GraphSession.Load` gains `allowFullFallback` parameter, threaded through `LoadIncremental`. `MaybeRefreshIfStale` opts into `AllowFullFallback=true` because its contract is "make state fresh." `ToolHandler.HandleAnalyze` reads `allowFullFallback` from MCP args. **No auto-retry on rejection** — surfacing the signal is the point of the typed shape.

Wire shape on the MCP layer: `mode` reports DID, `requestedMode` separately reports ASKED, `fallbackReason` + `fallbackDetail` populate whenever the cheap path could not be honored, rejection responses carry `canRetryFull: true` + `suggestedRetry: { incremental: true, allowFullFallback: true }`. Wire fallback-reason names are camelCase (`noPriorAnalysis` / `moduleSetChanged` / `moduleDescriptorChanged`) to match the rest of the MCP schema.

10 adapter-level ratchets (`IncrementalAnalyzeTests`, +4): NoPriorAnalysis always Rejected (regardless of `AllowFullFallback`), ModuleSetChanged × {Rejected, FullFallback} both verified, eight happy-path tests migrated to the new record shape. 5 wire-shape ratchets (`AnalyzeWireShapeTests`, new file): full / incremental-noop / incremental / rejected / full-fallback shapes all asserted via parsed JSON.

### Added. SearchResult.MatchKind (G2 / INV-SEARCH-MATCHKIND-001)

New `MatchKind` enum on `Lifeblood.Application.Ports.Right`: `Name` / `QualifiedName` / `XmlDoc` / `Multiple`. `SearchResult` record gains a final positional `MatchKind` field. `LifebloodSemanticSearchProvider` derives the value from the existing `firstNameHitToken` / `firstQNameHitToken` / `firstXmlDocHitToken` trackers — bucketCount > 1 → `Multiple`; otherwise the single-bucket label. No re-scoring, no second pass — purely additive lift. Existing `MatchSnippets` strings unchanged so existing renderers keep working. MCP tool description for `lifeblood_search` names the new field.

5 new ratchets (`SemanticSearchTests.Search_*MatchKind*`): name-only, qualifiedName-only-with-bare-name-guard, xmldoc-only, name+xmldoc Multiple, qualifiedName+xmldoc Multiple.

### Documentation. authority_report broadening (G4 dogfood)

`ToolRegistry.cs` `lifeblood_authority_report` description names use cases up front (partial-class hosts, dispatchers routing across row paths — filter family selectors, kernel routers, capability dispatchers — facade types, ports with many implementations) and adds triage heuristics + pairs-with note for `lifeblood_port_health`. `INV-AUTHORITY-001` body in `docs/invariants/tools.md` adds an explicit "Use cases are general — not extraction-specific" paragraph naming the same set + noting the DAWG case was one instance of a broader pattern. `docs/STATUS.md` + `docs/DOGFOOD_FINDINGS.md` prose extended to clarify the `3,367 PureForwarder` metric drives any host-with-many-subordinates split decision, not just ABG.

### Added. Wave-end verification ratchets

`WaveFunctionalVerificationTests` (4 tests):
- `Wave_R2_3_DogfoodReproduction_FieldMaskAndBurstVoiceStateShareShortName` — exact reproduction of the user's R2-3 scenario; permanent regression ratchet for the silent-wrong-answer class.
- `Wave_C3_WireShape_AllFiveStatesEndToEnd` + `Wave_C3_WireShape_RejectedAndFullFallback_ModuleSetDrift` — drives `GraphSession.Load` through every wire-shape state, parses actual JSON.
- `Wave_C4_MatchKind_AllFourValuesAchievable` — confirms all four values reachable through real Roslyn extraction with xmldoc summaries.

`All25ToolsSmokeTests` (24 tests): invokes every one of the 25 MCP tools through `ToolHandler.Handle` against a real Roslyn-analyzed workspace, including `lifeblood_execute` running real C# script (`return Graph.Symbols.Count;`). Catches the regression class "C2 / C4 broke a tool's dispatch via the resolver / search seam I didn't have eyes on."

## [0.7.1] - 2026-04-27

DAWG-dogfood polish on top of v0.7.0. **Tests 661 → 664 (+3).** One feature + one doc-correctness pass; both surfaced from real reviewer + DAWG-scan feedback.

### Headline changes

- **`dead_code` pagination** (`LB-FR-024`). Same shape as `cycles` + `context`. Every response carries `count` + `kindBreakdown` (per-`SymbolKind` histogram) + `truncated`. `summarize:true` returns `preview[]` instead of full `findings[]`. `maxResults` caps the embedded array (default 500, or 25 in summarize). Closes the DAWG-scale overflow class (53k-symbol workspace produced 286KB payloads that exceeded downstream tool-result limits).
- **Stale-claim sweep**: `docs/architecture.html` refreshed to current counts (22→25 tools, 22→26 ports, 539→664 tests, 58→65 invariants, 12R+10W→15R+10W). `lifeblood_dead_code` description refreshed to drop "under investigation for v0.6.4" wording and reflect the actual closed-FP-class history (v0.6.4 → v0.6.5 → v0.6.7 → v0.7.0/LB-FP-003). `docs/IMPROVEMENT_INBOX.md` active-roadmap entries (`LB-INBOX-001`, `LB-INBOX-003`) refreshed; the dated "review snapshot from v0.6.3" block stays frozen as historical record.

### Documentation. Stale-claim sweep (post-v0.7.0 review fold-in)

External reviewer flagged stale references to "22 tools / 22 ports / 539 tests / 58 invariants / 12R+10W" + "under investigation for v0.6.4" wording. Most of the surface was already current at v0.7.0 (README, STATUS, TOOLS, ARCHITECTURE, UNITY, DOGFOOD_FINDINGS); two real misses cleaned here:

- **`docs/architecture.html`** — full refresh: 22→25 tools, 22→26 ports, 539→664 tests, 58→65 invariants, 12R+10W→15R+10W, 1863→2191 self symbols, 5777→10194 self edges, 235→264 self types. Adds pills for truth envelope + Unity reachability. Adapter section gains `LifebloodAuthorityReporter`, `LifebloodResponseDecorator`, `UnityReachabilityAdapter`, `UnityAssemblyResolver` cards.
- **`lifeblood_dead_code` description** — refreshed in `ToolRegistry.cs` to drop the "v0.6.4 under investigation" wording and reflect the actual closed-FP-class history (v0.6.4 → v0.6.5 → v0.6.7 → v0.7.0 / LB-FP-003) + remaining advisory limitations.
- **`docs/IMPROVEMENT_INBOX.md`** — `LB-INBOX-001` + `LB-INBOX-003` active-roadmap entries refreshed from "22 tools / 22 ports" to "25 tools / 26 ports". The historical "review snapshot from v0.6.3, 2026-04-11" block stays frozen as dated record.

NuGet packages at v0.7.0 are immutable and unchanged — this is a doc-correctness pass, not a re-release. Test count unchanged: still 664 (LB-FR-024 below adds 3 more).

### Added. dead_code summarize / kindBreakdown / pagination (LB-FR-024 / DAWG dogfood)

`lifeblood_dead_code` adopts the same shape as `cycles` / `context`. DAWG (53k symbols, default kinds) produced ~286KB payloads that overflowed downstream tool-result limits — the analysis succeeded but the wire response was unconsumable. Same fix shape as `LB-FR-021` / `LB-FR-022`.

- Every response now carries `count`, `kindBreakdown` (per-`SymbolKind` histogram), and `truncated` alongside the existing `findings[]` / `status` / `warning` fields.
- `summarize:true` returns a compact response with `preview[]` (size capped by `maxResults`) instead of the full `findings[]` array, plus a `summarize:true` flag. Callers see the per-kind histogram + a small preview, then drill in via `includeKinds` if needed.
- `maxResults` caps the embedded array regardless of mode. Defaults: 500 in normal mode, 25 in summarize mode.
- `kindBreakdown` is always emitted — it's the cheap signal callers use to decide whether to call again with `includeKinds:["Type"]` or `includeKinds:["Method"]`.
- Tool description + INV-DEADCODE-001 caveat unchanged; experimental/advisory contract preserved.

DAWG dogfood: pre-fix `lifeblood_dead_code` on the 53k-symbol workspace produced a 286,954-character payload. Post-fix `summarize:true` returns counts + kind histogram + a small preview that fits inside the limit. Three new ratchet tests (`Handle_DeadCode_AlwaysReportsCountAndTruncationShape`, `Handle_DeadCode_SummarizeMode_OmitsFindingsField_ReturnsPreview`, `Handle_DeadCode_MaxResultsZero_ForcesTruncatedWhenAnyFinding`).

Test count: 661 → 664 (+3). 0 regressions.

## [0.7.0] - 2026-04-27

DAWG-dogfood polish on top of v0.6.7. Six findings + a repo reorg ship as one release. **Tests 632 → 661 (+29). Invariants restructured into `docs/invariants/` tree (8 domain files + INDEX), aggregated by the new dynamic tree-walker.** Hexagonal as ever — no patches, no special cases, every fix lands as a port + adapter + handler trio with regression tests + end-to-end DAWG dogfood.

### Headline changes

- **`cycles` pagination** (`LB-FR-021`). DAWG (117 SCCs ~70KB) overflowed downstream tool-result limits — now `summarize:true` returns a small `preview[]` and every response carries `count` / `totalSymbolCount` / `largestCycleSize` / `truncated`.
- **Invariant tree-walker + parser shapes C/D/E** (`LB-BUG-017` + `LB-BUG-018` + `LB-FR-023`). `lifeblood_invariant_check` discovers sources dynamically — `<root>/CLAUDE.md` + `<root>/AGENTS.md` + any `<root>/docs/invariants/**.md` tree. Five authoring shapes recognised. DAWG: 0 → 83 invariants discovered across 25 categories.
- **File-mode `compile_check`** (`LB-BUG-019`). Resolves the file's owning compilation by matching the path against every loaded compilation's syntax trees, then `ReplaceSyntaxTree`s the existing tree with the on-disk content. DAWG Unity files went from ~120 spurious CS0246 errors to `success:true`.
- **`context` smart-dynamic shaping** (`LB-FR-022`). Per-section caps with sane defaults (25 files / 50 boundaries / 20 hotspots / 50 reading-order / 100 matrix entries), `summarize:true` shortcut, `sections:[...]` allowlist. DAWG default response went from ~375KB overflow to comfortably-fits.
- **Dead-code Unity Editor reflection roster + type-via-child propagation** (`LB-FP-003`). Adds `[SettingsProvider]`, `[SettingsProviderGroup]`, `[Shortcut]`, `[OnOpenAsset]`, `[BurstCompile]`, `[MonoPInvokeCallback]`, full NUnit fixture lifecycle. A type is reachable if any directly-contained member carries an entrypoint attribute. DAWG type-level findings: 6 → 4.
- **Repo reorg: invariants moved to `docs/invariants/` tree.** CLAUDE.md slimmed 416 → 144 lines. Every formally-numbered `INV-XXX-NNN` rule lives in `docs/invariants/<domain>.md` (8 files + INDEX). Validates the tree-walker against the Lifeblood repo itself, not just DAWG. Self-analysis: 65 typed invariants across 31 categories.

### Fixed. dead_code Unity Editor reflection roster + type-via-child propagation (LB-FP-003 / DAWG dogfood)

`UnityReachabilityAdapter` was missing several Unity Editor reflection attributes — `[SettingsProvider]`, `[SettingsProviderGroup]`, `[Shortcut]`, `[OnOpenAsset]`, `[BurstCompile]`, `[MonoPInvokeCallback]`, plus the full NUnit fixture lifecycle (`SetUp` / `TearDown` / `OneTimeSetUp` / `OneTimeTearDown`, `TestCaseSource`, `TestFixtureSource`, `Theory`, `UnitySetUp`, `UnityTearDown`). Methods carrying any of these attributes are reflection-discovered by Unity / Burst / NUnit at runtime; without them in the entrypoint roster the dead-code analyzer surfaced their host types as false positives.

Also adds **type-via-child propagation**: a type is reachable if ANY of its directly-contained members carries an entrypoint attribute. The standard Unity pattern is `[SettingsProvider]` on a static method inside the host type — pre-fix only the method became reachable while the type itself surfaced as a dead candidate. Post-fix the host type inherits liveness from any tagged child. Negative case (plain type with non-entrypoint children) still flags correctly, so the propagation doesn't mask real dead code.

DAWG dogfood pre-fix: `XRaySettingsProvider` flagged dead. Post-fix: cleared via the new `[SettingsProvider]` + type-via-child rules.

Six new ratchet tests: `[SettingsProvider]`, `[Shortcut]`, `[BurstCompile]`, NUnit `[SetUp]`, type-with-entrypoint-child-is-reachable, type-with-no-entrypoint-children-still-not-reachable. Test count: 655 → 661 (+6). 0 regressions on the existing 15-test reachability suite.

### Added. context smart-dynamic shaping (LB-FR-022 / DAWG dogfood)

`lifeblood_context` previously returned the full pack on every call; on a 87-module workspace (DAWG) the response weighed ~375KB and overflowed downstream tool-result limits. Same DAWG-dogfood class as `cycles` (LB-FR-021): the analysis succeeded but the wire payload was too big to consume. Smart-dynamic shaping fixes it without forcing every caller to pass options.

**Per-section caps with sane defaults.** Five new optional ints: `maxFiles` (default 25), `maxBoundaries` (default 50), `maxHotspots` (default 20), `maxReadingOrder` (default 50), `maxMatrixEntries` (default 100). Each cap clips the section's array if the generator produced more; the response's new `truncated` map names every clipped section + its full pre-clip count so callers know what was hidden. `-1` means unlimited; `0` drops the section entirely.

**`summarize:true`** shortcut: drop every list-section to 0, keep only `summary` + `invariants` + `activeViolations`. Smallest viable shape — equivalent to passing `0` for every section cap.

**`sections:[...]`** allowlist: emit only the named list-sections (`highValueFiles`, `boundaries`, `hotspots`, `readingOrder`, `dependencyMatrix`). Anything not on the list is replaced with an empty array. Summary, invariants, and violations are always retained because they're the cheapest signal.

The shaping happens in the handler — `AgentContextGenerator` itself stays pure, returning the full pack as before. Application/Domain layers untouched.

Four new ratchet tests covering summarize-mode-drops-all-lists, sections-allowlist-drops-unlisted, per-section-cap-truncates-and-reports, negative-cap-allows-unlimited. Test count: 651 → 655 (+4). 0 regressions.

### Fixed. compile_check filePath module-aware tree replacement (LB-BUG-019 / DAWG dogfood)

`lifeblood_compile_check` with `filePath` now resolves the file's owning compilation and **swaps the existing syntax tree** for the on-disk content, instead of adding the file's content as a fresh snippet tree to an arbitrarily-picked first compilation. This was the failure mode that produced "every cross-file reference unresolved" on Unity files: passing `Assets/.../AdaptiveBeatGrid.cs` emitted CS0246 for `UnityEngine`, `MonoBehaviour`, sibling partials, `IBeatGridTickHost`, every `BeatGridConfig` member, etc., even though the file compiles cleanly inside Unity.

**New typed overload** `ICompilationHost.CompileCheck(CompileCheckRequest)`. The request carries `Code`, `FilePath`, and `ModuleName`; file-mode is selected by `FilePath` being non-null. The legacy string-based `CompileCheck(code, moduleName)` overload is preserved and now delegates to the typed path.

**File-mode** scans every loaded compilation's `SyntaxTrees` for a path match (case-insensitive forward-slash suffix), finds the owning module, and `ReplaceSyntaxTree`s the existing tree with the on-disk content. Results carry the resolved module name on `CompileCheckResult.ResolvedModule` and a boolean `ExistingTreeReplaced` so the wire response surfaces both. Pinned `moduleName` overrides auto-detection — if the file isn't in that module the request fails with `LB0002` rather than silently picking another.

**Snippet mode** unchanged — bare statements / expressions still wrap through `Internal.SnippetWrapper` and are added as new trees to the requested (or first) compilation.

DAWG dogfood: `compile_check filePath: "Assets/_Project/Scripts/BeatGrid/AdaptiveBeatGrid.cs"` previously emitted ~120 CS0246 / CS0103 / CS0538 errors against UnityEngine, sibling partials, every cross-file type. Post-fix the same call routes through the file's owning module compilation with all references intact.

Six new ratchet tests covering owning-module resolution, override-code tree replacement, missing-file LB0002, pinned-module foreign-file rejection, override-introduces-error reporting, and snippet-mode unchanged. Test count: 645 → 651 (+6). 0 regressions.

### Changed. Invariants moved to docs/invariants/ tree (eat-our-own-dogfood)

`CLAUDE.md` slimmed from 416 lines to 144. Every formally-numbered `INV-XXX-NNN` rule moved to `docs/invariants/<domain>.md`, leaving CLAUDE.md as a coordinator with the architecture diagram, dependency rules, naming conventions, rules-for-adding-features, and a pointer table to the tree. Validates the tree-walker shipped in LB-FR-023 against the Lifeblood repo itself, not just DAWG.

Tree layout (8 files + INDEX):

- `docs/invariants/INDEX.md` — master pointer + the five recognised authoring shapes (A/B/C/D/E)
- `docs/invariants/architecture.md` — DOMAIN, APP, GRAPH, ADAPT, CONN, ANALYSIS, TEST, PIPE, SCRIPTHOST, COMPROOT
- `docs/invariants/resolver.md` — RESOLVER 1-6
- `docs/invariants/csharp-adapter.md` — CANONICAL, VIEW 1-3, BCL 1-5, COMPFACT 1-3, full symbol-ID grammar
- `docs/invariants/pipeline.md` — STREAM 1-5, FILE-EDGE, INCR
- `docs/invariants/usage.md` — USAGE 1-2, USAGE-PORT 1-2, USAGE-PROBE 1-2
- `docs/invariants/mcp-protocol.md` — MCP 1-3, TOOLREG, ENVELOPE
- `docs/invariants/tools.md` — DEADCODE, INVARIANT, AUTHORITY, FORWARDER, EXECUTE, UNITY 1-2, FINDIMPL
- `docs/invariants/governance.md` — DOCS, CHANGELOG, TESTDISC

Self-tests still green: `LifebloodClaudeMdSelfTests` aggregates across CLAUDE.md + AGENTS.md (none) + the new tree, asserts TotalCount ≥ 50, required categories present (DOMAIN, GRAPH, MCP, RESOLVER, CANONICAL, BCL), known stable IDs resolvable (INV-CANONICAL-001, INV-RESOLVER-001, INV-RESOLVER-005, INV-BCL-001, INV-MCP-001, INV-TOOLREG-001), 0 duplicates, 0 parse warnings.

Test count unchanged: 645 / 645. INV ids in tree: 68 unique (all formally-numbered rules preserved verbatim from the old CLAUDE.md; the 5 example IDs in INDEX/CLAUDE shape demos are inside `Shape X:` prefixed lines that the parser doesn't anchor on).

### Added. invariant_check shape D + shape E (LB-BUG-018 / DAWG dogfood)

Two more invariant authoring shapes recognised, closing the 28 parse-warning tail surfaced in DAWG dogfood after the shape-C work landed.

**Shape D — parenthesized version tag.** A bullet with a version annotation between the bold close and the colon:
```
- **INV-DSP-012** (v1.1.566): POST_SAT_LP coefficient must be identical in mono and stereo paths.
```
Without shape D, the existing `ShapeAColonBody` regex didn't match (the parens broke the `**ID**:` adjacency) and title extraction fell to the bullet-prefix fallback. The version tag is intentionally NOT captured into the title — it's an annotation, not part of the rule.

**Shape E — colon inside the bold.** INDEX-style listings use this for terse summaries:
```
- **INV-ANIM-1:** BPM synchronization — Derive timing from _bpm
```
The colon sits before the closing `**` rather than after; whitespace between id and colon is allowed but not required. Single-digit numeric tails (`INV-ANIM-1` not just `INV-ANIM-001`) are accepted.

Try-order in `BuildInvariant`: shape B → shape D (more specific than A; checked first to avoid parens leaking into body capture) → shape A → shape E → fallback. Five new ratchet tests covering shape D alone, version-tag-no-leak, shape E alone, single-digit tail, and an all-five-shapes mixed document with zero warnings.

DAWG dogfood: 28 parse warnings → 0 (every previously-warned invariant now has a clean title). DAWG audit: 83 invariants across CLAUDE.md + AGENTS.md + `docs/invariants/**.md`.

Test count: 640 → 645 (+5). 0 regressions.

### Added. invariant_check shape-C + dynamic source discovery (LB-BUG-017 / LB-FR-023 / DAWG dogfood)

`lifeblood_invariant_check` now recognises DAWG's hot-rules authoring shape and discovers invariant sources dynamically across well-known repo conventions instead of reading a single hardcoded file.

**Shape C — bare bold paragraph (LB-BUG-017).** `ClaudeMdInvariantParser` accepts a third invariant shape used by DAWG's CLAUDE.md hot-rules section: `**INV-XXX-NNN: Title sentence.** body paragraph...`. No bullet prefix, id-and-title inside the bold separated by a colon, body following the closing `**`. Multiple consecutive shape-C blocks are recognised; block boundaries are the next opener (shape A/B/C) or a markdown heading. Pre-fix: parser returned 0 invariants on DAWG even though CLAUDE.md declared 30+.

**Dynamic source discovery (LB-FR-023 / no hardcoding).** `LifebloodInvariantProvider` now walks well-known repo conventions via `IFileSystem` instead of reading only `<root>/CLAUDE.md`:

- `<root>/CLAUDE.md` and `<root>/AGENTS.md` — root single-file conventions.
- `<root>/docs/invariants/**.md` — invariants-tree convention for projects that have outgrown a single-file authoring layout (DAWG hot-rules-stay/tree-everything-else).

Each discovered source is parsed with its own cache entry; results are aggregated. Per-id duplicate detection now spans the whole tree. The new `InvariantAudit.SourcePaths[]` reports every file that contributed; `SourcePath` returns the first source for back-compat. The conventions live in the adapter, NOT the port — a repo with a different layout supplies its own provider, reusing `ClaudeMdInvariantParser` + `InvariantParseCache<T>` without touching Application.

Five new ratchet tests in `ClaudeMdInvariantParserTests` covering shape C alone, multiple consecutive shape-C blocks, block-termination semantics, and mixed shape A/B/C in one document.

Test count: 635 → 640 (+5). 0 regressions.

### Added. cycles summarize / pagination (LB-FR-021 / DAWG dogfood)

`lifeblood_cycles` adopts the same `summarize` + `maxResults` shape as `lifeblood_blast_radius`. DAWG's 117 SCCs serialize to ~70KB, which exceeds downstream tool-result limits and surfaces as a fake "tool failed" to callers even though the analysis succeeded. Same fix shape as P1 LB-FR-010.

- Every response now carries `count`, `totalSymbolCount`, `largestCycleSize`, and `truncated` alongside the existing array.
- `summarize:true` returns `preview[]` (size capped by `maxResults`) instead of the full `cycles[]` array, plus a `summarize:true` flag.
- `maxResults` caps the embedded array regardless of mode. Defaults: 500 in normal mode, 25 in summarize mode.
- Tool description updated to call out the wire shape and the DAWG-scale rationale.
- Three new ratchet tests (`Handle_Cycles_AlwaysReportsCountAndTruncationShape`, `Handle_Cycles_SummarizeMode_OmitsCyclesField_ReturnsPreview`, `Handle_Cycles_MaxResultsZero_ForcesTruncatedWhenAnyCycleExists`).
- Test count: 632 → 635 (+3). 0 regressions.

DAWG dogfood: pre-fix `lifeblood_cycles` on the 117-SCC workspace produced a 70,798-character payload that overflowed the harness limit. Post-fix `summarize:true` returns a small preview-only response that fits inside the limit.

## [0.6.7] - 2026-04-27

DAWG-dogfood backlog plan, full sweep. Six phases (P1..P6) ship as one combined release on top of v0.6.5. **Tests: 569 to 632 (+63). Invariants: 63 to 70 (+7). Ports: 22 to 26 (+4). MCP tools: 22 to 25 (+3 read-side: authority_report, port_health, cycles).** Hexagonal as ever - no patches, no special cases, every fix lands as a port + adapter + handler trio with regression tests + end-to-end dogfood. Five repeatable smoke harnesses (`smoke-mcp-p1-dogfood.ps1` through `smoke-mcp-p5-dogfood.ps1`) drive the full wire surface against Lifeblood and a real 87-module Unity workspace (DAWG).

### Fixed. Resolver kind-correction (P1 / LB-BUG-002)

`method:NS.Type.X` now resolves to a property / field / event named `X` on `NS.Type` when no method by that name exists. New `ResolveOutcome.KindCorrectedOnContainingType` plus a diagnostic explaining the correction. Type-scoped kind correction takes precedence over the global short-name fallback because the user already committed to a namespace; the more specific resolution is the honest answer. Method-by-that-name still wins when both a method and a same-named property exist on the type. Documented as `INV-RESOLVER-006`.

### Fixed. File-scoped diagnostics (P1 / LB-BUG-016)

`lifeblood_diagnose` accepts a `filePath` parameter that restricts the response to one source file. New `DiagnosticsRequest` record on `ICompilationHost` (typed alongside `FindReferencesOptions`, not stringly-typed). The Roslyn adapter filters by syntax-tree path with case-insensitive forward-slash suffix matching so callers can pass either the relative or absolute form. Closes the silent-fallback-to-300k-line-dump failure mode that appeared in three DAWG sessions. The string-only `GetDiagnostics(moduleName)` overload is preserved for back-compat.

### Fixed. compile_check accepts filePath (P1 / LB-BUG-015)

`lifeblood_compile_check` accepts either inline `code` or a `filePath` (relative to the loaded project root, or absolute). The file is read via the existing `IFileSystem` port. Exactly one of the two is required; supplying both is rejected. Tool schema drops `required: ["code"]`. Response includes a `source: "code"|"filePath"` discriminator + the `filePath` echo.

### Added. blast_radius summarize / direct dependants (P1 / LB-NICE-005, LB-FR-010)

`lifeblood_blast_radius` always reports `directDependants` (one-hop incoming edges, distinct sources, non-Contains) alongside the existing transitive `affectedCount`. `summarize:true` returns a compact response with `preview[]` (size capped by `maxResults`) instead of the full `affected[]` array. `truncated:true/false` tells callers whether the array was clipped. Closes the failure mode where transitive blast on a popular type produced 84KB+ JSON payloads that overflowed downstream agent thread guards.

### Closed by re-verification (P1)

Three regression tests against v0.6.5 confirmed prior walker work already closed two open backlog items:

- **LB-BUG-001** invocation through array indexer receiver (`voices[i].SetPatch(patch)` on partial struct). `RoslynEdgeExtractor.ExtractCallEdge` resolves the entire invocation semantically via `model.GetSymbolInfo(invocation)` - array-element + List indexer receivers both work.
- **LB-BUG-010** implicit interface implementation in `find_references`. The v0.6.4 method-level `Implements` edge work links the concrete impl to the interface method; the dispatch-site call goes to the interface method via `model.GetSymbolInfo`.

### Added. Truth envelope on every read-side response (P2 / LB-INBOX-001 + LB-OBS-004 / INV-ENVELOPE-001)

Every read-side MCP tool response carries a top-level `envelope` field. The envelope tells callers HOW MUCH to trust a result and WHEN it was true: truth tier (Semantic / Derived / Heuristic / Inferred), confidence band (Proven / Advisory / Speculative), evidence-source string, wall-clock staleness in seconds, files-changed-since-analyze count, per-tool documented limitations.

Pure-data record in Domain (`Lifeblood.Domain.Results.ResponseEnvelope` + `EnvelopeClassification`). Application port (`IResponseDecorator`) plus reference adapter (`LifebloodResponseDecorator`). Per-tool classification lives on `ToolDefinition.EnvelopeClassification` - registry IS the source. Composition root projects the registry into the decorator at startup; missing registrations fall through to the most-conservative envelope plus a `Limitations` entry naming the gap.

Staleness math walks the loaded graph's `SymbolKind.File` symbols, mtime-stats each via `IFileSystem.GetLastWriteTimeUtc`, counts files newer than `GraphSession.AnalyzedAtUtc`. Per-call scan capped at 256 files (short-circuits as soon as drift is detected) so even 87-module workspaces stay cheap.

Wire-format note: `lifeblood_dependencies` and `lifeblood_dependants` previously returned a bare JSON array. They now return `{ envelope, symbolId, count, dependencies/dependants }`. Callers must read the named field instead of the top-level array.

### Added. Unity-aware runtime-dispatch reachability (P3 / INV-UNITY-001, INV-UNITY-002)

`IUnityReachabilityProvider` port + `UnityReachabilityAdapter` reference implementation. The adapter recognizes:

- **Unity entrypoint attributes**: `RuntimeInitializeOnLoadMethod`, `InitializeOnLoadMethod`, `MenuItem`, `ContextMenu`, `PostProcessBuild`, `PostProcessScene`, `CustomEditor`, `CustomPropertyDrawer`, `Test`, `UnityTest`, full Unity message catalog.
- **MonoBehaviour magic methods**: `Awake`, `Start`, `Update`, `FixedUpdate`, `LateUpdate`, `OnEnable`, `OnDisable`, `OnDestroy`, `OnGUI`, `OnTriggerEnter` and variants, `OnCollisionEnter` and variants, `OnAudioFilterRead`, `OnRenderImage` and lifecycle - only when the containing type's transitive inheritance chain reaches `UnityEngine.MonoBehaviour`, `UnityEngine.ScriptableObject`, `UnityEditor.Editor`, `UnityEditor.EditorWindow`, or `UnityEngine.StateMachineBehaviour`.

The chain walk uses extractor-recorded `Symbol.Properties["baseType"]` (FQN string) so it works even when the chain ends in a type that lives in an external assembly not loaded into the graph (UnityEngine.dll). The graph drops dangling Inherits edges to external targets, so an edge-only walk would miss every direct `MonoBehaviour` subclass.

`LifebloodDeadCodeAnalyzer` takes the provider via optional ctor; pre-P3 callers see identical behavior. Composition root wires the Unity adapter by default; non-Unity workspaces see zero degradation.

Extractor adds three metadata properties on every relevant symbol:
- `Properties["attributes"]` - semicolon-separated simple attribute class names (Attribute suffix stripped).
- `Properties["baseType"]` - FQN of the direct base (Object / ValueType / Enum / Delegate skipped).
- `Properties["classification"]` - method body shape (`PureForwarder` / `ThinWrapper` / `RealLogic`); see P5 below.

Asmdef incremental (`INV-UNITY-002`, promoted from LB-NICE-003): `AnalysisSnapshot.AsmdefTimestamps` + `HasAsmdefDrift` force a full re-analyze on any added / removed / edited `*.asmdef`. Unity csprojs are generated from asmdefs - editing an asmdef without forcing Unity to regenerate the csproj would leave the csproj-timestamp tracker (`INV-BCL-005`) blind.

Dogfooded on a real 87-module Unity workspace (DAWG, 53,882 symbols, 180,814 edges):
- **Dead-code findings: 1095 to 729** (-33% / -366 false positives).
- **MonoBehaviour-magic FPs: 378 to 13** (-97%).
- **Unity-shaped magic-name FPs in Assets/Editor/Runtime paths: 226 to 10** (-95.5%).

Remaining 13 are documented edge cases (UI Toolkit `VisualElement` subclasses, audio callbacks on non-MonoBehaviour bases). Closes LB-FP-001 (MonoBehaviour message dispatch), LB-FP-002 (UnityEvent / Button.onClick - partially; YAML scanner deferred), LB-BUG-009 (RuntimeInitializeOnLoadMethod entrypoints).

### Added. Execute robustness (P4 / INV-EXECUTE-001 / LB-BUG-014, LB-BUG-007, LB-BUG-008, LB-FR-012, LB-FR-013)

`lifeblood_execute` is now trustworthy on Unity workspaces and gives sandbox scripts the introspection they need.

- **`IRuntimeAssemblyResolver`** (Application port) + **`UnityAssemblyResolver`** (C# adapter) probe `Library/ScriptAssemblies/`, `Library/Bee/artifacts/` (Unity 2022+), and `Library/PackageCache/`. The C# code executor injects discovered DLLs as additional script references so scripts can touch `UnityEngine` types. Empty `Library/` surfaces a friendly `runtimeAssemblyWarnings` entry ("run a Unity build first") instead of letting scripts fail with cryptic CS0246 errors. `GraphSession` auto-injects the Unity adapter when `Library/` exists at the project root; non-Unity workspaces see zero behavior change.
- **`ICodeExecutor.Execute(CodeExecutionRequest)`** typed-options overload + new `CodeExecutionRequest.TargetProfile` field (`host` default / `net-standard-2.1` / `net-6.0`). When non-default, the executor swaps host BCL refs for the matching reference pack from `dotnet/packs/`. Missing packs fall back to host BCL with a `targetRuntimeWarnings` entry. Unknown profile values fall back with a clear diagnostic. Both warning channels propagate on every result path: success, blocked-pattern, compile error, timeout, exception.
- **`RoslynSemanticView`** gains `Help` (sandbox cheat sheet - globals, EdgeKind names, SymbolKind names, common queries), `SymbolsOfKind(string)`, `EdgesOfKind(string)`. Scripts can write `SymbolsOfKind("Method").Count()` without importing the SymbolKind enum.
- **`CodeExecutionResult`** ships `RuntimeAssemblyWarnings` + `TargetRuntimeWarnings` alongside Output/Error/ReturnValue/ElapsedMs. `WriteToolHandler.HandleExecute` surfaces both verbatim. `lifeblood_execute` schema gains optional `targetProfile` enum.

Dogfood (smoke-mcp-p4-dogfood.ps1):
- Lifeblood: SymbolsOfKind("Type") = 262, EdgesOfKind("Contains") = 2121, Help reachable, host-profile execute returns "42" on `21*2`.
- DAWG: SymbolsOfKind("Type") = 3209, EdgesOfKind("Contains") = 54440. Unknown targetProfile fallback verified end-to-end.

### Added. Authority report + forwarder classifier + port health + cycles (P5 / INV-AUTHORITY-001 + INV-FORWARDER-001 / LB-FR-018, LB-FR-014, LB-FR-020, LB-NICE-007)

Three new read-side MCP tools plus a heuristic method-body classifier on the extractor. Together they automate the manual ABG-extraction triage that ate ~30 min per stage in DAWG sessions.

- **`IAuthorityReporter`** (Application port) + **`LifebloodAuthorityReporter`** (Connectors.Mcp adapter). Single graph walk produces `implementedInterfaceCount`, `ownedPublicSurface` (public method/property/field count, nested types excluded), per-implemented-interface usage (member count + distinct consumers via `Calls` / `References` edges), and `forwarderRatio` (in [0.0, 1.0] or sentinel `-1.0` when no method has classification). Tool: **`lifeblood_authority_report`**, envelope tier: Derived / Proven.
- **Method body classification** - `RoslynSymbolExtractor.AttachMethodClassification` records on every method's `Properties["classification"]`: `PureForwarder` (single-statement / expression-bodied invocation), `ThinWrapper` (<= 5 statements with exactly one invocation), `RealLogic` (everything else). Abstract / partial / extern methods get no entry. Dogfood: 1009 methods classified on Lifeblood (58 PureForwarders), **18,985 classified on DAWG with 3,367 PureForwarders identified**.
- **`lifeblood_port_health`** - walks an interface or class's `Contains` members, classifies each as live (>= 1 incoming non-Contains edge OR outgoing `Implements`) or dead. Returns `livenessPct` and a verdict: `healthy` (>=75%), `mixed` (>=25%), `vestigial` (<25%), or `empty`.
- **`lifeblood_cycles`** - exposes the existing `CircularDependencyDetector` SCC results as a callable tool. No new analysis. Lifeblood self-analysis: 0 cycles. DAWG: 117 SCCs.

### Added. Wire-shape clarifications (P6 / LB-OBSERVATION-001, LB-OBSERVATION-003)

- **`changedSourceFiles`** + **`touchedGraphFiles`** added alongside the existing `changedFileCount` on `lifeblood_analyze` responses. Currently the same value; surface kept stable for future divergence. Old `changedFileCount` field preserved for back-compat.
- **`lifeblood_dependencies`** description spells out where outgoing edges actually live: `Calls` edges live on the calling METHOD, `References` edges live on the referencing field/property/method body. A type-level dependencies query typically returns 0 because the type itself doesn't author calls.
- **`lifeblood_analyze`** description includes a Unity note about new-`.cs`-without-`.meta` files.

### Documentation

- **`CLAUDE.md`** adds `INV-RESOLVER-006`, `INV-ENVELOPE-001`, `INV-UNITY-001`, `INV-UNITY-002`, `INV-EXECUTE-001`, `INV-AUTHORITY-001`, `INV-FORWARDER-001`. 70 typed invariants total.
- **`README.md`** + **`docs/STATUS.md`** + **`docs/ARCHITECTURE.md`** + **`docs/TOOLS.md`** + **`docs/UNITY.md`** + **`docs/MCP_SETUP.md`** + **`docs/DOGFOOD_FINDINGS.md`** updated to reflect 25 tools (15 read + 10 write), 26 ports, 70 invariants, 632 tests, 2,132 self-analysis symbols. All em-dashes replaced with prose hyphens.
- **`docs/plans/dawg-dogfood-polish-2026-04-26.md`** committed as the source plan. Six phases (P1..P6); all shipped.
- **`smoke-mcp-p1-dogfood.ps1`** through **`smoke-mcp-p5-dogfood.ps1`** committed as repeatable end-to-end MCP smoke harnesses.

### Internal

- `ToolHandler.JsonOpts` gains `JsonStringEnumConverter` so envelope and payload enums ship as readable strings (`"Semantic"`, `"Proven"`) instead of integer ordinals.
- `GraphSession` records `AnalyzedAtUtc` on every successful Load (full + incremental + JSON-graph). Read by the response decorator via `EnvelopeContext`.
- `ICompilationHost` gains `GetDiagnostics(DiagnosticsRequest)`; existing string-only overload preserved.
- `ICodeExecutor.Execute(CodeExecutionRequest)` interface overload added; existing `Execute(string,string[],int)` overload preserved and now delegates to the typed path. `ProcessIsolatedCodeExecutor` updated to satisfy the new interface member.
- Test count drift: `ToolRegistry_Returns22Tools` ratchet renamed to `ToolRegistry_Returns25Tools` and asserts the three new read-side tools by name.

### Added. Execute robustness (P4 / LB-BUG-014, LB-BUG-007, LB-BUG-008, LB-FR-012, LB-FR-013)

`lifeblood_execute` is now trustworthy on Unity workspaces and gives sandbox scripts the introspection they need.

- **`IRuntimeAssemblyResolver`** (Application port) + **`UnityAssemblyResolver`** (C# adapter) probe `Library/ScriptAssemblies/`, `Library/Bee/artifacts/` (Unity 2022+), and `Library/PackageCache/`. The C# code executor injects discovered DLLs as additional script references so scripts can touch `UnityEngine` types. Empty `Library/` surfaces a friendly `runtimeAssemblyWarnings` entry ("run a Unity build first") instead of letting scripts fail with cryptic CS0246 errors. `GraphSession` auto-injects the Unity adapter when `Library/` exists at the project root; non-Unity workspaces see zero behavior change.
- **`ICodeExecutor.Execute(CodeExecutionRequest)`** typed-options overload + new `CodeExecutionRequest.TargetProfile` field (`host` default / `net-standard-2.1` / `net-6.0`). When non-default, the executor swaps host BCL refs for the matching reference pack from `dotnet/packs/`. Missing packs fall back to host BCL with a `targetRuntimeWarnings` entry. Unknown profile values fall back with a clear diagnostic. Both warning channels propagate on every result path: success, blocked-pattern, compile error, timeout, exception.
- **`RoslynSemanticView`** gains `Help` (sandbox cheat sheet - globals, EdgeKind names, SymbolKind names, common queries), `SymbolsOfKind(string)`, and `EdgesOfKind(string)`. Scripts can write `SymbolsOfKind("Method").Count()` without importing the SymbolKind enum.
- **`CodeExecutionResult`** ships `RuntimeAssemblyWarnings` + `TargetRuntimeWarnings` alongside Output/Error/ReturnValue/ElapsedMs. `WriteToolHandler.HandleExecute` surfaces both verbatim. `lifeblood_execute` schema gains optional `targetProfile` enum.

Documented as **`INV-EXECUTE-001`** in CLAUDE.md.

Dogfood (smoke-mcp-p4-dogfood.ps1):
- Lifeblood: SymbolsOfKind("Type") = 257, EdgesOfKind("Contains") = 2080, Help reachable, host-profile execute returns "42" on `21*2`.
- DAWG (87 modules): SymbolsOfKind("Type") = 3209, EdgesOfKind("Contains") = 54440. Unknown targetProfile fallback verified end-to-end.

### Added. Authority report + forwarder classifier + port health + cycles (P5 / LB-FR-018, LB-FR-014, LB-FR-020, LB-NICE-007)

Three new read-side MCP tools plus a heuristic method-body classifier on the extractor. Together they automate the manual ABG-extraction triage that ate ~30 min per stage in DAWG sessions.

- **`IAuthorityReporter`** (Application port) + **`LifebloodAuthorityReporter`** (Connectors.Mcp adapter). Single graph walk produces `implementedInterfaceCount`, `ownedPublicSurface` (public method/property/field count, nested types excluded), per-implemented-interface usage (member count + distinct consumers via `Calls`/`References` edges), and `forwarderRatio` (in [0.0, 1.0] or sentinel `-1.0` when no method has classification). Tool: **`lifeblood_authority_report`**, envelope: Derived / Proven.
- **Method body classification** - `RoslynSymbolExtractor.AttachMethodClassification` records on every method's `Properties["classification"]`: `PureForwarder` (single-statement / expression-bodied invocation), `ThinWrapper` (≤ 5 statements with exactly one invocation), `RealLogic` (everything else). Abstract / partial / extern methods get no entry. Dogfood: 1009 methods classified on Lifeblood (58 PureForwarders), **18,985 classified on DAWG with 3,367 PureForwarders** identified.
- **`lifeblood_port_health`** - walks an interface or class's `Contains` members, classifies each as live (≥ 1 incoming non-Contains edge OR outgoing `Implements`) or dead. Returns `livenessPct` and a verdict: `healthy` (≥75%), `mixed` (≥25%), `vestigial` (<25%), or `empty`.
- **`lifeblood_cycles`** - exposes the existing `CircularDependencyDetector` SCC results as a callable tool. No new analysis. Lifeblood self-analysis: 0 cycles. DAWG: 117 SCCs.

Documented as **`INV-AUTHORITY-001`** + **`INV-FORWARDER-001`** in CLAUDE.md.

### Added. Wire-shape clarifications (P6 / LB-OBSERVATION-001, LB-OBSERVATION-003)

- **`changedSourceFiles`** + **`touchedGraphFiles`** added alongside the existing `changedFileCount` on `lifeblood_analyze` responses. Currently the same value; the surface is kept stable for future divergence (e.g. when graph-file churn is more than the .cs-file churn). Old `changedFileCount` field preserved for back-compat.
- **`lifeblood_dependencies`** description spells out where outgoing edges actually live: `Calls` edges live on the calling METHOD, `References` edges live on the referencing field/property/method body. A type-level dependencies query typically returns 0 because the type itself doesn't author calls. Closes the dogfood report's "0 outbound edges from type:X is confusing" finding.
- **`lifeblood_analyze`** description includes a Unity note about new-`.cs`-without-`.meta` files (the incremental walker picks them up; a later full analyze will refresh symbol IDs if Unity assigns a different GUID).

### Internal

- `ICodeExecutor.Execute(CodeExecutionRequest)` interface overload added; existing `Execute(string,string[],int)` overload preserved and now delegates to the typed path. `ProcessIsolatedCodeExecutor` updated to satisfy the new interface member.
- Test count drift: `ToolRegistry_Returns22Tools` ratchet renamed to `ToolRegistry_Returns25Tools` and asserts the three new read-side tools by name.

## [0.6.5] - 2026-04-14

Closes the three Roslyn extractor gaps that v0.6.4 left explicitly marked "by design / known gap" under `INV-DEADCODE-001`, plus a regression in `LifebloodSymbolResolver` that made the new ctor edges unreachable via the read-side tools. Publish workflow hardened against the drift class that would ship helper tags as real NuGet packages. `CLAUDE.md` trimmed 20% without dropping a single invariant rule. 569 tests (was 557, +12 new: 8 extractor, 4 resolver). 0 regressions. 0 build warnings.

### Fixed. Three more Roslyn extractor gaps + ctor resolver bug

- **Constructor `Calls` edge.** `ExtractConstructorCallEdge` now emits BOTH a type-level `References` edge (prior behaviour - module coupling signal) AND a method-level `Calls` edge to the `.ctor`. `find_references` on any explicit constructor returns its construction sites.
- **Field-initializer containing method.** `FindContainingMethodOrLocal` resolves references inside `static T _x = Bar()` / `T _x = Bar()` / `public int X { get; } = Compute()` to the type's synthesized `.cctor` (static) or first `.ctor` (instance) via `INamedTypeSymbol.StaticConstructors` / `InstanceConstructors`. Closes the `new Lazy<>(Load)` "no containing method" false-positive class.
- **Property accessor context.** `FindContainingMethodOrLocal` returns the accessor `IMethodSymbol` for references inside bodied `get { ... }`, expression-bodied `=> _field`, and indexer expression bodies. `GetMethodId` routes `AssociatedSymbol` to the property/event id so the edge source matches the extracted graph node.
- **Constructor resolver (regression fix).** `LifebloodSymbolResolver.TryParseMethodWithoutParens` recognizes the `..ctor` / `..cctor` suffixes explicitly - the leading dot is part of the canonical method name in Lifeblood's ID grammar, so a plain `LastIndexOf('.')` split produced an invalid type id (`type:NS.Foo.`). Without the fix, `find_references`, `blast_radius`, `dependants`, and every other read-side tool were unable to resolve `method:NS.Foo..ctor` (truncated) despite the symbol existing in the graph.

### Changed. Publish workflow hardening

- **Tag trigger restricted to pure semver.** Previously `tags: ['v*']` fired the NuGet publish on ANY `v`-prefixed tag, including helper / checkpoint tags. Combined with MinVer (tag → package version), a helper tag like `v0.6.4.1-post-extractor` would ship a real NuGet package with a prerelease-suffixed version. Trigger is now `v[0-9]+.[0-9]+.[0-9]+` (triple-dot semver only). Pre-release / hotfix publishes must go through `workflow_dispatch` with an explicit `confirm=publish` input.
- **Post-pack artifact guard.** Even under the strict trigger, the workflow now fails loudly if any produced nupkg filename carries a prerelease suffix (`-alpha`, `-beta`, `-rc`, `-preview`, `-dev`, `-post`), preventing a garbage version from reaching nuget.org.
- **MinVer fetch-depth.** `actions/checkout` now uses `fetch-depth: 0` so MinVer can walk the full tag history when deriving the version.

### Documentation

- **`CLAUDE.md` trim.** 556 → 402 lines (~20% smaller) without dropping any invariant rule. Stale `## 17 MCP Tools` heading removed (actual: 22, authoritative source `docs/STATUS.md` + `ToolRegistry.cs`). Port Interfaces section compressed to a directory-layout pointer. Verbose evidence narratives trimmed on `INV-RESOLVER-005`, `INV-CANONICAL-001`, `INV-MCP-003`, `INV-TOOLREG-001`, `INV-DEADCODE-001`, `INV-FINDIMPL-001`, `INV-USAGE-*`, `INV-BCL-001..005`, `INV-TESTDISC-001`, `INV-INVARIANT-001`. `INV-DEADCODE-001` updated to document the three new closed FP classes.
- **`docs/IMPROVEMENT_INBOX.md` hygiene.** Deleted the "Shipped since v0.6.0" block and the `LB-INBOX-007` RESOLVED block per the file's own "when the fix ships, delete the entry" rule. Resolved the `LB-INBOX-001..005` id collision between shipped-history entries and phase entries. Rewrote `LB-INBOX-002` Phase 2 to reflect current state.
- **Self-analysis sample refreshed.** `docs/STATUS.md` self-analysis block showed pre-v0.6.4 numbers (1834 symbols / 5708 edges / 235 types / 57 invariants); updated to post-v0.6.4 values (1887 / 8223 / 238 / 63).
- **`docs/ARCHITECTURE.md` Seam 4 wording.** Removed pre-release framing that referred to `INV-DEADCODE-001` "for the v0.6.4 investigation" - the v0.6.4 gap classes are now closed.
- **Count refresh.** Test count 557 → 569 across STATUS / README / ARCHITECTURE / DOGFOOD. Invariant count 58 → 63.

### Internal

- `CS8620` nullable warning in `ModuleCompilationBuilder` eliminated by projecting the filtered `Where(t => t != null)` through `Select(t => t!)` so downstream flow sees `SyntaxTree[]` instead of `SyntaxTree?[]`.

## [0.6.4] - 2026-04-13

### Fixed. Dead-code false positives and call-graph completeness

Five extraction gaps closed in `RoslynEdgeExtractor`. Root-cause compilation fix in `ModuleCompilationBuilder`. Self-analysis: 150 to 10 dead-code findings (93% reduction). Edges: 5,777 to 8,223 (+42%). Tests: 539 to 557 (+18 new, 0 regressions). All call-graph tools benefit: `find_references`, `dependants`, `blast_radius`, `file_impact`, `dead_code`.

- **BUG-004 (interface dispatch).** Method-level `Implements` edges via `FindImplementationForInterfaceMember` + `AllInterfaces`. Dead-code analyzer checks outgoing `Implements` as proof of liveness. Fixes ~54% of false positives.
- **BUG-005 (member access granularity).** Symbol-level `References` edges for properties and fields via `EmitSymbolLevelEdge` shared helper. `ExtractReferenceEdge` restructured to handle `IFieldSymbol` (bare field reads) and `IMethodSymbol` (method-group references: `Lazy<T>(Load)`, `event += Handler`). Fixes ~20% of false positives.
- **BUG-006 (null-conditional property).** `MemberBindingExpressionSyntax` handler for `obj?.Property` patterns. Fixes ~15% of false positives.
- **Lambda context.** `FindContainingMethodOrLocal` skips lambda syntax nodes. Calls inside `.Select(x => Foo(x))` attribute to the enclosing named method.
- **Implicit global usings (LB-INBOX-007).** `ModuleInfo.ImplicitUsings` discovered from `<ImplicitUsings>enable</>` in the csproj. `ModuleCompilationBuilder.CreateCompilation` injects a synthetic global using tree with the 7 standard namespaces. Without this, Roslyn could not resolve `List<>`, `Dictionary<>`, `HashSet<>`, etc. and `GetSymbolInfo` returned null for 42% of invocations. Follows the INV-COMPFACT-001..003 pattern.
- **Reference assemblies.** `BclReferenceLoader` prefers SDK pack reference assemblies from `dotnet/packs/Microsoft.NETCore.App.Ref/` over runtime implementation assemblies.

Remaining 10 findings: runtime entry points (6), static field initializer method-groups (2), static field in accessor (1), internal constructor (1). All correct or known edge-case.

## [0.6.3] - 2026-04-11

One release covering the full 12-commit span from `v0.6.1` through Phase 8. Adds four new MCP tools (semantic search, dead code, partial view, invariant check), five new port interfaces, the wrong-namespace resolver fallback, `compile_check` auto-wrap for library modules, the MCP wire/internal DTO split that unblocks Claude Code reconnect, tokenized ranked-OR search, a Linux CI fix, and a release-wide documentation sweep. Ships `lifeblood_dead_code` as experimental / advisory with three documented false-positive classes. **Tools: 18 to 22 (+4). Ports: 17 to 22 (+5). Tests: 362 to 539.**

### Commits included since v0.6.1

- `898b125` feat(resolver, extraction): improvement-master plan phases 0-4
- `e42f51b` feat(search): `lifeblood_search` tool + xmldoc persistence (phase 5)
- `db09dfe` feat(analysis, compile-check): phases 6+7 dead_code, partial_view, break_kind, auto-refresh
- `96abd06` fix(review): self-review pass with live-drift ratchet + dedup / classification edge cases
- `334b47d` fix(mcp): wire / internal split + single source of truth; unblocks Claude Code reconnect
- `9cc9e50` fix(search): tokenized ranked OR so multi-word queries stop collapsing to zero
- `ea08264` test(mcp): close stdio-loop and search dispatch coverage gaps
- `a077e35` fix(ci): extract `CsprojPaths` shared helper; unblocks Linux build
- `24f607e` fix(write): `compile_check` auto-wraps statement snippets for library modules
- `afbc358` fix(resolver): wrong-namespace inputs resolve via trailing short-name segment (`INV-RESOLVER-005`)
- `26bb8bf` feat(invariants): `lifeblood_invariant_check` (Phase 8)
- `c31b44a` docs(plans): publish Phase 8 design spike under `docs/plans/`

### Added. Resolver, extraction, and view hardening (phases 0-4)

- **`ISymbolResolver` port in Application**, with `LifebloodSymbolResolver` as the reference implementation in `Connectors.Mcp`. Every read-side MCP tool that takes a `symbolId` routes through one resolver before any graph or workspace lookup. Resolution order: exact canonical match, then truncated method form (single-overload lenient), then bare short name. Partial-type unification is computed as a read model on the resolution result, not a graph schema change. See `INV-RESOLVER-001..004`.
- **`IUserInputCanonicalizer` port**, with `CSharpUserInputCanonicalizer` handling primitive-alias rewriting (`System.String` to `string`, `global::` stripping) at step 0 of the pipeline so every diagnostic, `Candidates[]` entry, and log line quotes the canonical form rather than the user's raw input.
- **`RoslynSemanticView`**: typed adapter-side read-only view exposing `Compilations`, `Graph`, and `ModuleDependencies` as a single shared reference. Constructed once per `GraphSession.Load`. `RoslynCodeExecutor` consumes it as the script-host globals object. See `INV-VIEW-001..003`.

### Added. `lifeblood_search` (phase 5)

- **New tool** backed by the new `ISemanticSearchProvider` port and `LifebloodSemanticSearchProvider` reference implementation. Ranks symbols by name match, qualified-name match, and persisted xmldoc summary match.
- **XML doc persistence**: during symbol extraction the C# adapter now attaches `<summary>` text to `Symbol.Properties["xmlDocSummary"]` so the search provider can rank on what a symbol does, not just what it is named.

### Added. Dead code, partial view, and compile-check auto-refresh (phases 6-7)

- **New tool `lifeblood_dead_code`** (ships experimental / advisory in v0.6.3; see "Known limitation" below), backed by `IDeadCodeAnalyzer` and `LifebloodDeadCodeAnalyzer`. Scans the graph for symbols with no incoming semantic references.
- **New tool `lifeblood_partial_view`**, backed by `IPartialViewBuilder` and `LifebloodPartialViewBuilder`. Returns the combined source of every partial declaration of a type with file headers between segments.
- **`lifeblood_compile_check` auto-refresh**: if any tracked file on disk has changed since the last analyze, the handler incrementally re-analyzes before compiling the snippet. Opt out with `staleRefresh: false`. The response carries `autoRefreshed: true` plus `changedFileCount: N` when a refresh actually ran.

### Fixed. MCP wire / internal DTO split (`334b47d`)

- **The bug.** `McpToolInfo` was a conflated wire + internal type with `[JsonIgnore] required init ToolAvailability Availability`. .NET 8 `System.Text.Json` has a latent bug where `[JsonIgnore]` is not honoured on `required init` properties during serialization metadata construction: the runtime threw `JsonException` on every `tools/list` response, which Claude Code interpreted as a dead server and refused to reconnect.
- **The fix.** Split `ToolDefinition` (internal registry record, carries `Availability`) from `McpToolInfo` (pure wire DTO, no `Availability` field at all). `ToolRegistry.GetDefinitions()` returns definitions for test seams; `ToolRegistry.GetTools()` projects to wire DTOs, applying `[Unavailable...]` description decoration based on the session's compilation state. The wire DTO has no `required` / `init` interaction quirks, so `System.Text.Json` serializes it cleanly.
- **Typed availability dispatch.** Removed the previous 8-prefix string match that silently misclassified `lifeblood_resolve_short_name`. Classification is now via the typed `ToolAvailability` enum on `ToolDefinition`; omitting `Availability` is a compile error. See `INV-TOOLREG-001`.
- **Single source of truth for MCP protocol constants.** Protocol version, JSON-RPC method names, and notification method names live exclusively in `Lifeblood.Connectors.Mcp.McpProtocolSpec`. The Unity bridge ships a standalone mirror at `unity/Editor/LifebloodBridge/McpProtocolConstants.cs` pinned byte-equal to `McpProtocolSpec` by a ratchet test. Fixes two latent Unity bridge bugs: empty `initialize.params` and legacy `initialized` notification alias usage. See `INV-MCP-003`.

### Fixed. Resolver wrong-namespace short-name fallback (`afbc358`, `INV-RESOLVER-005`)

- **Two dogfood reports landed the same failure class.** User typed `type:Nebulae.BeatGrid.Audio.DSP.VoicePatchAdapter` when the real symbol lives in `Audio.Tuning`. User typed `type:Nebulae.BeatGrid.Audio.Tuning.DspPolicy` when the real symbol lives in `Infrastructure.Audio.Synthesis.Recipes`. Both cases returned three unrelated long-named `MixerScreenAdapter` properties as "Did you mean" suggestions.
- **Two compounding bugs.** Rules 1-3 in `LifebloodSymbolResolver.Resolve` all failed because the input had a kind prefix and namespace dots, but rule 3's bare short-name lookup only fires when the input has neither prefix nor dots. And the fallback ranker scored the full canonical-shaped input string against every bare symbol name; Levenshtein closeness is `candidateLength - distance`, which biases toward long candidate names regardless of semantic similarity.
- **Architectural fix** in `LifebloodSymbolResolver`: one helper, one new rule, one ranker correction.
  - `ExtractLikelyShortName(string)`: pure function. Strips kind prefix, strips method parameter list, returns the final dot-separated segment. Single source of truth used by both Rule 4 and the suggestion ranker.
  - **Rule 4** (`ResolveOutcome.ShortNameFromQualifiedInput` and `AmbiguousShortNameFromQualifiedInput`). After rules 1-3 fail and the extracted short name is non-empty, look it up via `SemanticGraph.FindByShortName`. Single hit resolves with a diagnostic explaining the namespace correction. Multiple hits surface all candidates. Zero hits fall through to `NotFound` with an honest diagnostic.
  - `SuggestNearMatchesInternal` passes the query through `ExtractLikelyShortName` before scoring. Literal short-name index hits land at `ShortNameHitScore = 1000`, deliberately above any reachable `ScoreCandidate` value, so real short-name matches always sort above fuzzy-ranking accidents.
- Every one of the 10 read-side and write-side tools that route through `ISymbolResolver` inherits the fix automatically (`INV-RESOLVER-001`).
- Pinned by `ResolverShortNameFallbackTests` (24 tests including both exact dogfood cases reproduced in synthetic graphs with bias-inducing noise symbols).

### Fixed. `lifeblood_compile_check` auto-wraps bare statement snippets (`24f607e`)

- **The bug.** The tool was documented as "compile a snippet in the project context" but fed raw text to `CSharpSyntaxTree.ParseText` and pasted the result at the top level of the target module. Library modules (the overwhelming majority of csprojs) refused bare statement snippets like `var x = 1 + 1;` with CS8805 "Program using top-level statements must be an executable". Users had to manually wrap snippets in `class _Probe { void M() { ... } }` to test anything that was not already a complete compilation unit.
- **The fix.** New `Internal.SnippetWrapper` helper. If the parsed `CompilationUnitSyntax` contains any type, namespace, or delegate declaration at the top level, pass through unchanged. Otherwise wrap the statements in a synthetic `class _LifebloodCompileProbe { void _LifebloodCompileBody() { ... } }` on a single inserted line above the user's body. Using directives are preserved at the top level. `MapLineToUser` remaps diagnostic line numbers back to the user's original coordinates.
- `RoslynCompilationHost.CompileCheck` routes through `SnippetWrapper.Prepare` and applies `MapLineToUser` when projecting diagnostics.
- Pinned by `SnippetWrapperTests` (21 tests) plus 5 end-to-end `CompileCheck` integration tests in `WriteSideToolTests`.

### Fixed. `lifeblood_search` tokenized ranked-OR scoring (`9cc9e50`)

- **The bug.** The provider scored against `sym.Name.Contains(query, ...)` with the whole untokenized query string. Dogfood against DAWG: `"interpolate"` returned 5 hits, `"interpolate values"` returned 0. `"quantize"` returned 5 hits, `"quantize timing to grid"` returned 0. A tool billed as ranked keyword search that zeros out the moment you add specificity.
- **The fix.** The query is now an ordered list of tokens, not an opaque string. Split on whitespace, dedup case-insensitively, drop tokens below 3 chars to keep noise from saturating scores, fall back to the whole trimmed query as a single literal when every token is sub-threshold so "id" or "db" still work. Each surviving token is an independent scoring signal; scores OR-accumulate across fields and tokens. Per-field weights and the FQ vs. bare-name dedup semantics are preserved.
- Pinned by 6 new regression tests in `SemanticSearchTests`.

### Fixed. Linux CI path normalization drift (`a077e35`)

- **CI was red on every commit since `96abd06`.** `ArchitectureInvariantTests.CompositionRoot_CLI_UsesOnlyAllowedModules` and the `ServerMcp` variant failed on Linux because `Path.GetFileNameWithoutExtension` treats backslash as a literal filename character on non-Windows hosts. Csproj `ProjectReference Include` attributes are authored in MSBuild convention (`..\Lifeblood.Domain\Lifeblood.Domain.csproj`), so the test received an unsplittable string and the allowlist check failed against `..\Lifeblood.Domain\Lifeblood.Domain` instead of `Lifeblood.Domain`.
- **The fix.** Same class as commits `c9606b9` and `562dc6a` ("normalize path separators at every csproj / sln raw-path site") but for the ratchet test that was missed. Extracted `Internal.CsprojPaths` with `NormalizeSeparators` and `GetReferencedModuleName`, and routed every caller through it. `RoslynModuleDiscovery`'s four call sites and the architecture ratchet test now share one implementation, so future csproj-path bugs cannot affect one without affecting the other.

### Added. `lifeblood_invariant_check` (Phase 8, `26bb8bf`, `INV-INVARIANT-001`)

- **New MCP tool** that turns `CLAUDE.md`'s 58+ architectural invariants into queryable structured data. Three modes selected by parameter shape:
  - `id`: return one invariant's full body, title, category, and source line.
  - `mode: "audit"` (default): summary with total count, per-category breakdown, duplicate-id collisions, and parse warnings.
  - `mode: "list"`: every id plus title plus category plus source line, bodies omitted for compact responses.
- **Hexagonal stack**, five new classes across Application and `Connectors.Mcp`:
  - `Invariant`, `InvariantAudit`, `CategoryCount`, `DuplicateInvariantId` value types in `Lifeblood.Application.Ports.Right.Invariants/`.
  - `IInvariantProvider` port.
  - `Internal.ClaudeMdInvariantParser`: pure text to records. Handles shape A (`- **INV-X-N**: body`) and shape B (`- **INV-X-N. Title.** body`). Multi-line titles, multi-paragraph bodies, duplicate detection, category inference from id prefix including multi-segment prefixes (`INV-USAGE-PROBE-002` to `USAGE-PROBE`), backtick-code-span-safe first-sentence extraction.
  - `Internal.InvariantParseCache<T>`: generic timestamp-invalidated cache, reusable for any future invariant source.
  - `LifebloodInvariantProvider`: thin orchestrator. Reads `CLAUDE.md` at the loaded project root via `IFileSystem`, delegates parsing to the parser, caches per-root.
- **Source of truth.** `CLAUDE.md` is parsed at runtime; no companion metadata file. Deliberate simplification of the original Phase 8 spike's Option C, eliminating drift between a prose source and a structured mirror. Phase 8C can still add a metadata companion later without breaking the contract.
- **Works on any project.** Lifeblood itself declares 58 invariants. DAWG (production Unity workspace) declares 61. Projects without `CLAUDE.md` get a graceful empty audit.
- **Regression pins**: 43 tests across four files.
  - `ClaudeMdInvariantParserTests`: 17 tests covering both bullet shapes, block termination, duplicate detection, category inference, backtick-code-span-safe title extraction.
  - `InvariantProviderAndHandlerTests`: 14 tests including provider direct tests, handler dispatch, and one end-to-end test via real `lifeblood_analyze` against a minimal Roslyn workspace.
  - `LifebloodClaudeMdSelfTests`: 5 tests that parse Lifeblood's own `CLAUDE.md` and audit the invariant inventory (>= 50 invariants floor, every core category present, known stable ids resolve, no duplicates, no parse warnings).
  - `InvariantParseCacheTests`: 7 tests pinning the generic cache against a fake `IFileSystem`: empty path, missing file, cache hit on unchanged timestamp, invalidation on timestamp change, parser-throws retry, concurrent reads.
- Phase 8 spike published at `docs/plans/invariant-check-spike.md`.

### Added. MCP stdio-loop and ToolHandler search dispatch coverage (`ea08264`)

- **`McpStdioLoopTests`** (3 tests). Every previous MCP test exercised `McpDispatcher` in-process; none spawned the real compiled dll. Closes that gap with tests that boot `Lifeblood.Server.Mcp.dll` via `dotnet <dll>`, speak real JSON-RPC 2.0 over real stdin / stdout, and pin:
  1. `initialize` handshake round-trips through the real reader / writer / dispatcher chain.
  2. `tools/list` returns `result.tools[]`, never `error`. Pins the serialization regression class that `334b47d` fixed.
  3. **Stdout purity.** Every line read from stdout must parse as valid JSON-RPC. Any future `Console.WriteLine` that lands on stdout instead of `Console.Error.WriteLine` (banner, log, stray print) corrupts MCP framing and breaks every client. No in-process dispatcher test can catch this.
- **`Handle_Search_*` tests in `ToolHandlerTests`** (5 new tests). `lifeblood_search` had zero ToolHandler-layer coverage; the dispatch path (args parsing, kinds-array coercion, query string coercion, error envelopes) was untested.

### Known limitation. `lifeblood_dead_code` is experimental / advisory (`INV-DEADCODE-001`)

- **Dogfood against DAWG** surfaced three false-positive classes.
  1. **Method-group references.** A private method passed as a delegate (`new Lazy<T>(Load)`, event handler registration, LINQ `Where(predicate)`) never produces an `InvocationExpressionSyntax` at the call site, so no `Calls` edge is emitted into the referenced method. Example: `BclReferenceLoader.Load` is flagged as dead because its only caller is `new Lazy<>(Load)` in the same type's static field initializer.
  2. **Multi-module canonical-id drift.** Direct invocations of some methods with complex signatures (nullable generics, cross-module source-type parameters) fail to produce `Calls` edges in the full multi-module workspace even though isolated synthetic reproductions work. Example: `ModuleCompilationBuilder.CreateCompilation` is called directly on line 96 of the same file but has zero incoming edges in the graph. **Root cause found and fixed in v0.6.4:** missing implicit global usings in compilation, not canonical-id drift. See v0.6.4 changelog entry.
  3. **Same-class private field reads.** Private fields read from sibling methods on the same type are flagged because the extractor does not emit read-edges at method-to-field granularity.
- **Ship decision.** The tool is marked `[EXPERIMENTAL. ADVISORY ONLY]` in its description. Every response carries `status: "experimental"` plus a `warning` field listing the classes so agents cannot consume findings without seeing the caveat. `Handle_DeadCode_Response_IncludesExperimentalWarning` pins the caveat against future regression.
- **v0.6.4 resolution.** All three classes fixed in v0.6.4. Root cause of class 2 was missing implicit global usings in compilation, not canonical-id drift.
- **Impact on other tools.** `lifeblood_find_references`, `lifeblood_dependants`, `lifeblood_blast_radius`, and `lifeblood_file_impact` inherit the same class-2 gap for the same subset of methods. They are still authoritative for the 95%+ of symbols outside the gap class. Regression tests `ExtractEdges_MethodCall_NullableGenericParameter_SameClass_ProducesCallsEdge` and `ExtractEdges_MethodCall_ComplexSignature_MatchesRealProcessInOrder` in `RoslynExtractorTests` pin the synthetic happy-path.

### Changed

- **Tool count**: 18 to **22** (added `lifeblood_search`, `lifeblood_dead_code`, `lifeblood_partial_view`, `lifeblood_invariant_check`).
- **Port interfaces**: 17 to **22** (added `ISemanticSearchProvider`, `IDeadCodeAnalyzer`, `IPartialViewBuilder`, `IUserInputCanonicalizer`, `IInvariantProvider`).
- **Tests**: 362 to **539**.
- **Duplicate invariant id resolved.** `CLAUDE.md` had two invariants sharing the id `INV-TEST-001` (one under "Testing", one under "Test Discipline"). The drift was caught by `lifeblood_invariant_check` itself, which is why the tool exists. The Test Discipline instance is now `INV-TESTDISC-001`.
- **`Lifeblood.Connectors.Mcp.csproj`** gains `InternalsVisibleTo Lifeblood.Tests`, matching the convention `Lifeblood.Adapters.CSharp.csproj` already uses.
- **docs/STATUS.md ratchets updated**: `portCount: 17 to 22`, `toolCount: 18 to 22`, `testCount: 362 to 539`.
- **Release-wide documentation sweep.** `README.md`, `docs/TOOLS.md`, `docs/ARCHITECTURE.md`, `docs/STATUS.md`, `docs/architecture.html`, and this `CHANGELOG.md` are rewritten to reflect 22 tools, 22 ports, the new invariant-check tool, the compile_check auto-wrap, the resolver wrong-namespace fallback, the search tokenization, and the dead_code experimental caveat.

### Invariants registered in CLAUDE.md

- `INV-RESOLVER-001..004`. Identifier resolution as an Application port. Every read-side tool routes through one resolver. Partial-type unification is a read model.
- `INV-RESOLVER-005`. Wrong-namespace inputs resolve via the trailing short-name segment.
- `INV-VIEW-001..003`. Each language adapter publishes a typed semantic view; consumers take it by reference.
- `INV-TOOLREG-001`. Tool availability dispatch is by typed enum, never by name prefix. Internal registry records and wire-format DTOs are separate types.
- `INV-MCP-003`. Every MCP wire-format constant has exactly one canonical source per side.
- `INV-INVARIANT-001`. `CLAUDE.md` is the single source of truth for architectural invariants; the tool parses it at runtime.
- `INV-DEADCODE-001`. `lifeblood_dead_code` is advisory and ships with three documented false-positive classes. Every response carries the experimental marker and warning.
- `INV-TESTDISC-001`. Tests never silently early-return on precondition failure.

## [0.6.1] - 2026-04-10

Credibility pass after the v0.6.0 release. Closes an MCP-spec interoperability gap, extracts the MCP dispatcher into its own testable class, closes a latent display-string-for-matching bug class in `FindImplementations`, converts silent test skips to explicit `Skip.IfNot` calls that can no longer hide real failures, tightens the architecture ratchet, and syncs the doc numbers that drifted during v0.6.0 preparation. 344 → 362 tests. No behavior change in analyze or any read-side tool.

### Fixed

- **`FindImplementations` used `ToDisplayString()` for cross-assembly type equality** (latent bug class caught during the v0.6.1 deep-audit pass). The same bug class that v0.6.0 Layer 2 of the BCL fix closed in `FindReferences` was still present in `FindImplementations` because the v0.6.0 fix did not sweep every display-string comparison. Display strings can diverge subtly at the source/metadata boundary (nullability, reduced names, attribute round-trips), and using them for *matching*. As opposed to *display*. Is fragile. The deep-audit sweep exposed the bug: after converting silent test skips to explicit `Skip.IfNot` (see below), `FindImplementations_IGreeter_FindsGreeterAndFormalGreeter` ran against the real golden repo for the first time in a while and failed loudly. Fix: compare via canonical Lifeblood symbol IDs (`BuildSymbolId(roslynSymbol) == BuildSymbolId(candidate)`), the same strategy v0.6.0 Layer 3 adopted in `FindReferences`. `CanonicalSymbolFormat` produces identical ID strings for source and metadata copies by design, so cross-assembly matching is correct-by-construction. All other `ToDisplayString()` call sites in the C# adapter were audited and classified. The remaining ones are all stable human-display or namespace-filter uses, not matching comparisons. Also removed the orphaned `ResolveDisplayString` helper that the v0.6.0 `FindReferences` rewrite had left dead.
- **17 silent test early-returns in `WriteSideIntegrationTests`**: every test in the file began with `if (!TryAnalyze(out var graph, out _)) return;`. The `TryAnalyze` helper silently returned `false` on both a legitimate skip condition (golden repo missing) AND a real failure (golden repo present but analysis returned zero symbols). Silent early-return makes both look like passing tests. This directly hid the `FindImplementations` bug above for multiple commits. Fix: replaced `TryAnalyze(out)` with `EnsureAnalyzed()` that uses `Skip.IfNot(precondition, reason)` for missing preconditions (explicit skip via `Xunit.SkippableFact`) and `Assert.True` for real failures (loud fail). All 17 tests converted to `[SkippableFact]`, the silent-return guards removed, and the entire file now either runs for real or skips with a documented reason. Registers `INV-TEST-001`.
- **`notifications/initialized` was rejected by the dispatcher**: the `Program.Dispatch` switch only matched the bare `initialized` method name. Spec-compliant MCP clients sending the canonical `notifications/initialized` form fell through to the `default:` branch and received a `-32601 Method not found` error. Compounding the first violation: that response was a body for a notification. JSON-RPC 2.0 forbids responding to notifications at all. Strict clients saw both violations at once. Tolerant clients (the current Claude Code CLI) silently ignored the errors and the bug went unnoticed.
- **`ToolRegistry.GetTools` used a string-prefix dispatcher** with 8 hard-coded `StartsWith` checks to decide which tools were write-side. `lifeblood_resolve_short_name` was added in v0.6.0 under the write-side comment divider but matched none of the 8 prefixes, so it was never decorated as unavailable even when classified as write-side by its physical position. Root-cause fixed by replacing the string dispatch with a typed `ToolAvailability` enum on `McpToolInfo` (see below). `lifeblood_resolve_short_name` is now classified `ReadSide`. It consults the graph's short-name index, not the Roslyn compilation host.
- **CHANGELOG link-references were stale**: the `[0.6.0]` heading at the top had no matching `[0.6.0]: .../compare/v0.5.1...v0.6.0` reference at the bottom, so the compare link did not render. `[Unreleased]` still pointed at `v0.5.1...HEAD`. Fixed and pinned by a new ratchet test.
- **CHANGELOG was missing `[0.4.0]` and `[0.4.1]` entries entirely**: the file jumped from `[0.5.0]` straight to `[0.3.0]` even though both tags were published. Reconstructed both entries from git log.

### Added. MCP-spec compliance hardening

- **New class `McpDispatcher`** (`src/Lifeblood.Server.Mcp/McpDispatcher.cs`). First-class JSON-RPC / MCP method dispatcher. `Program.cs` used to contain a private `static Dispatch` method on the program entry class; it has been extracted into its own public sealed class so the dispatch logic is testable via the normal public API (no `InternalsVisibleTo`, no reflection, no visibility tricks). `Program.Main` is now a thin stdio I/O loop that constructs the dispatcher and delegates every request to it.
- **`McpDispatcher.SupportedProtocolVersion`** const. The MCP protocol version string (`"2024-11-05"`) is owned by the dispatcher class, single source of truth for the `initialize` response and any future version-gated capability negotiation.
- **`KnownNotifications` set**. `notifications/initialized` (canonical, MCP spec), `initialized` (legacy alias during the deprecation window), `notifications/cancelled`, and `$/cancelRequest` are all recognized. Any entry in the set short-circuits `Dispatch` to return `null` before response construction, guaranteeing JSON-RPC 2.0 compliance. Unknown notifications (any method with `request.Id == null` that is not in the set) also return `null` and log to stderr for operator visibility.
- **`initialize` response explicitly populates `ProtocolVersion` and `Capabilities`** at the construction site in addition to the class-level defaults on `McpInitializeResult`. Belt-and-braces pin so a future edit that strips the defaults does not silently break the wire shape.
- **`serverInfo.version` now reads `AssemblyInformationalVersionAttribute`** (MinVer's canonical output carrying the full semver + provenance form like `0.6.1+abc123`), falling back to the three-part `AssemblyName.Version` form. Previously the dispatcher used the three-part form unconditionally, losing the provenance suffix on non-tagged commits.
- **New `McpProtocolTests.cs`** (10 tests) pinning the dispatcher contract end-to-end: `Initialize_ReturnsSpecCompliantResult`, `Initialize_SerializedJson_HasProtocolVersionAndCapabilities`, `NotificationsInitialized_SpecCompliantForm_ProducesNoResponse`, `NotificationsInitialized_LegacyAlias_ProducesNoResponse`, `UnknownNotification_ProducesNoResponse`, `UnknownRequest_ReturnsMethodNotFound`, `ToolRegistry_EveryToolHasExplicitAvailability`, `ToolRegistry_WriteSideTools_MarkedUnavailable_WhenNoCompilationState`, `ToolRegistry_ReadSideTools_NeverMarkedUnavailable`, `ToolRegistry_ResolveShortName_IsClassifiedReadSide`.

### Added. Typed tool availability dispatch

- **`ToolAvailability` enum** (`ReadSide | WriteSide`) and **`required McpToolInfo.Availability` property**. The `required` modifier (C# 11) makes it a compile error to declare a new tool without setting `Availability`. The invariant is enforced at the language level, not by convention. `ToolRegistry.GetTools(hasCompilationState)` now filters on the typed enum and no longer touches tool name strings.
- Registers `INV-TOOLREG-001`: dispatch is by the typed property, never by name prefix.

### Added. Architecture ratchet for composition roots and ScriptHost

- **`ScriptHost_HasZeroProjectReferences`**. Enforces `INV-SCRIPTHOST-001`. `Lifeblood.ScriptHost` is an isolated child process; it may reference NuGet packages (Microsoft.CodeAnalysis.CSharp.Scripting is load-bearing) but must not reference any Lifeblood project. Without this test the isolation guarantee was documented but not enforced.
- **`CompositionRoot_CLI_UsesOnlyAllowedModules`** and **`CompositionRoot_ServerMcp_UsesOnlyAllowedModules`**. Enforce `INV-COMPROOT-001`. The allowed module set `{Domain, Application, Analysis, Adapters.CSharp, Adapters.JsonGraph, Connectors.ContextPack, Connectors.Mcp}` is declared once as a `HashSet<string>` on `ArchitectureInvariantTests`. Single source of truth. Adding a new module to either composition root requires editing the allowlist, making the scope expansion a conscious architectural decision.

### Added. Documentation ratchet

- **New `DocsTests.cs`** with three ratchet tests pinning `INV-DOCS-001` and `INV-CHANGELOG-001`:
  - `StatusDoc_PortCount_MatchesApplicationPortsDeclarations`. Parses the `<!-- portCount: N -->` HTML comment in `docs/STATUS.md`, counts `public interface I*` declarations under `src/Lifeblood.Application/Ports`, asserts the two match. Single source of truth is the HTML comment.
  - `StatusDoc_ToolCount_MatchesToolRegistryLiterals`. Parses `<!-- toolCount: N -->`, counts `Name = "lifeblood_*"` literals in `ToolRegistry.cs`, asserts the two match.
  - `Changelog_EveryHeadingHasLinkReference`. Parses every `## [X.Y.Z]` heading and every `[X.Y.Z]: ...` link reference, asserts bijection. Pins the bug class where v0.6.0 shipped with stale link refs.

### Changed

- **Port count corrected: 14 → 17** (not `14 → 15` as the v0.6.0 entry claimed). The v0.6.0 release added three port interfaces (`IUsageProbe`, `IUsageCapture`, `ISymbolResolver`) on top of the 14 that existed at v0.5.1. The previous "15" count was counting the usage probe pair as one port unit; the `StatusDoc_PortCount` ratchet counts declared interfaces, which is the accurate semantic. Docs across `README.md`, `docs/STATUS.md`, `docs/ARCHITECTURE.md`, `docs/architecture.html`, and this CHANGELOG have been synced.
- **`docs/architecture.html` stats pill updated**: `1376 symbols, 3822 edges` → `1379 symbols, 3830 edges` (live counts after the v0.6.0 additions). Test count pill updated to `362 tests`.
- **Program.cs** is now a ~70-line composition root. Reduced by ~100 LOC after the `McpDispatcher` extraction.
- **Tests: 344 → 362** (+18 across architecture ratchet, docs ratchet, and MCP protocol tests).

### Dependencies

- **`Xunit.SkippableFact` 1.*** added to `Lifeblood.Tests.csproj`. Enables real skip semantics on xunit 2.x (`Skip.IfNot(condition, reason)`) so missing preconditions turn into explicit Skip states with a reason instead of silent passes. Needed for `INV-TEST-001`.

### Invariants registered in CLAUDE.md

- `INV-MCP-001`. MCP `initialize` response always carries `protocolVersion` and `capabilities`. `protocolVersion` is owned by `McpDispatcher.SupportedProtocolVersion`, never inlined.
- `INV-MCP-002`. Notifications (messages with no `id`) never receive a response body. The `KnownNotifications` set in `McpDispatcher` is the single source of truth for which method names are notifications. Both `notifications/initialized` and the legacy `initialized` alias are accepted.
- `INV-TOOLREG-001`. Every `McpToolInfo` declares its `Availability` explicitly via the required init-only property. The `GetTools(hasCompilationState)` guard filters on `Availability`, never on tool name prefixes. Adding a new tool without setting `Availability` is a compile error.
- `INV-SCRIPTHOST-001`. `Lifeblood.ScriptHost` has zero `ProjectReference`. Ratchet-tested.
- `INV-COMPROOT-001`. Composition roots (`Lifeblood.CLI`, `Lifeblood.Server.Mcp`) reference only the allowlist `{Domain, Application, Analysis, Adapters.CSharp, Adapters.JsonGraph, Connectors.ContextPack, Connectors.Mcp}`. Ratchet-tested via `ArchitectureInvariantTests.CompositionRootAllowedModules`.
- `INV-DOCS-001`. `docs/STATUS.md` port and tool counts match the repository. Pinned via HTML comments as single source of truth and `DocsTests` as ratchet.
- `INV-CHANGELOG-001`. Every `## [X.Y.Z]` heading in `CHANGELOG.md` has a corresponding link reference. Ratchet-tested.
- `INV-TEST-001`. Tests never silently early-return on precondition failure. Missing preconditions turn into explicit `Skip.IfNot(condition, reason)` calls via `Xunit.SkippableFact`. Real failures (presence-but-broken) turn into loud `Assert.True` / `Assert.Fail`. The `TryAnalyze(out) ⇒ bool` silent-return pattern is forbidden.
- `INV-FINDIMPL-001`. `FindImplementations` compares candidates via canonical Lifeblood symbol IDs (`BuildSymbolId`), never via `ToDisplayString()` or `SymbolEqualityComparer.Default`. The canonical-ID comparison is the same strategy `FindReferences` uses, closing the display-string-for-matching bug class across the whole write-side read surface.

## [0.6.0] - 2026-04-10

Three-seam framing after the BCL ownership fix, native usage reporting on every analyze run, and the v0.6.0 doc pack refresh. 329 → 344 tests. 17 → 18 MCP tools. 14 → 17 port interfaces (+`IUsageProbe`, +`IUsageCapture`, +`ISymbolResolver`). `lifeblood_analyze` responses now carry a structured `usage` block with wall time, CPU time, peak memory, GC pressure, and per-phase timings.

### Added. Native usage reporting on every analyze run (LB-INBOX-005)

Every `lifeblood_analyze` response now carries a structured `usage` block
with wall time, CPU time (total, user, kernel), peak working set, peak
private bytes, GC collection counts per generation, host logical core
count, and per-phase timings. No external measurement wrapper required.
The feature is on by default in both CLI and MCP shapes; constructing
`AnalyzeWorkspaceUseCase` without a probe still works and returns a null
`Usage` field with zero overhead.

- **`Lifeblood.Domain.Results.AnalysisUsage`** POCO with `WallTimeMs`,
  `CpuTimeTotalMs`, `CpuTimeUserMs`, `CpuTimeKernelMs`,
  `PeakWorkingSetBytes`, `PeakPrivateBytesBytes`, `HostLogicalCores`,
  `GcGen0Collections`, `GcGen1Collections`, `GcGen2Collections`,
  `Phases[]`, and a derived `CpuUtilizationPercent`. All fields are inert
  data; no `System.Diagnostics` types leak onto the record.
- **`Lifeblood.Application.Ports.Infrastructure.IUsageProbe`** +
  **`IUsageCapture`** port pair, sitting alongside `IFileSystem` in the
  Application layer so the use case stays free of host diagnostics types.
  `IUsageCapture.Stop` is idempotent (INV-USAGE-PORT-002), and the use
  case disposes the capture on the validation-error path so the sampling
  timer does not outlive a failed run.
- **`Lifeblood.Adapters.CSharp.ProcessUsageProbe`** concrete. Uses
  `Process.GetCurrentProcess()` + `Stopwatch` + a background `Timer`
  polling peak RSS at a configurable interval (default 250 ms). Takes an
  initial RSS sample in the constructor so sub-sample-interval runs still
  report a non-zero peak (INV-USAGE-PROBE-001). Captures CPU time as a
  delta of `UserProcessorTime` / `PrivilegedProcessorTime` across the
  run, so the reported numbers are specific to this analyze call and not
  contaminated by earlier work inside the same process.
- **`AnalyzeWorkspaceUseCase`** gains an optional third constructor
  parameter `IUsageProbe? usageProbe`. When non-null, the use case wraps
  its work in a capture, marks `"analyze"` and `"validate"` phase
  boundaries, and returns the snapshot on
  `AnalyzeWorkspaceResult.Usage`. When null, behavior is identical to
  before and `Usage` is null.
- **CLI** prints a `── usage ──` block to stderr after the graph summary
  with a fixed-column layout and InvariantCulture formatting, so the
  output reads the same on every locale. Phase breakdown is shown with
  one line per phase.
- **MCP** `GraphSession.Load` returns a structured JSON response with
  `summary`, `changedFileCount`, and a `usage` object carrying the full
  snapshot. Agents consuming `lifeblood_analyze` now read
  `usage.wallTimeMs`, `usage.peakWorkingSetMb`, `usage.phases[]`, etc.
  without parsing free text. The prior plain-text `"Loaded: ..."`
  response is replaced by the structured form.
- **15 new unit tests** across `UseCaseTests` (3 tests: null usage when
  no probe, populated usage with phase marks, capture disposed on
  validation error) and `ProcessUsageProbeTests` (12 tests: non-zero
  wall time on sleep, CPU time delta on busy work, utilization
  consistency with wall and CPU, peak WS non-zero guarantee, host core
  count match, phase mark order, idempotent `Stop`, `Dispose` after
  `Stop` no-op, `Dispose` without `Stop` no throw, independent captures,
  short-run non-zero peak, GC collection counts non-negative). Tests:
  329 → 344.
- **Invariants documented** in `CLAUDE.md` under the new
  "Usage Reporting (Analyze Pipeline)" section:
  `INV-USAGE-001`, `INV-USAGE-002`, `INV-USAGE-PORT-001`,
  `INV-USAGE-PORT-002`, `INV-USAGE-PROBE-001`, `INV-USAGE-PROBE-002`.

#### Why this shipped instead of living in the improvement inbox

The v0.6.0 doc pack was corrected twice during preparation because the
published memory and timing figures had drifted out of date. The old docs
quoted roughly 90 s wall and 4 GB peak for a 75-module Unity workspace.
The real measured numbers on a 16-core, 32-thread host are 32.6 s wall
and 571 MB peak working set. That drift is inevitable as long as
published benchmarks come from one-off measurement sessions. Putting the
`usage` block on every analyze response makes the docs self-verifying:
any consumer can cite the live output, and the project stays 1:1 honest
without per-release measurement chores. The CLAUDE.md invariants make
this a durable rule. Any future use case that adds measurable work
should reach for the same `IUsageProbe` port, not a bespoke wrapper.

### Added. Plan v4 (three-seam framing for the post-BCL findings)

After v2's BCL fix, two reviewer reports surfaced five remaining findings.
v4 closes them via three architectural seams (one resolver port, one
csproj-driven compilation-fact convention, one adapter semantic view) instead
of five piecemeal patches.

#### Seam #1. `ISymbolResolver` (closes LB-BUG-002, LB-BUG-004, LB-FR-002, LB-FR-003)

- **`Lifeblood.Application.Ports.Right.ISymbolResolver`** + single `SymbolResolutionResult` DTO. Every read-side MCP tool that takes a `symbolId` (lookup, dependants, dependencies, blast_radius, file_impact, find_references, find_definition, find_implementations, documentation, rename) routes through the resolver before doing graph or workspace lookups.
- **`Lifeblood.Connectors.Mcp.LifebloodSymbolResolver`**. Reference implementation. Resolution order: exact canonical match → truncated method form (lenient single-overload) → bare short name. Returns `Outcome` + `Candidates` + `Diagnostic` for ambiguous and not-found cases.
- **Partial-type unification as a read model.** The graph stores raw symbols (one record per partial declaration; last-write-wins remains the storage policy). The resolver computes a deterministic primary file path + the full `DeclarationFilePaths` array by walking the existing `file:X Contains type:Y` edges. **Zero schema change to `Lifeblood.Domain.Graph.Symbol`.** Primary picker rule: filename matches type name → filename starts with `"<TypeName>."` (shortest first) → lexicographic first.
- **Short-name index** added to `SemanticGraph.GraphIndexes` (case-insensitive bucket) + public `FindByShortName(name)` accessor. Built lazily alongside existing indexes.
- **New MCP tool `lifeblood_resolve_short_name`**. Discover canonical IDs from a bare short name when you don't know the namespace.
- **`FindReferencesOptions.IncludeDeclarations`**. Explicit operation policy on `ICompilationHost.FindReferences`. When true, the result includes one synthetic `(declaration)` entry per source declaration site (one per partial for partial types). Two-overload signature preserves backward compat.
- **9 regression tests in `SymbolResolverTests.cs`** including the LB-BUG-002 truncated-id misdiagnosis pin-down (`method:Voice.SetPatch` → resolver canonicalizes → caller graph lookup succeeds).
- **`lifeblood_lookup` response now includes `filePaths[]`**. The full sorted list of partial declaration files. The single `filePath` field is now the deterministic primary, no longer a non-deterministic last-write-wins pick.

#### Seam #2. Csproj-driven compilation facts as a documented convention (closes LB-BUG-005)

- **`ModuleInfo.AllowUnsafeCode`**. New typed bool field, default false, set during `RoslynModuleDiscovery.ParseProject` from `<AllowUnsafeBlocks>` element (case-insensitive on the value to handle Unity's `True` casing). Consumed by `ModuleCompilationBuilder.CreateCompilation` via `WithAllowUnsafe(...)`.
- **`INV-COMPFACT-001..003` documented in CLAUDE.md**. Csproj is the source of truth for module-level compilation options; each fact lives as a typed `ModuleInfo` field set at discovery and consumed at compilation; csproj-edit invalidation flows for free through the existing v2 `AnalysisSnapshot.CsprojTimestamps` infrastructure (re-discovery rebuilds the entire `ModuleInfo`, not just one field).
- Closes the **CS0227 false positive class on Unity packages with `<AllowUnsafeBlocks>`**: any csproj that uses unsafe blocks no longer poisons its semantic model with CS0227, restoring `find_references` / dependants / call-graph extraction for symbols inside `unsafe { ... }` regions.
- **5 regression tests** (3 discovery casing + 2 compilation contract).

#### Seam #3. `RoslynSemanticView` (closes LB-BUG-003)

- **`Lifeblood.Adapters.CSharp.RoslynSemanticView`**. Read-only typed accessor for the C# adapter's loaded semantic state (`Compilations`, `Graph`, `ModuleDependencies`). Constructed once per `GraphSession.Load` and shared by reference across consumers.
- **`RoslynCodeExecutor` refactor**. Primary constructor takes a `RoslynSemanticView`. The view IS the script-host globals object: passed to `CSharpScript.RunAsync<RoslynSemanticView>(code, options, view, ...)` so `lifeblood_execute` scripts can read `Graph`, `Compilations`, `ModuleDependencies` as bare top-level identifiers. Backward-compatible secondary constructor takes only `compilations` for tests and standalone callers.
- **Default script imports extended** with `Lifeblood.Adapters.CSharp`, `Lifeblood.Domain.Graph`, `Microsoft.CodeAnalysis`, `Microsoft.CodeAnalysis.CSharp`. **Default script references extended** to include the assemblies that define the script-globals types so `Graph` / `Compilations` resolve at compile time.
- **`INV-VIEW-001..003` documented in CLAUDE.md**. Each language adapter publishes a typed read-only view of its loaded state; tools that need read access consume the view (not raw fields); the view is shared by reference across consumers.
- **4 regression tests** (1 view-shape sanity + 3 script-globals end-to-end including backward compat for pure-C# scripts).

### Changed

- **All read-side MCP handlers route through `ISymbolResolver`.** Truncated method ids and bare short names now resolve correctly across `lookup`, `dependants`, `dependencies`, `blast_radius`, `find_references`, `find_definition`, `find_implementations`, `documentation`, `rename`. The reviewer's LB-BUG-002 (`method:Voice.SetPatch` returning `[]`) is closed.
- **`find_references` adds optional `includeDeclarations` parameter.** Default false preserves prior behavior.
- **`lifeblood_lookup` response includes `filePaths[]`** alongside the existing single `filePath` (now deterministic for partial types).
- **MCP tool count: 17 → 18** (added `lifeblood_resolve_short_name`).
- **Tests: 310 → 328** (+18 across the three seams).

### Fixed

- **`find_references` / call-graph extraction silently returned 0 results for every method in any Unity / .NET Framework / Mono workspace.** Type-only references (`type:Foo`) worked, method-level references (`method:Foo.Bar()`, `dependants`, call edges) returned empty. Three-layer root cause:
  1. **Resolver silent fallback**: `RoslynWorkspaceManager.FindInCompilation` enumerated *all* methods on a type when no overload matched, returning `methods[0]`. So asking for a method that didn't exist returned an unrelated method's call sites. The wrong target then matched no nodes and the query came back empty.
  2. **Display-string match across the source/metadata boundary**: `RoslynCompilationHost.FindReferences` compared `ISymbol.ToDisplayString()` against the resolved target. Different parameter formatters across source and metadata symbols (driven by Roslyn's default `CSharpErrorMessageFormat` and version drift) silently dropped legitimate call sites.
  3. **BCL double-load** (the dominant cause): `ModuleCompilationBuilder.CreateCompilation` always prepended the host .NET 8 BCL bundle, even for modules that already shipped their own BCL via csproj `<Reference Include="netstandard|mscorlib|System.Runtime">` (Unity ships .NET Standard 2.1; .NET Framework / Mono ship mscorlib). Result: every System type existed in two assemblies. Roslyn emitted CS0433 (ambiguous type) and CS0518 (predefined type missing) on every System usage, the semantic model became unusable, `GetSymbolInfo` returned null at every call site, and every walker tool silently produced empty results.

  Empirical impact on a real 75-module Unity workspace: a single audio-DSP module went from 29,523 errors before to 3 unused-field warnings after. `find_references` for the canonical regression target `method:Voice.SetPatch(VoicePatch)` went from 0 to 18 references, including the previously-invisible `voices[i].SetPatch(patch)` array-indexer call sites. Total graph edges across the workspace: 78,126 to 86,334, restoring +8,208 edges that the broken compilation was silently dropping.

  Fix:
  - **Layer 1 (resolver)**: kind-filtered, name-filtered, signature-strict member lookup with documented contract. Never silently substitute an unrelated member. Lenient escape valves (single overload, no signature given) are explicit.
  - **Layer 2 (matcher)**: replace display-string comparison with canonical Lifeblood symbol-ID comparison via `BuildSymbolId(resolved) == targetCanonicalId`. The graph and the walker now share one builder.
  - **Layer 3 (canonical format)**: `Internal.CanonicalSymbolFormat.ParamType` is the single pinned `SymbolDisplayFormat` for parameter type display strings. Every method-ID builder in the adapter (`RoslynSymbolExtractor`, `RoslynEdgeExtractor`, `RoslynCompilationHost.BuildSymbolId`, `RoslynWorkspaceManager.FindInCompilation`) routes through it. Lifeblood's symbol ID grammar is now owned by Lifeblood, not implicitly inherited from Roslyn's default.
  - **Layer 4 (BCL ownership, INV-BCL-001..005)**: new `BclOwnershipMode` enum on `ModuleInfo`. `RoslynModuleDiscovery` decides ownership ONCE during csproj parsing by inspecting `<Reference Include>` (parsed as assembly identity, handles both bare names and strong-name shapes) and `<HintPath>` basename. `ModuleCompilationBuilder` reads the field and gates host BCL injection. No detection logic at the compilation layer. Decision is single-source-of-truth.
  - **Layer 5 (incremental csproj invalidation, INV-BCL-005)**: `AnalysisSnapshot.CsprojTimestamps` tracks csproj timestamps. `IncrementalAnalyze` checks csproj timestamps before the .cs file loop and forces re-discovery + recompile when a csproj edits. Closes the silent-stale-BclOwnership hole that would otherwise re-introduce the bug under incremental mode.

### Added

- `BclOwnershipMode` enum and `ModuleInfo.BclOwnership` field on `Lifeblood.Application.Ports.Left.IModuleDiscovery`.
- `Internal.CanonicalSymbolFormat`. Single source of truth for parameter type display strings in Lifeblood method symbol IDs.
- `AnalysisSnapshot.CsprojTimestamps` for incremental csproj-edit detection.
- 9 regression tests in `FindReferencesCrossModuleTests.cs` (cross-module class methods, struct methods, partial structs, array-indexer receivers, library-defined parameter types, overload disambiguation, silent-fallback bug class).
- 4 BCL ownership discovery tests in `HardeningTests.cs` (bare Include, strong-name Include, HintPath-only, plain SDK).
- 3 BCL ownership compilation/integration tests in new `BclOwnershipCompilationTests.cs` (ModuleProvided contract, HostProvided contract, end-to-end two-module find_references).
- 2 incremental csproj-invalidation tests in `IncrementalAnalyzeTests.cs`.
- New invariants documented in `CLAUDE.md`: INV-BCL-001 through INV-BCL-005, plus the canonical symbol ID grammar.
- Architectural plan at `.claude/plans/bcl-ownership-fix.md` (v2 with reviewer corrections folded in).

### Changed

- Tests: 293 → 310 (+17 regression tests across discovery, compilation, incremental, and integration layers).

## [0.5.1] - 2026-04-09

### Fixed

- **CS0518 "System.Object is not defined" on multi-module workspaces** (#1): `lifeblood_execute` failed on any code against workspaces with many modules (for example, a 75-module Unity workspace). Three-layer root cause:
  1. `ScriptOptions.Default` contains 25 "Unresolved" named references. Placeholders that never resolve to actual DLLs in a published app.
  2. Adding `compilation.References` (target project's transitive deps) injected Unity's netstandard BCL stubs, conflicting with the host .NET 8 runtime.
  3. Two competing `System.Object` definitions from different BCL flavors caused the script compiler to fail.
  
  Fix: Explicitly load the host .NET runtime's BCL assemblies from the runtime directory (17 core assemblies). Use `WithReferences` (replace) instead of `AddReferences` to drop the useless "Unresolved" defaults. Only add `CompilationReference` per project module. No transitive deps.

### Added

- 5 regression tests for the CS0518 bug class (downgraded multi-module topology).
- `LoadHostBclReferences()`. Resolves host BCL once per process via `Lazy<T>`.

### Changed

- **MCP server deployment**: `.mcp.json` now points to `dist/` (published output) instead of `dotnet run --project`. Decouples running server from build locks.
- Tests: 288 → 293.

## [0.5.0] - 2026-04-09

Cross-assembly semantic analysis. The gap that required grep fallback for cross-module consumer counting is gone.

### Fixed

- **Cross-assembly edges were silently dropped**: `IsFromSource` rejected metadata symbols from other analyzed modules. Renamed to `IsTracked`. Now accepts symbols whose `ContainingAssembly.Name` matches a known workspace module. Resolves F3 (empty cross-module dependency matrix).
- **FindReferences returned empty for cross-assembly symbols**: `SymbolFinder.FindReferencesAsync` doesn't work across AdhocWorkspace project boundaries. Rewritten to direct compilation scan (same proven pattern as `FindImplementations`).
- **FindDefinition/GetDocumentation returned empty for cross-assembly symbols**: `ResolveSymbol` could return a metadata copy (no source location, no XML docs). New `ResolveFromSource` prefers source-defined symbols across all compilations.
- **Dead `IsFromSource` in RoslynCompilationHost**: removed and replaced with wired `IsFromSource` used by `ResolveFromSource` for source preference.
- **Module discovery merge**: filesystem scan now merges with csproj `<Compile Include>` items instead of choosing one path.

### Changed

- **CrossModuleReferences capability**: upgraded from `BestEffort` to `Proven`.
- **MinVer auto-versioning**: version derived from git tags via MinVer. No manual bumping. Just tag and push.
- **MCP server version**: `initialize` response now reports actual assembly version instead of hardcoded `"1.0.0"`.
- Tests: 281 → 288.

### Added

- **Cross-assembly edge extraction**: `RoslynEdgeExtractor.KnownModuleAssemblies` enables edges between symbols in different assemblies (References, Implements, Inherits, Calls, Overrides).
- **HintPath DLL loading**: `ModuleInfo.ExternalDllPaths` + `RoslynModuleDiscovery` parses `<HintPath>` from csproj `<Reference>` elements. Unity engine assemblies loaded as metadata references via `SharedMetadataReferenceCache`, resolving thousands of CS0246 false diagnostics.
- **Module dependency threading**: `RoslynWorkspaceAnalyzer.ModuleDependencies` propagated through `RoslynCompilationHost`/`RoslynWorkspaceRefactoring` → `RoslynWorkspaceManager` for ProjectReference-linked workspace (used by Rename).
- 7 new tests: 4 cross-assembly edge extraction (type ref, method call, BCL filter, backward compat), 2 cross-assembly graph integration, 1 cross-assembly FindReferences.
- `.gitattributes` for consistent line endings.
- `global.json` to pin .NET 8 SDK.
- Silent test skips replaced with explicit `Skip` output.
- Bare catches narrowed to typed exceptions in tests and Unity bridge.

## [0.4.1] - 2026-04-08

### Fixed

- **Unity Editor bridge ReadLine hangs**: `LifebloodBridge` sidecar process could hang indefinitely on stalled ReadLine calls with no timeout or liveness check. Every `ReadLine` now has a bounded wait plus process-liveness monitoring, so a stuck child cannot silently freeze the bridge.

## [0.4.0] - 2026-04-08

Release hygiene pass + per-module progress + large-project modes.

### Added

- **Per-module progress reporting**: `lifeblood_analyze` emits progress marks per module via `IProgressSink`, so agents see which module is currently compiling during long analyses.
- **Read-only streaming mode**: `lifeblood_analyze readOnly=true` uses streaming compilation (lower memory, ~4 GB vs ~7 GB on large projects). Write-side tools are unavailable in this mode; intended for large-project read-only analysis.
- **Server GC tuning**: `<ServerGarbageCollection>true</ServerGarbageCollection>` in the build props for better steady-state behavior on long MCP sessions.
- **MinVer auto-versioning**: version derived from git tags by MinVer at build time. No manual version bumping. Tag and push.

### Fixed

- **Unity csproj filesystem scan hang**: when an old-format csproj declared `<Compile Include>` items, the discoverer still performed a recursive filesystem scan of the project directory, which could take minutes on a 75-module Unity workspace. Now trusts the csproj `<Compile>` items and skips the recursive scan when they are present.
- **Discovery merge**: filesystem scan now merges with csproj `<Compile Include>` items instead of choosing one or the other, so hybrid csproj layouts (some items declared, others implicit) produce the correct file set.
- **Release hygiene**: bare `catch {}` narrowed to typed exceptions across test infrastructure and Unity bridge, silent test skips replaced with explicit `Skip` output.

## [0.3.0] - 2026-04-08

Incremental re-analyze, file-level impact, Unity bridge, built-in rule packs.

### Added

- **Incremental re-analyze**: `lifeblood_analyze` with `incremental: true` only recompiles modules with changed files. Seconds instead of minutes. Falls back to full analysis if no previous snapshot exists or if modules were added/removed.
- **File-level edge derivation**: `GraphBuilder.Build()` derives `file:X → file:Y References` edges from symbol-level edges with `edgeCount` property. Evidence: Inferred.
- **lifeblood_file_impact tool**: "If I change this file, what other files break?". Derived from file-level edges.
- **Unity Editor bridge**: `unity/Editor/LifebloodBridge/`. Sidecar MCP server auto-discovered via `[McpForUnityTool]`. All 17 tools available in Unity Editor.
- **Built-in rule packs**: `hexagonal`, `clean-architecture`, `lifeblood`. Resolve by name (`--rules hexagonal`).
- **MCP setup docs**: copy-paste configs for Claude Code, Cursor, VS Code, Claude Desktop, Unity.
- **Editorconfig**: `.editorconfig` with C# conventions.
- **Graceful shutdown**: Ctrl+C / SIGTERM handling in MCP server.
- **ProcessIsolatedCodeExecutor tests**: integration tests with build guard + timeout kill test.
- **10 regression tests**: security scanner (constructors, expression compile), edge deduplication, streaming downgrade.

### Fixed

- **ProcessIsolatedCodeExecutor path with spaces**: quoted project path in dotnet CLI arguments.
- **CI golden repo restore**: WriteSideIntegrationTests skip gracefully when golden repo unavailable.

### Changed

- Tool count: 12 → 17 (7 read + 10 write).
- Self-analysis: 878 symbols → 1148 symbols, 2139 edges → 3196 edges.
- Tests: 214 → 281.
- Architecture screenshot regenerated.

## [0.2.2] - 2026-04-08

### Added

- **75-module Unity workspace production verification**: 43,800 symbols, 70,600 edges, 75 modules, 2,404 types, 34 cycles, around 4 GB peak.

## [0.2.1] - 2026-04-08

### Fixed

- **Edge deduplication in GraphBuilder**: partial classes caused duplicate Overrides/Inherits/Implements edges. `Build()` now deduplicates all edges by `(sourceId, targetId, kind)`.
- **Unity csproj support**: detect `<Compile Include>` items in old-format csproj. If present, use those instead of filesystem scan. Prevents 75-project recursive scan hang.

## [0.2.0] - 2026-04-08

Bidirectional Roslyn, streaming compilation, 45-pass hardening, Python adapter.

### Added

- **Bidirectional Roslyn**: 10 write-side MCP tools. Execute, diagnose, compile-check, find references, find definition, find implementations, symbol at position, documentation, rename, format.
- **Streaming compilation with downgrading**: compile one module at a time in topological order, then `Emit()` → `MetadataReference.CreateFromImage()`. Memory: 32GB → ~4GB for 75-module project.
- **SharedMetadataReferenceCache**: NuGet MetadataReferences deduplicated across modules.
- **NuGet package resolution**: compilations resolve packages from `obj/project.assets.json`.
- **Domain result types**: DiagnosticInfo, CompileCheckResult, CodeExecutionResult, ReferenceLocation, TextEdit.
- **3 new port interfaces**: ICodeExecutor, ICompilationHost, IWorkspaceRefactoring.
- **3 Roslyn implementations**: RoslynCodeExecutor, RoslynCompilationHost, RoslynWorkspaceRefactoring.
- **Compilation preservation**: `AnalysisConfig.RetainCompilations` controls mode (streaming CLI vs retained MCP).
- **Cross-module Roslyn resolution**: compilations built in dependency order via topological sort.
- **Python adapter**: standalone `ast`-based analyzer. Zero dependencies. Self-analyzing.
- **IFileSystem wired**: expanded interface, `PhysicalFileSystem` implementation, injected everywhere.
- **IRuleProvider wired**: `RulesLoader` converted from static class.
- **IProgressSink wired**: `ConsoleProgressSink` writes to stderr.
- **Use case tests**: 8 tests for AnalyzeWorkspaceUseCase and GenerateContextUseCase.
- **Edge extraction tests**: generics, typeof, attributes, return types, C# 9 target-typed new.
- **Golden repo tests**: mini-app fixtures for TypeScript and Python adapters.
- **Dotnet tool packaging**: `Directory.Build.props`, CLI as `lifeblood`, MCP Server as `lifeblood-mcp`.
- **CHANGELOG.md**: version history.

### Fixed

- **Native DLL metadata error**: `LoadBclReferences` filters non-.NET DLLs via `PEReader.HasMetadata`.
- **Session corruption on failed analyze**: `GraphSession.Load` commits state atomically.
- **Symbol resolution from wrong compilation**: FindReferences and Rename resolve from workspace projects.
- **CompileCheck false negatives**: pre-existing diagnostics filtered out.
- **Timeout bypass**: `Task.Run` + `Task.Wait` for hard timeout enforcement.
- **Notification null leak**: `Program.cs` no longer serializes `null` for notifications.
- **RS1024 warning**: Roslyn symbol comparisons use `SymbolEqualityComparer.Default`.
- **Return type edges**: fixed MethodDeclarationSyntax filter that blocked return type references.

### Changed

- Port count: 10 → 14 (3 write-side + 1 blast radius).
- Tests: 109 → 241.
- Self-analysis: 634 symbols → 1057 edges, 888 edges → 2594.
- CI: 4 parallel jobs (build, TypeScript adapter, Python adapter, dogfood).
- Build: 0 warnings, 0 errors.

## [0.1.1] - 2026-04-08

Deep hardening pass. 10 bugs fixed, architecture granulated, AST security scanner, 180 tests.

### Fixed

- **Property symbol ID collision**: `SymbolIds.Property` generated `field:` prefix. Now uses `property:` prefix.
- **GetDiagnostics silent fallback**: non-existent module returned ALL diagnostics. Now returns empty.
- **File.Exists bypassing IFileSystem port**: NuGet resolver used raw `File.Exists`.
- **Property accessor dangling edges**: edge extractor used `get_X`/`set_X` with no matching symbol.
- **MCP parse error response**: malformed JSON-RPC caused no response. Now returns -32700.
- **Notification null leak**: `Dispatch()` returned `null!` for notifications. Now typed as `JsonRpcResponse?`.
- **NuGet catch-all too broad**: bare `catch {}` narrowed to typed exceptions.
- **BCL loader bare catches**: narrowed to typed exceptions.

### Added

- **ScriptSecurityScanner**: Roslyn AST-based security layer with two-layer defense.
- **RoslynWorkspaceManager**: shared workspace infrastructure (~80 LOC dedup).
- **BclReferenceLoader, NuGetReferenceResolver, ModuleCompilationBuilder**: extracted internal components.
- **WriteToolHandler**: extracted 6 write-side MCP handlers.
- **AnalysisPipeline**: moved to Analysis assembly. Single source of truth.
- **59 new tests**: write-side tools, AST security, symbol ID parsing, pipeline, architecture.

### Changed

- Tests: 121 → 180.
- Source files: 58 → 63.
- Average LOC/file: 84 → 80.
- Zero bare catches remaining in source.

## [0.1.0] - 2026-04-07

First public release. Framework is dogfood-verified and CI-green.

### Added

- **Domain**: immutable semantic graph model (Symbol, Edge, Evidence, ConfidenceLevel, GraphBuilder, GraphValidator, GraphDocument).
- **Application**: 10 port interfaces, AnalyzeWorkspaceUseCase, GenerateContextUseCase.
- **Adapters.CSharp**: Roslyn-based workspace analyzer, module discovery, symbol/edge extractors.
- **Adapters.JsonGraph**: universal JSON import/export with full metadata round-trip.
- **Analysis**: CouplingAnalyzer, BlastRadiusAnalyzer, CircularDependencyDetector, TierClassifier, RuleValidator.
- **Connectors.ContextPack**: AI context pack generator, instruction file generator, reading order.
- **Connectors.Mcp**: MCP graph provider with blast radius delegation.
- **Server.Mcp**: MCP server with 6 tools over stdio (JSON-RPC 2.0).
- **CLI**: `analyze`, `context`, `export` commands with centralized validation and exit codes.
- **TypeScript adapter**: standalone Node.js adapter using `ts.createProgram` + TypeChecker.
- **Rule packs**: hexagonal, clean-architecture, lifeblood (self-validating).
- **JSON schemas**: `graph.schema.json`, `rules.schema.json`.
- **Tests**: 109 xUnit tests.
- **CI**: 3 parallel jobs (build, TypeScript adapter, dogfood with cross-language proof).
- **Adapter contribution guides**: Go, Python, Rust (contract and checklist, no implementation code).
- **Documentation**: architecture docs, 11 frozen ADRs, adapter guide, dogfood findings, CLAUDE.md.

[Unreleased]: https://github.com/user-hash/Lifeblood/compare/v0.7.11...HEAD
[0.7.11]: https://github.com/user-hash/Lifeblood/compare/v0.7.10...v0.7.11
[0.7.10]: https://github.com/user-hash/Lifeblood/compare/v0.7.9...v0.7.10
[0.7.9]: https://github.com/user-hash/Lifeblood/compare/v0.7.8...v0.7.9
[0.7.8]: https://github.com/user-hash/Lifeblood/compare/v0.7.7...v0.7.8
[0.7.7]: https://github.com/user-hash/Lifeblood/compare/v0.7.6...v0.7.7
[0.7.6]: https://github.com/user-hash/Lifeblood/compare/v0.7.5...v0.7.6
[0.7.5]: https://github.com/user-hash/Lifeblood/compare/v0.7.4...v0.7.5
[0.7.4]: https://github.com/user-hash/Lifeblood/compare/v0.7.3...v0.7.4
[0.7.3]: https://github.com/user-hash/Lifeblood/compare/v0.7.2...v0.7.3
[0.7.2]: https://github.com/user-hash/Lifeblood/compare/v0.7.1...v0.7.2
[0.7.1]: https://github.com/user-hash/Lifeblood/compare/v0.7.0...v0.7.1
[0.7.0]: https://github.com/user-hash/Lifeblood/compare/v0.6.7...v0.7.0
[0.6.7]: https://github.com/user-hash/Lifeblood/compare/v0.6.5...v0.6.7
[0.6.5]: https://github.com/user-hash/Lifeblood/compare/v0.6.4...v0.6.5
[0.6.4]: https://github.com/user-hash/Lifeblood/compare/v0.6.3...v0.6.4
[0.6.3]: https://github.com/user-hash/Lifeblood/compare/v0.6.1...v0.6.3
[0.6.1]: https://github.com/user-hash/Lifeblood/compare/v0.6.0...v0.6.1
[0.6.0]: https://github.com/user-hash/Lifeblood/compare/v0.5.1...v0.6.0
[0.5.1]: https://github.com/user-hash/Lifeblood/compare/v0.5.0...v0.5.1
[0.5.0]: https://github.com/user-hash/Lifeblood/compare/v0.4.1...v0.5.0
[0.4.1]: https://github.com/user-hash/Lifeblood/compare/v0.4.0...v0.4.1
[0.4.0]: https://github.com/user-hash/Lifeblood/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/user-hash/Lifeblood/compare/v0.2.2...v0.3.0
[0.2.2]: https://github.com/user-hash/Lifeblood/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/user-hash/Lifeblood/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/user-hash/Lifeblood/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/user-hash/Lifeblood/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/user-hash/Lifeblood/releases/tag/v0.1.0

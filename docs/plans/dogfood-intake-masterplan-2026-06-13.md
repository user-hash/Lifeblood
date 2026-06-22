# Dogfood Intake Execution Masterplan

> ## 📍 CURRENT POSITION (2026-06-22)
> Branch `codex/lifeblood-tracking-complete`, **NOT pushed / NOT tagged** (user owns push+tag).
> 21 branch commits plus current uncommitted local implementation cover the full intake queue through Wave 7.
> Last full-suite receipt before the final five was **1435 passed / 0 failed / 11 native-clang skips / 1446 total**. The focused W6/W7 suite passed 85/85, and the rebuilt local MCP server passed direct stdio behavior smokes. Final full-suite/docs preflight is still required before any public handoff.
> **IMPLEMENTED LOCALLY:** Wave 0; Wave 1 (A grouped dependants/dependencies, B dead_code pathExclude); Wave 2 (`lifeblood_callsite_arguments`, `lifeblood_struct_layout`); Wave 3 (`lifeblood_wire_audit` all 5 passes, `lifeblood_feature_switch_audit` + 3 dogfood fixes + summarize, `lifeblood_member_count`); Wave 4 authority coverage (`lifeblood_authority_coverage`); Wave 5 Unity false-positive/boundary atoms; Wave 6 session recovery + content-hash incremental + execute CS1061 hint + authoritative changed-set; Wave 7 source-generator concurrency isolation; skill parity ratchet.
> **PREFLIGHT PASSED LOCALLY:** focused doc/contract ratchets, full Release suite, direct local MCP smoke against `dist`, and `git diff --check`. No push, tag, NuGet publish, or release cut.
> **Intake: 0 entries remaining (0 partials).**
> **Local dev tool:** standalone `dist/Lifeblood.Server.Mcp.dll` rebuilt from Release publish output and direct-stdio smoke-tested locally. NOT the NuGet release.

**Status:** ADOPTED 2026-06-21 as the canonical execution recipe for the current
`devmemory/lifeblood-intake.md` queue, goal **complete and verify the intake plan locally**. This
document is the point-by-point order for turning intake into shipped, ratcheted
Lifeblood work.

**Re-verification 2026-06-21 (before adoption):** every code anchor in this plan
was re-checked against live source. No drift since v0.7.11 (only 4 doc commits
landed). Confirmed: intake = 19 entries (now 18 after Wave 0); `groupBy` exists
only on `HandleBlastRadius` (dependants/dependencies still flat); `BlastRadiusGroups`
precedent in `LifebloodMcpProvider` + `IMcpGraphProvider`; `RoslynStaticTableExtractor.BuildCells`
binds args via `IArgumentOperation`; `ICompilationHost` is the left-port seam;
all seven proposed new tools (`callsite_arguments`, `wire_audit`,
`authority_coverage`, `struct_layout`, `member_count`, `feature_switch_audit`,
`asmdef_check`) are un-started; `IntakeLedgerTests` was absent.

**Progress:**
- **Wave 0 — IMPLEMENTED LOCALLY 2026-06-21.** `IntakeLedgerTests` + `INV-INTAKE-SHAPE-001`
  landed; nine older intake entries backfilled with `Source:`/`Workspace:`;
  `LB-INTAKE-20260613-005` promoted to the archive; STATUS.md anchors refreshed;
  CHANGELOG `[Unreleased]` updated. Ledger + Docs tests green.
- **Wave 1 (part A) — IMPLEMENTED LOCALLY 2026-06-21.** Grouped/filtered `dependants` /
  `dependencies` (`groupBy` / `excludeTests` / `excludeGenerated` /
  `includeBuckets` / `previewPerGroup`) via new `IMcpGraphProvider.ClassifyEdges`
  + `INV-EDGE-GROUP-001`, sharing the blast-radius bucket/module SSoT.
  `LB-INTAKE-20260613-003` promoted. `EdgeGroupingTests` + full suite green
  (1337).
- **Wave 1 (part B) — IMPLEMENTED LOCALLY 2026-06-21.** `lifeblood_dead_code pathExclude`
  glob filter (`INV-DEADCODE-TRIAGE-003`) — first half of `LB-INTAKE-20260601-004`;
  that intake entry shrank to its remaining halves (`analyze` excludePaths + a
  first-class `Vendored` bucket) which land in Wave 5. Full suite green (1339).
  **Wave 1 complete.**
- **Wave 2 (atom 1) — IMPLEMENTED LOCALLY 2026-06-21.** New write-side tool
  `lifeblood_callsite_arguments` (`INV-CALLSITE-ARGS-001`) — `ICompilationHost.GetCallsiteArguments`
  + `RoslynCallsiteArgumentExtractor` + shared `RoslynArgumentBinding` (default-
  value re-sourcing extracted from the static-table cell binder). Per-site arg
  facts + supplied/omitted histogram. `LB-INTAKE-20260613-001` promoted; tool
  count 31→32. Full suite green (1344). **Wave 2 remaining atoms now implemented locally:**
  `lifeblood_member_count` (`LB-INTAKE-20260611-001`) + `lifeblood_struct_layout`
  (`LB-INTAKE-20260601-002`).
- **Wave 3 (MVP) — IMPLEMENTED LOCALLY 2026-06-21.** New write-side tool `lifeblood_wire_audit`
  (`INV-WIRE-AUDIT-001`) — `ICompilationHost.GetWireAudit` + `RoslynWireAuditExtractor`,
  one operation-tree read/write classification pass. Passes (a) field-read-without-write
  + (b) delegate-slot-never-assigned shipped; `LB-INTAKE-20260611-004` shrank to
  remaining passes (c) events + (d) degenerate-args. Tool 32→33, 165 invariants.
  Live-dogfooded after reload. Full suite green (1350). **Remaining Wave 3:**
  `lifeblood_feature_switch_audit` (`LB-INTAKE-20260613-002`) + wire_audit c/d.
- **Wave 4 — IMPLEMENTED LOCALLY 2026-06-22.** New read-side graph-only tool
  `lifeblood_authority_coverage` (`INV-AUTHORITY-COVERAGE-001`) — subject vs
  authority reachability matrix with type/file expansion, required-authority
  expansion, shortest path previews, allowed-alternative evidence, and shared
  bucket filters. `LB-INTAKE-20260613-004` promoted.
- **Wave 5 (atom 1) - IMPLEMENTED LOCALLY 2026-06-22.** Unity magic-method reachability now
  consumes the Roslyn-resolved `baseTypeChain`, so DAWG-style
  `Graphic -> UIBehaviour -> MonoBehaviour` components do not need a hardcoded
  intermediate subclass roster and `Update` / `Reset` no longer surface as
  dead-code candidates after re-analyze. DAWG read-only dogfood on the reloaded
  local server returned 180 method findings, `truncated:false`, with none of
  the target UI lifecycle methods present. `LB-INTAKE-20260608-001` promoted.
- **Wave 5 (atom 2) - IMPLEMENTED LOCALLY 2026-06-22.** Unity define profiles now include
  `Standalone`, which strips editor discriminators and adds `UNITY_STANDALONE`
  so platform-neutral desktop guarded callsites are present in the semantic
  graph. `LB-INTAKE-20260608-002` promoted.
- **Wave 5 (atom 3) - IMPLEMENTED LOCALLY 2026-06-22.** New read-side graph-only tool
  `lifeblood_asmdef_check` (`INV-ASMDEF-CHECK-001`) audits DirectOnly module
  compile-direction boundaries from module `DependsOn` edges and first offending
  cross-module source edges. Reloaded `dist` DAWG dogfood checked 42,867
  cross-module source edges across 94 DirectOnly modules and found 0 violations.
  `LB-INTAKE-20260601-003` promoted.
- **Wave 5 (atom 4) - IMPLEMENTED LOCALLY 2026-06-22.** `lifeblood_dead_code` now downranks
  intentional reference-free scaffolding as `bucket:"Scaffolding"`: non-public
  static types whose direct members are exclusively `[Conditional]` methods
  and/or static const string anchors, plus those direct members. Shared path
  buckets remain unchanged. `LB-INTAKE-20260608-003` promoted.
- **Wave 5 (atom 5) - IMPLEMENTED LOCALLY 2026-06-22.** Unity asset reachability now scans
  `.prefab` / `.unity` / `.asset` YAML, resolves `m_Script` GUIDs through
  `.cs.meta`, and marks resolved UnityEvent persistent-call method targets plus
  host types reachable. Closed `LB-INTAKE-20260601-001` for UnityEvent method
  reachability; serialized enum production remains a documented advisory
  boundary.
- **Wave 5 (atom 6) - IMPLEMENTED LOCALLY 2026-06-22.** `lifeblood_analyze excludePaths`
  excludes project-relative POSIX glob matches before Roslyn compilation, with
  `analysisScopeChanged` incremental fallback on scope drift. The shared path
  classifier now includes `Vendored` (`Generated > Vendored > Test > Editor >
  Production`) and all bucket-aware wire docs include it. Closed the remaining
  halves of `LB-INTAKE-20260601-004`.
- **Wave 6 - IMPLEMENTED LOCALLY 2026-06-22.** Read-only recovery now returns
  `fallbackReason:"compilationStateUnavailable"` with exact restore guidance;
  content-hash incremental distinguishes mtime touches from real content changes;
  `lifeblood_analyze authoritativeChangedFiles` bounds editor-supplied changed
  sets; `lifeblood_execute` CS1061 diagnostics name public scripting-surface
  members and point at `Help`.
- **Wave 7 - IMPLEMENTED LOCALLY 2026-06-22.** `SourceGeneratorRunner` serializes
  framework analyzer loading and generator-driver execution at the process-local
  runner boundary, with deterministic concurrent-analysis coverage.

**Current truth snapshot (2026-06-22):**

- Lifeblood self-analyze on this repo is clean on rules: 0 violations, 1 existing cycle.
  Symbol/edge totals are intentionally not plan gates; `docs/STATUS.md` owns
  ratcheted repository counts.
- Current intake queue: 0 entries remain after Wave 6/7 local implementation; no partial
  intake entries remain.
- Fresh DAWG-derived planning-friction entries: `LB-INTAKE-20260613-001` through
  `LB-INTAKE-20260613-004`.
- Fresh Lifeblood self-audit entry: `LB-INTAKE-20260613-005`.
- Verified code anchors:
  - `TrackingLedgerTests` machine-checks `devmemory/lifeblood-tracking.md`, not
    `devmemory/lifeblood-intake.md`.
  - `ToolHandler` is the current MCP dispatch hotspot; `lifeblood_dependencies`
    and `lifeblood_dependants` are thin wrappers over `IMcpGraphProvider`.
  - `lifeblood_blast_radius` already has grouped bucket/module precedent through
    `BlastRadiusGroups`, `LifebloodMcpProvider`, and `PathBucketClassifier`.
  - `RoslynStaticTableExtractor.BuildCells(...)` already proves named,
    positional, optional, and default-value argument binding through
    `IArgumentOperation`.
  - `ICompilationHost` is the left-port extension point for Roslyn-backed tools
    that need retained compilation state.

## Execution Rules

1. Work one atom at a time. A finished atom either ships an intake entry, or it
   promotes a partially shipped entry into `lifeblood-tracking.md` with a
   concrete `Remaining open work:` line.
2. Never park plain `Open` or `Candidate` entries in `lifeblood-tracking.md`;
   that ledger is intentionally ratcheted against that drift.
3. Every new MCP tool updates all wire surfaces in the same atom:
   `ToolRegistry`, `ToolInputContractCatalog`, handler dispatch, tests, schema
   snapshot if applicable, docs/status anchors if tool counts change.
4. Keep `ToolHandler` thin. New algorithms live in `Lifeblood.Analysis`,
   `Lifeblood.Connectors.Mcp`, or `Lifeblood.Adapters.CSharp` depending on the
   dependency boundary. Dispatch code only binds arguments and shapes the wire.
5. Roslyn/Unity-specific logic stays out of `Domain` and `Application`.
   Result DTOs in `Domain.Results` are allowed when they are protocol-neutral.
6. Every shipped feature gets a small synthetic fixture test and one dogfood
   receipt against the real DAWG shape that motivated it.

## Wave 0 - Backlog Quality Gate

**Goal:** Make the intake queue safe before adding more planning debt.

**Covers:** `LB-INTAKE-20260613-005`.

Recipe:

1. Add `IntakeLedgerTests` beside `TrackingLedgerTests`.
2. Parse `devmemory/lifeblood-intake.md` outside fenced code blocks.
3. Assert every level-2 intake heading matches `LB-INTAKE-\d{8}-\d{3}`.
4. Assert intake IDs are unique.
5. Assert required metadata exists: `Type:`, `Priority`, `Source:`,
   `Workspace:`.
6. Assert every entry has `What:`, `Why it matters:`, and `Fix shape:`.
7. Assert no intake ID appears in both intake and tracking after promotion.
8. Add a short governance invariant for the intake shape only if the tests need
   a named contract; do not turn intake into a status ledger.

Done when:

- `dotnet test Lifeblood.sln --filter IntakeLedgerTests --no-restore` passes.
- `dotnet test Lifeblood.sln --filter TrackingLedgerTests --no-restore` still
  passes.
- The current 19-entry intake file parses cleanly.

## Wave 1 - Shared Triage Grouping And Path Control

**Goal:** Give agents immediate production/test/editor/generated shape without
manual caller classification.

**Covers:** `LB-INTAKE-20260613-003`, first half of
`LB-INTAKE-20260601-004`.

Recipe:

1. Extract a reusable edge-grouping shape parallel to `BlastRadiusGroups`.
   Prefer a provider/helper in `Lifeblood.Connectors.Mcp`; avoid putting
   grouping loops directly in `ToolHandler`.
2. Extend `lifeblood_dependencies` and `lifeblood_dependants` with optional:
   `groupBy`, `excludeTests`, `excludeGenerated`, `includeBuckets`, and
   `previewPerGroup`.
3. Reuse `PathBucketClassifier` exactly. Do not invent a second classifier.
4. Preserve the existing flat response by default.
5. Add tests mirroring `BlastRadiusGroupingTests`:
   production/test split, module split, preview cap, zero-preview, and legacy
   flat-shape compatibility.
6. Add `pathExclude` to `lifeblood_dead_code` before changing bucket enums.
   This solves vendored triage without forcing a breaking `Vendored` bucket yet.
7. Defer analyze-time `excludePaths` until the dead-code path filter proves the
   user-facing shape.

Done when:

- A DAWG caller-list query such as
  `lifeblood_dependants(GetPatternCharacter, groupBy:"bucket")` shows test-only
  authority without manual source classification.
- Existing `blast_radius groupBy` behavior remains byte-compatible.
- Tool schema and registry tests pass.

## Wave 2 - Roslyn Call-Site Facts And Small Semantic Probes

**Goal:** Surface facts that already exist in Roslyn but are currently hidden
behind manual source reads or Unity test runs.

**Covers:** `LB-INTAKE-20260613-001`, `LB-INTAKE-20260601-002`,
`LB-INTAKE-20260611-001`.

Recipe:

1. Split the argument/default-value classification logic out of
   `RoslynStaticTableExtractor.BuildCells(...)` into a shared internal helper.
   It must keep the current default-argument provenance behavior.
2. Add Domain result DTOs for a call-site argument report:
   callee id, caller id, source span, parameter name/type/ordinal, argument
   kind, supplied-vs-omitted flag, classified value, raw text, and histograms.
3. Add `ICompilationHost.GetCallsiteArguments(...)` and implement it in
   `RoslynCompilationHost` through a new operation-tree extractor.
4. Expose MCP as `lifeblood_callsite_arguments`. This is write-side /
   compilation-state dependent, like `static_tables` and
   `assignment_coverage`.
5. Test constructor calls, method calls, overloads, named args, optional args,
   params arrays, metadata-only defaults, and default enum/member references.
6. Add `lifeblood_member_count(typeId, semantics)` after call-site facts, not
   before. It should reproduce reflection-declared counting without loading the
   workspace assembly.
7. Add `lifeblood_struct_layout(typeId)` as a separate atom. Start exact for
   blittable/sequential/explicit unmanaged structs; report confidence drops for
   reference-bearing or `LayoutKind.Auto` shapes.

Done when:

- DAWG's `GeneratedNote` constructor call sites show `lengthSteps` omitted in a
  single tool call.
- Static table tests still prove default-argument provenance.
- Member-count and struct-layout tools carry explicit semantics/confidence so
  they do not masquerade as runtime reflection when they are source-derived.

## Wave 3 - Dead-Wire And Dormant-Feature Audits

**Goal:** Catch code that has references but is structurally unplugged.

**Covers:** `LB-INTAKE-20260611-004`, `LB-INTAKE-20260613-002`.

Recipe:

1. Build `lifeblood_wire_audit` as a multi-pass read-side report, not one huge
   verdict. Start with the cheapest and most proven checks:
   private/internal fields with reads and zero writes, and delegate slots with
   zero assignment sites.
2. Reuse `lifeblood_assignment_coverage` logic for binding-slot evidence where
   possible; do not duplicate construction-site analysis.
3. Add the event pass only after field/slot tests are stable:
   events with subscribers and zero fire sites, and events with fire sites but
   zero subscribers.
4. Add degenerate-argument-only call-site detection using Wave 2 call-site
   facts. Keep the default rule narrow: constants such as zero/null/empty that
   are supplied by every production caller.
5. Build `lifeblood_feature_switch_audit` as the same family, focused on bool
   fields/properties used in branch conditions. Report initializer/default,
   assignment sites, mutator dependants, and branch-gated methods.
6. Use verdict labels only as evidence summaries: `AlwaysDefaultInGraph`,
   `TestOnlyActivation`, `RuntimeMutable`. Each label must point at the facts
   that produced it.

Done when:

- Synthetic tests reproduce all four DAWG dead-wire shapes from the intake.
- `UseGrammarGeneration`-style dormant switches are explainable without reading
  three separate Lifeblood outputs plus source.
- The response remains advisory where reflection, Unity serialization, or
  runtime assignment could affect the answer.

## Wave 4 - Authority Coverage Matrix

**Goal:** Detect missing dependencies on the intended source of truth.

**Covers:** `LB-INTAKE-20260613-004`.

Recipe:

1. Implement a graph-only `AuthorityCoverageAnalyzer` in `Lifeblood.Analysis`.
2. Inputs: `subjects[]`, `requiredAuthority[]`, optional
   `allowedAlternatives[]`, max depth, bucket filters.
3. Expand subject types/files into contained methods before walking.
4. Walk outgoing non-Contains edges from each subject and record the first
   path to each authority symbol.
5. Report a matrix: subject, reached authorities, missing authorities,
   shortest path preview, and first competing authority actually used.
6. Expose MCP as `lifeblood_authority_coverage`.
7. Add tests for direct reach, transitive reach, missing authority, allowed
   alternative, and file/type expansion.

Done when:

- A generator-method family can be checked against `BeatGridState.InstrumentPresets`
  without manual dependency/source-read composition.
- The tool reports evidence, not architectural judgment.

## Wave 5 - Unity False-Positive Reduction And Unity Boundaries

**Goal:** Remove the highest-risk Unity-specific dead-code false positives and
pre-Unity boundary blind spots.

**Covers:** `LB-INTAKE-20260601-001`, `LB-INTAKE-20260601-003`,
`LB-INTAKE-20260608-001`, `LB-INTAKE-20260608-002`,
`LB-INTAKE-20260608-003`, second half of `LB-INTAKE-20260601-004`.

Recipe:

1. First fix transitive Unity magic reachability:
   `MonoBehaviour` magic-method detection must walk the full base chain, so
   `UIBehaviour` / `Graphic` subclasses are covered.
2. DONE 2026-06-22: add the `Standalone` define profile before adding source
   text heuristics. The real bug was missing active preprocessor symbols for
   `UNITY_STANDALONE && !UNITY_EDITOR`.
3. Add an inactive-define hint only after profile coverage exists. It should
   say "nearest references exist only under unanalyzed defines" and include the
   relevant define expression.
4. DONE 2026-06-22: add scaffolding downrank for all-`Conditional` / const-anchor-only internal
   types. Keep it a bucket/downrank, not a deletion recommendation.
5. DONE 2026-06-22: implement asmdef compile-direction checking as a static graph/asmdef query:
   for every cross-module edge, compare the source asmdef's declared references
   to the target asmdef. Report the first offending call site.
6. DONE 2026-06-22: add Unity serialized/YAML reachability behind a port that can
   parse `.prefab`, `.unity`, and `.asset` files, resolve script GUIDs through
   `.meta`, and feed UnityEvent method targets as reachability roots. Serialized
   enum production remains advisory/deferred.
7. DONE 2026-06-22: add vendored/sample path control in two layers: `analyze`
   `excludePaths` before compilation, plus a first-class `Vendored` bucket with
   bucket parity tests and wire docs updated.

Done when:

- `dead_code` no longer flags `Graphic`-derived `Update` / `Reset` methods as
  dead.
- Desktop-only guarded calls are visible under a documented profile.
- UnityEvent-wired methods no longer look identical to genuinely dead methods.
- asmdef violations can be caught before Unity compile.
- Vendored/sample code can be excluded from analyze scope or folded out via a
  first-class bucket.

## Wave 6 - Session Recovery, Incremental Reliability, And Execute UX

**Goal:** Make long DAWG-scale sessions recoverable and cheaper after Unity
reloads.

**Covers:** `LB-INTAKE-20260602-001`, `LB-INTAKE-20260611-002`,
`LB-INTAKE-20260611-003`, `LB-INTAKE-20260611-005`.

Recipe:

1. Make read-only recovery explicit:
   write-side tool errors must include `hasCompilationState` and the exact
   analyze call needed to restore it.
2. Reject unsafe non-read-only incremental recovery from a read-only session
   with a structured `canRetryFull` suggestion if full compilation state is
   required.
3. Add a capabilities/session-state field naming read/write availability.
4. Improve `lifeblood_execute` CS1061 failures for known scripting API types:
   append actual public member names or a direct `Help` pointer.
5. Switch incremental change detection from mtime-only to content hash or
   size-plus-hash. Receipts should distinguish `mtimeTouched` from
   `contentChanged`.
6. Only after hash-based incremental works, add a Unity-side sync hook or MCP
   custom tool that posts compilation-finished plus changed source list to the
   Lifeblood server.

Done when:

- A read-only analyze cannot trap the session in a confusing half-write state.
- Unity domain reloads no longer turn small edits into a whole-workspace rebuild.
- Execute API-shape mistakes require one fewer round-trip.

## Wave 7 - Deferred Runtime/TFM Hardening

**Goal:** Keep latent platform risk visible without stealing focus from daily
DAWG value.

**Covers:** `LB-INTAKE-20260601-005`.

Recipe:

1. Keep net10 source-generator concurrency isolation deferred until net10 is an
   active target or concurrent in-process analysis becomes production.
2. When opened, isolate framework analyzer loading through
   `AssemblyLoadContext` or serialize generator-driver execution.
3. Scope doc self-analyze anchors to the production TFM before comparing net10
   results to net8 anchors.

Done when:

- net8 counts remain stable.
- net10 full-suite source-generator tests stop racing without perturbing net8.

## Global Validation Recipe

Run this after every implementation atom:

1. Focused unit tests for the changed analyzer/tool.
2. `dotnet test Lifeblood.sln --no-restore` unless the atom clearly requires a
   narrower, explicitly named filter during iteration.
3. `lifeblood_analyze(projectPath:"D:/Projekti/lifeblood", rulesPath:"lifeblood",
   incremental:true)` and confirm 0 violations, 0 cycles.
4. MCP dogfood against the real DAWG shape that motivated the item.
5. Update `devmemory/lifeblood-tracking.md` only when there is shipped or
   partially shipped evidence; remove or shrink the corresponding intake entry.
6. `git diff --check` before handoff.

## Recommended First Sprint

Start with the smallest work that reduces future mistakes:

1. Wave 0: `IntakeLedgerTests`.
2. Wave 1: grouped `dependants` / `dependencies`.
3. Wave 2: `lifeblood_callsite_arguments`.
4. Wave 3 MVP: field read-without-write plus delegate slot zero-assignment.
5. DAWG dogfood checkpoint: re-run the pattern, ABG, and dead-wire examples
   before deciding whether to continue into feature-switch and authority tools.

This order is deliberately boring: protect the queue, improve triage visibility,
surface hidden Roslyn facts, then build higher-level audits out of those facts.

---

# 🔁 SESSION HANDOFF — resume here (last updated 2026-06-22, branch `codex/lifeblood-tracking-complete`)

Single combined source of truth for resuming the campaign in a fresh session.
Read top-to-bottom before touching anything.

## Where we are
- **Repo** `D:/Projekti/Lifeblood`. **Branch** `codex/lifeblood-tracking-complete`,
  local worktree contains the full intake implementation through W7. **`main` is
  untouched**; do NOT push or tag mid-plan. User owns any push + tag.
- **Goal** burn `devmemory/lifeblood-intake.md` down to locally implemented, ratcheted features
  with local verification. No push, tag, NuGet publish, or release cut in this run.
- **Live state** 38 MCP tools (20 read + 18 write), 30 ports. Final local
  preflight on 2026-06-22 is green: focused doc/contract ratchets 146/146,
  full Release suite **1441 passed / 0 failed / 11 native-clang skips / 1452 total**,
  `git diff --check`, and direct local MCP smoke against `dist`.
- **Local dev MCP tool** = standalone server at `dist/Lifeblood.Server.Mcp.dll`.
  Reload recipe for this branch: local Release staging build → stop
  `lifeblood-mcp.exe` / `*Lifeblood.Server.Mcp*` processes → copy staging into
  `dist/` via `redeploy-watcher.ps1` → reconnect the MCP client. No push, tag, or
  external package publication is part of the reload.

## Implemented this campaign (18 commits plus current local worktree)
- W0 `IntakeLedgerTests` + `INV-INTAKE-SHAPE-001` (`95d5d11`)
- W1A grouped/filtered `dependants`/`dependencies` + `IMcpGraphProvider.ClassifyEdges`
  + `INV-EDGE-GROUP-001` (`23969f7`)
- W1B `dead_code pathExclude` glob + `INV-DEADCODE-TRIAGE-003` (`a34e79e`)
- W2.1 tool `lifeblood_callsite_arguments` + `INV-CALLSITE-ARGS-001` (`e538e1d`)
- live-dogfood fixes: groupBy omits flat array; callsite rawText from source default
  (`da7f3f7`)
- W3-MVP tool `lifeblood_wire_audit` passes a+b + `INV-WIRE-AUDIT-001` (`a24af3f`)
- W3 tool `lifeblood_feature_switch_audit` + `INV-FEATURE-SWITCH-001` (`02465c8`) —
  shared `RoslynOperationFacts` primitive extracted; wire_audit refactored onto it.
  Closes `LB-INTAKE-20260613-002`. **LIVE-DOGFOOD VERIFIED** (`_stopped` → RuntimeMutable).
- Public skill ↔ tool-surface parity ratchet `INV-SKILL-TOOL-PARITY-001` (`d5b9566`).
- feature_switch dogfood fix: dispatch-alias call sites (interface/override/using) —
  caught `_stopped`/`_disposed` false-dormant on the real graph (`5b25306`).
- `summarize` on wire_audit + feature_switch_audit (`INV-LIST-SHAPE-UNIFORM-001`,
  `05fbb87`) — feature_switch summarize is a verdict CENSUS (drops evidence arrays;
  a count cap alone was still 104 KB). Both tools in `UniformListShapeRatchetTests`.
- feature_switch dogfood fix: positional-record properties (`ed0d54e`) — parameter
  default + constructor-arg writes; `DeadCodeOptions.ExcludePublic` was `false`/dormant,
  now `true`/RuntimeMutable. LIVE-VERIFIED.
- W3 `lifeblood_wire_audit` passes c+d (`850897e`) — `EventSubscribedNeverRaised` /
  `EventRaisedNeverSubscribed` / `DegenerateConstantCallSites`. CLOSES `LB-INTAKE-20260611-004`.
- W3 tool `lifeblood_member_count` + `INV-MEMBER-COUNT-001` (`2526c33`) — bit-exact
  reflection parity (emit-reflect-vs-parse harness) + sourceSymbols. CLOSES `LB-INTAKE-20260611-001`.
- W2 tool lifeblood_struct_layout + INV-STRUCT-LAYOUT-001 (local worktree) -
  field offsets/sizes/alignment/pack/total, fixed buffers, Exact vs Advisory confidence;
  emit-vs-compute Marshal.SizeOf/OffsetOf harness. CLOSES LB-INTAKE-20260601-002.
- W4 tool lifeblood_authority_coverage + INV-AUTHORITY-COVERAGE-001 (local worktree) -
  graph-only subject-vs-authority reachability matrix with shortest path previews
  and allowed-alternative evidence. CLOSES LB-INTAKE-20260613-004.
- W5 atom 1 resolved base-chain Unity reachability + INV-UNITY-001 (local worktree) -
  `baseTypeChain` records Roslyn's full base walk, so Unity UI `Graphic` /
  `UIBehaviour` lifecycle methods reach `MonoBehaviour` without hardcoded
  intermediate subclass rosters. CLOSES LB-INTAKE-20260608-001.
- W5 atom 2 Standalone define profile + INV-MULTI-DEFINE-UNITY-RESOLVER-001
  (local worktree) - `UnityDefineProfileResolver` now exposes Editor + Player
  + Standalone. Standalone removes editor discriminators and adds
  `UNITY_STANDALONE`, so `UNITY_STANDALONE && !UNITY_EDITOR` callsites are
  semantically present without source-text heuristics. CLOSES
  LB-INTAKE-20260608-002.
- W5 atom 3 `lifeblood_asmdef_check` + INV-ASMDEF-CHECK-001 (local worktree) -
  graph-only DirectOnly module boundary audit, grouped by source-target module
  pair with first offending edge/call site/profile set and declared dependency
  list. Reloaded `dist` DAWG dogfood: 94 DirectOnly modules, 42,867 checked
  cross-module source edges, 0 asmdef violations. CLOSES
  LB-INTAKE-20260601-003.
- W5 atom 4 dead_code scaffolding downrank + INV-DEADCODE-SCAFFOLDING-001
  (local worktree) - `bucket:"Scaffolding"` for non-public static conditional
  guard / const-string anchor types and their direct members; shared path buckets
  unchanged. CLOSES LB-INTAKE-20260608-003.
- W5 atom 5 UnityEvent YAML reachability + INV-UNITYEVENT-REACHABILITY-001
  (local worktree) - resolved UnityEvent persistent calls from `.prefab` /
  `.unity` / `.asset` YAML now mark target methods and host types reachable.
  CLOSES LB-INTAKE-20260601-001.
- W5 atom 6 analyze `excludePaths` + Vendored path bucket
  (local worktree) - source-scope glob exclusion before Roslyn compilation plus
  shared `Vendored` bucket parity. CLOSES LB-INTAKE-20260601-004.
- W6 read-only recovery/content-hash incremental/execute hints/authoritative
  changed-set (local worktree) - `compilationStateUnavailable`, content-hash
  noop, `authoritativeChangedFiles`, and CS1061 public-member hints. CLOSES
  LB-INTAKE-20260602-001 and LB-INTAKE-20260611-002/003/005.
- W7 source-generator concurrency isolation (local worktree) -
  `SourceGeneratorRunner` serializes framework analyzer loading and generator
  driver execution. CLOSES LB-INTAKE-20260601-005.
- (+ `b16b198` adopt, `f243d2c` position marker)

## NEXT atoms, in order
1. **Final preflight**: full suite, docs/status anchors, direct local MCP smoke,
   and `git diff --check`.
2. **Public handoff remains blocked** until the user explicitly asks for push,
   tag, NuGet publish, or release work.

Partials in intake: none.
**0 intake entries remain.** All intake ids are tombstoned in
`devmemory/lifeblood-intake.md` and recorded as local receipts in
`devmemory/lifeblood-tracking-archive.md`.

## Per-atom DISCIPLINE (non-negotiable — how every atom above was implemented)
1. **Hexagonal:** protocol-neutral DTOs in `Domain.Results`; port on `ICompilationHost`
   (write-side, retained compilations) or `IMcpGraphProvider` (read-side graph);
   algorithm in a `Roslyn*Extractor` (`Adapters.CSharp`) or `Lifeblood.Analysis`
   (graph-only); thin handler dispatch. Extract shared helpers, never duplicate
   (`RoslynArgumentBinding`, `CreateModuleResolver`/`ShapeGroups`). NO hotpatches.
2. **New tool wiring, same atom:** `ToolRegistry.cs` (entry + `EnvelopeClassification`),
   `ToolInputContractCatalog.cs` (args), `ToolHandler.cs` dispatch,
   `WriteToolHandler.cs` handler, new `schemas/tools/v1/<tool>.schema.json`,
   `StubCompilationHost` in `UseCaseTests.cs` (if new `ICompilationHost` member), tests,
   `docs/invariants/tools.md` invariant.
3. **Schema snapshots** authored raw; `ToolSchemaSnapshotTests` canonicalizes both
   sides — only property ORDER + text must match the Arg() order.
4. **Anchor lockstep** (`DocsTests` enforces): update `docs/STATUS.md` hidden anchors +
   visible prose + self-analyze block: `toolCount`, `testCount`, `invariantCount`,
   `invariantCategoryCount`, `selfAnalyze{Symbols,Edges,Types}`, the "(N read + M write)"
   splits, and `docs/MCP_SETUP.md` "18 of the N tools". Novel `INV-FOO` prefix bumps
   count AND category; reused prefix bumps only count. Run full suite, read live
   numbers from the failures, set anchors to those.
5. **Two hardcoded count tests** in `ToolHandlerTests.cs` bump on every tool add
   (`ToolRegistry_ReturnsNTools` + capabilities read/write split).
6. **Tracking SSoT:** implementation completion -> MOVE intake entry to a local
   receipt in `lifeblood-tracking-archive.md` + DELETE from `lifeblood-intake.md`
   (HTML-comment tombstone ok). Partial = SHRINK the intake entry + add receipt. `IntakeLedgerTests`
   forbids an id in both files + requires `Type:`/`Priority:`/`Source:`/`Workspace:` +
   `What:`/`Why it matters:`/`Fix shape:` (keep `Fix shape:` literal). `TrackingLedgerTests`
   keeps the live ledger to Shipped+in-flight only.
7. **CHANGELOG** `[Unreleased]` entry per atom.
8. **Verify:** build -> focused test -> full `dotnet test Lifeblood.sln` green ->
   commit only when requested/appropriate -> repack+reload local tool ->
   live-dogfood the new tool. No push, tag, NuGet publish, or release cut without
   an explicit user instruction.

## Gotchas banked
- Live dogfooding finds real bugs (groupBy hub-overflow, callsite cross-module rawText
  were both caught that way). Reload + dogfood every new tool.
- `groupBy` on edge tools REPLACES the flat array (overflow-safe); filter-only keeps the
  narrowed flat list; legacy stays byte-stable.
- Write-side extractor test harness: `RoslynCompilationHost` test ctor takes
  `Dictionary<string,CSharpCompilation>` (see `CallsiteArgumentExtractorTests` /
  `WireAuditExtractorTests`); canonical method ids resolve via truncated-method
  fallback so `method:NS.T.M` works without full param FQNs.
- `local-nupkg/` is an untracked build artifact — never commit it.

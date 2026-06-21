# Dogfood Intake Execution Masterplan

> ## 📍 CURRENT POSITION (2026-06-21)
> Branch `codex/lifeblood-tracking-complete`, **NOT pushed / NOT tagged** (user owns push+tag).
> 6 commits landed: `b16b198` (adopt) → `95d5d11` (W0) → `23969f7` (W1A) → `a34e79e` (W1B) → `e538e1d` (W2.1).
> Suite **1333 passed / 0 failed / 11 native-clang skips / 1344 total**. **32 tools**, 164 invariants, 0 self-analyze violations.
> **DONE:** Wave 0; Wave 1 (A grouped dependants/dependencies, B dead_code pathExclude); Wave 2 atom 1 (`lifeblood_callsite_arguments`); Wave 3 (`lifeblood_wire_audit` all 5 passes, `lifeblood_feature_switch_audit` + 3 dogfood fixes + summarize, `lifeblood_member_count`); skill ↔ tool parity ratchet. 35 tools, 168 invariants. Closed intake: 20260613-002, 20260611-004, 20260611-001.
> **NEXT:** `lifeblood_feature_switch_audit` (`LB-INTAKE-20260613-002`) + wire_audit passes c/d → then Wave 2 `lifeblood_member_count` (`LB-INTAKE-20260611-001`, needs a reflection-parity harness) + `lifeblood_struct_layout` (`LB-INTAKE-20260601-002`) → Wave 4 `authority_coverage`.
> **Intake: 15 entries remaining (3 partials: 20260601-004, 20260611-004 now partial).**
> **Local dev tool:** global `lifeblood.server.mcp` reinstalled to **0.7.12-alpha.0.9** from `local-nupkg/` (this branch build) for live testing. NOT the published NuGet release.

**Status:** ADOPTED 2026-06-21 as the canonical execution recipe for the current
`devmemory/lifeblood-intake.md` queue, goal **ship intake → v0.7.12**. This
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
- **Wave 0 — SHIPPED 2026-06-21.** `IntakeLedgerTests` + `INV-INTAKE-SHAPE-001`
  landed; nine older intake entries backfilled with `Source:`/`Workspace:`;
  `LB-INTAKE-20260613-005` promoted to the archive; STATUS.md anchors refreshed;
  CHANGELOG `[Unreleased]` updated. Ledger + Docs tests green.
- **Wave 1 (part A) — SHIPPED 2026-06-21.** Grouped/filtered `dependants` /
  `dependencies` (`groupBy` / `excludeTests` / `excludeGenerated` /
  `includeBuckets` / `previewPerGroup`) via new `IMcpGraphProvider.ClassifyEdges`
  + `INV-EDGE-GROUP-001`, sharing the blast-radius bucket/module SSoT.
  `LB-INTAKE-20260613-003` promoted. `EdgeGroupingTests` + full suite green
  (1337).
- **Wave 1 (part B) — SHIPPED 2026-06-21.** `lifeblood_dead_code pathExclude`
  glob filter (`INV-DEADCODE-TRIAGE-003`) — first half of `LB-INTAKE-20260601-004`;
  that intake entry shrank to its remaining halves (`analyze` excludePaths + a
  first-class `Vendored` bucket) which land in Wave 5. Full suite green (1339).
  **Wave 1 complete.**
- **Wave 2 (atom 1) — SHIPPED 2026-06-21.** New write-side tool
  `lifeblood_callsite_arguments` (`INV-CALLSITE-ARGS-001`) — `ICompilationHost.GetCallsiteArguments`
  + `RoslynCallsiteArgumentExtractor` + shared `RoslynArgumentBinding` (default-
  value re-sourcing extracted from the static-table cell binder). Per-site arg
  facts + supplied/omitted histogram. `LB-INTAKE-20260613-001` promoted; tool
  count 31→32. Full suite green (1344). **Remaining in Wave 2:**
  `lifeblood_member_count` (`LB-INTAKE-20260611-001`) + `lifeblood_struct_layout`
  (`LB-INTAKE-20260601-002`).
- **Wave 3 (MVP) — SHIPPED 2026-06-21.** New write-side tool `lifeblood_wire_audit`
  (`INV-WIRE-AUDIT-001`) — `ICompilationHost.GetWireAudit` + `RoslynWireAuditExtractor`,
  one operation-tree read/write classification pass. Passes (a) field-read-without-write
  + (b) delegate-slot-never-assigned shipped; `LB-INTAKE-20260611-004` shrank to
  remaining passes (c) events + (d) degenerate-args. Tool 32→33, 165 invariants.
  Live-dogfooded after reload. Full suite green (1350). **Remaining Wave 3:**
  `lifeblood_feature_switch_audit` (`LB-INTAKE-20260613-002`) + wire_audit c/d.

**Current truth snapshot (2026-06-13):**

- Lifeblood self-analyze on this repo is clean: 0 violations, 0 cycles.
  Symbol/edge totals are intentionally not plan gates; `docs/STATUS.md` owns
  ratcheted repository counts.
- Current intake queue: 18 entries (was 19; `LB-INTAKE-20260613-005` shipped in Wave 0).
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
2. Add a `Standalone` or `DesktopPlayer` define profile before adding source
   text heuristics. The real bug is missing active preprocessor symbols for
   `UNITY_STANDALONE && !UNITY_EDITOR`.
3. Add an inactive-define hint only after profile coverage exists. It should
   say "nearest references exist only under unanalyzed defines" and include the
   relevant define expression.
4. Add scaffolding downrank for all-`Conditional` / const-anchor-only internal
   types. Keep it a bucket/downrank, not a deletion recommendation.
5. Implement asmdef compile-direction checking as a static graph/asmdef query:
   for every cross-module edge, compare the source asmdef's declared references
   to the target asmdef. Report the first offending call site.
6. Add Unity serialized/YAML reachability last. Build it behind a port that can
   parse `.prefab`, `.unity`, and `.asset` files, resolve script GUIDs through
   `.meta`, and feed UnityEvent method targets plus serialized enum values as
   reachability/production roots.
7. Add vendored/sample path control in two layers: path filters first, optional
   `Vendored` bucket only if all bucket parity tests and wire docs are updated.

Done when:

- `dead_code` no longer flags `Graphic`-derived `Update` / `Reset` methods as
  dead.
- Desktop-only guarded calls are visible under a documented profile.
- UnityEvent-wired methods no longer look identical to genuinely dead methods.
- asmdef violations can be caught before Unity compile.

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

# 🔁 SESSION HANDOFF — resume here (last updated 2026-06-21, HEAD `a24af3f`)

Single combined source of truth for resuming the campaign in a fresh session.
Read top-to-bottom before touching anything.

## Where we are
- **Repo** `D:/Projekti/Lifeblood`. **Branch** `codex/lifeblood-tracking-complete`,
  pushed to `origin/codex/lifeblood-tracking-complete` (HEAD `2526c33`). **`main` is
  untouched** and awaits the eventual tagged `v0.7.12` — do NOT push to main mid-plan.
  User owns push + tag; commit freely, push the branch when green.
- **Goal** burn `devmemory/lifeblood-intake.md` down to shipped, ratcheted features,
  then cut `v0.7.12`.
- **Live state** 35 MCP tools (18 read + 17 write), 30 ports, 168 invariants / 115
  categories, suite **1378 passed / 0 failed / 11 native-clang skips / 1389 total**,
  self-analyze 0 violations / 0 cycles (4837 sym / 26836 edges / 503 types).
- **⚠️ Local dev MCP tool is BEHIND HEAD** — last installed `0.7.12-alpha.0.18`
  (commit `ed0d54e`), but HEAD is `2526c33` (wire_audit c+d + member_count NOT in the
  running server). REPACK + reinstall before the next live dogfood (recipe below).
- **Local dev MCP tool** = global dotnet tool `lifeblood.server.mcp`
  (`lifeblood-mcp.exe`). Reload
  recipe: DAWG memory `reference-lifeblood-local-mcp-reload` (pack →
  `dotnet tool update --global lifeblood.server.mcp --add-source local-nupkg --version
  <ver>` → kill `lifeblood-mcp.exe`/`*Lifeblood.Server.Mcp*` procs → reconnect via
  Claude Code reload / `/mcp`). After landing more commits, repack + reinstall so live
  dogfooding tests the latest build.

## Shipped this campaign (18 commits)
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
- (+ `b16b198` adopt, `f243d2c` position marker)

## NEXT atoms, in order
1. **REPACK + reload the local tool** (it is behind HEAD) then live-dogfood the
   batch since 0.18: wire_audit c (events) + d (degenerate calls) + member_count
   against the Lifeblood graph. Live dogfooding has caught 3 real bugs this session.
2. **`lifeblood_struct_layout(typeId)`** (`LB-INTAKE-20260601-002`, MED). Roslyn-metadata
   offset/size/align/total for unmanaged structs (`[StructLayout]`, `Pack`,
   `[FieldOffset]`, `fixed`, enum underlying, nested, Unity.Mathematics). Exact for
   blittable; confidence downgrade + reason for reference-bearing/Auto. Use the same
   emit-vs-compute parity HARNESS shape that pinned `member_count` (compile a fixture,
   `Assembly.Load` for ground-truth `Marshal.SizeOf`/`OffsetOf`, feed the same
   compilation to the tool, assert equal) — it caught a real subtlety there.
3. **Wave 4 `lifeblood_authority_coverage`** (`LB-INTAKE-20260613-004`) — graph-only
   `AuthorityCoverageAnalyzer` in `Lifeblood.Analysis`: do subjects[] reach
   requiredAuthority[]? negative-dependency matrix output.
4. **Wave 5 Unity FP** (`20260608-001/002/003`, `20260601-001/003`, `20260601-004`
   Vendored half): transitive MonoBehaviour magic-method (UIBehaviour/Graphic chain) →
   Standalone define profile → asmdef-direction check → scaffolding downrank →
   UnityEvent/YAML reachability → Vendored bucket.
5. **Wave 6** session recovery + content-hash incremental + execute CS1061 hint + Unity
   sync hook (`20260602-001`, `20260611-002/003/005`).
6. **Wave 7 (deferred)** net10 sourcegen concurrency (`20260601-005`).

Partials in intake: `20260601-004` (analyze excludePaths + Vendored bucket) only.
**12 intake entries remain** (`20260611-004` and `20260611-001` fully shipped + tombstoned this session).

## Per-atom DISCIPLINE (non-negotiable — how every atom above shipped)
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
6. **Tracking SSoT:** ship → MOVE intake entry to a Shipped receipt in
   `lifeblood-tracking-archive.md` + DELETE from `lifeblood-intake.md` (HTML-comment
   tombstone ok). Partial = SHRINK the intake entry + add receipt. `IntakeLedgerTests`
   forbids an id in both files + requires `Type:`/`Priority:`/`Source:`/`Workspace:` +
   `What:`/`Why it matters:`/`Fix shape:` (keep `Fix shape:` literal). `TrackingLedgerTests`
   keeps the live ledger to Shipped+in-flight only.
7. **CHANGELOG** `[Unreleased]` entry per atom.
8. **Verify:** build → focused test → full `dotnet test Lifeblood.sln` green → commit
   (conventional + `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`)
   → push branch → repack+reload local tool → live-dogfood the new tool.

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

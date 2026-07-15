# Lifeblood Backlog Clearance Masterplan

Date: 2026-07-15

Status: adopted for the fresh backlog-clearance goal. Investigation and plan
atom only; no intake entry is considered implemented merely because it is
routed here.

Scope: `D:/Projekti/Lifeblood`, dogfooded read-only against
`D:/Projekti/DAWG`. No push, tag, NuGet publication, or release cut is part of
this goal.

## Goal

Clear every remaining Lifeblood intake and partially shipped entry without
turning each observation into a bespoke tool. A closure is valid only when it
is one of:

1. implemented behind the correct hexagonal owner and proven by tests plus a
   real dogfood receipt where the request came from DAWG;
2. already satisfied by current behavior, with a new regression ratchet and a
   receipt proving that fact; or
3. explicitly declined/superseded because the requested product surface would
   violate ownership, duplicate an authority, or cost more than its durable
   value. A decline must name the existing supported workflow and is archived
   just like an implementation decision.

Line count is not a success metric. The preferred result is fewer public
surfaces, one fact model, and small projections over existing authorities.

## Audited Baseline

The 2026-07-15 source/ledger audit found:

- 35 unstarted intake entries after recording the stale shared-maturity wire
  field discovered during this audit;
- 2 partially shipped `.NET` entries in the living tracker;
- 18 original feature requests, 6 improvements, 4 UX requests, 3
  optimizations, 2 original bugs, 1 docs request, plus the new maturity bug;
- 18 High, 16 Medium, and 1 Low priority intake entries;
- six shared-base entries (`019`, `031`, `037`-`040`) already closed by the
  preceding goal and therefore intentionally absent from this plan.

The existing source also establishes five important facts:

1. `ICompilationHost` is already the retained-semantic left port and must not
   gain one method per proposed audit.
2. `RoslynOperationFacts` is a useful primitive but is currently only
   read/write classification plus source-type enumeration; it is not yet a
   reusable semantic contract fact model.
3. multi-profile graphs retain only the first profile's Roslyn compilations by
   design. Any wider operation query must preserve one semantic base rather
   than retaining N compilation heaps.
4. `ServerIdentity` currently runs Git relative to the Lifeblood server process,
   which explains the external-workspace provenance defect in `028` and cannot
   be copied into diagnostic/performance features.
5. runtime profiler evidence is a distinct external-evidence boundary. It does
   not belong in the C# adapter or the static semantic graph.

## Non-Negotiable Architecture Gates

### One authority per fact

- Source-control state has one Application port and one concrete Git adapter.
  Analyze receipts, diff ownership, release ratchets, and performance
  comparability consume its neutral snapshot; no handler launches Git itself.
- Compilation/file ownership has one C#-adapter resolver used by diagnose,
  compile-check, batch checks, and Unity package visibility. First-match path
  guessing is forbidden; unique, ambiguous, and absent are typed outcomes.
- Semantic contracts use one language-neutral fact vocabulary and one stateless
  rule engine. Roslyn only extracts facts; it does not decide whether a domain
  contract is good or bad.
- Runtime traces use neutral evidence records plus format adapters. Static graph
  correlation and cross-device comparison consume those records without
  pretending runtime measurements are semantic facts.
- `AnalysisUsage` remains the one per-request usage record. Start/end/delta
  fields extend it; no second memory receipt type is created.

### Layer ownership

| Layer | Owns | Must not own |
|---|---|---|
| Domain | inert contract, source-control, diagnostic-ownership, runtime-evidence, and result records | Roslyn, Git, JSON/CSV parsing, filesystem/process APIs |
| Application | ports, orchestration, validation, bounded request policies | concrete adapters or MCP wire objects |
| Analysis | deterministic graph/fact/evidence analyzers | file/process access, retained mutable state |
| Adapters.CSharp | Roslyn compilation ownership and operation-to-neutral-fact extraction | DAWG vocabulary or product policy |
| Source-control adapter | Git discovery, commit/dirty/diff facts | diagnostic classification or MCP shaping |
| Runtime-evidence adapters | Unity/generic JSON/CSV trace parsing | semantic graph judgment |
| Connectors/MCP | argument binding, behavior declaration, bounded wire projection | semantic algorithms or duplicate counters |
| Server/CLI | composition only | reusable adapter logic |

### Surface budget

At most two new MCP tools are justified by this backlog:

1. `lifeblood_contract_audit` for the shared semantic-contract family;
2. `lifeblood_performance_evidence` for import/correlation/comparison of runtime
   evidence.

Every other request extends an existing tool or closes through tests/docs:

- `analyze`: source visibility, source-control provenance;
- `diagnose`: diff ownership;
- `compile_check`: bounded multi-file mode and typed ownership;
- `invariant_check`: compact source projection and invariant coverage;
- `snapshots` or analyze evidence projection: baseline drift comparison;
- existing usage block: start/end/peak-above-start memory facts.

A third new tool requires a written proof that neither existing owner nor the
two new bounded contexts can express the behavior without becoming a god
surface.

## Complete Routing Table

This table owns implementation order and acceptance shape, not lifecycle
status. Intake/tracking/archive remain the only status authorities.

| Intake | Wave | Canonical owner | Required proof |
|---|---:|---|---|
| `LB-INTAKE-20260629-001` | 3 | contract fact query | guarded/unguarded operation fixture plus DAWG sensitive-math route |
| `LB-INTAKE-20260629-002` | 2 | existing compile-check use case | bounded multi-file aggregate, one refresh, legacy single-file parity |
| `LB-INTAKE-20260629-003` | 2 | profile-scoped semantic query service | requested active profiles without retaining a second Roslyn base; memory receipt |
| `LB-INTAKE-20260629-004` | 5 | contract text-policy projection | caller-authored retired term, invariant evidence, advisory confidence |
| `LB-INTAKE-20260629-005` | 5 | governance decision | explicitly decline product CLI mutation; retain tested ledger/template workflow |
| `LB-INTAKE-20260629-006` | 4 | contract rule engine: numeric domain | conversion/non-finite fixtures plus DAWG domain manifest |
| `LB-INTAKE-20260629-007` | 4 | contract rule engine: shape | count/stride/channel/sidecar mismatch fixtures |
| `LB-INTAKE-20260629-008` | 4 | contract rule engine: lifecycle | early reset versus final-idle fixture and DAWG lifecycle route |
| `LB-INTAKE-20260629-009` | 4 | contract rule engine: rate transition | smoothed/direct control-path fixtures |
| `LB-INTAKE-20260629-010` | 4 | contract rule engine: cadence | explicit/missing conversion and boundary fixtures |
| `LB-INTAKE-20260629-011` | 4 | contract rule engine over enum/table facts | width/shift/duplicate/missing-row fixtures |
| `LB-INTAKE-20260629-012` | 4 | contract route projection | end-to-end manifest route with per-hop conversions/ranges/defaults |
| `LB-INTAKE-20260629-013` | 5 | documented probe golden path | generated cases from one contract manifest, compile-checked in user tests |
| `LB-INTAKE-20260629-014` | 4 | contract rule engine: shared state | readonly/runtime-mutable/shared-scratch fixtures |
| `LB-INTAKE-20260629-015` | 4 | contract rule engine: discontinuity | branch/reset/bypass fixtures with suppressions |
| `LB-INTAKE-20260629-016` | 5 | contract evidence summary | category evidence gaps; no global quality score |
| `LB-INTAKE-20260629-017` | 1 | analyze profile contract | descriptor-fallback regression preserving Editor+Player; close as already fixed only if proven |
| `LB-INTAKE-20260629-018` | 1 | compilation ownership resolver | unique/ambiguous/absent file fixtures plus newly imported Unity file receipt |
| `LB-INTAKE-20260629-020` | 1 | release governance ratchet | latest local semver tag represented by changelog heading/link or explicit release-state record |
| `LB-INTAKE-20260629-021` | 4 | contract rule engine: hot path | direct/transitive allocation and forbidden API fixtures |
| `LB-INTAKE-20260629-022` | 4 | contract rule engine: constant provenance | named/raw/near-equal/domain-conflict fixtures |
| `LB-INTAKE-20260629-023` | 4 | contract rule engine: determinism | clock/random/order/static-state fixtures |
| `LB-INTAKE-20260629-024` | 1 | C# Unity source-visibility inventory | embedded-package included/excluded/unbound fixture plus DAWG receipt |
| `LB-INTAKE-20260629-025` | 4 | contract rule engine: sibling parity | symmetric/asymmetric operations, constants, guards, state writes |
| `LB-INTAKE-20260629-026` | 4 | contract rule engine: ownership | configured lane/handoff and bypass fixtures |
| `LB-INTAKE-20260629-027` | 5 | existing invariant provider plus test graph | covered/prose-only/source-only/test-only/orphan fixtures |
| `LB-INTAKE-20260629-028` | 1 | source-control port/adapter | external analyzed repo root, commit, dirty state, bounded failure detail |
| `LB-INTAKE-20260714-029` | 2 | diagnose plus source-control snapshot | introduced/pre-existing-touched/unrelated/unknown fixtures |
| `LB-INTAKE-20260714-030` | 5 | current snapshot versus baseline projection | tolerance, violation/cycle/invariant flags, refresh recommendation |
| `LB-INTAKE-20260714-032` | 5 | existing invariant audit projection | nonzero/all modes, no duplicated ledger, truthful caps on DAWG-sized fixture |
| `LB-INTAKE-20260714-033` | 5 | existing usage probe/record | start/end/peak/delta invariants plus retained full/noop/edit/fallback benchmark |
| `LB-INTAKE-20260714-034` | 6 | runtime-evidence import/correlation | generic trace fixture, Unity export fixture, ambiguous/unmapped markers |
| `LB-INTAKE-20260714-035` | 6 | runtime-evidence comparator | comparable/partial/rejected scenarios and normalized deltas |
| `LB-INTAKE-20260714-036` | 3 | versioned contract cost manifest | external symbol annotation joined to loop/callback callsites |
| `LB-INTAKE-20260715-041` | 1 | shared-service capability contract | machine maturity equals verified setup/status posture |

The living tracker entries route separately:

| Tracking entry | Wave | Decision gate |
|---|---:|---|
| Lifeblood .NET feature adoption revised stage order | 7 | close every child lane, then make one production target decision |
| Lifeblood .NET runtime/JIT benchmark lane | 7 | comparable net8/net10 Lifeblood+DAWG measurements with semantic/schema/package parity |

## Execution Waves

### Wave 0 - Plan And Baseline Lock

- Read every active entry, relevant invariant, port, handler, and adapter seam.
- Verify the routing table covers every intake ID exactly once and both living
  tracker headings.
- Run existing intake/tracking/docs ratchets.
- Commit this plan as a standalone atom before production changes.

Exit: a new session can locate every item and its acceptance gate without
reading chat history.

### Wave 1 - Correctness, Provenance, And Coverage Truth

Covers `017`, `018`, `020`, `024`, `028`, `041`.

1. Ratchet current multi-profile fallback behavior before changing it. The
   canonical analysis identity path introduced after the original report may
   already satisfy `017`; proof decides, not commit chronology.
2. Extract one compilation ownership resolver with `Unique`, `Ambiguous`, and
   `Absent` outcomes. Diagnose and compile-check consume the same result.
3. Introduce one neutral source-control snapshot port and Git adapter. Analyze
   evidence resolves from the analyzed root; server-build provenance remains a
   separately named fact.
4. Build Unity package/source visibility from project/package descriptors and
   compilation membership, not raw path heuristics.
5. Add release metadata and shared-maturity parity ratchets.

Exit: no evidence receipt claims the wrong repo; no file silently binds to the
first module; package coverage and shared maturity are machine-readable.

### Wave 2 - Changed-Set And Multi-Profile Diagnostics

Covers `002`, `003`, `029`.

- Extend `compile_check` with a bounded `filePaths` mode. Prevalidate the entire
  request, refresh at most once, execute serially under one session generation,
  return one aggregate plus typed per-file ownership/diagnostics. Existing
  single-file JSON stays additive and byte-compatible.
- Add diff ownership to `diagnose` using the Wave 1 source-control snapshot.
- Prototype profile-specific operation querying and measure two choices:
  compact precomputed neutral facts versus an ephemeral sequential compilation
  pass. The accepted design must expose every requested active profile while
  `additionalSemanticBaseCount` remains zero and peak memory stays bounded.

Exit: touched-file verification is one auditable call, diagnostics name their
change owner, and profile queries do not multiply retained Roslyn heaps.

### Wave 3 - Semantic Contract Kernel

Covers `001`, `036` and establishes the only engine used by Waves 4-5.

- Define a language-neutral operation fact vocabulary: symbol/span/profile,
  calls/arguments, value origin, assignments/reads, branches/loops, constants,
  conversions, allocations, state access, and caller-authored annotations.
- Roslyn extracts facts behind one port. A stateless Analysis service evaluates
  versioned contract manifests. The MCP connector only binds and projects.
- Ship one bounded `lifeblood_contract_audit` surface with `summarize`, caps,
  profile scope, contract identity, confidence, and limitations.
- External API costs are manifest data keyed by canonical external symbol ID;
  Unity/Burst names never enter Domain/Application/Analysis code.

Exit: operation guard and external-cost fixtures prove the fact/rule boundary;
DAWG dogfood finds a known guarded path and a known risk without hardcoded DAWG
tokens.

### Wave 4 - Contract Rule Families

Covers `006`-`012`, `014`, `015`, `021`-`023`, `025`, `026`.

Implement small stateless rule modules over the Wave 3 fact model. Rules share
route walking, provenance, confidence, suppression, and bounded output. They do
not grow new MCP registrations or `ICompilationHost` methods.

Natural commit groups:

1. numeric domains/non-finite policy plus constants and cadence;
2. buffer/stride/mask/sidecar shape;
3. lifecycle, smoothing, and discontinuity;
4. hot-path allocation/forbidden/external-cost and shared state;
5. determinism, sibling parity, ownership/handoff, and cross-layer control law.

Each group gets synthetic exact/advisory/negative fixtures and one read-only
DAWG receipt before its intake IDs move to the archive.

### Wave 5 - Evidence Governance And Memory Explainability

Covers `004`, `005`, `013`, `016`, `027`, `030`, `032`, `033`.

- Comment drift is advisory and requires caller-authored retired terms or a
  resolved invariant conflict.
- `005` is intentionally not a product CLI feature: a Lifeblood binary must not
  mutate this repository's private DevMemory format. The supported workflow is
  the existing template plus ledger tests; archive the explicit boundary
  decision rather than shipping repo-specific mutation authority.
- Probe generation is a documented golden path driven by the same contract
  manifest and verified through the user's test framework/compile-check. It is
  not an audio runner inside Lifeblood.
- Contract coverage returns named evidence categories/gaps, never one global
  quality score.
- Extend invariant audit with coverage and compact projections; extend snapshot
  evidence with read-only baseline comparison.
- Extend `AnalysisUsage` with start/end and peak-above-start fields, then run the
  retained full/noop/edit/fallback sequence.

Exit: evidence payloads stay bounded and non-duplicated; memory receipts explain
growth; governance requests close without turning Lifeblood into a repo editor.

### Wave 6 - Runtime Performance Evidence

Covers `034`, `035`.

- Define neutral trace/scenario/device/counter records.
- Add format adapters for a generic JSON/CSV schema and the smallest stable
  Unity export shape supported by a golden fixture.
- Correlate marker aliases/string ownership to graph symbols with explicit
  ambiguous/unmapped outcomes.
- `lifeblood_performance_evidence` supports import/correlate/compare modes while
  keeping runtime truth distinct from semantic truth.
- Comparison validates build/scenario/device/workload identity before computing
  normalized deltas.

Exit: unlike captures are rejected before ranking; mapped markers name evidence
and ambiguity; DAWG cross-device fixtures reproduce the intended decision loop.

### Wave 7 - .NET Runtime Decision

Covers both partially shipped tracking entries.

1. Recheck official .NET support/EOL and packaging guidance from primary
   Microsoft sources at execution time.
2. Run identical net8/net10 workloads on installed stable SDKs: Lifeblood full,
   incremental-noop, one-file incremental, retained MCP reads, package install,
   and DAWG full/read-only/shared-host lanes.
3. Require identical semantic graphs, schemas, tests, and tool packaging.
4. Record median wall/CPU/memory/GC/startup/dispatch results across repeated
   runs. Do not migrate for marketing claims or one noisy win.
5. Make one explicit production decision: migrate, multi-target where packaging
   evidence justifies it, or stay temporarily with a dated support blocker.

Exit: both tracker entries move to closed history with measurements and one
production target decision; no indefinite `Partially shipped` status remains.

### Wave 8 - Whole-System Verification

For every implementation commit:

1. scoped format verification;
2. clean Release build;
3. focused positive/negative/compatibility tests;
4. architecture, schema, registry, and ledger ratchets when the surface changes;
5. `git diff --check`;
6. an atomic conventional commit with only that natural change.

For every wave and before final closure:

- full Release suite, with native-clang fail-hard when its executable is
  available/required;
- exact self-analysis and status-anchor parity;
- local package/install/startup smoke;
- shared-host process/lifecycle regressions;
- read-only DAWG dogfood using a frozen commit or clean clone, never mutating
  concurrent DAWG work;
- performance/memory gates for any change that adds retained facts or profile
  execution.

Code existing on disk is not completion. The entry stays active until its
acceptance receipt passes.

### Wave 9 - Ledger And Documentation Closure

- Move each proven/declined entry from intake to closed history; partial work
  moves through the living tracker with an exact `Remaining open work:` line.
- Update CHANGELOG, STATUS, tool/setup docs, schemas, invariant tree, and this
  plan's final receipt as each public contract changes.
- Re-run ledger audits and prove: intake has zero entries, living tracker has
  zero partially shipped entries, archive does not recreate living authority,
  and no ID appears in more than one lifecycle ledger.
- Commit the final reconciliation atom. Do not push or tag.

## Failure And Rollback Policy

- A candidate feature that cannot prove value on its motivating DAWG shape does
  not ship merely to clear a Markdown list.
- A new abstraction must have at least two real consumers or replace an existing
  duplicate; speculative frameworks are rejected.
- Any failed candidate leaves the last committed graph/session/package intact.
- Additive wire changes preserve v1 compatibility. Breaking changes require the
  existing schema deprecation policy, not an in-place rewrite.
- If a profiler format is not stable/exportable, ship the generic format adapter
  and record the Unity adapter blocker rather than parse screenshots or private
  binary formats heuristically.
- If net10 measurements regress materially or package compatibility fails, keep
  net8 temporarily with the measured blocker and dated support decision; do not
  hide the result or force a migration.

## Definition Of Complete

The goal is complete only when all of the following are simultaneously true:

1. every routed intake ID has a tested implementation/already-shipped/declined
   receipt in closed history;
2. both `.NET` partials are closed with a production decision;
3. no duplicate fact authority, project-specific domain vocabulary, extra
   retained semantic base, or unjustified MCP tool was introduced;
4. all focused/full/process/package/self-analysis/DAWG gates pass;
5. current docs and schemas describe the tested behavior;
6. the tracked Lifeblood worktree contains only intentional committed atoms;
7. no push, tag, or publication occurred without separate user authorization.

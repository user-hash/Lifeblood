# Lifeblood Backlog Clearance Masterplan

Date: 2026-07-15

Status: active. Wave 1 is closed; Waves 2-7 remain the feature-backlog roadmap.
The 2026-07-16 consolidation audit approved one controlled semantic-contract
program for 20 of the 29 remaining intake entries. "One program" means one
fact authority, one bounded public surface, and natural tested commits; it does
not mean a big-bang rewrite or one unreviewable commit.
The 2026-07-16 reliability continuation was executed first because live DAWG
transport, latency, response-size, provenance, and Unity-wrapper defects made
Lifeblood unsafe as a release gate. Routing never counts as implementation;
only the receipts below close work.

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

## 2026-07-16 Consolidation Decision

The remaining backlog was re-audited against the live C# adapter, graph edge
identity, retained-profile ownership, and every active intake heading. The
hypothesis is confirmed with one correction: the reusable seam is not a new
graph edge kind or a different architecture-edge count. It is one bounded,
language-neutral operation-fact stream consumed by stateless rules.

Graph edges intentionally collapse repeated source occurrences by semantic
identity and retain only the first authoring callsite. That is correct for
architecture coupling. Contract questions need occurrence-level, often n-ary
facts such as argument position, value origin, conversion, guard/loop context,
state access, and ordering. Encoding those as graph edges would both corrupt
coupling counts and lose the fact relationships the requests need.

### Exact closure set

| Relationship to the controlled swing | Intake IDs | Count |
|---|---|---:|
| Direct consumers of the shared operation-fact vocabulary and rule engine | `001`, `006`-`012`, `014`, `015`, `021`-`023`, `025`, `026`, `036` | 16 |
| Required execution foundation: truthful requested-profile operation facts with zero additional retained semantic bases | `003` | 1 |
| Same manifest/audit program, but non-operation projections or documented workflow | `004`, `013`, `016` | 3 |
| **Total controlled semantic-contract program** |  | **20 / 29** |

The nine remaining entries keep their proper owners and MUST NOT be forced
through the contract kernel merely to improve the consolidation percentage:

| Owner lane | Intake IDs | Reason it stays separate |
|---|---|---|
| compile/diff diagnostics | `002`, `029` | compilation ownership and source-control change ownership, not operation policy |
| explicit governance decline | `005` | a product binary must not mutate Lifeblood's private DevMemory ledger |
| invariant/evidence projections | `027`, `030`, `032` | invariant tree and graph/snapshot evidence are existing authorities |
| usage telemetry | `033` | extends the existing per-request `AnalysisUsage` authority |
| runtime evidence | `034`, `035` | measured external traces are not static semantic facts |

The partially shipped `LB-INTAKE-20260716-044` is adjacent but not silently
counted among the 29 intake entries. Reflection-string and `Resources.Load`
call/literal relationships may consume the new call/argument facts as bounded,
confidence-tagged adapters. Serialized Unity asset relationships remain an
external asset-evidence adapter. None become proven semantic graph edges.

### One-pass and one-base contract

The controlled swing MUST satisfy all of the following:

1. Domain owns only inert, language-neutral operation fact, manifest, finding,
   confidence, and limitation records.
2. Application owns one fact-query port and orchestration contract. It does not
   add one `ICompilationHost` method per rule family.
3. `Adapters.CSharp` performs one deterministic Roslyn operation traversal per
   requested execution scope and emits neutral facts. It makes no DAWG, DSP,
   Burst, Unity-product, or contract-goodness decisions.
4. Analysis owns stateless rule modules and shared route/provenance/suppression
   logic. Multiple selected rule families consume the same traversal.
5. MCP owns only typed argument binding, caps, pagination/summary projection,
   and envelope classification. The surface budget remains one
   `lifeblood_contract_audit` tool for this entire program.
6. Facts do not enter `SemanticGraph`, do not add `EdgeKind` members, and do not
   change established symbol/edge counts.
7. No second Roslyn compilation heap is retained. Requested profiles execute
   sequentially or use a measured compact neutral index; either design must
   report `additionalSemanticBaseCount = 0`.
8. A universal retained fact cache is forbidden until a benchmark proves it is
   smaller and faster than bounded extraction. The default design is a bounded
   stream with selected rules sharing the pass.
9. Existing wire/feature-switch/assignment/callsite/static-table tools migrate
   only one at a time after byte/semantic parity, limitation parity, and
   performance parity. The contract kernel may coexist temporarily; a
   speculative big-bang rewrite is forbidden.

### Controlled-swing acceptance matrix

Every implementation atom must prove its layer locally. Closing an intake ID
additionally requires its motivating DAWG receipt.

| Gate | Required evidence |
|---|---|
| architecture | Domain zero-dependency, Application ports-only, Analysis stateless, no connector-to-adapter dependency, no new graph edge kind |
| extraction | positive, negative, ambiguous/advisory, deterministic-order, generated-source, and profile-guarded fixtures |
| compatibility | existing operation-tool focused suite stays green; graph symbol/edge counts and existing v1 schema snapshots stay stable unless an intentional additive surface changes |
| one base | profile test asserts zero additional retained compilations/semantic bases and releases ephemeral profile state after the call |
| bounds | request prevalidation, manifest/rule caps, finding/evidence caps, summary-first response, cancellation, and no partial publication |
| performance | Lifeblood-self full/noop/query timings plus peak working set; frozen/read-only DAWG cold/warm/query comparison against the recorded baseline |
| value | each rule family detects a known positive and rejects a known negative in synthetic fixtures, then returns an actionable DAWG result without hardcoded DAWG vocabulary |
| closure | intake moves only after implementation/already-satisfied/decline evidence; routing or partial scaffolding never counts as closure |

### Open reliability verification gate

The semantic-contract program does not subsume transport reliability. A DSP
handoff after the installed `0.7.13-alpha.0.51` receipt reported three live
symptoms that must be reproduced before code changes:

- direct Codex Lifeblood connector calls return `Transport closed`;
- Unity custom-tool calls can remain `_mcp_status: pending` without a job id or
  terminal result;
- a previously healthy shared graph can present `noPriorAnalysis`, then close
  or time out during the attempted full rebuild.

The reliability lane must capture client identity, workspace key, daemon
identity/handshake, publication generation, pending token/job identity, and
process lifetime on both success and failure. Classification must distinguish
server state loss from a stale direct connector, Unity polling-token misuse,
and maintenance restart. No transport patch lands from the report alone.

Reproduction has now separated two server-edge defects from the still-open
direct-connector lane. Unity action-only status polls were keyed by their
original argument object and therefore could not recover the admitted call;
the registry now keys status by tool identity while retaining arguments only
for duplicate/conflict admission. Shared graph loss was also real: the daemon
hardcoded a five-minute last-client eviction policy, so ordinary agent gaps
disposed the sole retained base and the next proxy correctly found generation
zero. The lifecycle fix removes every implicit wall-clock deadline, preserves
the exact publication with zero leases, and keeps automatic reclamation only
as an explicit `LIFEBLOOD_SHARED_IDLE_SECONDS` operator policy. Focused fake-
clock, long-policy, capability, and process reconnect tests are green; installed
DAWG verification remains required before this reliability item closes. Direct
Codex connector `Transport closed` remains independently open and must not be
reported as solved by either server fix.

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

## Execution Receipts

| Entry | Resolution | Verification | Commit |
|---|---|---|---|
| `LB-INTAKE-20260715-041` | shared capability maturity derives from the verified `recommended` rollout contract | focused capability/docs/ledger ratchets, Release build, full suite | `fix(shared-host): align maturity with verified rollout` atom |
| `LB-INTAKE-20260629-017` | already satisfied by the shared analysis-request pipeline; exact MCP fallback behavior ratcheted without production duplication | Editor+Player asmdef-drift full fallback preserves response, retained profiles, and analysis identity; Release/full suite | `test(analyze): ratchet profile fallback preservation` atom |
| `LB-INTAKE-20260629-018` | one C#-adapter resolver owns file-to-compilation matching for diagnose and compile-check; ambiguity fails closed with stable candidates | adapter/public MCP fixtures cover unique/ambiguous/absent/pinned/missing-module paths; Release/full suite; private new-server DAWG receipt resolves the motivating file uniquely (frozen-clone rerun timed out and is not counted as a pass) | `fix(compilation): fail closed on ambiguous file ownership` atom |
| `LB-INTAKE-20260629-024` | one C#-adapter package visibility receipt feeds analyze and compile-check; package sources are included/excluded/unbound from descriptors, asmdefs, compilation membership, and `excludePaths` | `PackageSourceVisibilityTests` cover included/excluded/unbound analyze shape and compile-check package resolution; 59/59 focused docs/schema/ledger/package gate; clean Release build; 1,545-case full suite; fresh-server self-dogfood; DAWG Editor+Player read-only package receipt; retained DAWG unbound-package compile-check receipt | `fix(package): surface Unity package source visibility` atom |
| `LB-INTAKE-20260629-020` | latest reachable stable tag, changelog history/base, and tracker snapshot are one ratcheted release fact | latest-tag docs ratchet; full-history CI; 64 focused checks; clean Release build; 1,543-case suite; self-dogfood | `fix(provenance): unify source control evidence` atom |
| `LB-INTAKE-20260629-028` | one Domain receipt, Application port, and bounded Git adapter serve analyzed-workspace, invariant, capability, and release provenance | temp-repo bounds/root fixtures; persistent-stdin MCP process regression; 64 focused checks; clean Release build; 1,543-case suite; self-dogfood; DAWG Editor+Player full receipt rooted at DAWG | `fix(provenance): unify source control evidence` atom |

### 2026-07-16 reliability continuation receipts

These fixes are an urgent interlude, not a declaration that Waves 2-7 are
implemented. The final installed-build DAWG receipt used
`0.7.13-alpha.0.51+55af5d25b2be828e9fb7baeecbf41beb091a9900`, the
canonical Unity bridge, and an independent direct client against one daemon
publication: cold Editor+Player server work 53.53 seconds; direct warm reuse
6.34 seconds; Unity warm reuse 6.70 seconds; 88,832
symbols / 346,496 edges / 100 modules / 5,567 types / 138 cycles / zero
configured violations; one retained semantic base; exact snapshot
`snap_a47f202a590540989e5d6471e8b539f8`; latest reachable stable tag
`v1.2.376.0`.

| Finding | Permanent owner/fix | Verification | Commit |
|---|---|---|---|
| `LB-INTAKE-20260716-037` | registry-owned action routing lets snapshot `list` use the shared-read lease while catalog mutations remain exclusive | focused handler/protocol gate and concurrent DAWG read during analyze | `ac1f50a` |
| `LB-INTAKE-20260716-038` | immutable publication identity reuse, publication-time source control, and waiter-isolated shared transport | publication/process regressions plus exact live snapshot/generation reuse | `87b055f` |
| `LB-INTAKE-20260716-039` | summary-first package/profile projection with policy-aware coalescing; clean package rows omitted | bounded synthetic payload tests plus live 9-package receipt returning one actionable row | `6449ae3`, `1c31c57`, `2af780b`, `aa3aad2` |
| `LB-INTAKE-20260716-040` | sole Git adapter accepts stable three- and four-component semantic tags | real temporary-repository fixture and DAWG `v1.2.376.0` receipt | `bc70502` |
| `LB-INTAKE-20260716-041` | invariant audit separates parser errors from declaration-coverage confidence | DAWG-shaped coverage fixture and live coverage receipt | `d1ab449` |
| `LB-INTAKE-20260716-042` | C# adapter owns module/profile applicability provenance; MCP only projects it | multi-profile descriptor fixtures plus DAWG Editor/Player counts | `4757292`, `2af780b` |
| `LB-INTAKE-20260716-043` | accepted-change projection distinguishes cold fallback reanalysis from content change | focused wire-shape fixtures and live no-op reuse | `5f82678` |
| `LB-INTAKE-20260716-044` | semantic edges stay precise; opt-in advisory source-file-IO receipt names supported and unsupported heuristic families | focused zero-semantic-impact fixture | `b00fce6` (partial; reflection/resource/serialized families remain explicit limitations) |
| `LB-INTAKE-20260716-045` | one server/source-control provenance authority labels local prerelease and stable release-gate evidence | source-control/release-gate tests plus installed alpha DAWG receipt | `44e11e4` |
| DAWG cold-scan regression | bounded deterministic per-tree Roslyn extraction in the C# adapter | 90 focused cases, stable export hash, full suite, DAWG 51.94-second server receipt | `37de605` |
| Unity wrapper parameters/polling | canonical UPM outer adapter, typed nested parameter properties, one polling coordinator | 5 contract tests, Unity discovery of all 18 tools, live cold/warm calls | `827c4f2` |
| Duplicate consumer bridge | DAWG manifest references `Lifeblood/unity`; copied 11-file package removed | Unity compile/discovery and process command-line proof | DAWG `42d1b484c` |

Final in-tree verification passed 1,570 tests with 11 native-Clang
precondition skips and zero failures (1,581 discovered total). The focused
docs/schema/ledger gate passed 62/62 after lifecycle reconciliation. No tag,
push, NuGet publication, or release cut was performed.

The two generation-0/closed-server reports emitted during final installation
coincided with the deliberate stop of the 0.50 proxy/daemon while the global
tool store was replaced. They are classified as maintenance-restart evidence,
not a spontaneous 0.51 persistence failure. Post-install, one persistent
direct proxy completed full analyze, `lifeblood_execute`, and incremental-noop;
Unity then completed its own polled execute and incremental-noop against the
same generation-1 snapshot without fallback or transport loss.

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

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

Latest shipped Lifeblood tag: **`v0.7.7`** (2026-05-16). v0.7.6 and v0.7.7
both shipped after the snapshot block was last refreshed. LB-TRACK-009 /
LB-TRACK-010 / LB-TRACK-011 closed in v0.7.6; v0.7.7 added the native-Clang
adapter beta plus the `INV-CHANGELOG-001` link-reference ratchet that caught
the v0.7.6 release-tag drift. The next planned tag will be cut after the
**2026-05-19 two-phase hardening plan** (`D:/Projekti/lifeblood_plan.txt`)
lands its Phase-1 functionality atoms F0..F6 + Phase-2 hexagonal architecture
atoms A0..A6 — no tag, no push, no release until those land green end-to-end
through a fresh MCP redeploy against DAWG.

Verification baseline at plan start (2026-05-19):
- Debug test suite: **1011/1011 green**, 0 skipped.
- Lifeblood self-analysis (per `docs/STATUS.md`): 3,278 symbols, 16,973 edges,
  11 modules, 350 types, 0 violations, 0 cycles.
- Invariant audit: 101 typed invariants across 63 categories.
- Native Clang fixture smoke checks green via CLI JSON import path.

Gravity-well measurement at plan start (Phase-2 targets):
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

Status: Shipped v0.7.4 - **DAWG verification deferred to v0.7.6 redeploy wave**
Type: Improvement
Source: `D:/Projekti/Lifeblood/docs/IMPROVEMENT_INBOX.md` (`LB-INBOX-009`)
Workspace: DAWG, Lifeblood self
Verification: Lifeblood `f288b7c` (`feat(enum_coverage): per-member
reference coverage tool (LB-TRACK-003)`), shipped in v0.7.4 and
carried into v0.7.5; 821 / 821 green (+8 over the post-LB-TRACK-008
baseline of 813); `lifeblood verify --incremental` self-passes
(2686 / 13356 after the wave, identical full vs incremental); DAWG
roundtrip verification deferred to the v0.7.6 fresh MCP redeploy.

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

Status: Shipped v0.7.4 - **DAWG verification deferred to v0.7.6 redeploy wave**
Type: Improvement
Source: `docs/invariants/eternal-arch-audit-2026-05-12.md`
Workspace: DAWG, Lifeblood self
Verification: Lifeblood `68fd4a2` (`feat(dead_code): triage fields —
directDependants/bucket/declarationOnly (LB-TRACK-004)`), shipped in
v0.7.4 and carried into v0.7.5; 798 / 798 green (+16 over the v0.7.3
baseline of 776 + 6 already-landed inter-stage tests); DAWG roundtrip
verification deferred to the v0.7.6 fresh MCP redeploy.

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

Status: Shipped v0.7.4 - **DAWG verification deferred to v0.7.6 redeploy wave**
Type: Improvement
Source: `.claude/devmemory/lifeblood-field-report-2026-05-11.md`
Workspace: DAWG, Lifeblood self
Verification: Lifeblood `f204032` (`feat(test_impact): which tests
transitively depend on a target (LB-TRACK-007)`), shipped in v0.7.4
and carried into v0.7.5; 830 / 830 green (+9 over the post-LB-TRACK-003
baseline of 821); `lifeblood verify --incremental` self-passes
(2686 / 13356, identical full vs incremental); DAWG roundtrip
verification deferred to the v0.7.6 fresh MCP redeploy. Re-measurement
of recall against DAWG ratchets is the W2-F gate of the v0.7.6 plan —
if recall < 95% after LB-INBOX-011 graph edges synthesize (LB-TRACK-010),
an Advisory heuristic layer ships as a separate INV.

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

Status: Shipped v0.7.4 - **DAWG verification deferred to v0.7.6 redeploy wave**
Type: UX
Source: `docs/invariants/eternal-arch-audit-2026-05-12.md`
Workspace: DAWG, Lifeblood self
Verification: Lifeblood `d5482a3` (`feat(cycles): taxonomy buckets on
lifeblood_cycles (LB-TRACK-008)`), shipped in v0.7.4 and carried into
v0.7.5; 813 / 813 green (+8 over the post-LB-TRACK-002 baseline of
805); `lifeblood verify --incremental` self-passes (2686 / 13356
across the wave); DAWG roundtrip verification deferred to the v0.7.6
fresh MCP redeploy.

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

Status: Shipped on Lifeblood `main` (not tagged) — DAWG roundtrip
deferred to v0.7.6 fresh-MCP redeploy
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
pins overload disambiguation. DAWG roundtrip verification deferred to
the v0.7.6 fresh MCP redeploy.

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

Status: Shipped on Lifeblood `main` (not tagged) — DAWG roundtrip
deferred to v0.7.6 fresh-MCP redeploy
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
(graph dependants + dead_code axes). DAWG roundtrip verification
deferred to the v0.7.6 fresh MCP redeploy.

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

Status: Shipped on Lifeblood `main` (not tagged) — **MCP redeploy gate
holds the tag** (the dist `Lifeblood.Server.Mcp.dll` is still the
v0.7.5-alpha binary even though source HEAD = `198af1e`)
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

**DAWG-side / MCP roundtrip verification gate:** the active MCP binary
is still `v0.7.5-alpha` per the 2026-05-15 audit; rebuild the
`Lifeblood.Server.Mcp` Release dist and restart the MCP server before
running `lifeblood_diagnose` against DAWG to confirm parity-class
diagnostics drop to 0 through the deployed surface. Until that
roundtrip succeeds, the v0.7.6 tag is NOT cut.

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

Status: Open
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

Status: Open
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

Status: Open
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

Status: Open
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

Status: Open (qualitative feedback, not a product bug entry)
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

Status: Open
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

Status: Open
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

Status: Open
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

Status: Open
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
Closing commit: post-`881b39f` F1a commit; both `RoslynEdgeExtractor.GetMethodId`
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

Status: Open
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

Status: Partially Shipped (in-tree, untagged) — F1c atom of the 2026-05-19 plan.
Closing commit: post-`b0b5eb5` F1c commit; `ParseSymbolId` now preserves the literal
`.ctor` / `.cctor` IL names, and `FindInCompilation` looks up constructors via
`INamedTypeSymbol.Constructors` / `.StaticConstructors`. Pinned by
`INV-CANONICAL-ID-PARITY-001` and `SymbolIdCanonicalParityTests` (7 fixtures —
4 invocation-site, 3 parser-white-box). Debug suite 1017 → 1024. STATUS test-count
anchor refreshed. Remaining scope (deferred to a follow-up atom under the same
LB-TRACK-023): explicit consolidation of `BuildSymbolId` and `GetMethodId` into
a single SSoT routed through `CanonicalSymbolFormat` — they already share
`CanonicalSymbolFormat.BuildParamSignature` and now both honor `ReducedFrom`
(F1a) + `OriginalDefinition`, but the two `IMethodSymbol → method:...()` paths
remain distinct call sites; a parity-matrix ratchet across all four paths
(extractor declaration, extractor edge, compilation-host BuildSymbolId,
workspace-manager ParseSymbolId round-trip) is the next-best follow-up.
Type: Bug
Source: `docs/plans/lifeblood-correctness-masterplan-2026-05-15.md` Stage 1 observed-risk note ("Lifeblood has multiple symbol-id paths with subtly different behavior"); re-surfaced as F1c in the 2026-05-19 plan.
Workspace: Lifeblood self
Verification:
- `RoslynEdgeExtractor.GetMethodId` (line 709) routes through `OriginalDefinition` and handles `AssociatedSymbol` (property/event accessor rewrite).
- `RoslynCompilationHost.BuildSymbolId` and `RoslynWorkspaceManager.ParseSymbolId` are referenced in the older masterplan as having different behavior.
- `LifebloodSymbolResolver` has its own resolution rules (canonical, truncated, kind-correction, short-name, qualified-input, type-aware Rule 4).
- No parity test fixture covers a single method id flowing through all four paths.

Summary:
Symbol-id generation is reproduced in at least four places. Each path has its own subtle handling for constructors, static constructors, accessors, generic methods, and (post LB-TRACK-021) extension methods. Drift between them surfaces as "find_references finds the symbol but dependants returns 0" or vice versa.

Impact:
Reference-side correctness depends on every read tool answering against the same canonical id. Stage 2 (dead-code triage) and Stage 3 (port-health composite) both assume reference data is trustworthy, so this is a load-bearing dependency for the rest of the plan.

Fix shape:
- Audit the four paths against a single fixture (method, ctor, cctor, property/event accessor, generic method, extension method).
- Promote `Lifeblood.Domain.CanonicalSymbolFormat` (already exists as the grammar SSoT) to be the *only* id-building primitive; route every other site through it.
- Make `RoslynWorkspaceManager.ParseSymbolId` handle `.ctor` and `.cctor` canonical ids without dot-splitting ambiguity (already explicitly called out in the 2026-05-15 plan Stage 1).
- Pin `INV-CANONICAL-ID-PARITY-001` with a parity matrix ratchet across the four paths.

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
  (committed to Lifeblood `main` `fc8ff96`, DAWG-verified 2026-05-14).
- `dead_code` triage fields: closed via `LB-TRACK-20260514-004`
  (committed to Lifeblood `main` `ca1fae0`, DAWG-verified 2026-05-14).
- Dependency cycle taxonomy: closed via `LB-TRACK-20260514-008`
  (committed to Lifeblood `main` `001be0d`, DAWG-verified 2026-05-14).

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

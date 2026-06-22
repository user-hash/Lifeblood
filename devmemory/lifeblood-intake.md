# Lifeblood Intake — un-started findings & feature requests

Un-prioritized intake. Items here are NOT yet started. The ratcheted ledger
[`lifeblood-tracking.md`](lifeblood-tracking.md) holds only Shipped + in-flight
work (`TrackingLedger_HasNoPlainOpenOrCandidateEntries` forbids parked Open
items), so new findings land here first. When work begins, promote the item:
ship it and record it directly in the ledger as Shipped (or Partially shipped
with a `Remaining open work:` line), then delete it from this intake.

Source: DAWG dogfood research pass 2026-06-01 (Lifeblood v0.7.11, server `1100895`).
Method: exercised v0.7.11 against the DAWG graph (69,350 symbols), verified every
candidate with `find_references` + grep + source read before filing — no
unverified claims.

---

<!-- LB-INTAKE-20260601-001 (Unity serialized/UnityEvent wiring invisible to
     reachability) IMPLEMENTED LOCALLY 2026-06-22: `UnityReachabilityAdapter` now scans
     `.prefab` / `.unity` / `.asset` YAML, resolves script GUIDs through `.meta`
     files, and marks resolved UnityEvent persistent-call method targets plus
     host types reachable. Residual boundary (documented on-wire): unresolved
     serialized targets, runtime-procedural assignment, Addressables/Resources-
     loaded values, unsaved Inspector edits, and serialized enum production.
     INV-UNITYEVENT-REACHABILITY-001. Recorded in lifeblood-tracking-archive.md.
     Do not re-add here. -->
<!-- LB-INTAKE-20260601-002 (static struct-layout / sizeof tool) IMPLEMENTED LOCALLY
     2026-06-22 as `lifeblood_struct_layout` (field offsets/sizes/alignment,
     pack, total size, fixed buffers; Exact for known blittable Sequential /
     Explicit structs, Advisory with limitations for Auto/reference/non-blittable
     shapes) -> recorded as the 2026-06-22 receipt in
     lifeblood-tracking-archive.md. INV-STRUCT-LAYOUT-001. Do not re-add here. -->
<!-- LB-INTAKE-20260601-003 (asmdef compile-direction boundary check) IMPLEMENTED LOCALLY
     2026-06-22 as `lifeblood_asmdef_check` (graph-only DirectOnly module
     dependency audit; reports first offending edge/call site/profile set per
     source-target module pair, skips SDK-style transitive modules honestly) ->
     recorded as the 2026-06-22 receipt in lifeblood-tracking-archive.md.
     INV-ASMDEF-CHECK-001. Do not re-add here. -->

<!-- LB-INTAKE-20260601-004 (Vendored/third-party path exclusion for dead_code
     + analyze) IMPLEMENTED LOCALLY 2026-06-22: first half (`dead_code pathExclude`)
     implemented 2026-06-21; remaining halves now implemented as `lifeblood_analyze
     excludePaths` with `analysisScopeChanged` incremental fallback, shared
     `PathGlobMatcher`, and first-class `Vendored` path bucket
     (Generated > Vendored > Test > Editor > Production). Pinned by analyze
     wire-shape, csproj compilation, bucket parity, grouping, schema, and DAWG
     dogfood receipts. INV-ANALYZE-EXCLUDEPATHS-001 /
     INV-PATHBUCKET-SHARED-001. Recorded in lifeblood-tracking-archive.md. Do
     not re-add here. -->

<!-- LB-INTAKE-20260601-005 (net10 source-generator concurrency isolation)
     IMPLEMENTED LOCALLY 2026-06-22. `SourceGeneratorRunner` now serializes framework
     analyzer loading and generator-driver execution behind a process-local
     gate, closing the concurrent in-process race without changing production
     TFM or generated-code semantics. Pinned by
     `CsprojCompilationFactsTests.Compilation_RunsFrameworkSourceGenerators_ConcurrentAnalyses_AreDeterministic`.
     Recorded in lifeblood-tracking-archive.md. Do not re-add here. -->

<!-- LB-INTAKE-20260602-001 (retained-session recovery after read-only analyze)
     IMPLEMENTED LOCALLY 2026-06-22. `GraphSession` now rejects non-read-only incremental
     recovery from a read-only/no-compilation-state session with
     `fallbackReason:"compilationStateUnavailable"` and an exact
     `incremental:false, readOnly:false` recovery hint, or performs a full
     restore when `allowFullFallback:true` is supplied. Write-side errors and
     capabilities expose the same recovery hint. Pinned by
     `AnalyzeWireShapeTests.Load_ReadOnlyThenWriteSideIncremental_*`. Recorded in
     lifeblood-tracking-archive.md. Do not re-add here. -->

---

## Refuted this pass (do not re-investigate)

- **No `unsafe` pointer-param extraction gap.** `ApplyOscDriftIfActive(BurstPatch*, …)`
  overload A is correctly seen live (called by RenderMono/Stereo with
  `pitchBendFactor`); overload B is correctly dead. dead_code is accurate on the
  Burst kernel, including pointer/ref-param overload resolution.

---

## 2026-06-08 — DAWG architecture-sealing dogfood (Lifeblood v0.7.11, server 1100895)

Method: full analyze + `defineProfiles:["Editor","Player"]` union; every claim cross-checked
with `find_references` / `dependants profileFilter` + grep + source read.

<!-- LB-INTAKE-20260608-001 (MonoBehaviour magic-method reachability misses
     Unity UI-derived components) IMPLEMENTED LOCALLY 2026-06-22. The C# extractor now
     records SymbolPropertyKeys.BaseTypeChain, and UnityReachabilityAdapter
     consumes the resolved chain so Graphic/UIBehaviour-derived components reach
     UnityEngine.MonoBehaviour without hand-maintaining intermediate subclass
     rosters. Recorded in lifeblood-tracking-archive.md. INV-UNITY-001. -->
<!-- LB-INTAKE-20260608-002 (Editor+Player profile pair misses
     UNITY_STANDALONE desktop-guarded callsites) IMPLEMENTED LOCALLY 2026-06-22.
     UnityDefineProfileResolver now exposes a Standalone profile that strips
     Unity editor discriminators and adds UNITY_STANDALONE, making
     UNITY_STANDALONE && !UNITY_EDITOR edges visible through the semantic
     multi-profile graph without source-text heuristics. Recorded in
     lifeblood-tracking-archive.md. INV-MULTI-DEFINE-UNITY-RESOLVER-001. -->

<!-- LB-INTAKE-20260608-003 (dead_code intentional scaffolding downrank)
     IMPLEMENTED LOCALLY 2026-06-22. `lifeblood_dead_code` now reports non-public static
     types whose direct members are exclusively `[Conditional]` methods and/or
     static const string anchors, plus those direct members, as
     `bucket:"Scaffolding"` instead of ordinary Production deletion work.
     Recorded in lifeblood-tracking-archive.md. INV-DEADCODE-SCAFFOLDING-001.
     Do not re-add here. -->

## DAWG-side findings (NOT Lifeblood issues — for the DAWG burst owner)

- `BurstSynthSustainKernel.DrainComb` (Comb.cs:95) — genuinely dead leftover,
  superseded by `DrainCombFilterState`.
- `ApplyOscDriftIfActive` 5-param overload (OscDrift.cs:172) — dead unused
  convenience overload; only the 6-param overload + `TestOnly_` wrapper are live.
- (2026-06-08) `MixerScreenAdapter.UpdateChannelWaveformInternal` /
  `ClearAllChannelWaveformsInternal` (Mixer/MixerScreenAdapter.Controls.cs:97/105) —
  grep finds ONLY the declarations, zero callers (not HostBindings-wired, not
  reflection). Genuine wire-or-delete candidate: a mixer per-channel waveform-display
  capability built but never connected. Verify intended feature before removing.

<!-- LB-INTAKE-20260611-001 (offline declared-member-count lane) IMPLEMENTED LOCALLY
     2026-06-22 as `lifeblood_member_count` (semantics reflectionDeclared bit-exact
     System.Reflection parity, pinned by an emit-reflect-vs-parse harness; +
     sourceSymbols graph-child semantics) -> recorded as the 2026-06-22 receipt in
     lifeblood-tracking-archive.md. INV-MEMBER-COUNT-001. Do not re-add here. -->

<!-- LB-INTAKE-20260611-002 (execute CS1061 scripting-surface hints) IMPLEMENTED LOCALLY
     2026-06-22. `RoslynCodeExecutor` now unwraps task-wrapped script
     compilation errors and appends public member lists plus a `Help` pointer
     when CS1061 hits a known Lifeblood scripting-surface type. Pinned by
     `ExecuteRobustnessTests.Executor_Cs1061OnKnownScriptingSurface_AppendsPublicMemberHint`.
     Recorded in lifeblood-tracking-archive.md. Do not re-add here. -->

<!-- LB-INTAKE-20260611-003 (content-hash incremental reliability) IMPLEMENTED LOCALLY
     2026-06-22. Incremental analyze now records source text hashes for the
     parsed files, treats mtime as a pre-filter, updates timestamps on
     contentless touches without graph replacement, and reports
     `mtimeTouchedSourceFiles` vs `contentChangedSourceFiles`. Pinned by
     `IncrementalAnalyzeTests.IncrementalAnalyze_ContentlessTouch_ReportsMtimeTouchWithoutReextracting`.
     Recorded in lifeblood-tracking-archive.md. Do not re-add here. -->

<!-- LB-INTAKE-20260611-004 (dead-WIRE audit: read-without-write fields,
     never-assigned binding slots, never-fired/never-subscribed events,
     degenerate constant-only call sites) IMPLEMENTED LOCALLY 2026-06-22 as the five
     passes of `lifeblood_wire_audit` (a+b 2026-06-21, c+d 2026-06-22) ->
     recorded as the 2026-06-22 receipt in lifeblood-tracking-archive.md.
     INV-WIRE-AUDIT-001. Do not re-add here; the id must not live in both files. -->

<!-- LB-INTAKE-20260611-005 (Unity editor sync authoritative changed-set)
     IMPLEMENTED LOCALLY 2026-06-22 at the Lifeblood MCP boundary. `lifeblood_analyze`
     accepts `authoritativeChangedFiles` for incremental analyze; the adapter
     bounds the source scan to that editor/build-system supplied set and still
     uses content hashes before replacing graph facts. Descriptor drift checks
     remain independent. Pinned by
     `IncrementalAnalyzeTests.IncrementalAnalyze_AuthoritativeChangedFiles_BoundsSourceScanToListedFiles`
     and `ToolArgumentContractTests.ToolRequestBinder_BindsAnalyzeRequestRecord`.
     Recorded in lifeblood-tracking-archive.md. Do not re-add here. -->

<!-- LB-INTAKE-20260613-001 (call-site argument/default-parameter facts) IMPLEMENTED LOCALLY
     2026-06-21 as the lifeblood_callsite_arguments tool (INV-CALLSITE-ARGS-001)
     → recorded as the 2026-06-21 receipt in lifeblood-tracking-archive.md. -->

<!-- LB-INTAKE-20260613-002 (dormant feature-switch / static-flag audit) IMPLEMENTED LOCALLY
     2026-06-21 as `lifeblood_feature_switch_audit` → recorded as the 2026-06-21
     receipt in lifeblood-tracking-archive.md. INV-FEATURE-SWITCH-001. Do not
     re-add here; the id must not live in both files. -->

<!-- LB-INTAKE-20260613-003 (dependants/dependencies grouping + filters) IMPLEMENTED LOCALLY
     2026-06-21 → recorded as the 2026-06-21 receipt in
     lifeblood-tracking-archive.md. INV-EDGE-GROUP-001. -->

<!-- LB-INTAKE-20260613-004 (authority coverage / negative dependency matrix)
     IMPLEMENTED LOCALLY 2026-06-22 as `lifeblood_authority_coverage` (graph-only
     subject-vs-authority reachability matrix) -> recorded as the 2026-06-22
     receipt in lifeblood-tracking-archive.md. INV-AUTHORITY-COVERAGE-001. Do not
     re-add here; the id must not live in both files. -->

<!-- LB-INTAKE-20260613-005 (intake ledger shape ratchet) IMPLEMENTED LOCALLY 2026-06-21 →
     recorded as the 2026-06-21 receipt in lifeblood-tracking-archive.md.
     Do not re-add here; the id must not live in both files. -->

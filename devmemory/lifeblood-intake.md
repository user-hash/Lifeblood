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

## LB-INTAKE-20260601-001 — Unity serialized/UnityEvent wiring invisible to reachability

Type: Improvement · Priority: HIGH (largest real false-positive driver)
Source: DAWG dogfood research pass 2026-06-01 (Lifeblood v0.7.11, server 1100895)
Workspace: DAWG

What: On a real Unity workspace `lifeblood_dead_code summarize` returned 215
production method candidates. The dominant false-positive class is pointer/event
handlers (`OnPadPointerDown`, `OnSamplerPadClick`, …): `find_references` = 0 AND
no `= OnPadPointerDown` delegate assignment exists in any `.cs` — they are wired
through prefab/scene `EventTrigger` UnityEvents (YAML on disk), invisible to
static analysis. Same root makes `enum_coverage` flag serialized-enum production
as candidate-only (`isUnproduced` not proof).

Verified accurate (NOT false positives): `BurstSynthSustainKernel.DrainComb` and
`ApplyOscDriftIfActive` 5-param overload are genuinely dead (grep +
overload-resolution read). Magic-methods (`IUnityReachabilityProvider`),
`[BurstCompile]`, and `unsafe` pointer-param overloads all resolve correctly.

Why it matters: forces manual triage of the whole dead_code list on Unity
projects; an Inspector-wired method reads identically to a genuinely-dead one.

Fix shape: new Unity-asset adapter behind a port — parse `.prefab`/`.unity`/
`.asset` YAML, resolve `m_Script: {guid}` → `.meta` GUID → C# type, extract
UnityEvent `m_PersistentCalls` method targets + serialized enum field values;
feed as reachability roots / produced-enum values. Honest residual boundary
(still invisible): runtime-procedural assignment, Addressables/Resources-loaded
values, unsaved Inspector edits.

## LB-INTAKE-20260601-002 — Static struct-layout / sizeof tool for unmanaged structs

Type: Improvement · Priority: HIGH (unlocks a ratchet that today requires Unity)
Source: DAWG dogfood research pass 2026-06-01 (Lifeblood v0.7.11, server 1100895)
Workspace: DAWG

What: `lifeblood_execute Unsafe.SizeOf<BurstVoiceState>()` correctly returns the
structured `INV-EXECUTE-WORKSPACE-LOAD-BOUNDARY-001` boundary (honest, new in
v0.7.11) — but the value is still unobtainable. DAWG ratchets
`sizeof(BurstVoiceState) = 1280B` against a 64KB/call stack watchpoint and must
round-trip to Unity to measure it. For `unmanaged`/blittable structs (all Burst
state) layout is fully determined by Roslyn metadata + ECMA-335 packing — no
runtime load needed.

Why it matters: struct-size / stack-frame invariants can't be ratcheted by
Lifeblood; the only gate is Unity `sizeof`.

Fix shape: new read-side `lifeblood_struct_layout(typeId)` (or extend
`lifeblood_static_tables`): walk the Roslyn type, compute per-field offset + size
+ alignment + total for unmanaged structs (honor `[StructLayout]`, `Pack`,
`[FieldOffset]`, `fixed` buffers, enum underlying types, nested structs,
Unity.Mathematics types). Exact for blittable; downgrade to `SequentialEquivalent`
+ confidence drop + named reason for reference-bearing `LayoutKind.Auto` structs.

## LB-INTAKE-20260601-003 — asmdef compile-direction boundary check

Type: Improvement · Priority: MEDIUM (cheap; data already loaded)
Source: DAWG dogfood research pass 2026-06-01 (Lifeblood v0.7.11, server 1100895)
Workspace: DAWG

What: `compile_check` ignores per-asmdef boundaries, so a reference that compiles
in the merged view can still violate an asmdef's declared reference set; the
Unity console is the only current honest gate (DAWG `feedback_asmdef_direction_check`).
Lifeblood already loads the 90-module map (= asmdefs) and the `.asmdef`
`references[]` are on disk.

Why it matters: illegal back-references / undeclared dependencies are invisible
to Lifeblood; they only surface at Unity compile time, defeating the pre-Unity
gate value.

Fix shape: extend `lifeblood_invariant_check` (or new `lifeblood_asmdef_check`):
for every cross-module edge assert the source asmdef declares the target in its
`references` set, else report a directed boundary violation with the offending
edge + first call site. Pure-static graph query.

## LB-INTAKE-20260601-004 — Vendored/third-party path exclusion for dead_code + analyze

Type: UX / Control · Priority: MEDIUM
Source: DAWG dogfood research pass 2026-06-01 (Lifeblood v0.7.11, server 1100895)
Workspace: DAWG

What: On the DAWG dead_code pass, ~12% of the first 25 production candidates were
third-party example code — `TMPro.Examples.TMP_TextInfoDebugTool.DrawSolidRectangle`,
`DrawDottedRectangle`, `TextConsoleSimulator.RevealWords` (all under
`Assets/TextMesh Pro/Examples & Extras/`) — all classified `bucket: Production`.
There is no way to exclude vendored / sample / package paths from `analyze` or
`dead_code` (analyze takes only `projectPath` / `graphPath` / `rulesPath` /
`mode` / `defineProfiles`; no exclude glob), and the bucket classifier treats
vendored example code as Production.

Why it matters: vendored noise pollutes dead_code triage and cycle/metric counts,
and a path-scoped analyze would also cut the full-analyze cost on large trees.

Fix shape: (a) an `excludePaths`/`vendorGlobs` parameter on `analyze` (and a
matching `pathExclude` on `dead_code`), and/or (b) extend the bucket classifier to
recognize known-vendored roots (`*/Examples*`, `*/Samples*`, `Packages/`,
third-party asset dirs) as a `Vendored` bucket distinct from `Production`.

## LB-INTAKE-20260601-005 — net10 source-generator concurrency isolation (deferred fix)

Type: Bug (latent) / Robustness · Priority: LOW until net10 is a real target
Source: DAWG dogfood research pass 2026-06-01 (Lifeblood v0.7.11, server 1100895), diagnosed + archived 2026-05-31
Workspace: Lifeblood self

What: Diagnosed + archived 2026-05-31 — net10's wider assembly-load window exposes
a race in framework source-generator loading/execution when MULTIPLE analyses run
concurrently in one process (the xunit suite). `BuildDiagnosticParityTests` +
`CsprojCompilationFactsTests.Compilation_RunsFrameworkSourceGenerators_*` pass in
isolation, fail in the full suite; self-analyze counts swing run-to-run
(4354/4349/4391). net8 reliably wins the race (deterministic 4385/25092).
Production is never affected (MCP serializes via `GraphSessionGate`; CLI is
one-shot). A speculative shared-loader/Lazy-cache patch was trialled, didn't fully
close the flake, perturbed net8 counts (+5), and was reverted.

Why it matters: blocks a clean net10 evaluation and is a latent hazard on any
future concurrent-analysis path; process-global Roslyn analyzer state is shared.

Fix shape: (deferred, its own atom) isolate framework-analyzer loading per
analysis via `AssemblyLoadContext`, OR serialize the generator-driver run so
concurrent in-process analyses cannot race on process-global analyzer state. Scope
`DocsTests.Anchor_MatchesLiveSource` self-analyze arms to the production TFM so an
experimental retarget does not assert net8 counts against a net10 build.

## LB-INTAKE-20260602-001 - Retained-session recovery after read-only analyze

Type: UX / Robustness · Priority: MEDIUM
Source: DAWG Burst WT/FM/PWM dogfood pass 2026-06-02 (Lifeblood v0.7.11, server 1100895)
Workspace: DAWG

What: During the DAWG Burst WT/FM/PWM dogfood pass, Lifeblood was the backbone for
safe work on a DAW-sized Unity project: multi-profile analyze scoped the actual
Editor/Player graph, dependency/reference tools kept patch-surface changes honest,
file compile checks caught import/descriptor issues before Unity, and grouped
blast/test/cycle signals let the agent keep moving without guessing. The workflow
value is high precisely because DAWG is too large and interconnected for grep-only
reasoning.

Funky bit: after an accidental `lifeblood_analyze(readOnly:true)` full analyze,
the session retained the DAWG graph but dropped compilation state. A later
non-read-only incremental analyze still left write-side tools unavailable; the
2026-06-02 capability receipt showed `hasGraphLoaded: true`,
`hasCompilationState: false`, `projectRoot: "D:/Projekti/DAWG"`, and
`retainedProfileNames: ["Editor", "Player"]`.

Why it matters: read-only analyze is the right escape hatch for huge workspaces,
but recovering from it is easy to fumble during long product work. The current
state is honest, but the next step is not obvious enough when the agent needs to
return from read-only triage to `diagnose`, `compile_check`, `find_references`, or
`rename`.

Fix shape: make the transition explicit and self-healing where safe. Options:
allow a non-read-only full analyze to reliably restore compilation state after a
read-only session; reject non-read-only incremental recovery with a precise
`canRetryFull` suggestion; and surface `hasCompilationState` plus the recovery
hint directly in write-side tool errors. Nice-to-have: a small
`lifeblood_session_state` or capabilities subfield that names the currently
available read/write mode and the exact analyze call needed to switch modes.

Follow-up observed 2026-06-02: after a successful non-read-only incremental
Editor+Player analyze on DAWG (70,015 symbols, 0 violations) and three successful
`compile_check(filePath)` calls, the next compile-check closed the MCP transport.
Subsequent `lifeblood_capabilities` calls in the same Codex session continued to
fail with `Transport closed`, so the agent had to finish verification with Unity
compile/tests. This is a stronger recovery/robustness case than "mode is
unavailable": the session-level tool channel became unusable after normal
write-side checks.

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

## LB-INTAKE-20260608-001 — MonoBehaviour magic-method reachability misses UIBehaviour/Graphic-derived components

Type: Bug · Priority: HIGH (false-positive dead-code on every custom UI component)
Source: DAWG architecture-sealing dogfood 2026-06-08 (Lifeblood v0.7.11, server 1100895)
Workspace: DAWG

What: `dead_code` flagged `VUMeter.Update()`, `WaveformScope.Update()`, `ADSRGraphView.Update()`,
`WaveformPreview.Update()`, `VUMeter.Reset()` as dead. All extend `UnityEngine.UI.Graphic`
(→ `UIBehaviour` → `MonoBehaviour`); `Update`/`OnEnable`/`Reset` are Unity magic methods invoked
by the runtime with no static caller. `IUnityReachabilityProvider` (INV-UNITY-001) excludes magic
methods on DIRECT MonoBehaviour subclasses but misses components deriving through the
`UIBehaviour`/`Graphic` chain (UnityEngine.UI.dll). Directly contradicts LB-INTAKE-20260601-001's
"magic-methods resolve correctly" — true for direct subclasses, false for UI-derived.

Verified: read class declarations (`public class VUMeter : Graphic`, `public class WaveformScope : Graphic`);
flagged members are instance `Update()`/`Reset()`.

Why it matters: every custom `Graphic`/`Selectable`/`UIBehaviour` component's lifecycle methods read
as dead → large FP class on any Unity UI project.

Fix shape: change the magic-method exclusion test to "type transitively derives from
`UnityEngine.MonoBehaviour`" (walk the full base chain incl. UnityEngine.UI assembly), not
"directly derives". Cover editor magic (`Reset`, `OnValidate`) too.

## LB-INTAKE-20260608-002 — Editor+Player profile pair doesn't cover UNITY_STANDALONE (desktop-guarded call sites invisible)

Type: Improvement · Priority: HIGH (FP class for all desktop-only code paths)
Source: DAWG architecture-sealing dogfood 2026-06-08 (Lifeblood v0.7.11, server 1100895)
Workspace: DAWG

What: `BeatGridShutdownOrchestrator.HandleDesktopFocusLost()`/`HandleFocusReturn()` flagged dead;
`find_references` AND `dependants profileFilter:["Editor","Player"]` both returned 0. Source shows
they ARE called from `OnApplicationFocus()` (same file, lines 519/521) but inside
`#if UNITY_STANDALONE && !UNITY_EDITOR`. The canonical 2-profile MVP (`Editor`,`Player`) does not
define `UNITY_STANDALONE`, so desktop-standalone-only call sites are invisible under both — the
union does not help.

Verified: grep found the call sites; read confirmed the `#if UNITY_STANDALONE && !UNITY_EDITOR`
guard; union `dependants` = 0.

Why it matters: any `#if UNITY_STANDALONE*`/desktop-guarded handler (OS focus, file dialogs, desktop
input) is a guaranteed dead-code FP even when analyzing both Editor and Player. "Wire vs delete" is
dangerous — these look identically dead.

Fix shape: add a canonical "Standalone"/"DesktopPlayer" profile (or let `defineProfiles` accept a
target hint that sets `UNITY_STANDALONE` + `UNITY_STANDALONE_WIN/_OSX/_LINUX`). At minimum, have
`dead_code`/`find_references` emit a "would-be callers exist only under unanalyzed `#if`" hint when a
symbol's nearest references are behind inactive defines.

## LB-INTAKE-20260608-003 — dead_code flags intentional reference-free scaffolding types

Type: Improvement · Priority: LOW
Source: DAWG architecture-sealing dogfood 2026-06-08 (Lifeblood v0.7.11, server 1100895)
Workspace: DAWG

What: `dead_code` flagged Types `DawgToolsPackageAssert` (internal static; `[Conditional("UNITY_EDITOR")]`
methods that exist only to force asmdef package refs to compile) and `AudioCallbackSchedulerInvariant`
(internal static holding one `const string Id = "INV-…"` documentation anchor). Both are reference-free
BY DESIGN.

Verified: read both files; confirmed intentional-scaffolding patterns.

Why it matters: minor triage noise; deleting `DawgToolsPackageAssert` would silently drop a deliberate
compile-time package guard.

Fix shape: optional `bucket: Scaffolding` for types whose members are all `[Conditional]` and/or whose
body is only `const` id strings; downrank from dead-code candidates. Documentable instead.

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

## LB-INTAKE-20260611-001 — execute: blocked Assembly.Load leaves declared-member-count queries with no lane

Type: Improvement · Priority: MEDIUM
Source: DAWG session 2026-06-11 (Lifeblood v0.7.11, server `1100895`), ABG member-count ratchet triage
Workspace: DAWG

What: A DAWG ratchet (`AbgMemberCountRatchetTests`) pins reflection-declared
member count (GetMethods/Fields/Properties/Events/Ctors, DeclaredOnly,
CompilerGenerated-filtered) at a ceiling. When the Unity test runner was
unavailable (editor restart), the natural fallback
`lifeblood_execute Assembly.LoadFrom(Library/ScriptAssemblies/...)` was
refused with `Blocked pattern detected: Assembly.Load` (correct per the
workspace-load boundary). The Graph fallback
(`Graph.Symbols.Where(s => s.ParentId == abg.Id)`) returns a DIFFERENT count
(2077 vs reflection's 2051): source-symbol semantics include nested types and
diverge from reflection's event/ctor/backing-field accounting — unusable for
the ratchet's number.

Why it matters: declared-member-count ratchets are a common architecture-debt
gate; today they can only be verified through a live Unity test run. Lifeblood
is the natural offline gate but neither lane produces the reflection-equivalent
number.

Fix shape: read-side `lifeblood_member_count(typeId, semantics: "reflectionDeclared" | "sourceSymbols")`
(or a documented Graph recipe) that reproduces reflection DeclaredOnly counting
(methods incl. accessors, fields excl. compiler-generated backing for counted
events, properties, events, ctors; nested types excluded). Honest delta table
in docs for source-vs-reflection accounting.

## LB-INTAKE-20260611-002 — execute: Symbol API misguesses cost iterations; CS1061 errors carry no member hint

Type: UX · Priority: LOW
Source: DAWG session 2026-06-11 (Lifeblood v0.7.11), ABG member-count fallback attempt
Workspace: DAWG

What: First `lifeblood_execute` attempt guessed `s.CanonicalId` /
`s.ContainingTypeId` on `Symbol` (actual members: `Name`, `Kind`, `ParentId`).
The failure surfaced as a bare compiler `CS1061` with no nudge toward the
actual Symbol surface; one extra round-trip to discover the shape by trial.

Why it matters: every execute consumer re-learns the Symbol/Graph object model
by guessing; canonical-id strings (used everywhere else in the MCP surface)
don't match the in-script property names, which invites exactly this misguess.

Fix shape: when execute compilation fails with CS1061 on a known Lifeblood
script-API type (Symbol, Graph, Compilations...), append the type's actual
public member list (or a one-line `Help` pointer) to the error payload.

## LB-INTAKE-20260611-003 — incremental analyze degrades to full sweep after Unity domain reload (mtime-based change detection)

Type: Optimization · Priority: MEDIUM
Source: DAWG session 2026-06-11 (Lifeblood v0.7.11), gen-3→gen-4 analyze receipts
Workspace: DAWG

What: Same-session incremental analyzes touched 431 then 951 changed files
(10-18s). After a Unity editor restart + several domain reloads, the next
`incremental:true` analyze reported `changedFileCount: 3741` — every source
file in the workspace — and took 43.5s wall / 2.2GB peak despite only ~12
files having real content changes since the prior generation.

Why it matters: Unity touches file metadata wholesale on reimports; an
mtime-keyed change detector turns routine editor lifecycle events into
full-graph rebuilds, eating the incremental lane's entire benefit exactly when
sessions are most active.

Fix shape: content-hash (or size+hash hybrid) change detection for the
incremental scope decision, with mtime as a cheap pre-filter only (hash check
on mtime-changed files before counting them as changed). Receipt could then
report `mtimeTouched` vs `contentChanged` separately.

## LB-INTAKE-20260611-004 — Dead-WIRE audit: read-without-write fields, never-assigned binding slots, never-fired events

Type: Improvement · Priority: HIGH (today's dominant real-bug class; complements 20260601-001)
Source: DAWG session 2026-06-11 (Lifeblood v0.7.11), MP polish pass root-causing
Workspace: DAWG

What: Four shipped DAWG bugs in ONE day share a shape that no current tool
catches: code that compiles green but is structurally unplugged at runtime.
Receipts (all verified + fixed 2026-06-11):
- `MultiplayerUI._gridParent` + `_tabIndicatorParent`: private fields READ by
  guards/usages but with ZERO assignment sites anywhere — the entire in-grid
  presence layer silently dead since an extraction.
- `SetMuteCommand`: constructed ONLY inside remote-apply lanes; zero local
  dispatch sites — the full mute wire existed end-to-end and nothing ever sent.
- `BeatGridMpClockSync.DrainToInbox`: effectively-empty body (flag clear only)
  on the consumer end of a complete clock system — free-run drift root cause.
- `MultiplayerManager.SendCursorUpdate`: only caller passed `Vector2.zero`
  (constant-argument-only call sites = degenerate use).
`dead_code` misses all four: every symbol HAS references — the defect is the
DIRECTION/COMPLETENESS of the wiring, not reachability.

Why it matters: extraction-heavy codebases sever runtime-only wiring while
compiles + reference counts stay green. This is the recurring DAWG bug class
(also: TabsContainer eager-null, melodic-trail "0 callers but live" from June
memories).

Fix shape: new read-side `lifeblood_wire_audit` (or dead_code mode) emitting:
(a) private/internal fields with ≥1 read site and 0 write sites (excluding
ctor-default), (b) delegate-typed members (Action/Func fields on *Bindings/
*Context classes) with 0 assignment sites across all composition roots,
(c) events with subscribers but 0 fire sites and vice versa, (d) methods whose
only call sites pass compile-time-constant degenerate args (stretch). (a)-(c)
are pure Roslyn semantics — no Unity assets needed, unlike 20260601-001.
`lifeblood_assignment_coverage` already covers per-construction-site slot
coverage; this generalizes it to a workspace sweep with zero-assignment as the
red flag.

## LB-INTAKE-20260611-005 — Unity editor sync: domain-reload hook + authoritative changed-set

Type: Improvement · Priority: MEDIUM (pairs with 20260611-003)
Source: DAWG session 2026-06-11 (Lifeblood v0.7.11), repeated stale-graph cycles
Workspace: DAWG

What: Lifeblood and the Unity editor compile independently; after every Unity
domain reload the graph is stale and the first query in an active session pays
either a staleness-warning round-trip or (post-restart) the 43.5s mtime
full-sweep from 20260611-003. The session cadence today was
edit → refresh_unity → query → manual re-analyze, many times.

Why it matters: in an MCP-driven workflow Unity already KNOWS the
authoritative changed-set (its compilation pipeline inputs); Lifeblood
re-derives it badly from mtimes.

Fix shape: (a) an optional Unity-side hook (editor package or MCP custom tool)
that posts "compilation finished + changed source list" to the Lifeblood
server, triggering a warm incremental with an exact scope; (b) failing that,
read `Library/ScriptAssemblies` assembly timestamps to bound which asmdefs
changed and scope the incremental to their source globs. Either kills both the
stale-first-query and the full-sweep degradation.

## LB-INTAKE-20260613-001 — Call-site argument/default-parameter facts

Type: Improvement · Priority: HIGH
Source: DAWG pattern-engine planning pass 2026-06-13 (Lifeblood v0.7.11+1100895), pattern sustain investigation
Workspace: DAWG

What: Lifeblood correctly resolved that `PatternGeneratorController.BuildMonoPattern(...)`
has exactly two callers, `BuildPolyPattern(...)` has exactly five callers, and
both depend on `GeneratedNote..ctor(int,int,byte,int)`. It did NOT expose the
critical fact that both call sites omit the optional `lengthSteps` constructor
argument, so every generated melodic note defaults to one step even though the
downstream clip path already consumes `GeneratedNote.LengthSteps`. The agent had
to read `PatternGeneratorController.cs` directly to verify the omitted argument.

Why it matters: API adoption bugs often look exactly like this: a richer model
or new optional parameter exists, all callers still use the old argument shape,
and semantic "callee is referenced" checks look green. This is a real planning
accuracy gap for feature migration work (sustain, new flags, policy params,
quality knobs).

Fix shape: add `lifeblood_callsite_arguments(symbolId)` or enrich
`dependencies` / `find_references` with bound argument facts: callee parameter
name/type/ordinal, supplied vs omitted, default value used, constant/literal vs
member/reference expression kind, receiver, and call-site span. Include summary
histograms such as "parameter X omitted by 7/7 call sites" and filters by
production/test bucket.

## LB-INTAKE-20260613-002 — Dormant feature-switch / static-flag audit

Type: Improvement · Priority: HIGH
Source: DAWG pattern-engine planning pass 2026-06-13 (Lifeblood v0.7.11+1100895), grammar activation check
Workspace: DAWG

What: `BeatGridPatternEngine.UseGrammarGeneration` is referenced by the live
pattern generator branches, but defaults to `false`. `SetGrammarMode(bool)` has
zero semantic callers, while `InitializeGrammarSystem()` is called from bootstrap.
Lifeblood exposed the individual facts (`dependants` on the field/setter/init),
but there is no single audit that says "this feature branch is initialized but
has no live activation authority; the grammar path is dormant."

Why it matters: dormant infrastructure is easy to mislabel as shipped behavior.
In the pattern pass this distinction changed the plan: grammar became a future
activation lane behind ratchets, not the current user-facing generator. The same
shape applies to static feature flags, config toggles, migration gates, and
compile-time fallback switches across large products.

Fix shape: add a `lifeblood_feature_switch_audit` or `wire_audit` mode for
static/instance bool fields and properties used in branch conditions. Report
default/initializer value, assignment sites, public setter/mutator dependants,
branch-gated methods, production/test bucket breakdown, and a verdict like
`AlwaysDefaultInGraph`, `TestOnlyActivation`, or `RuntimeMutable`.

<!-- LB-INTAKE-20260613-003 (dependants/dependencies grouping + filters) SHIPPED
     2026-06-21 → archived as the 2026-06-21 receipt in
     lifeblood-tracking-archive.md. INV-EDGE-GROUP-001. -->

## LB-INTAKE-20260613-004 — Authority coverage / negative dependency matrix

Type: Improvement · Priority: MEDIUM
Source: DAWG pattern-engine planning pass 2026-06-13 (Lifeblood v0.7.11+1100895), preset-aware generation check
Workspace: DAWG

What: The pattern plan needed to know whether random generation actually reads
current instrument preset state. Lifeblood proved `BeatGridState.InstrumentPresets`
is real state and has production dependants, and source reads proved
`PatternGeneratorController` generation methods choose pattern shape by genre
but not by the selected Bass/Groove/Synth/Electric/Vocal/Guitar/Arp preset.
There is no direct tool for "given this method family, does every method reach
one of these authority symbols, and which ones do not?"

Why it matters: architecture bugs are often missing dependencies, not extra
dependencies: a controller compiles and runs, but ignores the intended authority
(preset, edition, theme, policy, locale, save state). Manual proof requires
combining `dependants`, `dependencies`, blast radius, and source reads.

Fix shape: new read-side `lifeblood_authority_coverage`:
inputs are `subjects[]` (methods/types/files) and `requiredAuthority[]`
(symbols/types/namespaces/files), optional `allowedAlternatives[]`, max depth,
and bucket filters. Output a matrix of subject -> reaches authority? direct or
transitive path preview, missing authorities, and first competing authority
actually used. This complements `wire_audit`: it catches "wired to the wrong or
incomplete source of truth" rather than zero wiring.

<!-- LB-INTAKE-20260613-005 (intake ledger shape ratchet) SHIPPED 2026-06-21 →
     archived as the 2026-06-21 receipt in lifeblood-tracking-archive.md.
     Do not re-add here; the id must not live in both files. -->

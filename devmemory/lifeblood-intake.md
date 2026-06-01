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

---

## Refuted this pass (do not re-investigate)

- **No `unsafe` pointer-param extraction gap.** `ApplyOscDriftIfActive(BurstPatch*, …)`
  overload A is correctly seen live (called by RenderMono/Stereo with
  `pitchBendFactor`); overload B is correctly dead. dead_code is accurate on the
  Burst kernel, including pointer/ref-param overload resolution.

## DAWG-side findings (NOT Lifeblood issues — for the DAWG burst owner)

- `BurstSynthSustainKernel.DrainComb` (Comb.cs:95) — genuinely dead leftover,
  superseded by `DrainCombFilterState`.
- `ApplyOscDriftIfActive` 5-param overload (OscDrift.cs:172) — dead unused
  convenience overload; only the 6-param overload + `TestOnly_` wrapper are live.

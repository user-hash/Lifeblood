# Lifeblood Intake - un-started findings and feature requests

Un-prioritized intake. Items here are NOT yet started. The ratcheted ledger
[`lifeblood-tracking.md`](lifeblood-tracking.md) holds only Shipped + in-flight
work (`TrackingLedger_HasNoPlainOpenOrCandidateEntries` forbids parked Open
items), so new findings land here first. When work begins, promote the item:
ship it and record it directly in the ledger as Shipped, or Partially shipped
with a `Remaining open work:` line, then delete it from this intake.

Live file hygiene:
- Keep only active product feedback here.
- Do not park shipped receipt comments in this file; use
  [`lifeblood-tracking-archive.md`](lifeblood-tracking-archive.md).
- Do not log DAWG architecture debt unless it exposes a Lifeblood product gap.
- Every entry should be usable by someone who was not present for the dogfood
  session.

Current dogfood rating for DAWG/Burst work: **8.5/10**. Lifeblood is now strong
for structural truth, dependency tracing, file compile checks, and large Unity
workspace baselining. The remaining pain is not correctness of the existing
answers; it is missing first-class workflows for value-domain DSP/math audits,
multi-file verification, and source-comment drift.

---

## LB-INTAKE-20260629-001 - Semantic contract-pattern query for unchecked control math

Type: Feature request
Priority: High
Source: DAWG Burst DSP dogfood, 2026-06-27 to 2026-06-29; Lifeblood local `v0.7.12-0-gdbfd871`
Workspace: DAWG
Rating for DAWG work: 9/10 value if shipped

What:
- DAWG's Burst bug hunts repeatedly needed the same question: "which consumers
  feed a bounded control value into trig, gain, pan, filter, or direct DSP math
  without a local clamp or domain conversion?"
- Lifeblood could prove symbols, edges, compile state, and graph structure, but
  this value-domain search still fell back to manual source reads plus `rg`
  sweeps over `DspMath.Sin`, `DspMath.Cos`, pan formulas, and gain consumers.

Why it matters:
- The production failures were not broad architecture failures; they were small
  contract breaks at math seams. A semantic operation-pattern query would catch
  siblings of that bug class faster and with less hotpatch risk.
- This matters for any real-time DSP, game physics, animation, serialization, or
  UI-control system where caller values must be clamped, normalized, converted,
  or otherwise proven before reaching sensitive math.

Fix shape:
- Add a first-class operation-pattern tool, or extend `lifeblood_execute` with a
  documented recipe, that can search IOperation trees by callee, argument source,
  field/parameter flow, and required guard shapes.
- Minimum useful predicates: called method name/id, containing module/bucket,
  argument originates from field/parameter/property, argument passes through
  `math.clamp` or a named clamp helper, argument is compared against constants,
  and result feeds assignment/multiply/trig/filter calls.
- Response should group by declaring type/file and include compact evidence:
  callsite span, callee, argument expression, detected guard or missing guard,
  and profile scope.

## LB-INTAKE-20260629-002 - Batch compile-check for changed file sets

Type: Optimization
Priority: Medium
Source: DAWG Burst and tuning dogfood sessions, 2026-06-27 to 2026-06-29; Lifeblood local `v0.7.12-0-gdbfd871`
Workspace: DAWG
Rating for DAWG work: 8/10 value if shipped

What:
- `lifeblood_compile_check` currently accepts one `code` snippet or one
  `filePath`. During DAWG sessions, natural verification atoms often touched a
  small set of related files and tests; each file needed a separate compile
  check or a Unity compile loop.
- The current single-file shape is precise, but it makes repeated verification
  slower and easier to under-run when an edit spans kernel, dispatch, tests, and
  documentation guard files.

Why it matters:
- Lifeblood already owns the loaded compilation and stale-refresh contract. A
  batch shape would amortize workspace refresh cost, reduce agent/tool chatter,
  and make "verify every touched C# file" a single auditable receipt.
- For DAWG, this is especially useful when Unity is open or MCP is unavailable
  and batchmode test execution would collide with the editor.

Fix shape:
- Extend `lifeblood_compile_check` with `filePaths: string[]`, or add a sibling
  `lifeblood_compile_check_batch`.
- Return an aggregate status plus one result per file: owning module, profile
  scope, diagnostics, stale-refresh mode, and any file that could not be mapped
  to a compilation.
- Keep the existing single-file response stable; batch mode can be an additive
  shape.

## LB-INTAKE-20260629-003 - Operation-walking tools need multi-profile support

Type: Improvement
Priority: High
Source: DAWG Unity/Burst dogfood, 2026-06-29; schema review of `lifeblood_wire_audit` and `lifeblood_static_tables`
Workspace: DAWG
Rating for DAWG work: 8/10 value if shipped

What:
- Lifeblood's operation-walking tools document that `profileScope` must match
  the retained profile from the most recent analyze. In a Unity workspace,
  DAWG usually analyzes `Editor` and `Player` together, but operation-level
  audits cannot freely ask the same question against Player-only code without
  changing the retained profile.

Why it matters:
- Burst and runtime-only bugs often live behind Player or platform define sets.
  Graph-level multi-profile analysis is useful, but operation-exact tools are
  where value-domain and wiring bugs become actionable.
- The current limit is honest and safe; it is still a workflow gap for Unity
  dogfood because the user thinks in "Editor plus Player" while the operation
  tool answers one retained compilation profile.

Fix shape:
- Allow operation-walking tools to run against any loaded define profile from
  the retained session, or expose a fast profile-switch/re-analyze path that is
  explicit in the response.
- Responses should include `profileScope`, `availableProfiles`, and clear
  failure guidance when a requested profile was not retained.

## LB-INTAKE-20260629-004 - Source-comment drift audit for retired authority prose

Type: Feature request
Priority: Medium
Source: DAWG Burst migration dogfood, 2026-06-27 to 2026-06-29; Lifeblood local `v0.7.12-0-gdbfd871`
Workspace: DAWG
Rating for DAWG work: 7/10 value if shipped

What:
- DAWG's Burst work exposed stale source prose and comments that still described
  managed DSP mirror/parity ideas after Burst had become the production DSP
  authority. Lifeblood can inspect symbols and docs/invariants, but there is no
  focused tool for finding source comments whose authority wording is stale,
  retired, or inconsistent with the current invariant tree.

Why it matters:
- Stale comments changed investigation behavior. They did not break compiled
  code, but they pulled attention toward retired managed-DSP comparisons and
  away from the Burst math contract that mattered.
- This is a product-level Lifeblood opportunity because it joins semantic code
  evidence with the instruction/invariant corpus Lifeblood already parses.

Fix shape:
- Add a comment/prose drift audit that scans source comments and XML docs for
  forbidden or retired terms supplied by the caller or invariant tree.
- Useful outputs: file/line, matched phrase, nearby symbol, referenced invariant
  or rule, confidence, and suggested action category: delete, update authority,
  or keep because the seam still exists.
- Keep it advisory. The tool should not claim a comment is wrong without either
  a caller-supplied retired-term list or a resolved invariant/rule conflict.

## LB-INTAKE-20260629-005 - Dogfood feedback capture command

Type: UX
Priority: Low
Source: DAWG + Lifeblood maintenance session, 2026-06-29; Lifeblood local `v0.7.12-0-gdbfd871`
Workspace: DAWG and Lifeblood self
Rating for DAWG work: 6/10 value if shipped

What:
- Valuable tool feedback currently lands by manually editing
  `devmemory/lifeblood-intake.md`. That keeps the repo simple, but it is easy to
  leave behind stale shipped comments, mix DAWG debt with Lifeblood product
  feedback, or forget a rating/source/version while moving fast.

Why it matters:
- Lifeblood is being improved through heavy dogfood loops. A small capture lane
  would preserve provenance without turning every observation into an immediate
  engineering task.
- This would also keep the strict tracking ledger clean while making intake
  maintenance less manual.

Fix shape:
- Add a CLI or script command that appends a valid intake entry from structured
  prompts or arguments: type, priority, source, workspace, rating, what, why,
  fix shape.
- The command should reject duplicate IDs, keep entries ASCII/Markdown-clean,
  and optionally run `IntakeLedgerTests` after writing.

## LB-INTAKE-20260629-006 - Numeric domain and unit contract audit

Type: Feature request
Priority: High
Source: DAWG DSP/Burst dogfood, 2026-06-29; Lifeblood local `v0.7.12-0-gdbfd871`
Workspace: DAWG
Rating for DAWG work: 10/10 value if shipped

What:
- Many hard audio bugs are value-miscommunication bugs: percent vs normalized,
  milliseconds vs samples, frames vs stereo samples, Hz vs normalized cutoff,
  cents vs semitones, dB vs linear gain, phase cycles vs radians, BPM beats vs
  seconds.
- Lifeblood can show call edges, but it does not yet infer or check that values
  keep the same numeric domain across fields, parameters, DTOs, dispatchers,
  kernels, tests, and UI controls.

Why it matters:
- This is the class of bug that looks like "the code is wired" while audio is
  wrong. It is also common outside audio: physics units, animation time,
  networking ticks, layout pixels, and serialization sizes all fail this way.
- A generic unit/domain pass would catch whole families of DSP and sync bugs
  without hardcoding any DAWG parameter names.

Fix shape:
- Add a `lifeblood_numeric_contract_audit` style tool that derives candidate
  domains from names, XML docs, attributes, constants, range tables, static
  manifests, and caller-supplied contract maps.
- Report domain crossings where no explicit conversion helper, clamp, scale, or
  documented adapter exists.
- Output should include source symbol, target symbol, inferred source/target
  domain, confidence, evidence, and the conversion point if one was found.
- Keep inference advisory; allow projects to promote inferred domains into a
  checked contract file.

## LB-INTAKE-20260629-007 - Buffer shape, stride, and sidecar contract audit

Type: Feature request
Priority: High
Source: DAWG DSP/Burst dogfood, 2026-06-29; Lifeblood local `v0.7.12-0-gdbfd871`
Workspace: DAWG
Rating for DAWG work: 9/10 value if shipped

What:
- DAWG investigations repeatedly raised buffer-shape questions: frame count vs
  sample count, mono vs stereo, interleaved vs planar, sidecar array length,
  oversample ratio, ring-buffer wrap, mask width, and per-slot state array
  alignment.
- Lifeblood has structural graph facts, but no first-class way to ask whether
  producer and consumer agree on buffer shape.

Why it matters:
- A single length/stride/channel mismatch can create deterministic crackles,
  delayed pops, alias-like garbage, or silent memory corruption in unsafe/Burst
  style code.
- This is not audio-specific. The same shape class appears in image buffers,
  networking packets, ECS component arrays, ML tensors, and binary serializers.

Fix shape:
- Add a buffer/array contract audit that traces arrays, spans, native arrays,
  pointer parameters, and count/stride companion arguments across calls.
- Detect likely mismatches: `frames` used as sample length, `channels` ignored,
  mask bit-width narrower than enum/slot count, sidecar arrays indexed by a
  different dimension, and copy loops whose bound differs from the target
  buffer length.
- Let callers supply generic dimension labels such as `frames`, `samples`,
  `channels`, `voices`, `slots`, `oversampleRatio`, and `maskBits`.
- Return compact evidence with source/target symbols, index expression, loop
  bound, companion parameter, and confidence.

## LB-INTAKE-20260629-008 - Temporal DSP state lifecycle audit

Type: Feature request
Priority: High
Source: DAWG release-tail and filter-glitch dogfood, 2026-06-29; Lifeblood local `v0.7.12-0-gdbfd871`
Workspace: DAWG
Rating for DAWG work: 10/10 value if shipped

What:
- The hardest DAWG audio failures lived in temporal state: a note is releasing,
  a filter/reverb/tail still has energy, then state is reset, retired, reused,
  or bypassed at a threshold that does not match the audible lifecycle.
- Lifeblood can find state fields and callers, but it does not yet classify
  state writes that happen during lifecycle transitions such as attack, release,
  retire, tail drain, gate close, buffer boundary, or voice reuse.

Why it matters:
- Pops and crackles often come from discontinuities, not from the oscillator
  formula itself. Static dependency graphs do not make temporal discontinuity
  risk visible enough.
- The tool would be useful for DSP, animation state machines, gameplay cooldowns,
  pooling systems, and network reconnect/resync flows.

Fix shape:
- Add a lifecycle-state audit that finds state fields written or zeroed inside
  branches involving names/contracts like `release`, `retire`, `tail`, `active`,
  `gate`, `reset`, `dispose`, `reuse`, `pool`, `phase`, or caller-supplied
  lifecycle markers.
- Report early-reset risks where the reset threshold differs from the final idle
  threshold, or where a stateful filter/delay/ring/phase object is cleared while
  the owning entity can still output non-zero values.
- Include evidence: state member, write expression, guard condition, lifecycle
  variable, adjacent thresholds, and downstream read sites.

## LB-INTAKE-20260629-009 - Control-rate automation smoothing audit

Type: Feature request
Priority: High
Source: DAWG filter sweep / LFO / pan dogfood, 2026-06-29; Lifeblood local `v0.7.12-0-gdbfd871`
Workspace: DAWG
Rating for DAWG work: 9/10 value if shipped

What:
- DAWG glitches exposed the need to prove that fast-moving controls are smoothed
  or otherwise safe before hitting audio-rate math. Examples include filter
  cutoff, resonance, pan, gain, LFO depth, drive, delay time, and reverb mix.
- Existing tools can show the field is referenced, but not whether a control
  path crosses from UI/control-rate state into sample-rate DSP without a
  smoother, slew limiter, coefficient interpolation, or bounded direct lane.

Why it matters:
- Zipper noise, metallic stepping, and deterministic crackle commonly come from
  discontinuous automation, especially when values feed filters or time-varying
  delay lines.
- This should be generic: any project can label "control-rate input" and
  "sample-rate consumer" contracts without Lifeblood knowing the product.

Fix shape:
- Add a control-rate audit that traces fields/parameters from UI, automation,
  LFO, MIDI, network, or serialized state into per-sample loops.
- Detect missing smoothing on caller-supplied sensitive consumers: filter
  coefficient update, pan/gain multiply, delay read position, oscillator phase
  increment, wavetable index, saturation drive, and custom method IDs.
- Return the path, detected smoother/interpolator if present, direct writes if
  absent, and whether the consumer is inside a loop identified as sample-rate.

## LB-INTAKE-20260629-010 - Clock, cadence, and sync contract audit

Type: Feature request
Priority: High
Source: DAWG step pattern / BPM / sample-position dogfood, 2026-06-29; Lifeblood local `v0.7.12-0-gdbfd871`
Workspace: DAWG
Rating for DAWG work: 9/10 value if shipped

What:
- DAWG debugging depended on mapping pattern columns, BPM, samples, recorded
  audio, note gates, and visual playback cursors. Lifeblood does not yet expose
  a generic way to audit conversions across beats, bars, seconds, samples,
  frames, ticks, buffers, and UI steps.

Why it matters:
- Sync bugs often look like audio or graphics bugs because the event is correct
  but placed in the wrong clock domain. A 1-buffer, 1-step, or 1-frame offset can
  be perfectly deterministic and still very hard to see in code review.
- This also applies to multiplayer replication, animation timelines, video,
  sequencers, schedulers, and streaming systems.

Fix shape:
- Add a clock-domain audit that identifies time/cadence variables and conversion
  formulas, then traces them across scheduling boundaries.
- Support caller-authored domains such as `samples`, `frames`, `seconds`,
  `beats`, `bars`, `ticks`, `steps`, `buffers`, and `networkSeq`.
- Flag suspicious direct assignments, integer truncation, modulo/wrap boundaries,
  off-by-one comparisons, buffer-start vs buffer-end scheduling, and mismatched
  sample-rate/BPM constants.
- Return a conversion graph with formulas and source spans.

## LB-INTAKE-20260629-011 - Bitmask, field-mask, and enum-width consistency audit

Type: Improvement
Priority: Medium
Source: DAWG Burst field-mask and sidecar dogfood, 2026-06-29; Lifeblood local `v0.7.12-0-gdbfd871`
Workspace: DAWG
Rating for DAWG work: 8/10 value if shipped

What:
- DAWG Burst work uses masks and field tables to decide what state is copied,
  initialized, sidecar-routed, or dispatched. A mask-width mismatch or stale enum
  table can silently skip a field even when all symbols compile.

Why it matters:
- Mask/table drift is a strong source of "wired but not actually updated" bugs:
  parameters appear in UI, DTOs, or tests, but do not reach the engine state.
- The pattern is generic for feature flags, ECS component masks, serialization
  dirty bits, network replication fields, and hardware register abstractions.

Fix shape:
- Extend enum/table tooling with a mask-width audit: enum max ordinal vs backing
  integer width, shift expression width, table length, mask constants, and
  switch/array coverage.
- Detect `1 << ordinal` vs `1UL << ordinal` risk, signed overflow, duplicate
  bit assignments, missing enum members in static tables, and table entries that
  exist but are never consumed.
- Allow projects to mark authoritative enum/table pairs through a manifest or
  attributes instead of hardcoding names.

## LB-INTAKE-20260629-012 - Cross-layer control-law trace

Type: Feature request
Priority: High
Source: DAWG tuning GUI to Burst DSP dogfood, 2026-06-29; Lifeblood local `v0.7.12-0-gdbfd871`
Workspace: DAWG
Rating for DAWG work: 10/10 value if shipped

What:
- A frequent DAWG risk is miscommunication between UI controls, stored presets,
  tuning snapshots, dispatch DTOs, and Burst kernels. A slider may show percent,
  a snapshot may store normalized, a kernel may expect Hz, and tests may only
  verify that fields exist.

Why it matters:
- This is exactly where "not hardcoded" matters: the same Lifeblood feature
  should work for any product that has user-facing controls feeding an engine.
- Structural references alone cannot prove the control law is preserved.

Fix shape:
- Add a cross-layer trace that starts at a caller-selected public control,
  serialized field, DTO member, enum entry, or static parameter manifest and
  follows it to engine consumers.
- For every hop, report the value domain, conversion function, clamp/range,
  default, smoothing, and consumer type.
- Highlight missing hops, multiple incompatible conversions, bypass lanes,
  tests that assert existence but not value behavior, and docs/comments that
  state a different domain from the code.
- Make contracts project-authored through a manifest so Lifeblood stays generic.

## LB-INTAKE-20260629-013 - Generated DSP/math probe recipe

Type: Feature request
Priority: Medium
Source: DAWG Burst regression-test dogfood, 2026-06-29; Lifeblood local `v0.7.12-0-gdbfd871`
Workspace: DAWG
Rating for DAWG work: 8/10 value if shipped

What:
- DAWG needed tests that render longer than a note, compare static vs swept
  controls, detect discontinuities, and verify final output instead of only
  checking wiring.
- Lifeblood can compile-check and execute C# snippets, but it does not yet guide
  agents toward a reusable generated-probe pattern for math/DSP systems.

Why it matters:
- The best fix loop combines static analysis with measured output. A generic
  probe recipe would help agents create meaningful tests without hardcoding a
  specific synth, preset, or DAWG path into Lifeblood itself.

Fix shape:
- Provide a documented or tool-assisted "probe generator" pattern: caller
  supplies setup code, run length, input schedule, changed controls, and output
  invariant; Lifeblood helps scaffold a test body and compile-check it.
- Suggested generic invariants: finite samples, max adjacent delta, RMS envelope
  continuity, silence-window noise floor, static-vs-swept high-band delta,
  no denormals/NaN/Inf, no output after declared tail, and deterministic
  repeated-run output.
- Keep execution in the user's test framework/project; Lifeblood should produce
  scaffolding and structural checks, not own audio playback.

## LB-INTAKE-20260629-014 - Mutable static and shared-state audit for Burst-style code

Type: Improvement
Priority: Medium
Source: DAWG Burst migration dogfood, 2026-06-29; Lifeblood local `v0.7.12-0-gdbfd871`
Workspace: DAWG
Rating for DAWG work: 7/10 value if shipped

What:
- Real-time/Burst-style code is sensitive to hidden shared state: mutable
  statics, cached buffers, lookup tables that can be rewritten, static scratch
  arrays, and cross-voice/shared filter or smoother state.

Why it matters:
- These bugs often build over time, differ between first and later notes, or
  appear only under multiple voices/jobs. They can look like DSP math failures
  even when the local formula is fine.
- Generic detection helps any performance-oriented codebase, not only Unity
  Burst.

Fix shape:
- Add or extend an audit that identifies mutable static fields, shared scratch
  buffers, static properties with setters, and instance fields written from
  multiple scheduling contexts.
- Correlate findings with call graph entrypoints, job/kernel methods, async
  methods, thread callbacks, and user-supplied realtime method markers.
- Return evidence plus a risk bucket: readonly table, initialized-once cache,
  runtime mutable, shared scratch, or unknown.

## LB-INTAKE-20260629-015 - Discontinuity-risk lint for branchy math

Type: Feature request
Priority: Medium
Source: DAWG pop/crackle dogfood, 2026-06-29; Lifeblood local `v0.7.12-0-gdbfd871`
Workspace: DAWG
Rating for DAWG work: 8/10 value if shipped

What:
- DAWG's audible bugs often came from branch boundaries: different equations on
  either side of a threshold, hard zeroing, bypass toggles, retire thresholds,
  denormal floors, min/max gates, or fallback paths that introduce a sample
  discontinuity.

Why it matters:
- A static discontinuity-risk pass would not prove an audible bug, but it would
  put the riskiest math seams in front of the agent before random code reading.
- The concept applies to any continuous system: DSP, physics, animation,
  interpolation, camera motion, and control loops.

Fix shape:
- Add an advisory lint over numeric branches inside caller-selected hot methods
  or loops.
- Flag branches that return constants on one side and continuous values on the
  other, reset state, switch filters/modes, bypass processing, clamp abruptly, or
  compare against multiple nearby thresholds.
- Include the branch condition, returned/assigned expressions, affected state,
  and nearby downstream consumers. Let callers suppress known intentional hard
  gates through comments or a manifest.

## LB-INTAKE-20260629-016 - Generic "contract coverage" score for critical paths

Type: UX
Priority: Medium
Source: DAWG DSP/Burst dogfood, 2026-06-29; Lifeblood local `v0.7.12-0-gdbfd871`
Workspace: DAWG
Rating for DAWG work: 7/10 value if shipped

What:
- DAWG needs to know whether a critical path is merely referenced, or actually
  defended by contracts: range checks, unit conversions, smoothing, buffer-shape
  assertions, lifecycle tests, and output probes.

Why it matters:
- A single score would help prioritize audits without pretending to prove
  correctness. Low contract coverage means "read this before trusting it."
- This keeps Lifeblood from hardcoding DSP rules while still supporting
  general-purpose reliability work.

Fix shape:
- Add a critical-path report where callers provide a root symbol or route
  manifest. Lifeblood computes a coverage summary over the reachable path:
  numeric contracts, clamps, asserts, compile checks, tests that hit symbols,
  operation-profile coverage, comments with invariant IDs, and generated-probe
  presence.
- Return scores by category plus concrete missing-evidence links. Avoid a
  global quality number; make it a triage aid with named evidence gaps.

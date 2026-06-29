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

## LB-INTAKE-20260629-017 - Multi-profile analyze fallback must preserve requested profiles

Type: Bug
Priority: High
Source: DAWG tuning/Burst dogfood, 2026-06-29; Lifeblood local `v0.7.12+dbfd871`
Workspace: DAWG
Rating for DAWG work: 9/10 value if shipped

What:
- During a DAWG edit session, `lifeblood_analyze` was called with
  `defineProfiles:["Editor","Player"]`, `incremental:true`, and
  `allowFullFallback:true`.
- The request widened to `mode:"full"` because of
  `fallbackReason:"moduleDescriptorChanged"`, but the response reported
  `profileCount:1` and `activeProfiles:null` instead of preserving the
  requested Editor+Player profile set.

Why it matters:
- A fallback from incremental to full should widen analysis scope, not silently
  narrow preprocessor coverage. For Unity/Burst work, Player-only callsites and
  runtime-only failures are exactly the reason agents request multi-profile
  analysis.
- If the response says the analysis is `full` and clean but profile coverage
  collapsed, an agent can over-trust structural evidence for release/runtime
  code.

Fix shape:
- Preserve `AnalysisConfig.DefineProfiles` through every fallback path, including
  descriptor drift and asmdef/csproj drift.
- If a fallback cannot honor the requested profiles, reject loudly or return an
  explicit limitation that names `requestedProfiles` and `effectiveProfiles`.
- Add a regression test where incremental multi-profile analyze falls back to
  full because of descriptor drift and still reports the same active profiles.

## LB-INTAKE-20260629-018 - File-scope diagnose should resolve newly imported Unity files without explicit moduleName

Type: UX
Priority: Medium
Source: DAWG tuning UI ratchet session, 2026-06-29; Lifeblood local `v0.7.12+dbfd871`
Workspace: DAWG
Rating for DAWG work: 7/10 value if shipped

What:
- A new Unity EditMode test file had a `.meta` file and appeared in
  `Nebulae.Tests.Editor.Audio.csproj`.
- `lifeblood_diagnose(filePath:"Assets/Tests/Editor/Audio/TuningFxTonePresentationRatchetTests.cs")`
  returned no diagnostics but `resolvedModule:null`.
- The same file diagnosed with `moduleName:"Nebulae.Tests.Editor.Audio"`
  resolved correctly to that module.

Why it matters:
- Agents commonly add a test/source file, let Unity import it, then ask
  Lifeblood to diagnose the file by path. If the file is present in the generated
  project descriptor, the user should not need to know the exact asmdef module.
- A null module with no diagnostic can look harmless while still weakening the
  evidence receipt: the caller cannot tell whether Lifeblood checked the real
  owning compilation, a fallback parse, or an ambiguous path.

Fix shape:
- Strengthen file-path ownership resolution so a file included in exactly one
  compilation resolves that module automatically after descriptors include it.
- If multiple compilations match, return an ambiguity list with candidate
  modules and require `moduleName`.
- If no compilation matches, keep the existing stale-descriptor guidance, but
  make the reason explicit in `resolvedModule`/`limitations` instead of only
  returning clean diagnostics.

## LB-INTAKE-20260629-019 - Analyze changed-file accounting is hard to interpret under bounded incremental requests

Type: UX
Priority: Medium
Source: DAWG tuning UI ratchet session, 2026-06-29; Lifeblood local `v0.7.12+dbfd871`
Workspace: DAWG
Rating for DAWG work: 7/10 value if shipped

What:
- A bounded analyze call supplied three `authoritativeChangedFiles`, but the
  response reported `changedFileCount:199` / `changedSourceFiles:199` while
  also reporting `mtimeTouchedSourceFiles:1` and `contentChangedSourceFiles:1`.
- The numbers may be internally correct if descriptor/module fan-out widened the
  touched graph, but they are hard for an agent to explain as an evidence
  receipt.

Why it matters:
- In commit-as-you-go workflows, users care whether Lifeblood rechecked exactly
  the files in the current atom or silently widened to a much larger source set.
- Confusing changed-file counters make it harder to distinguish "only one file
  content changed", "199 files were re-extracted", and "199 files were considered
  because a descriptor changed".

Fix shape:
- Split response counts into distinct names: caller-supplied path count,
  resolved changed path count, descriptor/module fan-out file count, actual
  content-changed source count, and graph entries rebuilt.
- When `authoritativeChangedFiles` is supplied, echo the normalized accepted and
  rejected paths in compact form.
- Add a short `changeAccounting` explanation string or enum so the evidence
  receipt is readable without interpreting five counters by hand.

## LB-INTAKE-20260629-020 - Release metadata drift between local tag and changelog snapshot

Type: Docs
Priority: Medium
Source: Lifeblood tracker maintenance, 2026-06-29; local repo inspection
Workspace: Lifeblood self
Rating for DAWG work: 5/10 value if shipped

What:
- The local Lifeblood repo has tag `v0.7.12` at `dbfd871`, but
  `CHANGELOG.md` still links `[Unreleased]` as `v0.7.11...HEAD` and has no
  `[0.7.12]` section/link reference.
- `devmemory/lifeblood-tracking.md` was also still naming `v0.7.11` as the
  latest release snapshot before this maintenance pass clarified the local tag
  mismatch.

Why it matters:
- The tracker is used as dogfood provenance. If release metadata drifts, agents
  can cite the wrong version under test or miss that a local tag contains fixes
  not described in the changelog.
- This is especially risky for Lifeblood because tool behavior often changes
  through additive wire contracts and invariant IDs; version provenance matters.

Fix shape:
- Add a release-metadata ratchet that compares the latest semantic version tag
  against `CHANGELOG.md` headings and link references.
- Optionally ratchet the tracker snapshot to mention the latest tagged version
  or explicitly mark it as "latest changelog-tracked release".
- Keep the release checklist, but make drift visible in tests rather than relying
  on manual release hygiene.

## LB-INTAKE-20260629-021 - Realtime allocation and forbidden-API audit for hot paths

Type: Feature request
Priority: High
Source: DAWG Burst/DSP dogfood, 2026-06-29; Lifeblood local `v0.7.12+dbfd871`
Workspace: DAWG
Rating for DAWG work: 9/10 value if shipped

What:
- DSP/Burst-style code needs a generic way to ask whether a caller-selected hot
  path allocates, logs, formats strings, throws exceptions, uses LINQ/delegates,
  touches Unity APIs, or calls other APIs that are unsafe for realtime work.
- Lifeblood can show dependencies, but it does not yet classify realtime
  unsafety as a first-class audit over operation trees.

Why it matters:
- Audio glitches can come from GC pressure, logging, exception paths, or hidden
  managed work even when the math is correct.
- The same audit applies to physics loops, render loops, jobs, game networking,
  robotics, and embedded-style control loops. It should be caller-configured,
  not DAWG-specific.

Fix shape:
- Add a hot-path audit where callers provide root symbols, attributes, or naming
  patterns such as `Burst`, `Job`, `Audio`, `Render`, `Update`, or custom method
  IDs.
- Use semantic operation walking to flag object/array/delegate creation,
  closures, string interpolation/formatting, LINQ, reflection, exceptions,
  logging, locks, async waits, and caller-supplied forbidden APIs.
- Return grouped findings by hot root with callsite, operation kind, callee,
  allocation/forbidden category, and whether the path is direct or transitive.

## LB-INTAKE-20260629-022 - Hot-math constant provenance audit

Type: Feature request
Priority: Medium
Source: DAWG DSP/Burst dogfood, 2026-06-29; Lifeblood local `v0.7.12+dbfd871`
Workspace: DAWG
Rating for DAWG work: 8/10 value if shipped

What:
- DAWG audio work regularly depends on constants for thresholds, smoothing,
  denormal floors, cutoff mapping, time conversion, oversampling, release gates,
  and discontinuity boundaries.
- Lifeblood can find symbol references, but it does not yet distinguish a
  policy-owned constant from a magic literal embedded directly in hot math.

Why it matters:
- Small magic constants in DSP/math code can encode undocumented policy. If the
  same threshold is duplicated with slightly different values, behavior can
  drift while every symbol remains wired and compiling.
- This is generic for animation, physics, camera motion, filters, schedulers,
  networking timeouts, and numeric validation code.

Fix shape:
- Add an audit over caller-selected hot methods/modules that extracts numeric
  literals and groups them by value, unit-like name context, and operation type.
- Classify each literal as named-constant, static-table cell, config/manifest
  read, local derivation, or raw magic literal.
- Flag repeated near-equal constants, threshold pairs with no named owner,
  literals inside branches that reset or bypass state, and constants whose
  inferred unit/domain disagrees with neighboring values.

## LB-INTAKE-20260629-023 - Determinism and replay contract audit

Type: Feature request
Priority: High
Source: DAWG sync/DSP dogfood, 2026-06-29; Lifeblood local `v0.7.12+dbfd871`
Workspace: DAWG
Rating for DAWG work: 9/10 value if shipped

What:
- Sync and DSP investigations need to know whether a path is replay-safe:
  seeded randomness, time source, floating state, event order, buffer cursor,
  and global mutable state must all be deterministic under repeated runs.
- Lifeblood has dependency and operation facts, but no audit that names
  determinism hazards along a chosen route.

Why it matters:
- Non-determinism can masquerade as an audio bug, a multiplayer desync, a flaky
  test, or a bad scheduler. The code may be structurally correct and still fail
  because a path reads `Time`, `DateTime`, random state, static counters, or
  unordered collections.
- This is useful far beyond DAWG: simulations, netcode, procedural generation,
  replay systems, physics tests, and cache invalidation all need the same
  visibility.

Fix shape:
- Add a determinism audit where callers provide root symbols or route manifests.
- Detect reads from wall-clock/time APIs, unseeded random sources, static mutable
  counters, unordered collections, floating accumulation across frames, IO,
  thread scheduling, and event-list iteration without stable ordering.
- Return a route-level report with hazards, evidence spans, detected seed or
  ordering controls, and suggested contract hooks for tests/probes.

## LB-INTAKE-20260629-024 - Embedded Unity package source visibility report

Type: Improvement
Priority: High
Source: DAWG Unity MCP/package dogfood, 2026-06-29; Lifeblood local `v0.7.12+dbfd871`
Workspace: DAWG
Rating for DAWG work: 9/10 value if shipped

What:
- DAWG package-side investigations exposed a visibility class that is different
  from the already-logged "new file not yet imported" case: Unity can compile an
  embedded package source file, while Lifeblood reports the file as outside the
  loaded project descriptors.
- The package source is not a raw disk orphan. It lives under `Packages/`, has
  package/asmdef ownership, and Unity can load the resulting editor assembly.
  The missing bit is Lifeblood's explicit report of which package sources are
  included, excluded, or intentionally unsupported by the current analyze scope.

Why it matters:
- Tooling and test-job code often lives in embedded packages. If those files are
  outside the semantic graph, agents can accidentally treat a clean analyze as
  covering code that Lifeblood did not actually inspect.
- This is generic for Unity packages, vendored SDKs, source generators, local
  package references, samples promoted to packages, and any project where the
  build tool compiles code that does not appear in the primary solution graph.

Fix shape:
- Add a package/source-visibility section to `lifeblood_analyze` and
  `lifeblood_compile_check` for Unity workspaces.
- Report package roots from `manifest.json` / `packages-lock.json`, discovered
  package asmdefs, files included in Roslyn compilations, files excluded by
  `excludePaths`, and files Unity appears to compile but Lifeblood cannot bind.
- When `compile_check(filePath)` hits package source outside the loaded
  compilation, return a package-specific resolution with the owner package,
  expected assembly, exclusion reason, and a concrete remedy such as include
  packages, route to Unity compile, or regenerate project descriptors.

## LB-INTAKE-20260629-025 - Sibling implementation parity audit

Type: Feature request
Priority: High
Source: DAWG DSP/Burst dogfood, 2026-06-29; Lifeblood local `v0.7.12+dbfd871`
Workspace: DAWG
Rating for DAWG work: 9/10 value if shipped

What:
- DSP and math code often has sibling implementations that are meant to stay
  behaviorally aligned: mono/stereo, scalar/vectorized, managed/Burst,
  editor/player, fast/high-quality, dry/wet, or preview/production paths.
- Lifeblood can show each path's dependencies, but it does not yet compare two
  sibling algorithms for asymmetric calls, constants, clamps, branches, state
  resets, or table lookups.

Why it matters:
- Many real audio/math failures are "same contract, different branch" bugs. The
  graph can be fully wired and tests can pass a single branch while another
  sibling silently drifts.
- The same class appears outside audio in physics integrators, animation curves,
  camera rigs, networking serializers, validation code, and platform-specific
  math shims.

Fix shape:
- Add a parity audit where callers pass two or more root symbols, or where
  Lifeblood suggests pairs by naming/signature patterns.
- Compare operation shapes, call graphs, numeric literals, named constants,
  guards, clamps, allocations, state writes, table access, and exception/logging
  paths.
- Return symmetric and asymmetric facts with callsite spans, plus optional test
  hints naming branches that have no direct test coverage.

## LB-INTAKE-20260629-026 - Ownership and handoff contract audit

Type: Feature request
Priority: High
Source: DAWG sync/audio-thread dogfood, 2026-06-29; Lifeblood local `v0.7.12+dbfd871`
Workspace: DAWG
Rating for DAWG work: 9/10 value if shipped

What:
- Sync-heavy systems need to know who owns a value at each point in the route:
  UI thread, Unity main thread, audio callback, job, network receive loop,
  scheduler, queue consumer, or persistence layer.
- Lifeblood can expose dependencies and dependants, but it does not yet classify
  write sites by execution lane or flag direct mutation that bypasses the
  intended handoff boundary.

Why it matters:
- Many "miscommunication" bugs are not missing references. They are direct
  writes to a value that should only move through a command queue, snapshot,
  adapter, ring buffer, or owner-owned apply method.
- This is generic for DSP, Burst jobs, realtime simulation, multiplayer sync,
  UI-model handoff, editor tooling, and background indexing.

Fix shape:
- Add an ownership/handoff audit where callers provide owner manifests, naming
  patterns, attributes, route roots, or framework-known execution contexts.
- Classify reads and writes by likely lane: Unity main thread, audio callback,
  job/Burst path, async/task path, network/event callback, or plain synchronous
  call.
- Flag writes from the wrong lane, direct field/property mutation around a
  configured queue/adapter, mixed lock-free and lock-based access, missing
  volatile/interlocked/ring-buffer contracts, and tests that only exercise one
  side of the handoff.

## LB-INTAKE-20260629-027 - Invariant-to-test coverage mapper

Type: Feature request
Priority: Medium
Source: DAWG invariant/test dogfood, 2026-06-29; Lifeblood local `v0.7.12+dbfd871`
Workspace: DAWG
Rating for DAWG work: 8/10 value if shipped

What:
- DAWG uses invariant IDs as a contract language for DSP, Burst, UI, architecture,
  and test rules. Lifeblood can parse/check invariant docs, but it does not yet
  answer which tests and source symbols actively cover each invariant.
- Existing graph tools can find references one symbol at a time. The missing
  report is an invariant-centered coverage view: invariant id -> owning docs ->
  source symbols -> tests -> gaps/stale claims.

Why it matters:
- Agents can otherwise say "covered" because an invariant exists in prose or a
  test mentions nearby words, even when no executable test asserts the contract.
- This is useful beyond DAWG for any repo that keeps architecture, safety,
  runtime, or API contracts in living docs and wants tests to enforce them
  without hardcoding one-off checks.

Fix shape:
- Add an invariant coverage command that starts from parsed invariant IDs and
  joins doc mentions, source comments, symbol references, test names, and
  test-impact routes.
- Report covered, prose-only, source-only, test-only, stale-reference, and
  orphan states with evidence spans.
- Let callers configure required coverage depth per invariant family, such as
  one architecture ratchet, one compile check, one generated probe, or one
  runtime fixture.

## LB-INTAKE-20260629-028 - Analyze evidence receipt should resolve analyzed git root

Type: Bug
Priority: Medium
Source: DAWG Lifeblood analyze dogfood, 2026-06-29; Lifeblood local `v0.7.12+dbfd871`
Workspace: DAWG
Rating for DAWG work: 7/10 value if shipped

What:
- A full read-only `lifeblood_analyze(projectPath:"D:/Projekti/DAWG",
  defineProfiles:["Editor","Player"])` returned a valid semantic snapshot, but
  the evidence receipt reported `sourceControl.repositoryRoot:""`,
  `state:"unknown"`, and `source:"repositoryNotFound"`.
- A direct shell check from the same workspace resolves DAWG's git root as
  `D:/Projekti/DAWG`, and the repo has local dirty files that would be useful
  provenance on the receipt.

Why it matters:
- The evidence receipt is what makes Lifeblood output citation-safe. If source
  control provenance is rooted at the server process instead of the analyzed
  project path, the receipt loses commit/dirty context exactly when agents need
  to distinguish proven facts from stale workspace state.
- This applies to any external project analyzed by a long-running MCP server,
  especially when the server binary lives outside the target repository.

Fix shape:
- Resolve source control from the analyzed `projectPath` or `graphPath` first,
  falling back to the server process root only when no analyzed path exists.
- Include repository root, commit hash, short hash, dirty state, and a bounded
  dirty-file count or capped sample.
- If git metadata cannot be read, report the attempted root and failure reason
  so callers can tell "not a repo" from "wrong lookup root" from "git failed".

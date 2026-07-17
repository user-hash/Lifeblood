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
workspace baselining. The only unstarted product lane below is runtime profiler
trace import/correlation plus cross-device performance comparison. Implemented
features awaiting live acceptance now live in the tracking ledger.

---

## LB-INTAKE-20260714-034 - Runtime profiler trace import and code correlation

Type: Feature request
Priority: High
Source: DAWG mobile performance dogfood, 2026-07-12 to 2026-07-14; Lifeblood local `v0.7.12+dbfd871`
Workspace: DAWG
Rating for DAWG work: 10/10 value if shipped

What:
- DAWG performance work depended on Unity Profiler captures for CPU, GPU,
  rendering, memory, audio DSP, and custom DAWG-side markers. Lifeblood could
  prove static call structure, but it could not ingest a profiler capture and
  join hot markers back to symbols, files, invariants, or recent commits.
- The manual loop was still too easy to misread: `GfxDeviceVK.Present` could
  mean GPU backpressure or frame pacing; Unity "Audio Voices: 3" did not expose
  DAWG's true tab/note/worker workload; and custom marker names had to be
  interpreted outside the semantic graph.

Why it matters:
- Performance debugging needs the static truth and runtime truth in one receipt.
  A static graph can show where `FunctionPointer.Invoke` is called, but the
  profiler proves whether that host bridge is a real frame or callback cost.
- The same feature applies to Unity, game engines, servers, desktop apps, and
  any runtime with trace events: agents need to route runtime hotspots to source
  ownership without guessing from screenshots.

Fix shape:
- Add a profiler/trace import lane that accepts Unity Profiler exports or a
  generic event JSON/CSV schema with frame index, marker name, duration,
  thread/category, counters, device metadata, build id, and scenario id.
- Map marker names to source symbols through attributes, generated marker
  manifests, string literal ownership, or caller-supplied aliases.
- Return a correlation report: hottest markers, owning symbol/file/module,
  recent code owners, related invariants/tests, missing marker aliases, and
  ambiguity when a marker cannot be safely mapped.
- Keep runtime data separate from static facts. Lifeblood should say "this
  profiler marker correlates with this source route" instead of pretending the
  graph alone proves runtime cost.

## LB-INTAKE-20260714-035 - Cross-device performance evidence comparator

Type: UX
Priority: High
Source: DAWG S20/S23/S8/PC performance dogfood, 2026-07-12 to 2026-07-14; Lifeblood local `v0.7.12+dbfd871`
Workspace: DAWG
Rating for DAWG work: 9/10 value if shipped

What:
- DAWG collected repeated captures across Galaxy S20, S23, S8, and PC editor
  while changing graphics API, profile tier, buffer size, shader quality,
  workload density, and scenario stage. Lifeblood could analyze source, but it
  did not help decide whether two runtime captures were comparable.
- Several investigation branches were only trustworthy after manually checking
  scenario identity, app version, graphics API, sample rate, callback frames,
  target frame rate, active profiles, device model, GPU, and workload counters.

Why it matters:
- Cross-device optimization can easily compare unlike workloads and invent a
  false root cause. A DAW-grade investigation needs the tool to reject bad
  comparisons before the human optimizes the wrong thing.
- This is a product-level evidence problem, not a DAWG architecture issue:
  Lifeblood already wants to be the source of citation-safe investigation
  receipts.

Fix shape:
- Add a comparison report for multiple analyze/profiler/test receipts keyed by
  scenario id and build id.
- Validate comparability before ranking differences: app version, git commit,
  dirty state, define profiles, platform/API, device class, sample rate, buffer
  frames, target frame rate, workload fingerprint, and enabled feature flags.
- Return "comparable", "partially comparable", or "reject comparison" with exact
  mismatched fields, then show normalized deltas for CPU, GPU, audio callback,
  workers, allocations, batches, SetPass, and memory.
- Let callers attach domain counters such as active tabs, active notes, synth
  voices, worker count, and shader tier so runtime captures explain themselves.

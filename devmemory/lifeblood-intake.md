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
workspace baselining. The unstarted items below cover runtime performance
evidence, retained `find_references` scalability, and one ledger-governance
consistency defect. Implemented features awaiting live acceptance now live in
the tracking ledger.

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

## LB-INTAKE-20260718-046 - Retained `find_references` repeats a workspace-wide semantic scan per symbol

Type: Optimization
Priority: High
Source: DAWG final DSP/comment-closure field report and Lifeblood source audit, 2026-07-18; Lifeblood local `0.7.13-alpha.0.102+619fb044bbf30afa7183b997ea1fac6279f30a2d`
Workspace: DAWG and Lifeblood self

What:
- DAWG observed `lifeblood_find_references` taking 20-30 seconds for each
  symbol even though the semantic snapshot was already loaded.
- The current implementation resolves one target, then visits every syntax
  node in every tree of every retained compilation, asks Roslyn for symbol
  information, builds canonical ids, and discards the request-local scan. A
  sequence of symbol checks repeats that workspace-wide work once per symbol.
- The MCP contract accepts only one `symbolId`, so callers cannot amortize the
  traversal across a bounded verification set.

Why it matters:
- Reference verification is a primary safety gate for comment cleanup,
  refactors, dead-code triage, and public-surface changes. At 20-30 seconds per
  symbol, a modest audit becomes minutes of serial latency after the expensive
  workspace baseline has already been paid for.
- The delay creates pressure to substitute lexical search for semantic
  evidence. Lifeblood should make the authoritative path practical instead of
  encouraging a weaker fallback.

Fix shape:
- Add a large multi-compilation performance fixture and request telemetry that
  separates tree/node visits, semantic bindings, canonical-id comparisons,
  result shaping, and total duration.
- Preserve the existing single-symbol contract, but add a bounded multi-symbol
  mode that resolves all targets once and matches them during one request-local
  traversal. Safely prefilter syntax candidates before semantic binding where
  that does not change reference coverage.
- Do not retain a second semantic graph or a duplicate workspace-wide reference
  mirror. Prove byte-equivalent locations, ordering, declaration inclusion,
  deduplication, cross-module/partial behavior, and generic-definition matching
  against the current scanner, then record cold/warm DAWG timings for both
  single-symbol and bounded-batch calls.

## LB-INTAKE-20260718-047 - Ledger type taxonomy contradicts live entries and is not ratcheted

Type: Docs
Priority: Low
Source: Lifeblood tracking-governance audit, 2026-07-18; Lifeblood self at `0.7.13-alpha.0.102+619fb044bbf30afa7183b997ea1fac6279f30a2d`
Workspace: Lifeblood self

What:
- `lifeblood-tracking.md` says every entry type must be one of `Bug`,
  `Improvement`, `Optimization`, `UX`, `Docs`, or `Shipped`, and its required
  entry template repeats that list.
- The same living ledger contains `Type: Planning` and `Type: Feature request`,
  while this intake also contains `Type: Feature request`.
- `TrackingLedgerTests` does not validate entry types, and `IntakeLedgerTests`
  checks only that a `Type:` line exists, so the documented taxonomy can drift
  while both governance ratchets remain green.

Why it matters:
- Type is presented as required structured metadata, but consumers cannot
  reliably group or validate entries while the accepted vocabulary is
  ambiguous.
- A green governance suite currently gives stronger confidence than the ledger
  contract warrants.

Fix shape:
- Choose one canonical live-entry taxonomy: either explicitly admit `Planning`
  and `Feature request`, or normalize living entries to the existing six types.
- Put the vocabulary in one parser/test authority shared by tracking and intake
  validation, and fail on missing or unknown live-entry values.
- Preserve archived historical wording; do not rewrite closed receipts merely
  to retrofit a later taxonomy.

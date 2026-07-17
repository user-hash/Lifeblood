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
- 2026-07-14 tuning work showed that endpoint and resolution probes should be
  generated from the same mapping/range table agents inspect manually. The
  useful sweep is not "try insane extremes"; it is "sample the declared musical
  range, active range, storage sentinel, and default/neutral values."

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
- When a contract/range table is available, generate representative cases from
  default, neutral, musical min/max, midpoint, active zero/sentinel, and one or
  two high-resolution neighborhoods instead of blindly testing arbitrary global
  extremes.
- Keep execution in the user's test framework/project; Lifeblood should produce
  scaffolding and structural checks, not own audio playback.

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

## LB-INTAKE-20260714-029 - Diff-scoped diagnostic ownership report

Type: Feature request
Priority: High
Source: DAWG ADSR/LFO/genre follow-up, 2026-07-14; Lifeblood local `v0.7.12-4-gd2cfe30-dirty`
Workspace: DAWG
Rating for DAWG work: 9/10 value if shipped

What:
- DAWG verification needed "check Unity/Lifeblood errors and only fix yours."
  A project-wide diagnostic run can return hundreds of existing warnings while
  the actionable set is only diagnostics introduced by the current diff or the
  files touched in the current atom.
- In the latest pass, the useful signal was a single warning in a newly touched
  DSP policy file among a much larger baseline. Lifeblood can diagnose, but it
  does not yet classify diagnostics by ownership against git diff, staged files,
  commit range, or caller-supplied touched paths.

Why it matters:
- Commit-as-you-go workflows need a clean way to avoid laundering old warnings
  into the current task while still catching new regressions immediately.
- This is not DAWG-specific. Any large repo with a warning baseline needs to
  know "new in my change", "pre-existing in touched file", and "unrelated
  baseline" before deciding what to fix.

Fix shape:
- Add a diagnostic ownership mode to `lifeblood_diagnose` or a sibling report
  that accepts `sinceCommit`, `stagedOnly`, `workingTreeOnly`, or explicit
  `touchedFiles`.
- Return diagnostics grouped as `introducedByDiff`, `preExistingTouchedFile`,
  `preExistingUnrelated`, and `unknownOwnership`, with file/line spans and
  source-control provenance.
- For warnings without stable line history, use a conservative fallback: same
  diagnostic id/message/file before the diff means pre-existing; changed lines
  or new files mean current-change-owned.

## LB-INTAKE-20260714-030 - First-class evidence baseline drift check

Type: Improvement
Priority: Medium
Source: DAWG ADSR/LFO/genre checkup, 2026-07-14; Lifeblood local `v0.7.12-4-gd2cfe30-dirty`
Workspace: DAWG and Lifeblood self
Rating for DAWG work: 8/10 value if shipped

What:
- DAWG uses generated evidence baselines for symbols, edges, modules, cycles,
  invariants, and profile counts. The current workflow relies on an external
  Codex skill to compare live Lifeblood counts against the committed
  `EVIDENCE.generated.md` stamp and decide whether drift is within tolerance.
- Lifeblood already produces the live facts, but it does not expose a single
  product-level "baseline current / stale / refresh recommended" verdict
  against a repo-owned evidence file.

Why it matters:
- Agents need to cite semantic counts without over-trusting a stale generated
  doc. A small drift can be acceptable; a large drift means refresh evidence
  before using the docs as authority.
- Putting the check inside Lifeblood keeps the evidence loop close to the
  semantic source of truth and avoids per-repo helper scripts drifting in their
  tolerance rules.

Fix shape:
- Add `lifeblood_evidence_drift` or extend `lifeblood_analyze` with an optional
  `baselinePath` and tolerance policy.
- Parse the baseline counts, compare them to the retained live graph and
  invariant audit, and return deltas, percent drift, commit stamp if present,
  verdict, and a refresh recommendation.
- Keep this read-only. If a caller wants mutation, pair it with a separate
  explicit refresh command that rewrites the generated evidence file.

## LB-INTAKE-20260714-032 - Compact invariant audit without duplicated zero-source ledgers

Type: Optimization
Priority: Medium
Source: DAWG evidence-refresh session, 2026-07-11; live Lifeblood server `v0.7.12+dbfd871`
Workspace: DAWG
Rating for DAWG work: 8/10 value if shipped

What:
- `lifeblood_invariant_check(mode:"audit")` on DAWG discovered 56 candidate
  sources but only one source declared parser-recognized invariants. The result
  repeated the full `sourcePaths` and `sourceCounts` ledgers at the top level
  and inside `evidenceReceipt`, including every zero-count source.
- The response exceeded seven thousand tool-output tokens and was truncated by
  the client even though the actionable result was small: two unique
  invariants, two declarations, zero duplicates, and zero parse warnings.

Why it matters:
- Full source provenance is valuable for parser/discovery debugging, but it is
  expensive as the invariant tree grows and obscures duplicates or warnings - the
  fields an audit caller needs first.
- This is distinct from the shipped docs-safe evidence receipt. The receipt
  made audit evidence citable; this request keeps that contract while removing
  redundant wire payload and giving agents an intentional compact path.

Fix shape:
- Add `summarize:true` and/or a source projection such as
  `sourceMode:"nonzero"|"all"`. Compact mode should retain totals,
  category counts, duplicate IDs with occurrences, parse warnings, non-zero
  source counts, `discoveredSourceCount`, and `zeroDeclarationSourceCount`.
- Avoid serializing identical source ledgers twice. The receipt can carry a
  digest/reference to the top-level provenance, or the top level can reference
  the self-contained receipt, while the default full response stays backward
  compatible.
- Surface `truncated` and full pre-truncation counts if any source or occurrence
  list is capped. Add a DAWG-sized fixture where compact mode remains bounded
  and still exposes a duplicate and a parse warning from zero-heavy discovery.

## LB-INTAKE-20260714-033 - Retained-session memory telemetry needs start/end and peak delta

Type: Optimization
Priority: Medium
Source: DAWG multi-profile analyze and evidence-refresh session, 2026-07-11; live Lifeblood server `v0.7.12+dbfd871`
Workspace: DAWG
Rating for DAWG work: 8/10 value if shipped

What:
- A full Editor+Player analyze of DAWG reported 86,010 symbols, 331,980 edges,
  71.8 seconds wall time, 4.94 GB peak working set, and 5.10 GB peak private
  bytes. A subsequent authoritative one-file incremental update completed in
  6.7 seconds but reported 5.79 GB peak working set and 5.93 GB peak private
  bytes.
- `ProcessUsageProbe` correctly samples an isolated maximum during each call,
  but `AnalysisUsage` exposes only absolute peaks. It does not report the
  retained process baseline at call start, the end state, or peak growth above
  baseline, so the receipt cannot distinguish retained graph cost from
  transient incremental rebuild cost.

Why it matters:
- On large Roslyn workspaces, wall-time improvement alone is not enough. Agents
  and benchmark tooling need to know whether incremental analysis reuses the
  retained session efficiently or temporarily holds old and replacement
  compilation state at once.
- This is distinct from the active runtime/JIT benchmark lane. That lane
  compares target runtimes and workloads; this request makes the per-request
  telemetry itself capable of explaining memory behavior on any runtime.

Fix shape:
- Add working-set and private-byte samples for start, end, absolute peak, and
  `peakAboveStart` to `AnalysisUsage`. Keep existing peak fields as backward-
  compatible aliases/absolute values.
- Optionally record memory at analyze phase boundaries so module discovery,
  compilation, graph extraction, validation, and session commit can be
  attributed without requiring an external profiler.
- Add a retained-session benchmark that runs full, incremental-noop, one-file
  incremental, and descriptor-fallback analyzes in one process and reports
  start/end/delta plus semantic-count parity. Treat the observed DAWG numbers as
  a measurement lead, not proof of a leak, until the delta fields exist.

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

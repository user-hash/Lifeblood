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

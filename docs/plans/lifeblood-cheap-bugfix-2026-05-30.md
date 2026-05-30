# Lifeblood cheap bug-first pass — 2026-05-30

Source: DAWG dogfood + reconciliation of `docs/IMPROVEMENT_INBOX.md` +
`devmemory/lifeblood-tracking.md`. Discipline: eternal code, hexagonal
(Domain DTO → Application port → adapter → Server.Mcp handler), no hotpatches,
every fix ratchet-pinned + DAWG-verified. Order is **cheap-first** (user
directive 2026-05-30). User owns tags + pushes.

## Reconciled open/done/retire (verified against live source, not headings)

- **DONE, file stale-open:** `LB-INBOX-010` (method-group / generic-call dead_code
  FP) shipped as `LB-TRACK-20260515-009` v0.7.6 — `RoslynEdgeExtractor.cs:697`
  CandidateSymbols handler + `:225` OriginalDefinition canonicalization confirmed
  live. → flip SHIPPED.
- **DUPLICATE:** tracking line-129 "Unity new-file discovery" == `LB-TRACK-20260530-028`.
  → delete the unnumbered one.
- **ID drift:** inbox `LB-TRACK-20260530-001/002` collide with tracking's sequential
  `028/029`. → renumber inbox → `030`(compact) / `031`(execute), cross-link.
- **Defer (strategic, not cheap):** `LB-INBOX-003/004/005`, `.NET feature adoption`
  planning + 7 `.NET *` partial lanes, the *full* pre-meta disk-sweep impl.

## Stages

### Stage 0 — File honesty (markdown only, zero code)
Flip `LB-INBOX-010` → SHIPPED (cite v0.7.6 / `LB-TRACK-20260515-009`). Delete
duplicate line-129 tracking entry. Renumber inbox `530-001/002` → `030/031` +
cross-link to tracking. No build.

### Stage 1 — `execute` loader-error translation (inbox 031)
Boundary: `execute` compiles-against but cannot runtime-load workspace types;
raw `FileLoadException` leaks at `RoslynCodeExecutor.cs:281`.
- Adapter `RoslynCodeExecutor`: in the catch, when the runtime exception is a
  `FileLoadException`/`FileNotFoundException` whose assembly name matches a loaded
  workspace module, reframe into a structured `TargetRuntimeWarnings[]` entry
  naming the compile-against-not-run boundary + keep a clean `Error`. Module set
  already in hand from the reference resolver.
- Domain: reuse existing `CodeExecutionResult.TargetRuntimeWarnings` (no new
  field). Pure.
- Doc: one-line boundary caveat in execute tool description + `DOGFOOD_CODE_EXECUTION.md`.
- Test: `RoslynCodeExecutorTests` — workspace-type instantiation surfaces the
  structured boundary, not the raw loader message. INV candidate
  `INV-EXECUTE-WORKSPACE-LOAD-BOUNDARY-001`.

### Stage 2 — `compile_check` LB0002 split (tracking 028 sub-fix)
Today "path not found" and "exists on disk but not in any loaded compilation"
collapse to one LB0002.
- Adapter `RoslynCompilationHost.CompileCheckFile`: distinguish the two; emit a
  refresh hint ("Unity project files appear stale; run refresh/regenerate") for
  the on-disk-not-loaded case.
- Domain: add a typed marker to `CompileCheckResult` (e.g. `FileResolution`
  enum: `Resolved` / `PathNotFound` / `OnDiskNotInCompilation`) — eternal, not a
  string sniff.
- Test: `CompileCheck*Tests`. INV `INV-COMPILE-CHECK-FILE-RESOLUTION-001`.

### Stage 3 — Compact diagnostic envelope (inbox 030)
Repeated `compile_check`/`diagnose` spam the full 150-entry `DefinesActive[]`.
- Server.Mcp + `ToolRegistry`: additive `verbosity:"compact"` (default verbose,
  back-compat). Compact keeps success/diagnostics/`resolvedModule`/truth-envelope
  + a defines *summary* (`count`, `profileName`), drops the full list.
- Shape lives at the handler/registry seam, not the adapter (defines computed
  once, projected differently). Schema snapshot under `schemas/tools/v1/`.
- Test: `ToolHandlerTests`. INV `INV-DIAGNOSTIC-ENVELOPE-VERBOSITY-001`.

### Stage 4 — Unity new-file pre-meta honesty (tracking 028)
Contract claims pre-`.meta` files get picked up; live behavior doesn't.
Cheap half = honesty, NOT the disk-sweep build-out.
- Narrow analyze/compile_check tool descriptions; surface structured
  `fallbackReason` / `limitations[]` when project descriptors are stale.
- Test: Unity-shaped fixture (new `.cs`, no `.meta`) asserts the documented
  warning shape. INV `INV-UNITY-NEWFILE-DISCOVERY-HONESTY-001`.

### Stage 5 — Full-analyze NRE envelope (inbox LB-INBOX-012)
Full multi-profile analyze throws bare NRE after Unity asset churn.
- Application/`GraphSession` analyze pipeline: wrap so a failure returns a
  structured `{phase, module, file, activeProfile, failedBeforeCompilation}`
  envelope. Never a raw NRE on the wire.
- Test: churn fixture (new `.cs` then `.meta`, partial-class signature change),
  incremental then full multi-profile. INV `INV-ANALYZE-STRUCTURED-FAILURE-001`.

### Stage 6 — MCP transport resilience (tracking 029) — HIGH severity, biggest
Parallel `compile_check` kills the transport with no reconnect path.
- Server.Mcp: single-flight guard for write-side Roslyn compilation tools;
  concurrent compile-check returns a recoverable `RetrySerially` outcome instead
  of letting the transport die. Structured top-level fatal envelope
  (`phase`/`activeTool`/`requestId`/`workspaceRoot`/`profile`/`isConcurrent`/`serverPid`).
- Subsequent read-side calls must still succeed after a rejected compile-check.
- Test: concurrent-compile-check smoke regression. INV
  `INV-MCP-COMPILE-CHECK-SINGLE-FLIGHT-001`.

### Stage 7 — DAWG dogfood verification
Re-probe each shipped fix live against DAWG; record receipts in tracking file;
flip each entry SHIPPED with commit refs.

### Stage 8 — Release prep (no tag — user decides)
CHANGELOG `[Unreleased]` consolidation, STATUS.md anchors (`DocsTests`),
invariant count refresh, doc surface 1:1. Suggest tag; do not cut.

## Invariants introduced
`INV-EXECUTE-WORKSPACE-LOAD-BOUNDARY-001`, `INV-COMPILE-CHECK-FILE-RESOLUTION-001`,
`INV-DIAGNOSTIC-ENVELOPE-VERBOSITY-001`, `INV-UNITY-NEWFILE-DISCOVERY-HONESTY-001`,
`INV-ANALYZE-STRUCTURED-FAILURE-001`, `INV-MCP-COMPILE-CHECK-SINGLE-FLIGHT-001`.

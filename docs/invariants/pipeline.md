# Pipeline Invariants

Streaming compilation, derived file-edge truth, and incremental
re-analyze. Memory-safe by default; full retention only opted into by
the MCP server.

## Streaming Compilation Architecture (v0.3.0)

- **INV-STREAM-001**: `ModuleCompilationBuilder.ProcessInOrder` compiles one module at a time in topological order. After extraction, `Emit()` → `MetadataReference.CreateFromImage()` downgrades the full compilation (~200MB) to a lightweight PE reference (~10-100KB). Peak memory: O(1 compilation), not O(N).
- **INV-STREAM-002**: `SharedMetadataReferenceCache` deduplicates NuGet MetadataReferences across modules. One instance per `AnalyzeWorkspace` call.
- **INV-STREAM-003**: `AnalysisConfig.RetainCompilations` controls mode. `false` (default, CLI) = streaming/memory-safe. `true` (MCP server) = retained for write-side tools.
- **INV-STREAM-004**: Unity csproj support: if `<Compile Include>` items exist (old-format), use them. If absent (SDK-style), scan filesystem.
- **INV-STREAM-005**: `GraphBuilder.Build()` deduplicates ALL edges by `(sourceId, targetId, kind)`. Partial classes emit duplicate edges. The builder is the authoritative dedup boundary.

## Derived File Edges

- **INV-FILE-EDGE-001**: `GraphBuilder.Build()` derives file-level `References` edges from symbol-level edges. For each non-Contains edge between symbols in different files, a `file:X → file:Y References` edge is emitted with an `edgeCount` property. Evidence: `Inferred`, adapter: `GraphBuilder`. File edges are derived truth. Not primary.

## Incremental Re-Analyze

- **INV-INCR-001**: Incremental re-analyze (`lifeblood_analyze` with `incremental: true`) only recompiles modules whose files changed since the last analysis. Per-file extraction results are cached in `AnalysisSnapshot`. Changed files are detected via filesystem timestamps. v1 limitation: does not cascade to dependent modules when API surface changes.

- **INV-ANALYZE-FALLBACK-001. Scope-widening is a caller policy, not an adapter decision.** When the adapter detects drift it cannot honor cheaply (no prior cache, module set changed since the snapshot, project descriptor edited), the response shape is determined by the caller's `AnalysisConfig.AllowFullFallback` flag. **Default `false`** (fail-loud): the adapter returns `IncrementalAnalyzeResult { Mode = Rejected, Graph = null, Reason = ..., Detail = ... }` — no work done, caller decides next step. **Opt-in `true`**: the adapter widens to a full re-analyze and returns `Mode = FullFallback, Graph = ..., Reason = ...` so the result lands AND the cache miss stays visible. The reason taxonomy is adapter-agnostic — `NoPriorAnalysis`, `ModuleSetChanged`, `ModuleDescriptorChanged` — so future Python / TypeScript / etc. adapters reuse the same shape; the adapter-specific descriptor kind (`asmdef`, `csproj`, `pyproject.toml`) lives in `Detail`. Internal best-effort callers like `GraphSession.MaybeRefreshIfStale` deliberately set `AllowFullFallback = true` because their contract is "make state fresh"; user-facing callers route the user's own flag through unchanged. **NoPriorAnalysis is always Rejected regardless of flag** — without a snapshot there is no `projectRoot` to widen against; remediation is fixed (call `AnalyzeWorkspace` first). Wire shape on the MCP layer: `mode` reports what the adapter DID (`full` / `incremental` / `incremental-noop` / `rejected`); `requestedMode` separately reports what the caller ASKED (`full` / `incremental`); `fallbackReason` + `fallbackDetail` populate alongside whenever the cheap path could not be honored cleanly; rejection responses additionally carry `canRetryFull: true` and a `suggestedRetry: { incremental: true, allowFullFallback: true }` block so the agent's next move is self-documenting and requires no out-of-band knowledge. Rejection is a NORMAL structured result, not a transport / tool error. Pinned by `IncrementalAnalyzeTests` (10 adapter-level cases including NoPriorAnalysis-always-rejects + ModuleSetChanged × {Rejected,FullFallback}) and `AnalyzeWireShapeTests` (5 wire-level ratchets covering full, incremental-noop, incremental, rejected, and full-fallback shapes).

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

- **INV-INCR-001**: Incremental re-analyze (`lifeblood_analyze` with `incremental: true`) only recompiles modules whose files changed since the last analysis. Per-file extraction results are cached in `AnalysisSnapshot`. Changed files are detected via filesystem timestamps. Module additions/removals fall back to full re-analyze. v1 limitation: does not cascade to dependent modules when API surface changes.

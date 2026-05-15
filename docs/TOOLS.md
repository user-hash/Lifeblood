# Tools

Lifeblood exposes **29 MCP tools** in one session. 17 read, 12 write. All share the same loaded workspace.

Every read-side tool that takes a `symbolId` routes through `ISymbolResolver` before any graph or workspace lookup. Resolution order: exact canonical match, truncated method form (single-overload lenient), kind correction (`method:` prefix on a property/field/event name on the same type, `INV-RESOLVER-006`), bare short name, extracted short name from a kind-prefixed or qualified input (`INV-RESOLVER-005`). Truncated ids, bare short names, qualified-but-wrong-namespace ids, and kind-mismatched ids all resolve correctly across the whole read surface.

Every read-side tool response carries a top-level `envelope` field (`INV-ENVELOPE-001`) with `truthTier` (Semantic / Derived / Heuristic / Inferred), `confidence` (Proven / Advisory / Speculative), `evidenceSource` (Semantic / Inferred / Heuristic), `stalenessSeconds`, `filesChangedSinceAnalyze`, and per-tool `limitations[]`. Errors deliberately do NOT carry envelopes. Per-tool classification lives on `ToolDefinition.EnvelopeClassification` in the registry, projected into the decorator at startup, so registry and decorator cannot drift.

## Write-side (compiler-as-a-service)

**Static table coverage (v0.7.6 prep wave, SHIPPED on `main`):** `LB-INBOX-011`
is closed. `lifeblood_static_tables` recognises both explicit
`new T[] { ... }` and implicit `static readonly float[] X = { ... }` array
shapes (`INV-EXTRACT-STATIC-IMPLICIT-ARRAY-001`). Dispatch-table delegate
method-group cells AND member-access enum / field references emit graph
edges through the regular identifier / member-access walkers via
`INV-EXTRACT-METHOD-GROUP-CANDIDATE-001` (target-typed `new(MethodGroup)`)
+ `INV-EXTRACT-DISPATCH-TABLE-COVERAGE-001` (no separate synthesizer
needed) + `INV-EXTRACT-SYNTHESIZED-CTOR-001` (synthesized `.cctor` /
`.ctor()` surface as Symbols so `GraphBuilder` no longer drops the
initializer-derived edges). `dead_code`, `port_health`, `dependants`, and
`blast_radius` consume the synthesized edges directly; the cross-check
recommended in earlier docs is no longer required for table-driven
delegate methods.

## Read-side (semantic intelligence)

The difference: the AI agent doesn't guess what your code does. It **asks the compiler**.

---

## ¹ `lifeblood_dead_code` status

- **Target-typed `new(MethodGroup)`** — `INV-EXTRACT-METHOD-GROUP-CANDIDATE-001` (`LB-INBOX-010`). `RoslynEdgeExtractor.ResolveCandidateMethodGroup` accepts `CandidateSymbols`-bound method groups, so `dependants` / `dead_code` / `blast_radius` now see the target.

- **Generic-method call canonical-id drift** — `INV-EXTRACT-METHOD-ORIGINAL-DEFINITION-001` (`LB-INBOX-010`). `GetMethodId` routes through `OriginalDefinition` so an instantiated call lands on the source-declared open-generic id.

- **Static-table delegate references** — `INV-EXTRACT-DISPATCH-TABLE-COVERAGE-001` + `INV-EXTRACT-SYNTHESIZED-CTOR-001` (`LB-INBOX-011`). Dispatch-table cells emit through the regular identifier walkers; synthesized `.cctor` / `.ctor()` surface as Symbols so `GraphBuilder`'s dangling-edge filter no longer drops the initializer-derived edges.

**Remaining false-positive classes (structural; not closed by the extractor):**
- Runtime entry points (Program.Main, composition-root entries).
- UI Toolkit `VisualElement` subclasses with magic-named methods (Awake/Update on a non-MonoBehaviour base).
- Audio callbacks (`OnAudioFilterRead`) on bases not in the standard MonoBehaviour message-receiver set.
- Reflection-based dispatch invisible to static analysis (`Type.GetType` + `MethodInfo.Invoke`, Unity `SendMessage`-dispatched handlers).

**Consumer guidance:**
- Findings are advisory. Every response carries the `envelope.confidence = "Advisory"` band and a `limitations[]` entry naming the FP classes.
- Cross-check with `lifeblood_find_references` (which has the same gap class) and direct code inspection before acting.

See [CLAUDE.md, INV-DEADCODE-001 + INV-UNITY-001](../CLAUDE.md) for the full invariants.

---

## Symbol ID format

Tools that take a `symbolId` use this format:
- `type:Namespace.TypeName`
- `method:Namespace.TypeName.MethodName(ParamType)`
- `field:Namespace.TypeName.FieldName`
- `property:Namespace.TypeName.PropertyName`
- `property:Namespace.TypeName.this[ParamType]` (indexer)
- `mod:AssemblyName`
- `file:relative/path/to/File.cs`
- `ns:Namespace`

Lifeblood owns the parameter-type display format for method IDs via `Internal.CanonicalSymbolFormat`. Every method-ID builder in the C# adapter routes through it, so the symbol ID grammar does not silently drift with Roslyn version changes (`INV-CANONICAL-001`).

If you don't know the canonical id, ask `lifeblood_resolve_short_name MyType` and use the returned `symbolId`. The resolver also accepts:
- Truncated method ids like `method:Namespace.TypeName.MethodName` when there is exactly one matching overload (`LenientMethodOverload`).
- `method:` prefix on a property/field/event name on the same type when no method by that name exists (`KindCorrectedOnContainingType`, `INV-RESOLVER-006`). Type-scoped kind correction takes precedence over the global short-name fallback because the user already committed to a namespace.
- Bare short names with no kind prefix and no namespace (`ShortNameUnique`).
- Kind-prefixed ids with wrong namespace (`ShortNameFromQualifiedInput`, `INV-RESOLVER-005`): when the exact / truncated / bare paths all fail, the resolver extracts the trailing short-name segment and looks it up in the short-name index. If that produces exactly one hit, the resolver silently corrects the namespace and returns the real canonical id with a diagnostic explaining the correction.

## Incremental Re-Analyze

After the first `lifeblood_analyze`, subsequent calls with `incremental: true` only recompile modules whose source files changed since the last analysis. File changes are detected via filesystem timestamps, and csproj timestamp changes (`INV-BCL-005`) also trigger per-module re-discovery so a `<Nullable>` or `<AllowUnsafeBlocks>` toggle doesn't leave stale compilation facts behind.

```
lifeblood_analyze projectPath="/my/project"                    → full analysis (~14-34 s depending on workspace size)
lifeblood_analyze projectPath="/my/project" incremental=true   → seconds when nothing changed, else re-analyze only the dirty modules
```

### Caller-owned scope policy (`INV-ANALYZE-FALLBACK-001`)

`lifeblood_analyze` does not silently widen scope when it detects drift. The
adapter classifies the drift into a typed `fallbackReason`
(`noPriorAnalysis` / `moduleSetChanged` / `moduleDescriptorChanged` —
asmdef edits, csproj structural changes, etc.) and the caller decides
what to do.

| Caller setting | Adapter behaviour |
|---------------|-------------------|
| `incremental: false` | Always full analyze. `mode: "full"`, `requestedMode: "full"`. |
| `incremental: true`, `allowFullFallback: false` (default) | Cheap path on success. On detected drift: REJECT — `mode: "rejected"`, `requestedMode: "incremental"`, `fallbackReason` + `fallbackDetail` populated, `canRetryFull: true`, plus a `suggestedRetry: { incremental: true, allowFullFallback: true }` block. No work done; caller decides. |
| `incremental: true`, `allowFullFallback: true` | Cheap path on success. On detected drift: silently widen to full and report both — `mode: "full"`, `requestedMode: "incremental"`, `fallbackReason` populated so the cache miss stays visible, work succeeded with summary populated. |

Rejection is a NORMAL structured result, not a transport / tool error.
The `suggestedRetry` block is exactly the args needed to retry — pass it
back to `lifeblood_analyze` to re-attempt with widened scope.

## Workspace auto-refresh for compile-check

`lifeblood_compile_check` auto-refreshes the workspace when any tracked file has changed on disk since the last analyze, so you can edit source between an analysis and a compile-check without stale results. Opt out with `staleRefresh: false` to check against the pinned state. The response carries `autoRefreshed: true` + `changedFileCount: N` when a refresh actually ran. Asmdef edits also trigger a full re-analyze on the next round (`INV-UNITY-002`).

## File-mode compile-check (LB-BUG-019)

When called with `filePath`, `lifeblood_compile_check` resolves the file's owning compilation by matching the path against every loaded compilation's syntax trees, then **swaps the existing tree** for the on-disk content via `ReplaceSyntaxTree` instead of adding the file as a new snippet tree to an arbitrary first compilation:

```
lifeblood_compile_check filePath="Assets/Scripts/Core/MultiPartialHost.cs"
→ {
    "success": true,
    "diagnostics": [],
    "resolvedModule": "Acme.Module.Runtime",
    "existingTreeReplaced": true,
    ...
  }
```

Pre-fix the same call emitted ~120 spurious CS0246 / CS0103 errors (UnityEngine, MonoBehaviour, sibling partials, every cross-file type) because the snippet path was selected and the file's real owning compilation was never resolved. File-mode preserves every reference and filters pre-existing diagnostics in OTHER files in the module so only changes the user introduced in THIS file surface. Pinned `moduleName` overrides auto-detection — if the file isn't in that module the request fails with `LB0002` rather than silently picking another.

## Truth envelope on every read-side response

Every read-side tool response ships a top-level `envelope` field (`INV-ENVELOPE-001`):

```json
{
  "envelope": {
    "truthTier": "Semantic",
    "confidence": "Proven",
    "evidenceSource": "Semantic",
    "stalenessSeconds": 12,
    "filesChangedSinceAnalyze": 0,
    "limitations": []
  },
  "...": "...the rest of the tool's normal payload"
}
```

- `truthTier`: `Semantic` (compiler-grade), `Derived` (graph rollup over semantic edges), `Heuristic` (advisory only), `Inferred` (synthesized from secondary signals like file-level edges).
- `confidence`: `Proven` (deterministic), `Advisory` (correct most of the time, with documented FP classes), `Speculative` (ranked suggestions).
- `stalenessSeconds`: wall-clock between the loaded graph's analyze time and now.
- `filesChangedSinceAnalyze`: count of tracked source files with mtime newer than analyze (capped per call at 256 files; short-circuits as soon as drift is detected).
- `limitations[]`: per-tool documented FP/FN classes (e.g. `lifeblood_dead_code` lists Unity reflection dispatch and runtime entry points).

Errors deliberately do NOT carry envelopes. Per-tool classification lives on `ToolDefinition.EnvelopeClassification` in the registry, projected into `LifebloodResponseDecorator` at composition time. Adding a new read-side tool without a classification fails the registry ratchet test.

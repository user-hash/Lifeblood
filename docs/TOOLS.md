# Tools

Lifeblood exposes **25 MCP tools** in one session. 15 read, 10 write. All share the same loaded workspace.

Every read-side tool that takes a `symbolId` routes through `ISymbolResolver` before any graph or workspace lookup. Resolution order: exact canonical match, truncated method form (single-overload lenient), kind correction (`method:` prefix on a property/field/event name on the same type, `INV-RESOLVER-006`), bare short name, extracted short name from a kind-prefixed or qualified input (`INV-RESOLVER-005`). Truncated ids, bare short names, qualified-but-wrong-namespace ids, and kind-mismatched ids all resolve correctly across the whole read surface.

Every read-side tool response carries a top-level `envelope` field (`INV-ENVELOPE-001`) with `truthTier` (Semantic / Derived / Heuristic / Inferred), `confidence` (Proven / Advisory / Speculative), `evidenceSource` (Semantic / Inferred / Heuristic), `stalenessSeconds`, `filesChangedSinceAnalyze`, and per-tool `limitations[]`. Errors deliberately do NOT carry envelopes. Per-tool classification lives on `ToolDefinition.EnvelopeClassification` in the registry, projected into the decorator at startup, so registry and decorator cannot drift.

## Write-side (compiler-as-a-service)

| Tool | What it does |
|------|-------------|
| **Execute** | Run C# code against your actual project types, in-process. Access any class, call any method, inspect any state. Optional `targetProfile` argument selects the BCL ref-pack (`host` default, `net-standard-2.1`, `net-6.0`); missing packs fall back to host BCL with `targetRuntimeWarnings`. On Unity workspaces (`Library/` exists at the project root) DLLs from `Library/ScriptAssemblies/`, `Library/Bee/artifacts/`, and `Library/PackageCache/` are auto-injected; an empty `Library/` surfaces a `runtimeAssemblyWarnings` entry. Sandbox globals: `Graph`, `Compilations`, `ModuleDependencies`, plus `Help` (cheat sheet), `SymbolsOfKind(string)`, `EdgesOfKind(string)`. |
| **Diagnose** | Real compiler diagnostics. Errors, warnings, with file, line, and module. Optional `filePath` argument scopes the result to one source file (the typical "did the file I just edited compile cleanly?" question); without it, returns the full project diagnostics. Optional `moduleName` scopes to a single compilation. The response includes a `scope` discriminator (`file` / `module` / `project`). |
| **Compile-check** | "Will this snippet compile in my project?" answered in milliseconds. Pass either `code` (inline source) or `filePath` (read from disk via `IFileSystem`); supplying both is rejected. Bare statement snippets like `var x = 1 + 1;` are auto-wrapped in a synthetic class + method body so they compile against library modules (which otherwise reject top-level statements with CS8805). Complete compilation units (`class Foo { ... }`) still pass through unchanged. Diagnostic line numbers are remapped back to the user's original coordinates. |
| **Find references** | Every caller, every consumer, across the entire workspace. Verified by the compiler. |
| **Find definition** | Go-to-definition. Resolves through interfaces, base classes, partials. Returns file, line, docs. |
| **Find implementations** | What types implement this interface? What methods override this? Semantic, not grep. Compares via canonical Lifeblood symbol IDs (`INV-FINDIMPL-001`), not display strings or Roslyn's `SymbolEqualityComparer`, so cross-assembly matches are correct. |
| **Symbol at position** | Give a file:line:col, get the resolved symbol, type, and documentation. |
| **Documentation** | XML doc extraction. Pulls `<summary>`, `<param>`, `<returns>` from resolved symbols. |
| **Rename** | Safe rename across the workspace. Returns text edits as preview. The agent decides whether to apply. |
| **Format** | Roslyn's own formatter. Not regex hacks. |

## Read-side (semantic intelligence)

| Tool | What it does |
|------|-------------|
| **Analyze** | Load a project into a verified semantic graph. Symbols, edges, modules, violations. Pass `incremental: true` after the first analysis for fast re-analysis (only changed modules recompile, csproj edits trigger re-discovery). Every response carries a `usage` field with wall time, CPU time, peak memory, and GC counters. |
| **Context** | AI context pack with high-value files, boundaries, reading order, hotspots, dependency matrix. |
| **Lookup** | Symbol details: kind, file, line, visibility, properties. For partial types, returns the deterministic primary `filePath` and the full sorted `filePaths[]` of every partial declaration. |
| **Dependencies** | What does this symbol depend on? |
| **Dependants** | What depends on this symbol? |
| **Blast radius** | Change this symbol, what breaks? Transitive BFS over the dependency graph. Every response carries `directDependants` (one-hop incoming edges, distinct sources) alongside the existing `affectedCount` (transitive). `summarize:true` returns a compact result with `preview[]` instead of the full `affected[]` array; `maxResults` caps the embedded array; `truncated:true/false` tells callers whether the array was clipped. |
| **File impact** | Change this file, what other files break? Derived from symbol-level edges. |
| **Resolve short name** | Discover canonical IDs from a bare short name when you don't know the namespace. Returns kind, file, line, and disambiguation candidates. Three modes: `exact` (default), `contains` (substring), `fuzzy` (ranked near-matches). |
| **Search** | Ranked keyword search over symbol names, qualified names, and persisted xmldoc summaries. Queries are tokenized on whitespace, deduplicated case-insensitively, and scored as ranked-OR across fields. |
| **Dead code** ¹ | **[EXPERIMENTAL. ADVISORY ONLY]** Scan the graph for symbols with no incoming semantic references. Consults `IUnityReachabilityProvider` (when injected) so MonoBehaviour magic methods + Unity entrypoint attributes are excluded as runtime-reachable. See the caveat below before acting on findings. |
| **Partial view** | Combined source of every partial declaration of a type. Walks file-level `Contains` edges, reads each file via `IFileSystem`, emits per-segment source plus a concatenated combined view with file headers. |
| **Invariant check** | Query the architectural invariants declared in the loaded project's `CLAUDE.md`. Three modes: pass `id` to fetch one invariant's full body + title + category + source line; pass `mode="audit"` (default) for a summary with total count, per-category breakdown, duplicate-id collisions, and parse warnings; pass `mode="list"` for every id + title index. Works on any project with `INV-*` markers (Lifeblood itself has 70; DAWG has none today; empty projects gracefully return an empty audit). `INV-INVARIANT-001`. |
| **Authority report** | Quantify how much architectural authority a type holds. Returns `implementedInterfaceCount`, `ownedPublicSurface`, per-interface usage (`memberCount` + `consumerCount`), and `forwarderRatio` (in [0.0, 1.0] or sentinel `-1.0` when no method has classification data). Single graph walk produces every field. `INV-AUTHORITY-001`. |
| **Port health** | Score how 'alive' the members of an interface or class are. Returns `memberCount`, `liveMembers`, `deadMembers`, `livenessPct`, and a `verdict`: `healthy` (>=75%), `mixed` (>=25%), `vestigial` (<25%), or `empty`. Methods that implement an interface are alive by definition (Implements outgoing edge). |
| **Cycles** | List every dependency cycle in the loaded graph. Each entry is a strongly-connected component of size >= 2 in symbol-id order. Computed from the existing `CircularDependencyDetector` Tarjan SCC pass; this tool just exposes the result. `INV-NICE-007`. |

The difference: the AI agent doesn't guess what your code does. It **asks the compiler**.

---

## ¹ `lifeblood_dead_code` status

Self-analysis (post P3 / v0.6.7): 8 findings on Lifeblood itself. Five false-positive classes were closed in v0.6.4 (interface dispatch, member access granularity, null-conditional property access, lambda context attribution, method-group references) plus the root-cause compilation gap (missing implicit global usings). Three more closed in v0.6.5 (constructor `Calls` edge, field-initializer containing method, property-accessor body context). The Unity reachability port (`INV-UNITY-001`) closed the MonoBehaviour magic-method false-positive class on real Unity workspaces: DAWG (87 modules) went from 1095 dead-code findings to 729 (-33%), MonoBehaviour-magic FPs from 378 to 13 (-97%).

**Remaining false-positive classes (structural, not fixable by static analysis):**
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

Module additions/removals automatically fall back to full re-analyze.

## Workspace auto-refresh for compile-check

`lifeblood_compile_check` auto-refreshes the workspace when any tracked file has changed on disk since the last analyze, so you can edit source between an analysis and a compile-check without stale results. Opt out with `staleRefresh: false` to check against the pinned state. The response carries `autoRefreshed: true` + `changedFileCount: N` when a refresh actually ran. Asmdef edits also trigger a full re-analyze on the next round (`INV-UNITY-002`).

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

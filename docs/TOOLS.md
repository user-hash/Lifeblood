# Tools

Lifeblood exposes **29 MCP tools** in one session. 17 read, 12 write. All share the same loaded workspace.

Every read-side tool that takes a `symbolId` routes through `ISymbolResolver` before any graph or workspace lookup. Resolution order: exact canonical match, truncated method form (single-overload lenient), kind correction (`method:` prefix on a property/field/event name on the same type, `INV-RESOLVER-006`), bare short name, extracted short name from a kind-prefixed or qualified input (`INV-RESOLVER-005`). Truncated ids, bare short names, qualified-but-wrong-namespace ids, and kind-mismatched ids all resolve correctly across the whole read surface.

Every read-side tool response carries a top-level `envelope` field (`INV-ENVELOPE-001`) with `truthTier` (Semantic / Derived / Heuristic / Inferred), `confidence` (Proven / Advisory / Speculative), `evidenceSource` (Semantic / Inferred / Heuristic), `stalenessSeconds`, `filesChangedSinceAnalyze`, and per-tool `limitations[]`. Errors deliberately do NOT carry envelopes. Per-tool classification lives on `ToolDefinition.EnvelopeClassification` in the registry, projected into the decorator at startup, so registry and decorator cannot drift.

## Write-side (compiler-as-a-service)

| Tool | What it does |
|------|-------------|
| **Execute** | Run C# code against your actual project types, in-process. Access any class, call any method, inspect any state. Optional `targetProfile` argument selects the BCL ref-pack (`host` default, `net-standard-2.1`, `net-6.0`); missing packs fall back to host BCL with `targetRuntimeWarnings`. On Unity workspaces (`Library/` exists at the project root) DLLs from `Library/ScriptAssemblies/`, `Library/Bee/artifacts/`, and `Library/PackageCache/` are auto-injected; an empty `Library/` surfaces a `runtimeAssemblyWarnings` entry. Sandbox globals: `Graph`, `Compilations`, `ModuleDependencies`, plus `Help` (cheat sheet), `SymbolsOfKind(string)`, `EdgesOfKind(string)`. |
| **Diagnose** | Real compiler diagnostics. Errors, warnings, with file, line, and module. Optional `filePath` argument scopes the result to one source file (the typical "did the file I just edited compile cleanly?" question); without it, returns the full project diagnostics. Optional `moduleName` scopes to a single compilation. The response includes a `scope` discriminator (`file` / `module` / `project`) plus `definesActive` (the sorted, deduplicated preprocessor symbols bound under that scope) plus `resolvedModule` — distinguishes Editor-only findings from release-build risk without re-running (`INV-DIAGNOSTIC-ENVELOPE-DEFINES-001`). |
| **Compile-check** | "Will this snippet compile in my project?" answered in milliseconds. Pass either `code` (inline source) or `filePath` (read from disk via `IFileSystem`); supplying both is rejected. Every response surfaces `definesActive` (the sorted, deduplicated preprocessor symbols the snippet/file bound under) plus `resolvedModule` so callers can tell Editor-only findings apart from release-build risk without re-running (`INV-DIAGNOSTIC-ENVELOPE-DEFINES-001`). **File-mode (`LB-BUG-019`)**: when `filePath` matches a file already loaded into the workspace, the host auto-detects which compilation owns it and **swaps the existing syntax tree** for the on-disk content via `ReplaceSyntaxTree`. The check runs against the file's real reference set — UnityEngine, sibling partials, every cross-file type stays resolved. Pre-fix Unity files surfaced ~120 spurious CS0246 errors; post-fix `success:true` against the file's owning module. Response also carries `existingTreeReplaced`. **Snippet-mode**: bare statement snippets (`var x = 1 + 1;`) are auto-wrapped in a synthetic class + method body so they compile against library modules (which otherwise reject top-level statements with CS8805). Complete compilation units (`class Foo { ... }`) still pass through unchanged. Diagnostic line numbers are remapped back to the user's original coordinates. |
| **Enum coverage** | Per-member reference coverage for an enum type. Walks every loaded compilation once, classifies each member reference by parent syntax — `produced` (RHS of assignment, return / yield / arrow body, argument, variable initializer), `consumedComparison` (`==`, `!=`, `<`, `<=`, `>`, `>=`), `consumedSwitch` (case label, is-pattern, constant-pattern, switch-expression arm) — and returns one row per declared member with `totalReferences`, `producedCount`, `consumedComparisonCount`, `consumedSwitchCount`, `isUnproduced` (declared + referenced as a consumer but never assigned), `isUnreferenced` (zero references). Response carries `unproducedCount` + `unreferencedCount` as top-level summaries so the dogfood case ("which values in this state-machine enum are checked-for but never produced?") reads off one call. `enumTypeId` accepts canonical (`type:NS.T`), fully-qualified (`NS.T`), or bare short name (`T`) — routed through `ISymbolResolver` the same way every other type-id-taking tool resolves (`INV-ENUM-COVERAGE-001`). |
| **Static tables** | Extract `static` field / property collection-shaped initializers on a type as typed row + cell facts. Reads from `SemanticModel.GetOperation` only (no regex, no syntax-text parsing, no static-constructor execution). Each table reports container kind (`Array` / `CollectionExpression` / `ObjectCreation`), ordered rows in source order, and per-cell classification into `Null` / `Bool` / `String` / `Number` / `EnumMember` / `EnumFlags` (`Bits.A | Bits.B` flattened to a leaf id list in authoring order) / `MethodGroup` (delegate-target method id only — body content is dataflow, a separate truth tier) / `FieldReference` / `Array` (nested literal arrays recursively classified) / `Computed` (eternal fallback — caller reads `rawText` for source span). Constructor rows bind cells by parameter name + ordinal with `argumentKind` mirroring Roslyn's `Explicit` / `DefaultValue` / `ParamArray`; literal-array rows carry their value on `row.value` instead. Caps via `memberName` / `maxRows` (default 1024) / `maxTables` (default 64); zero / negative caps clamp to the default. The extractor is generic — no consumer-domain vocabulary in source, pinned by `StaticTableNameLeakageTests` (`INV-EXTRACT-STATIC-TABLES-001`). |
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
| **Analyze** | Load a project into a verified semantic graph. Symbols, edges, modules, violations. Pass `incremental: true` after the first analysis for fast re-analysis (only changed modules recompile, csproj edits trigger re-discovery). **Caller-owned scope policy (`INV-ANALYZE-FALLBACK-001`)**: by default `incremental: true` REJECTS when the adapter detects drift it cannot honor cheaply (no prior cache, module set changed, project descriptor edited). Pass `allowFullFallback: true` to opt into silent widening. Wire shape: `mode` reports what the adapter DID (`full` / `incremental` / `incremental-noop` / `rejected`), `requestedMode` separately reports what the caller ASKED, `fallbackReason` (`noPriorAnalysis` / `moduleSetChanged` / `moduleDescriptorChanged`) + `fallbackDetail` populate alongside whenever the cheap path could not be honored. Rejection responses additionally carry `canRetryFull: true` and a `suggestedRetry: { incremental: true, allowFullFallback: true }` block — the next move is self-documenting, no out-of-band knowledge required. Rejection is a NORMAL structured result, not a transport / tool error. Every response carries a `usage` field with wall time, CPU time, peak memory, and GC counters. |
| **Context** | AI context pack with summary, high-value files, boundaries, invariants, hotspots, reading order, and a module dependency matrix. **Smart-dynamic shaping (`LB-FR-022`)**: every list-section has a sensible default cap (25 files / 50 boundaries / 20 hotspots / 50 reading-order / 100 matrix entries) so the response fits inside conservative tool-result budgets even on multi-module Unity workspaces. Override per-section caps with `maxFiles` / `maxBoundaries` / `maxHotspots` / `maxReadingOrder` / `maxMatrixEntries` (`-1` unlimited; `0` drops the section). Pass `summarize:true` for the smallest viable shape (only summary + invariants + violations). Pass `sections:["boundaries"]` to allow-list specific sections. Every clipped section is reported in the response's `truncated` map with its full pre-clip count. |
| **Lookup** | Symbol details: kind, file, line, visibility, properties. For partial types, returns the deterministic primary `filePath` and the full sorted `filePaths[]` of every partial declaration. |
| **Dependencies** | What does this symbol depend on? Each edge carries `otherEndId`, `kind`, and an optional `callSite` (`filePath`, `line`, `column`, `endLine`, `endColumn`, `containingSymbolId`) pinning the authoring expression — null for graph-derived edges (module DependsOn, type Inherits). One call answers "where in source does X depend on Y?". |
| **Dependants** | What depends on this symbol? Same edge shape as Dependencies — every entry carries optional `callSite` provenance. |
| **Blast radius** | Change this symbol, what breaks? Transitive BFS over the dependency graph. Every response carries `directDependants` (one-hop incoming edges, distinct sources) alongside the existing `affectedCount` (transitive). `summarize:true` returns a compact result with `preview[]` instead of the full `affected[]` array; `maxResults` caps the embedded array; `truncated:true/false` tells callers whether the array was clipped. **Grouping (`groupBy=bucket\|module\|both`)**: switch the response shape from a flat list to bucketed (`byBucket: { Production, Test, Editor, Generated }`) and/or per-module (`byModule`) counts with optional per-group `preview[]`; cap via `previewPerGroup` (default 5). Mutually exclusive with `summarize`. Closes the field-report 2026-05-11 P1 bucketing ask. |
| **File impact** | Change this file, what other files break? Derived from symbol-level edges. |
| **Resolve short name** | Discover canonical IDs from a bare short name when you don't know the namespace. Returns kind, file, line, and disambiguation candidates. Three modes: `exact` (default), `contains` (substring), `fuzzy` (ranked near-matches). |
| **Resolve member** | Type-scoped member lookup with overload disambiguation. Pass `typeName` (canonical `type:NS.T`, fully-qualified `NS.T`, or bare short name `T`) + `memberName` (simple, no parens) + optional `paramTypes[]` for method overload filtering. Returns typed `outcome` (`Unique` / `MultipleMatches` / `NotFound` / `TypeNotFound` / `AmbiguousContainingType`), resolved `resolvedTypeId`, every matching member with `kind` / `filePath` / `line` / `paramDisplay`, and ambiguous-type candidates when the short name was not unique. Use this instead of `resolve_short_name` when you know the containing type — it scopes to ONE specific type instead of flattening every member sharing a global short name. Closes the field-report 2026-05-11 P1 type-scoped-member ask. |
| **Search** | Ranked keyword search over symbol names, qualified names, and persisted xmldoc summaries. Queries are tokenized on whitespace, deduplicated case-insensitively, and scored as ranked-OR across fields. Each result carries a structurally-typed `matchKind` (`name` / `qualifiedName` / `xmlDoc` / `multiple`) reporting which scoring bucket(s) drove the rank, so callers can filter or sort by signal source without parsing the snippet strings (`INV-SEARCH-MATCHKIND-001`). |
| **Dead code** ¹ | **[EXPERIMENTAL. ADVISORY ONLY]** Scan the graph for symbols with no incoming semantic references. Consults `IUnityReachabilityProvider` (when injected) so MonoBehaviour magic methods + Unity entrypoint attributes are excluded as runtime-reachable. Every finding carries triage fields — `directDependants` (incoming non-Contains edge count, forward-compatible signal for future relaxed-criteria modes), `bucket` (`Production` / `Test` / `Editor` / `Generated` — segment-aware path classification mirroring `blast_radius groupBy=bucket`), `declarationOnly` (true iff abstract — deleting one is a public-contract change). Response carries `bucketBreakdown` alongside `kindBreakdown` so a caller can fold the Editor/Generated tail without re-walking `findings[]` (`INV-DEADCODE-TRIAGE-001`). See the caveat below before acting on findings. |
| **Partial view** | Combined source of every partial declaration of a type. Walks file-level `Contains` edges, reads each file via `IFileSystem`, emits per-segment source plus a concatenated combined view with file headers. |
| **Invariant check** | Query the architectural invariants declared anywhere in the loaded project's invariant tree. **Dynamic discovery (`LB-FR-023`)**: walks `<root>/CLAUDE.md`, `<root>/AGENTS.md`, and any `<root>/docs/invariants/**.md` tree via `IFileSystem`. Each source is parsed and aggregated; per-id duplicate detection spans the full set; `audit.SourcePaths[]` reports every contributing file. **Five authoring shapes (`LB-BUG-017` / `LB-BUG-018`)**: A (`- **INV-X-N**: body`), B (`- **INV-X-N. Title.** Body`), C (`**INV-X-N: Title.** body` — bare bold paragraph, no bullet), D (`- **INV-X-N** (vX.Y.Z): body` — parenthesized version tag), E (`- **INV-X-N:** body` — colon inside the bold). Three modes: pass `id` to fetch one invariant's full body + title + category + source line; pass `mode="audit"` (default) for a summary with total count, per-category breakdown, duplicate-id collisions, and parse warnings; pass `mode="list"` for every id + title index. The dogfood Unity workspace: 83 invariants discovered across 25 categories (CLAUDE.md hot rules + AGENTS.md project instructions + `docs/invariants/**.md` tree), 0 parse warnings, 0 duplicates. Lifeblood self: 90 invariants across 52 categories. `INV-INVARIANT-001`. |
| **Authority report** | Quantify how much architectural authority a type holds. Returns `implementedInterfaceCount`, `ownedPublicSurface`, per-interface usage (`memberCount` + `consumerCount`), and `forwarderRatio` (in [0.0, 1.0] or sentinel `-1.0` when no method has classification data). Single graph walk produces every field. `INV-AUTHORITY-001`. |
| **Port health** | Score how 'alive' the members of an interface or class are. Returns `memberCount`, `liveMembers`, `deadMembers`, `livenessPct`, and a `verdict`: `healthy` (>=75%), `mixed` (>=25%), `vestigial` (<25%), or `empty`. Methods that implement an interface are alive by definition (Implements outgoing edge). |
| **Cycles** | List every dependency cycle in the loaded graph. Each entry is a strongly-connected component of size >= 2 in symbol-id order. Computed from the existing `CircularDependencyDetector` Tarjan SCC pass; this tool just exposes the result. **Taxonomy (`INV-CYCLE-TAXONOMY-001`)**: each SCC is classified into one of three triage buckets surfaced via `descriptors[] { symbols, bucket }` plus aggregate `bucketBreakdown` on the wire — `GeneratedOrStaticAnalysisArtifact` (build artifacts / source-generator output, never a refactor target — short-circuits ahead of every other signal), `PartialClassCluster` (every participating member resolves to the same enclosing type — intra-type mutual-recursion or partial-class spread), or `LikelyRealLoop` (cross-type / cross-module architectural-backlog cycles). Legacy `cycles[][]` shape stays alongside `descriptors[]` for back-compat. **Pagination (`LB-FR-021`)**: every response carries `count`, `totalSymbolCount`, `largestCycleSize`, and `truncated`. Pass `summarize:true` for a compact `preview[]` + `previewClassified[]` instead of the full arrays; `maxResults` caps the embedded array regardless of mode (default 500 / 25 in summarize). Large workspaces (100+ SCCs ~70KB) overflow downstream tool-result limits without summarize; the small-preview shape fits in default budgets. |
| **Test impact** | Which test classes transitively depend on a target symbol or file. BFS over incoming non-Contains edges with per-symbol minimum-distance tracking; affected methods are classified test-vs-non-test via the extractor-recorded `Properties["attributes"]` set (`Test`, `TestCase`, `TestCaseSource`, `Theory`, `UnityTest`, `Fact`). Lifecycle attributes (`SetUp`, `OneTimeSetUp`, `TearDown`, `UnityTearDown`) are intentionally excluded — they participate in test execution but are not the assertion-bearing methods a caller wants enumerated. Affected test methods are folded by containing type; per-class `minDistance` is the smallest hop count, mapped to confidence `Direct` (1) / `OneHop` (2) / `Transitive` (3+). Response carries `target`, `targetKind` (`Symbol` or `File`), `totalTestMethodCount`, `directTestClassCount`, `affectedTestClassCount`, `affectedTestClasses[]` sorted by ascending distance then qualified name, plus `recommendedFilters[]` — pre-composed `FullyQualifiedName~<class>` strings the caller pastes into `dotnet test --filter` without composing the filter syntax themselves. Disambiguation: a `target` value starting with a canonical-id prefix (`type:` / `method:` / `field:` / `property:` / `mod:` / `file:` / `ns:` / `namespace:`) routes through the symbol resolver; otherwise treated as a file path with every symbol declared in the file becoming a multi-source BFS start (`INV-TEST-IMPACT-001`). |

The difference: the AI agent doesn't guess what your code does. It **asks the compiler**.

---

## ¹ `lifeblood_dead_code` status

Self-analysis (post-v0.7.3): 6 findings on Lifeblood itself, 3 legitimate (`Program.Main` × 3) + 3 false positives currently tracked as `LB-INBOX-010`. Five false-positive classes were closed in v0.6.4 (interface dispatch, member access granularity, null-conditional property access, lambda context attribution, **explicit-form** method-group references via `new Lazy<T>(Method)`) plus the root-cause compilation gap (missing implicit global usings). Three more closed in v0.6.5 (constructor `Calls` edge, field-initializer containing method, property-accessor body context). The Unity reachability port (`INV-UNITY-001`) closed the MonoBehaviour magic-method false-positive class on real Unity workspaces: the dogfood Unity workspace (87 modules) went from 1095 dead-code findings to 729 (-33%), MonoBehaviour-magic FPs from 378 to 13 (-97%).

**Remaining false-positive classes (structural; not currently closed by the extractor):**
- Runtime entry points (Program.Main, composition-root entries).
- UI Toolkit `VisualElement` subclasses with magic-named methods (Awake/Update on a non-MonoBehaviour base).
- Audio callbacks (`OnAudioFilterRead`) on bases not in the standard MonoBehaviour message-receiver set.
- Reflection-based dispatch invisible to static analysis (`Type.GetType` + `MethodInfo.Invoke`, Unity `SendMessage`-dispatched handlers).
- **Target-typed `new(MethodGroup)`** (`LB-INBOX-010`, open). The v0.6.4 method-group fix closed the explicit form (`new Lazy<T>(Method)`); the target-typed form (`new(Method)`, C# 9+) does not currently emit a `Calls` edge to the method-group argument. `find_references` sees the usage via Roslyn but `dependants` / `dead_code` / `blast_radius` walk the graph and miss it. Pinned by a skipped regression test (`ExtractEdges_StaticFieldInitializerMethodGroup_TargetTypedNew_AttributedToCctor`) that will convert to a ratchet when the extractor fix ships.
- **Generic-method call canonical-id drift** (`LB-INBOX-010`, open). Calls to a generic method via type-inferred arguments (e.g. `ApplyCap(pack.HighValueFiles, maxFiles)`) appear to bind to the instantiated `IMethodSymbol`; if the edge is emitted under that instantiated id rather than the source-declared generic id, the graph misses the back-reference.

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

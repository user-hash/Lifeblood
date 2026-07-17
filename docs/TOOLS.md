# Tools

Lifeblood exposes the **MCP tool surface** — read side + write side — in one shared session against a single loaded workspace. Live read / write counts in [`STATUS.md`](STATUS.md).

Every read-side tool that takes a `symbolId` routes through `ISymbolResolver` before any graph or workspace lookup. Resolution order: exact canonical match, truncated method form (single-overload lenient), kind correction (`method:` prefix on a property/field/event name on the same type, `INV-RESOLVER-006`), bare short name, extracted short name from a kind-prefixed or qualified input (`INV-RESOLVER-005`). Truncated ids, bare short names, qualified-but-wrong-namespace ids, and kind-mismatched ids all resolve correctly across the whole read surface.

Every read-side tool response carries a top-level `envelope` field (`INV-ENVELOPE-001`) with `truthTier` (Semantic / Derived / Heuristic / Inferred), `confidence` (Proven / Advisory / Speculative), `evidenceSource` (Semantic / Inferred / Heuristic), `stalenessSeconds`, `filesChangedSinceAnalyze`, per-tool `limitations[]`, and the exact `snapshotId`, `analysisGeneration`, and bounded `analysisIdentity` used by the response. Errors deliberately do NOT carry envelopes. Per-tool classification lives on `ToolDefinition.EnvelopeClassification` in the registry, projected into the decorator at startup, so registry and decorator cannot drift.

## Write-side (compiler-as-a-service)

### Contract Audit State Access

Use manifest `stateAccesses[]` to classify state touched by bounded
`callRoutes[]`. Select exact `targetSymbolIds[]` or `matchAnyMember:true`, a
`memberScope` (`Static`, `Instance`, or `Any`), allowed risk buckets, and a hard
member cap. Wildcard discovery follows only profile-applicable graph
`References` edges from retained route methods; it does not sweep and retain a
second workspace member index. The existing operation stream then supplies
route reads, direct writes to retained candidates, and route element writes.

The receipt reports candidate, retained, and accessed member counts plus all
five buckets: `ReadonlyTable`, `InitializedOnceCache`, `RuntimeMutable`,
`SharedScratch`, and `Unknown`. A truncated fact scan forces observed members to
`Unknown`. Findings carry bounded route and write evidence. Reflection, native
or alias mutation, and container mutation outside selected direct/element
evidence remain limitations; do not interpret a bucket as runtime proof beyond
the reported semantic evidence.

### Unity Package Source Visibility

For Unity package workspaces, `lifeblood_analyze` includes
`packageSourceVisibility`: package roots discovered from
`Packages/manifest.json`, `Packages/packages-lock.json`, and embedded package
descriptors; package asmdefs; included/excluded/unbound source counts; and
truncation facts. The default `packageSourceVisibilityMode:"summary"` returns
aggregate counts plus only excluded/unbound package rows and omits per-file
inventories; pass `packageSourceVisibilityMode:"detail"` for every package row
with a bounded deterministic file preview. Included means the package file is in the loaded
Roslyn descriptor set after `excludePaths`; excluded means `excludePaths`
removed it from analysis; unbound means the file lives under a known package
source root but is absent from the loaded project descriptors.

When `lifeblood_compile_check(filePath)` targets a known package file, the
response adds `packageSourceResolution` with package name/root, status, reason,
expected asmdef assembly, and a concrete remedy. This shares the same
adapter-owned receipt as analyze (`INV-UNITY-PACKAGE-SOURCE-001`).

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

- **Intentional scaffolding downrank** — `INV-DEADCODE-SCAFFOLDING-001` (`LB-INTAKE-20260608-003`). Non-public static types whose direct members are exclusively `[Conditional]` methods and/or static const string anchors, plus those direct members, are still reported but bucketed as `Scaffolding` instead of ordinary Production deletion candidates.

- **Resolved UnityEvent persistent calls** — `INV-UNITYEVENT-REACHABILITY-001` (`LB-INTAKE-20260601-001`). The Unity reachability adapter scans `.prefab` / `.unity` / `.asset` YAML, resolves `m_Script` GUIDs through `.cs.meta`, and marks resolved UnityEvent target methods plus host types reachable.

**Remaining false-positive classes (structural; not closed by the extractor):**
- Runtime entry points (Program.Main, composition-root entries).
- UI Toolkit `VisualElement` subclasses with magic-named methods (Awake/Update on a non-MonoBehaviour base).
- Audio callbacks (`OnAudioFilterRead`) on bases not in the standard MonoBehaviour message-receiver set.
- Unresolved Unity serialized callbacks/asset references, serialized enum production, and reflection-based dispatch invisible to static analysis (`Type.GetType` + `MethodInfo.Invoke`, Unity `SendMessage`-dispatched handlers).

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

After the first `lifeblood_analyze`, subsequent calls with `incremental: true` only recompile modules whose source content changed since the last analysis. Filesystem mtimes are the cheap candidate prefilter; source content hashes decide whether a touched file actually re-extracts. Responses report `mtimeTouchedSourceFiles`, `contentChangedSourceFiles`, and `changedSourceFiles` so save-without-content-change is visible as an `incremental-noop`. Csproj timestamp changes (`INV-BCL-005`) and asmdef changes (`INV-UNITY-002`) still trigger per-module re-discovery so compilation facts do not go stale.

```
lifeblood_analyze projectPath="/my/project"                    -> full analysis (~14-34 s depending on workspace size)
lifeblood_analyze projectPath="/my/project" incremental=true   -> seconds when nothing changed; dirty source modules re-analyze by content hash
```

### Caller-owned scope policy (`INV-ANALYZE-FALLBACK-001`)

`lifeblood_analyze` does not silently widen scope when it detects drift. The
adapter classifies the drift into a typed `fallbackReason`
(`noPriorAnalysis` / `moduleSetChanged` / `moduleDescriptorChanged` /
`analysisScopeChanged` / `compilationStateUnavailable` - asmdef edits, csproj structural changes, analyze
`excludePaths` scope changes, read-only-to-retained recovery, etc.) and the caller decides
what to do.

| Caller setting | Adapter behaviour |
|---------------|-------------------|
| `incremental: false` | Always full analyze. `mode: "full"`, `requestedMode: "full"`. |
| `incremental: true`, `allowFullFallback: false` (default) | Cheap path on success. On detected drift: REJECT - `mode: "rejected"`, `requestedMode: "incremental"`, `fallbackReason` + `fallbackDetail` populated, `canRetryFull: true`, plus a `suggestedRetry: { incremental: true, allowFullFallback: true }` block. No work done; caller decides. |
| `incremental: true`, `allowFullFallback: true` | Cheap path on success. On detected drift: silently widen to full and report both - `mode: "full"`, `requestedMode: "incremental"`, `fallbackReason` populated so the cache miss stays visible, work succeeded with summary populated. |

Rejection is a NORMAL structured result, not a transport / tool error.
The `suggestedRetry` block is exactly the args needed to retry — pass it
back to `lifeblood_analyze` to re-attempt with widened scope.

## Snapshot-pinned reads, history, and batches

Every registered `Observe + SharedRead` tool accepts the common optional
preconditions `expectedSnapshotId` and `expectedAnalysisGeneration`. The server
first acquires the read lease, then compares the expectation with that exact
publication. A mismatch returns `failure:"snapshot-precondition"` with the
expected and actual id/generation, the actual canonical analysis identity, and
a retry suggestion; the requested tool does not execute. Omit both fields to
keep the normal latest-snapshot behavior.

Pass `snapshotId` to select one exact current or retained graph-only
publication. Missing or evicted ids return `failure:"snapshot-not-found"` with
the current id and retained inventory; a nested batch call cannot switch away
from its outer lease. Historical graph queries retain their normal response
shape and envelope. Historical publications deliberately contain neither
Roslyn services nor source bytes, so compilation-backed, `partial_view`, and
invariant-tree calls return unavailable instead of mixing historical graph
facts with changing live state (`INV-MCP-HISTORICAL-READ-001`).

`lifeblood_snapshots` owns history inventory and mutations. `action:"list"` is
an observe/shared-read call, so it reports the last committed catalog without
waiting for an in-flight analyze candidate to publish. `pin`, `unpin`, and
`evict` are exclusive catalog mutations. The default catalog retains at most
three graph-only publications, the Application hard maximum is 16, and
unpinned entries expire after 24 hours or by LRU pressure. Pinned entries count
toward the same bound; if all slots are pinned, automatic rollover is refused
and reported rather than growing another cache. Use `checkDrift:true` on list
only when filesystem hashing is worth the I/O cost.

The default `relativeTolerancePercent` is 0.5 for volume metrics. Violations,
cycles, duplicate declarations, and duplicate IDs have a fixed no-increase
policy; live invariant parse warnings must remain zero. Missing/conflicting
baseline metrics fail closed. Before a `current` verdict is allowed, the C#
adapter recaptures source/descriptor/rule identity. If that differs from the
leased publication, the response is `unavailable` with
`refreshAnalysisRecommended:true`; it will not compare a stale graph as if it
were live. A complete current graph with out-of-policy metrics returns
`refreshEvidenceRecommended:true`. The tool reports the exact baseline content
fingerprint and optional baseline/analyze commit comparison as provenance, but
neither replaces canonical analysis identity. It never runs analyze, compiles
a baseline, rewrites the generated file, launches Git, or retains another
semantic base (`INV-EVIDENCE-DRIFT-001`).

For several related reads, `lifeblood_batch` pins once and runs the plan in
stable input order:

The plan is capped at 32 calls and is fully rejected before execution if any
entry is unknown, nested, exclusive, or effectful. `lifeblood_analyze`,
compilation-backed tools, rename/format, and another `lifeblood_batch` are not
eligible. A concurrent refresh may publish a newer latest snapshot, but all
accepted subcalls and the outer batch envelope remain on the leased snapshot
(`INV-SNAPSHOT-PRECONDITION-001`, `INV-MCP-READ-BATCH-001`).

## Workspace auto-refresh for compile-check

`lifeblood_compile_check` auto-refreshes the workspace when any tracked file has changed on disk since the last analyze, so you can edit source between an analysis and a compile-check without stale results. Opt out with `staleRefresh: false` to check against the pinned state. The response carries `autoRefreshed: true` + `changedFileCount: N` when a refresh actually ran. Asmdef edits also trigger a full re-analyze on the next round (`INV-UNITY-002`).

For a natural multi-file edit, pass `filePaths` instead of issuing repeated
single-file calls:

```json
{
  "filePaths": ["src/Kernel.cs", "tests/KernelTests.cs"],
  "staleRefresh": true,
  "verbosity": "compact"
}
```

Batch mode accepts 1..32 ordered paths, prevalidates the complete set, rejects
duplicate resolved paths, refreshes at most once, and checks every file
serially against the same retained compilation/profile. The response carries
aggregate file/success/failure/diagnostic counts plus `results[]` rows with
module ownership, diagnostics, define count/list, file resolution, package
source resolution, retained profile, and `staleRefreshMode`. It does not retain
another graph or Roslyn base. `code`, `filePath`, and `filePaths` are mutually
exclusive (`INV-COMPILE-CHECK-BATCH-001`).

## File-mode compile-check (LB-BUG-019)

When called with `filePath`, `lifeblood_compile_check` resolves the file's owning compilation by matching the path against every loaded compilation's syntax trees, then **swaps the existing tree** for the on-disk content via `ReplaceSyntaxTree` instead of adding the file as a new snippet tree to an arbitrary first compilation:

```
lifeblood_compile_check filePath="Assets/Scripts/Core/MultiPartialHost.cs"
→ {
    "success": true,
    "diagnostics": [],
    "resolvedModule": "Acme.Module.Runtime",
    "existingTreeReplaced": true,
    "fileOwnership": {
      "outcome": "Unique",
      "resolvedModule": "Acme.Module.Runtime",
      "candidateModules": ["Acme.Module.Runtime"]
    },
    ...
  }
```

Pre-fix the same call emitted ~120 spurious CS0246 / CS0103 errors (UnityEngine, MonoBehaviour, sibling partials, every cross-file type) because the snippet path was selected and the file's real owning compilation was never resolved. File-mode preserves every reference and filters pre-existing diagnostics in OTHER files in the module so only changes the user introduced in THIS file surface.

`lifeblood_diagnose` and `lifeblood_compile_check` share one ownership resolver (`INV-COMPILATION-FILE-OWNERSHIP-001`). Exact normalized paths outrank suffix matches. If the best path matches more than one loaded compilation, neither tool selects the first module: `fileOwnership.outcome` is `Ambiguous`, `candidateModules` lists the stable choices, and the caller must pass `moduleName` or a more specific path. A pinned module miss remains `NotInModule`; an unknown module is `ModuleNotFound`; a path absent from every compilation is `NotFound` and compile-check composes the existing stale-descriptor guidance at the disk-aware handler boundary.

### Diagnose only the warnings owned by the current change

`lifeblood_diagnose` accepts `diagnosticOwnershipMode` with `workingTree`,
`staged`, `sinceCommit`, or `explicitFiles`. The first three modes join the
existing Roslyn diagnostics to Git's current-side changed lines; `sinceCommit`
also requires `sinceCommit`. `explicitFiles` requires 1..128 `touchedFiles` and
does not invent line history.

The additive `diagnosticOwnership` block groups entries as
`introducedByDiff`, `preExistingTouchedFile`, `preExistingUnrelated`, and
`unknownOwnership`. Each row contains only `diagnosticIndex` plus its evidence
reason; the full diagnostic remains in the existing `diagnostics[]` array and
is not duplicated. New files and diagnostics located on current-side changed
lines are current-change-owned. Diagnostics elsewhere in a touched file are
conservatively pre-existing. Missing/truncated Git evidence, external/generated
paths, caller paths without line spans, and diagnostics older than the current
source state fail closed to `unknownOwnership`. This is location ownership,
not proof that a changed line caused an error reported somewhere else.

## Truth envelope on every read-side response

Every read-side tool response ships a top-level `envelope` field (`INV-ENVELOPE-001`):

Errors deliberately do NOT carry envelopes. Per-tool classification lives on `ToolDefinition.EnvelopeClassification` in the registry, projected into `LifebloodResponseDecorator` at composition time. Adding a new read-side tool without a classification fails the registry ratchet test.

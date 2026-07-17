# Lifeblood Tool Routing

Use this file as a compact decision map. For complete current semantics, prefer the repository docs: `docs/TOOLS.md`, `docs/MCP_SETUP.md`, `docs/UNITY.md`, and live `lifeblood_capabilities`.

## Startup And State

| Need | Tool | Notes |
|---|---|---|
| Inspect live server/tool surface | `lifeblood_capabilities` | Call first when available; catches local-doc drift and reports session state. |
| Join several reads on one publication | `lifeblood_batch` | Pass ordered `{tool, arguments}` calls plus optional `expectedSnapshotId` / `expectedAnalysisGeneration`. Only `Observe` + `SharedRead` tools are admitted; execution is serial under one snapshot lease. |
| Inspect or retain publication history | `lifeblood_snapshots` | List current + bounded graph-only history; pin/name an investigation lane, unpin, evict, or opt into live drift hashing. Pinned entries still consume the hard limit. |
| Load a project | `lifeblood_analyze` | Use `projectPath` for C# / Unity, `graphPath` for JSON graph input. Use `excludePaths` to drop vendored/sample sources before compilation. |
| Fast re-load after edits | `lifeblood_analyze incremental:true` | Source mtimes are a prefilter and content hashes decide actual re-extraction. Pass `authoritativeChangedFiles` when the editor/watcher has exact changed paths. On rejected fallback, retry with `allowFullFallback:true` only when wider scope is acceptable. Changing `excludePaths` reports `analysisScopeChanged`; read-only-to-retained recovery reports `compilationStateUnavailable`. |
| Small context pack | `lifeblood_context summarize:true` | Good first read for unfamiliar repos or when handing off to another agent. |
| Architecture invariant audit | `lifeblood_invariant_check mode:"audit" summarize:true` | Use compact audit first on large invariant trees; it keeps duplicates/warnings/coverage while omitting zero-only source rows and duplicate ledgers. Fetch specific ids as needed; omit summarize only for the complete source inventory. |
| Check generated evidence freshness | `lifeblood_evidence_drift` | Compare the repo-owned generated baseline with the exact current publication and live invariant audit. Pin `expectedSnapshotId` / `expectedAnalysisGeneration`; refresh analysis on `verdict:"unavailable"`, refresh evidence when `evidenceRefreshRecommended:true`. |
| Import/correlate/compare runtime performance evidence | `lifeblood_performance_evidence` | `import` and `compare` accept an absolute `workspaceRoot` and need no loaded graph. `correlate` alone joins marker evidence to the current source-evidence publication with explicit Unique/Ambiguous/Unmapped outcomes and rejects a mismatched root. Runtime evidence stays separate from semantic truth. |

## Finding Symbols

| Need | Tool | Notes |
|---|---|---|
| Find canonical id from a name | `lifeblood_resolve_short_name` | Use `mode:"contains"` or `mode:"fuzzy"` when exact lookup fails. |
| Find a member on a known type | `lifeblood_resolve_member` | Prefer this over global short-name search for methods/properties/fields. |
| Identify code at file/line/column | `lifeblood_symbol_at_position` | Useful when the user points at a source location. |
| Inspect symbol metadata | `lifeblood_lookup` | Returns kind, file, line, visibility, and properties. |
| Read XML docs | `lifeblood_documentation` | Use for API intent, not behavioral proof. |
| View all partial declarations | `lifeblood_partial_view` | Essential before editing partial hosts or generated-like split types. |

## Dependency Questions

| Question | Tool | Notes |
|---|---|---|
| "What does this symbol use?" | `lifeblood_dependencies` | Outgoing graph edges with call-site provenance when available. |
| "Who uses this symbol?" | `lifeblood_dependants` | Incoming graph edges; group/filter by bucket or module for triage. |
| "What breaks if I change this symbol?" | `lifeblood_blast_radius` | Use `groupBy:"both"` for production/test/module split. |
| "What breaks if I change this file?" | `lifeblood_file_impact` | File-level impact derived from symbol edges. |
| "Does this Unity asmdef declare its cross-module dependency?" | `lifeblood_asmdef_check` | Graph-only DirectOnly module boundary check; reports first offending edge/call site per source-target pair. |
| "Which tests should I run?" | `lifeblood_test_impact` | Works on symbol ids or file paths; use recommended filters. |
| "Are there dependency cycles?" | `lifeblood_cycles summarize:true` | Inspect `bucketBreakdown`; prioritize `LikelyRealLoop`. |

## Compiler And Edit Validation

| Need | Tool | Notes |
|---|---|---|
| Check one edited file | `lifeblood_compile_check filePath:"..."` | Preferred post-edit validation; auto-refreshes stale workspace by default. |
| Check snippet feasibility | `lifeblood_compile_check code:"..."` | Good before adding API calls or experimenting with syntax. |
| Build a generated DSP/math probe | Contract/source inspection -> `lifeblood_compile_check` -> consumer test runner | Follow workflow 8 in `docs/PLAYBOOK_CSHARP.md`. Derive cases from one consumer-owned range/contract authority; Lifeblood verifies structure but does not execute audio or numerical output tests. |
| Get project/module/file diagnostics | `lifeblood_diagnose` | Use file scope first to avoid drowning in existing project warnings. |
| Find exact source references | `lifeblood_find_references` | Compiler-backed write-side operation; honors retained profile limitations. |
| Find declarations | `lifeblood_find_definition` | Use before editing unfamiliar APIs. |
| Find implementers/overrides | `lifeblood_find_implementations` | Use for interface and virtual-method changes. |
| Preview rename edits | `lifeblood_rename` | Returns edits only; caller applies them deliberately. |
| Format C# | `lifeblood_format` | Roslyn formatting for generated or replaced code. |
| Execute C# against workspace state | `lifeblood_execute` | Use for semantic inspection; CS1061 diagnostics include scripting-surface hints when possible. Do not rely on runtime instantiation of workspace types unless supported. |

## Specialized Analysis

| Need | Tool | Notes |
|---|---|---|
| Check enum values produced/consumed | `lifeblood_enum_coverage` | Finds unproduced or unreferenced state-machine-like values. |
| Count a type's declared members (for a ratchet) | `lifeblood_member_count` | `reflectionDeclared` = bit-exact System.Reflection DeclaredOnly; `sourceSymbols` = graph child count. Offline alternative to a live reflection run. |
| Compute struct offsets/sizes | `lifeblood_struct_layout` | Field offsets, size, alignment, pack, fixed buffers. Exact for known blittable Sequential/Explicit structs; Advisory with limitations for Auto/reference/non-blittable shapes. |
| Inspect static dispatch/config tables | `lifeblood_static_tables` | Operation-tree extraction; use `summarize:true` for large tables. |
| Check object-initializer wiring | `lifeblood_assignment_coverage` | Useful for bindings/delegate slots and construction completeness. |
| Triage unused code candidates | `lifeblood_dead_code` | Advisory; verify before deleting. Fold `bucket:"Vendored"` and `bucket:"Scaffolding"` separately from ordinary Production findings. |
| Field read but never written / delegate slot never wired? | `lifeblood_wire_audit` | Dead-WIRE complement of dead_code: referenced but structurally unplugged. Advisory. |
| Boolean feature flag gated but never flipped (dormant)? | `lifeblood_feature_switch_audit` | Verdict `AlwaysDefaultInGraph` / `TestOnlyActivation` / `RuntimeMutable`. Advisory. |
| Do call sites actually pass the new/optional argument? | `lifeblood_callsite_arguments` | Per-site argument facts + supplied/omitted histogram; the API-adoption gap. |
| Enforce consumer-owned guards, external API costs, shared-state risk, control-law handoffs, determinism hazards, sibling parity, ownership bypasses, value/shape, lifecycle, smoothing, or discontinuity policy? | `lifeblood_contract_audit` | One versioned manifest, profile-aware bounded call routes, one operation-fact scan, explicit per-contract coverage, summary-first findings; no extra graph or semantic base. Put policies that share a route/profile in the same manifest so secondary-profile compilation and scanning happen once. `routeFacts[]` supplies `RequiredOnEveryRoute`, consumer-dimensioned `EquivalentAcrossRoutes`, and exact-target `AllowedRoutesOnly`; the manifest names routes and meaning because Lifeblood does not infer execution lanes or behavioral equivalence. `stateAccesses[]` classifies route-referenced state into five risk buckets with candidate/retained/accessed counts. `evaluatedOccurrenceCount:0` is a coverage gap and finding-free counts are occurrence-local. Branch-arm context locates nested occurrences; only value-producing conditional expressions expose arm values, while statement branch bodies remain control structure. |
| Audit retired comment prose, named contract evidence, or invariant-to-test links? | `lifeblood_contract_audit` | Add caller-authored `sourceTextPolicies[]` and/or `invariantEvidence[]` to the same route/operation manifest. Text matches remain advisory; coverage is named category status + concrete gaps + non-exclusive states, never one score. `CallerDeclared` external receipts are attributed references, not executed proof. One bounded retained-tree evidence stream, no source index or extra semantic base. |
| Measure interface/class liveness | `lifeblood_port_health` | Good for ports, facades, and suspiciously wide contracts. |
| Quantify facade/dispatcher authority | `lifeblood_authority_report` | Use for types that aggregate many subordinates or interfaces. |
| Check source-of-truth authority reachability | `lifeblood_authority_coverage` | Matrix of subjects vs required authorities; reports missing authorities, shortest paths, and allowed alternatives. |
| Search by intent or xmldoc | `lifeblood_search` | Better than grep when names are unknown but docs mention behavior. |

### The wiring family

Three tools answer "is this code actually plugged in?", in escalating subtlety — reach for the right one by what the symbol's reference state is:

- `lifeblood_dead_code` — the symbol is **never referenced**. Classic unused code.
- `lifeblood_wire_audit` — the symbol **is referenced but structurally unplugged**: a field read with zero writes, a delegate/binding slot nothing assigns.
- `lifeblood_feature_switch_audit` — the boolean **is referenced, gates a live branch, but is pinned to its default** because no reachable code flips it (e.g. a public setter with zero callers). Looks shipped; never activates.

All three are advisory: resolved UnityEvent persistent calls are modeled, but unresolved serialized targets, reflection, and runtime/config assignment are still invisible to static analysis, so none is deletion authority on its own. Confirm with references, source inspection, and tests.

## Multi-Profile And Unity

- For Unity Editor/Player/Desktop differences, analyze with `defineProfiles:["Editor","Player","Standalone"]` when possible; `Standalone` covers platform-neutral `UNITY_STANDALONE && !UNITY_EDITOR` callsites.
- Use graph-side `dependants` and `dependencies` with `profileFilter` for union-profile dependency questions.
- Treat write-side Roslyn tools as retained-profile scoped. Check `analyzedUnderProfile` and `limitations`.
- New Unity files need Unity import/project descriptor regeneration before Lifeblood can include them.

## Result Interpretation

- `truthTier:"Semantic"` and `confidence:"Proven"` are the strongest evidence.
- `Derived` graph rollups are usually strong but one step removed from raw compiler facts.
- `Heuristic` / `Advisory` results are triage aids, not deletion or rewrite authority.
- Any non-empty `limitations[]`, high `stalenessSeconds`, or non-zero `filesChangedSinceAnalyze` should affect confidence and usually calls for re-analysis or direct source verification.
- Historical `snapshotId` selection is graph-only. Source-reading and Roslyn-backed tools are unavailable there; omit `snapshotId` to return to the latest semantic base.

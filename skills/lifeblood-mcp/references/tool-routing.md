# Lifeblood Tool Routing

Use this file as a compact decision map. For complete current semantics, prefer the repository docs: `docs/TOOLS.md`, `docs/MCP_SETUP.md`, `docs/UNITY.md`, and live `lifeblood_capabilities`.

## Startup And State

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
| Check one edited file | `lifeblood_compile_check filePath:"..."` | Auto-refreshes by default; inspect `refresh` for its outcome and trigger. Pass `expectedFileScope` plus optional expected publication identity for exact captured-byte admission; guarded checks disable refresh. Batches expose `staleRefresh.receipt`. |
| Check snippet feasibility | `lifeblood_compile_check code:"..."` | Good before adding API calls or experimenting with syntax. |
| Build a generated DSP/math probe | Contract/source inspection -> `lifeblood_compile_check` -> consumer test runner | Follow workflow 8 in `docs/PLAYBOOK_CSHARP.md`. Derive cases from one consumer-owned range/contract authority; Lifeblood verifies structure but does not execute audio or numerical output tests. |
| Get project/module/file diagnostics | `lifeblood_diagnose` | Use file scope first to avoid drowning in existing project warnings. |
| Inspect effective compiler options | `lifeblood_diagnose(mode:"environment", filePath:...)` | One explicit file/module; compact mode omits defines but preserves counts. Actual retained options, bounded references and snapshot descriptor/profile provenance; no refresh or runtime qualification. |
| Find exact source references | `lifeblood_find_references` | Compiler-backed write-side operation; honors retained profile limitations. |
| Find declarations | `lifeblood_find_definition` | Use before editing unfamiliar APIs. |
| Find implementers/overrides | `lifeblood_find_implementations` | Use for interface and virtual-method changes. |
| Preview rename edits | `lifeblood_rename` | Returns edits only; caller applies them deliberately. |
| Format C# | `lifeblood_format` | Roslyn formatting for generated or replaced code. |
| Execute C# against workspace state | `lifeblood_execute` | Use for semantic inspection; CS1061 diagnostics include scripting-surface hints when possible. Do not rely on runtime instantiation of workspace types unless supported. |

For conditional existing-file application on Windows, use the standalone
`lifeblood apply-file` companion with the pre-edit file-scope receipt and staged
raw bytes. It checks the entire scope while holding sharing locks through one
target write and returns the post-edit receipt. Obtain project source admission
first; separate verify-then-write does not prevent races. See
`docs/OWNER_SCOPED_WORK.md` for bounds, failure handling and exclusions.

## Specialized Analysis

| Need | Tool | Notes |
|---|---|---|
| Check enum values produced/consumed | `lifeblood_enum_coverage` | Finds unproduced or unreferenced state-machine-like values. |
| Count a type's declared members (for a ratchet) | `lifeblood_member_count` | `reflectionDeclared` = bit-exact System.Reflection DeclaredOnly; `sourceSymbols` = graph child count. Offline alternative to a live reflection run. |
| Compute struct offsets/sizes | `lifeblood_struct_layout` | Field offsets, size, alignment, pack, fixed buffers. Exact for known blittable Sequential/Explicit structs; Advisory with limitations for Auto/reference/non-blittable shapes. |
| Inspect static dispatch/config tables | `lifeblood_static_tables` | Operation-tree extraction; use `summarize:true` for large tables. |
| Check object-initializer wiring | `lifeblood_assignment_coverage` | Useful for bindings/delegate slots and construction completeness. |
| Triage unused code candidates | `lifeblood_dead_code` | Advisory; verify before deleting. Fold `bucket:"Vendored"` and `bucket:"Scaffolding"` separately from ordinary Production findings. |
| Find repeated executable bodies | `lifeblood_duplicates` | Bounded retained-profile token equality with physical spans and explicit truncation; advisory for behavior or deletion. For competing architecture owners, compose `lifeblood_authority_coverage` in one batch and keep the categories separate. |
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

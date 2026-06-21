# Lifeblood Tool Routing

Use this file as a compact decision map. For complete current semantics, prefer the repository docs: `docs/TOOLS.md`, `docs/MCP_SETUP.md`, `docs/UNITY.md`, and live `lifeblood_capabilities`.

## Startup And State

| Need | Tool | Notes |
|---|---|---|
| Inspect live server/tool surface | `lifeblood_capabilities` | Call first when available; catches local-doc drift and reports session state. |
| Load a project | `lifeblood_analyze` | Use `projectPath` for C# / Unity, `graphPath` for JSON graph input. |
| Fast re-load after edits | `lifeblood_analyze incremental:true` | On rejected fallback, retry with `allowFullFallback:true` only when wider scope is acceptable. |
| Small context pack | `lifeblood_context summarize:true` | Good first read for unfamiliar repos or when handing off to another agent. |
| Architecture invariant audit | `lifeblood_invariant_check mode:"audit"` | Use before architecture-sensitive changes; fetch specific ids as needed. |

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
| "Which tests should I run?" | `lifeblood_test_impact` | Works on symbol ids or file paths; use recommended filters. |
| "Are there dependency cycles?" | `lifeblood_cycles summarize:true` | Inspect `bucketBreakdown`; prioritize `LikelyRealLoop`. |

## Compiler And Edit Validation

| Need | Tool | Notes |
|---|---|---|
| Check one edited file | `lifeblood_compile_check filePath:"..."` | Preferred post-edit validation; auto-refreshes stale workspace by default. |
| Check snippet feasibility | `lifeblood_compile_check code:"..."` | Good before adding API calls or experimenting with syntax. |
| Get project/module/file diagnostics | `lifeblood_diagnose` | Use file scope first to avoid drowning in existing project warnings. |
| Find exact source references | `lifeblood_find_references` | Compiler-backed write-side operation; honors retained profile limitations. |
| Find declarations | `lifeblood_find_definition` | Use before editing unfamiliar APIs. |
| Find implementers/overrides | `lifeblood_find_implementations` | Use for interface and virtual-method changes. |
| Preview rename edits | `lifeblood_rename` | Returns edits only; caller applies them deliberately. |
| Format C# | `lifeblood_format` | Roslyn formatting for generated or replaced code. |
| Execute C# against workspace state | `lifeblood_execute` | Use for semantic inspection; do not rely on runtime instantiation of workspace types unless supported. |

## Specialized Analysis

| Need | Tool | Notes |
|---|---|---|
| Check enum values produced/consumed | `lifeblood_enum_coverage` | Finds unproduced or unreferenced state-machine-like values. |
| Count a type's declared members (for a ratchet) | `lifeblood_member_count` | `reflectionDeclared` = bit-exact System.Reflection DeclaredOnly; `sourceSymbols` = graph child count. Offline alternative to a live reflection run. |
| Inspect static dispatch/config tables | `lifeblood_static_tables` | Operation-tree extraction; use `summarize:true` for large tables. |
| Check object-initializer wiring | `lifeblood_assignment_coverage` | Useful for bindings/delegate slots and construction completeness. |
| Triage unused code candidates | `lifeblood_dead_code` | Advisory; verify before deleting. |
| Field read but never written / delegate slot never wired? | `lifeblood_wire_audit` | Dead-WIRE complement of dead_code: referenced but structurally unplugged. Advisory. |
| Boolean feature flag gated but never flipped (dormant)? | `lifeblood_feature_switch_audit` | Verdict `AlwaysDefaultInGraph` / `TestOnlyActivation` / `RuntimeMutable`. Advisory. |
| Do call sites actually pass the new/optional argument? | `lifeblood_callsite_arguments` | Per-site argument facts + supplied/omitted histogram; the API-adoption gap. |
| Measure interface/class liveness | `lifeblood_port_health` | Good for ports, facades, and suspiciously wide contracts. |
| Quantify facade/dispatcher authority | `lifeblood_authority_report` | Use for types that aggregate many subordinates or interfaces. |
| Search by intent or xmldoc | `lifeblood_search` | Better than grep when names are unknown but docs mention behavior. |

### The wiring family

Three tools answer "is this code actually plugged in?", in escalating subtlety — reach for the right one by what the symbol's reference state is:

- `lifeblood_dead_code` — the symbol is **never referenced**. Classic unused code.
- `lifeblood_wire_audit` — the symbol **is referenced but structurally unplugged**: a field read with zero writes, a delegate/binding slot nothing assigns.
- `lifeblood_feature_switch_audit` — the boolean **is referenced, gates a live branch, but is pinned to its default** because no reachable code flips it (e.g. a public setter with zero callers). Looks shipped; never activates.

All three are advisory: reflection, Unity serialized YAML, and runtime/config assignment are invisible to static analysis, so none is deletion authority on its own. Confirm with references, source inspection, and tests.

## Multi-Profile And Unity

- For Unity Editor/Player differences, analyze with `defineProfiles:["Editor","Player"]` when possible.
- Use graph-side `dependants` and `dependencies` with `profileFilter` for union-profile dependency questions.
- Treat write-side Roslyn tools as retained-profile scoped. Check `analyzedUnderProfile` and `limitations`.
- New Unity files need Unity import/project descriptor regeneration before Lifeblood can include them.

## Result Interpretation

- `truthTier:"Semantic"` and `confidence:"Proven"` are the strongest evidence.
- `Derived` graph rollups are usually strong but one step removed from raw compiler facts.
- `Heuristic` / `Advisory` results are triage aids, not deletion or rewrite authority.
- Any non-empty `limitations[]`, high `stalenessSeconds`, or non-zero `filesChangedSinceAnalyze` should affect confidence and usually calls for re-analysis or direct source verification.

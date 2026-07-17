---
name: lifeblood-mcp
description: "Use when an agent should work with Lifeblood's MCP semantic-code tools: analyzing C# or Unity workspaces, querying symbols/dependencies/blast radius/file impact/test impact, checking architectural invariants, validating edits with compile_check/diagnose, or choosing the right Lifeblood tool instead of grep-style guessing."
---

# Lifeblood MCP

## Core Posture

Use Lifeblood when the question is semantic: "what calls this?", "what breaks if I change it?", "is this test-only?", "which invariant governs this?", "does the edited file still compile?", or "which tests should I run?"

Prefer Lifeblood answers over text search when correctness depends on compiler knowledge, symbol resolution, cross-module references, Unity reachability, partial types, or architectural boundaries. Still read source before editing; Lifeblood narrows the search and validates assumptions, it does not replace engineering judgment.

## Session Start

1. Call `lifeblood_capabilities` if available. Use it to detect live server version, tool count, feature flags, and whether a graph is already loaded.
2. Load the workspace with `lifeblood_analyze`.
   - For a normal first load, pass `projectPath`.
   - For a prebuilt JSON graph, pass `graphPath`.
   - For large read-only triage, consider `readOnly:true`; remember write-side Roslyn tools will then be unavailable until a retained full analyze is run.
   - For Unity cross-define questions, prefer `defineProfiles:["Editor","Player","Standalone"]` when the server supports it; include `Standalone` for desktop guards such as `UNITY_STANDALONE && !UNITY_EDITOR`.
   - For vendored/sample-heavy workspaces, use `excludePaths` (for example `["Packages/*","*/Samples*/*","*/Examples*/*"]`) when you want those sources excluded before compilation.
3. After source edits, use `lifeblood_analyze` with `incremental:true`. If your editor or watcher supplies exact changed paths, pass them as `authoritativeChangedFiles` to narrow source scanning. Content hashes decide whether touched files actually re-extract; mtime-only touches can return `mode:"incremental-noop"` with `mtimeTouchedSourceFiles > 0` and `contentChangedSourceFiles == 0`. If the response is `mode:"rejected"` with `canRetryFull:true`, retry with the provided `suggestedRetry` or with `allowFullFallback:true` if widening is acceptable. `fallbackReason:"compilationStateUnavailable"` means a previous `readOnly:true` analysis left no retained Roslyn compilations; run a full retained analyze or allow full fallback before using write-side tools.
4. Treat read-side `envelope` metadata as part of the answer. Staleness, changed files, confidence, and limitations can change whether an answer is safe to act on.
5. When parallel investigation lanes must revisit an older publication, use `lifeblood_snapshots` to inspect or pin the bounded graph-only catalog and pass its `snapshotId` to graph reads. Historical selections do not provide live source or Roslyn services; omit `snapshotId` for those tools.

## Tool Routing

Read `references/tool-routing.md` when choosing among Lifeblood tools, building a workflow for a larger change, or explaining tool choice to another agent.

Common fast paths:

- Need a canonical symbol id: `lifeblood_resolve_short_name`, then `lifeblood_lookup`.
- Before refactoring a symbol: `lifeblood_blast_radius groupBy:"both"`, then `lifeblood_dependants` or `lifeblood_find_references` for source locations.
- Before changing a file: `lifeblood_file_impact`, then `lifeblood_test_impact` on the file.
- After editing C#: `lifeblood_compile_check filePath`, then `lifeblood_diagnose filePath` if the result or project state is unclear.
- Before deleting code, pick by reference state — the wiring family: `lifeblood_dead_code` (never referenced), `lifeblood_wire_audit` (referenced but unplugged — field read-without-write, never-assigned slot), `lifeblood_feature_switch_audit` (boolean gates a branch but no reachable code flips it off its default). All three are advisory only; verify with references, source inspection, and tests. In `dead_code`, fold `bucket:"Vendored"` and `bucket:"Scaffolding"` separately from ordinary Production findings; Scaffolding marks intentional conditional/const-string anchor shapes, not normal deletion work.
- For Unity/old-format asmdef compile-direction checks: `lifeblood_asmdef_check summarize:true` after a fresh analyze.
- For architecture rules: `lifeblood_invariant_check mode:"audit"` or `id:"INV-..."`, then read the cited invariant file.
- For a generated DSP/math probe: route through workflow 8 in
  `docs/PLAYBOOK_CSHARP.md`. Derive parameterized cases from the consumer's one
  range/contract authority, inspect that authority with `contract_audit`,
  `static_tables`, or source-backed lookup as appropriate, compile-check the
  resulting test file, then run it in the consumer's test framework. Lifeblood
  does not own audio playback or a second case database.
- For consumer-owned operation policy: `lifeblood_contract_audit`. Use manifest `callRoutes[]` to bind policies to exact roots and bounded direct/transitive membership; put policies that share a route and profile in one manifest so they reuse one route plan, one ephemeral secondary-profile compilation, and one operation-fact pass. Use `routeFacts[]` with `RequiredOnEveryRoute` for mandatory handoffs, `EquivalentAcrossRoutes` for consumer-selected parity dimensions, and `AllowedRoutesOnly` for exact-target bypass/ownership checks. These policies compare declared semantic evidence; they do not infer sibling paths, determinism, or thread/audio/job lanes. Read route, fact, and member truncation receipts before making a coverage claim. External costs select exactly one target mode: `targetSymbolIds[]` for named APIs, or `matchAnyTarget:true` for targetless/all-target kinds such as array/delegate creation, interpolation, throw, await, and lock. Shared state uses `stateAccesses[]` with exact `targetSymbolIds[]` or route-referenced `matchAnyMember:true`; inspect candidate/retained/accessed counts and all five risk buckets, and treat `Unknown` or any truncation as unresolved. Treat `evaluatedOccurrenceCount:0` as a coverage gap, not a clean pass, and read finding-free counts as occurrence-local only. Branch-arm evidence names where a nested occurrence appears; statement `if`/`else` bodies are control structure, while only value-producing conditional expressions expose `WhenTrue` / `WhenFalse` value inputs.
- For invariant evidence or retired source prose, keep the same `lifeblood_contract_audit` manifest. `sourceTextPolicies[]` accepts only caller-owned retired terms and returns advisory comment/XML matches with source, nearby symbol, invariant context, and `Delete` / `UpdateAuthority` / `Keep` guidance. `invariantEvidence[]` selects exact ids or prefixes and reports named `InvariantDeclaration`, `SourceReference`, `TestReference`, `ReachableTest`, `OperationContract`, or caller-owned receipt categories with concrete gaps; it never emits a global quality score. External receipt entries are `CallerDeclared`, not Lifeblood-executed proof. Read `sourceEvidenceScan`, `invariantEvidencePolicies[]` selector counts (including zero-match prefixes), category counts/status, state labels, and every truncation flag before claiming coverage. Put related text, route, operation, and invariant policies in one manifest so one retained-tree evidence pass and at most one operation pass cover the request with `additionalSemanticBaseCount:0`.
- For runtime profiling evidence, use `lifeblood_performance_evidence`. Start with `action:"import"` plus an absolute `workspaceRoot` to validate a generic JSON/CSV capture or structural Unity ProfilerRecorder receipt without first analyzing the repository; `compare` has the same no-graph path. When a publication is already loaded, omit the root or pass the exact matching root. Use `action:"correlate"` only on the current live publication; supply exact `markerAliases` when the capture owns a manifest, and treat `Ambiguous` / `Unmapped` as unresolved rather than guessing. Read the comparison verdict and every identity check before deltas: `RejectComparison` deliberately withholds numeric rankings, while `PartiallyComparable` exposes them with provenance/environment caveats. Do not treat correlated runtime cost as semantic causality, and do not create a second capture ledger or graph.

## Refactor Workflow

1. Identify the target using `resolve_short_name`, `resolve_member`, `symbol_at_position`, or source reading.
2. Inspect the target with `lookup`, `documentation`, and direct file reads.
3. Estimate risk with `blast_radius` for symbols or `file_impact` for files. Use grouping to separate Production, Test, Editor, Generated, and Vendored callers.
4. Find concrete edit sites with `dependants`, `dependencies`, `find_references`, or `partial_view`.
5. Check applicable invariants with `invariant_check`; read the relevant `docs/invariants/*.md` file before changing an area with pinned architecture rules.
6. Make the code edit using the repo's normal editing tools.
7. Validate with `compile_check filePath`, targeted tests from `test_impact`, and the repository's test command when risk warrants it.

## Unity Notes

Use the Unity bridge tools when working inside Unity MCP, but keep the same semantics:

- `lifeblood_analyze_project` is the Unity-side convenience wrapper for project analysis.
- New `.cs` files are only included after Unity imports them and regenerates project descriptors. If compile-check reports `NotInAnyCompilation` or `NotInModule`, refresh/regenerate Unity project files, then re-analyze.
- Prefer multi-profile analysis for `#if UNITY_EDITOR` / player-only paths when the task depends on runtime/editor differences.
- Use `lifeblood_asmdef_check` when a Unity module may compile only because an undeclared dependency leaked through a merged or stale view.
- Unity dead-code results account for known MonoBehaviour and Editor reflection entry points plus resolved UnityEvent persistent calls from YAML, but unresolved serialized targets, reflection, and message-based dispatch still require manual verification.

## Limits And Honesty

- Do not cite stale docs as live truth when `lifeblood_capabilities` or schemas disagree.
- Do not treat heuristic/advisory tools as deletion authority by themselves.
- Do not use write-side tools after `readOnly:true` analysis unless the server reports they are available; recover with a full retained analyze when needed.
- Do not assume `find_references`, `rename`, or other write-side Roslyn tools cover every define profile in a multi-profile snapshot. They operate on the retained profile and report limitations; use graph-side `dependants` / `dependencies` with `profileFilter` for union-graph questions.

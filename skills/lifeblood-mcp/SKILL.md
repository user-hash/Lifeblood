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
   - For large read-only triage, consider `readOnly:true`; remember write-side Roslyn tools will then be unavailable.
   - For Unity cross-define questions, prefer `defineProfiles:["Editor","Player"]` when the server supports it.
3. After source edits, use `lifeblood_analyze` with `incremental:true`. If the response is `mode:"rejected"` with `canRetryFull:true`, retry with the provided `suggestedRetry` or with `allowFullFallback:true` if widening is acceptable.
4. Treat read-side `envelope` metadata as part of the answer. Staleness, changed files, confidence, and limitations can change whether an answer is safe to act on.

## Tool Routing

Read `references/tool-routing.md` when choosing among Lifeblood tools, building a workflow for a larger change, or explaining tool choice to another agent.

Common fast paths:

- Need a canonical symbol id: `lifeblood_resolve_short_name`, then `lifeblood_lookup`.
- Before refactoring a symbol: `lifeblood_blast_radius groupBy:"both"`, then `lifeblood_dependants` or `lifeblood_find_references` for source locations.
- Before changing a file: `lifeblood_file_impact`, then `lifeblood_test_impact` on the file.
- After editing C#: `lifeblood_compile_check filePath`, then `lifeblood_diagnose filePath` if the result or project state is unclear.
- Before deleting code, pick by reference state — the wiring family: `lifeblood_dead_code` (never referenced), `lifeblood_wire_audit` (referenced but unplugged — field read-without-write, never-assigned slot), `lifeblood_feature_switch_audit` (boolean gates a branch but no reachable code flips it off its default). All three are advisory only; verify with references, source inspection, and tests.
- For architecture rules: `lifeblood_invariant_check mode:"audit"` or `id:"INV-..."`, then read the cited invariant file.

## Refactor Workflow

1. Identify the target using `resolve_short_name`, `resolve_member`, `symbol_at_position`, or source reading.
2. Inspect the target with `lookup`, `documentation`, and direct file reads.
3. Estimate risk with `blast_radius` for symbols or `file_impact` for files. Use grouping to separate Production, Test, Editor, and Generated callers.
4. Find concrete edit sites with `dependants`, `dependencies`, `find_references`, or `partial_view`.
5. Check applicable invariants with `invariant_check`; read the relevant `docs/invariants/*.md` file before changing an area with pinned architecture rules.
6. Make the code edit using the repo's normal editing tools.
7. Validate with `compile_check filePath`, targeted tests from `test_impact`, and the repository's test command when risk warrants it.

## Unity Notes

Use the Unity bridge tools when working inside Unity MCP, but keep the same semantics:

- `lifeblood_analyze_project` is the Unity-side convenience wrapper for project analysis.
- New `.cs` files are only included after Unity imports them and regenerates project descriptors. If compile-check reports `NotInAnyCompilation` or `NotInModule`, refresh/regenerate Unity project files, then re-analyze.
- Prefer multi-profile analysis for `#if UNITY_EDITOR` / player-only paths when the task depends on runtime/editor differences.
- Unity dead-code results account for known MonoBehaviour and Editor reflection entry points, but reflection and message-based dispatch still require manual verification.

## Limits And Honesty

- Do not cite stale docs as live truth when `lifeblood_capabilities` or schemas disagree.
- Do not treat heuristic/advisory tools as deletion authority by themselves.
- Do not use write-side tools after `readOnly:true` analysis unless the server reports they are available.
- Do not assume `find_references`, `rename`, or other write-side Roslyn tools cover every define profile in a multi-profile snapshot. They operate on the retained profile and report limitations; use graph-side `dependants` / `dependencies` with `profileFilter` for union-graph questions.

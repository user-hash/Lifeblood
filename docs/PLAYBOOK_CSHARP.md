# Lifeblood playbook: large C# / Roslyn / Unity workspaces

How to drive Lifeblood end-to-end on the workloads it was built for —
serious .NET / Unity codebases where AI agents and human reviewers need
compiler-grounded answers instead of grep guesses.

This is the operator-facing complement to `docs/STATUS.md` (architecture
truth) and `docs/TOOLS.md` (the tool reference). Read this when you have
a task and want to know which Lifeblood tool to reach for.

Every workflow below names:

- **What you start from** (the input — a file path, a symbol id, a stack
  trace, an error message).
- **The Lifeblood tool calls** in order, with the argument shapes.
- **What good output looks like**, including which envelope field tells
  you the answer is trustworthy.
- **What goes wrong**, including the limitation classes the envelope
  surfaces.

The shape of every workflow is the same — pick a starting point, route
through the right tool, read the envelope, act. The envelope (`INV-ENVELOPE-001`,
pinned by `INV-WIRE-CONTRACT-001`) is the single signal for "is this
answer trustworthy enough to ship?" — `confidence: Proven` answers can
be acted on; `Advisory` answers need a human review pass; `Speculative`
answers need a different tool.

## 1. Triage a breakage

You see an error in CI, on disk, or in a log. You need to know who else
touches the failing surface.

1. Open the log or build output and find the file + line of the failure.
2. Call `lifeblood_symbol_at_position` with the file path + 1-based line +
   1-based column. Read the returned `symbolId` — that is the canonical
   handle for every other tool.
3. Call `lifeblood_blast_radius` on the `symbolId` with `groupBy: "both"`
   to see who depends on the failing surface, grouped by production /
   test / editor / generated bucket AND by module. Set `maxResults: 50`
   to bound the response.
4. Read the envelope. `truthTier: Semantic` + `confidence: Proven` means
   the dependency graph is compiler-grounded; act on it directly. If
   `Limitations[]` carries a "stale" entry per `INV-ANALYZE-SKIPPED-PROMINENCE-001`,
   run `lifeblood_analyze incremental:true` first.
5. For each affected module, call `lifeblood_test_impact` with the same
   `symbolId` to get the `recommendedFilters[]` — paste-ready
   `FullyQualifiedName~<class>` strings for `dotnet test --filter`. Run
   that filter; if it passes, the breakage is local. If it fails,
   pivot to workflow §2.

## 2. Audit module boundaries

You suspect a module is leaking types or pulling in surfaces it should
not. Common driver: a refactoring proposal where you want to confirm
the boundary will survive.

1. Call `lifeblood_dependencies` on the module's owner type (or its
   directory's primary type) to see outbound coupling.
2. Cross-check with `lifeblood_dependants` on the same target to see
   inbound pressure. The two together describe the module's full
   coupling surface.
3. For Unity-shaped workspaces, `lifeblood_invariant_check` reads any
   `CLAUDE.md` / `AGENTS.md` / `docs/invariants/**.md` tree in the
   repo. Run `lifeblood_invariant_check` to surface every INV the
   repo declared; if the module owns an INV (`INV-XYZ-001`), the
   audit names the file location for a focused read.
4. `lifeblood_cycles summarize:true` reports the SCC list bucketed
   into `GeneratedOrStaticAnalysisArtifact`, `PartialClassCluster`,
   and `LikelyRealLoop` (per `INV-CYCLE-TAXONOMY-001`). Filter to
   `LikelyRealLoop` and triage; the other two buckets are usually
   structural noise.

## 3. Safe rename

You want to rename a symbol across the workspace. The mechanics matter:
a missed reference is a silent breakage.

1. Call `lifeblood_find_references` on the canonical id of the symbol
   you intend to rename. `includeDeclarations: true` adds every partial
   declaration to the result so you do not miss a partial-type
   declaration that lives in a different file.
2. Read the envelope. `truthTier: Semantic` means Roslyn bound every
   reference. If you see `confidence: Advisory` here, do not run a
   rename — investigate the limitations first (typically a Unity
   reflection roster the static analyzer cannot resolve).
3. Run `lifeblood_rename` with the old + new canonical ids. The tool
   returns the proposed text edits as a preview; review the diff in
   your editor before applying.
4. After applying, run `lifeblood_analyze incremental:true` and then
   `lifeblood_diagnose` on every module the rename touched. Zero
   error-severity diagnostics in the canonical parity ID set is the
   ship gate (per `INV-DIAGNOSTIC-PARITY-001`).

## 4. Inspect blast radius before a deletion

You want to delete a class, method, or field. The reachability question
must be answered statically before the deletion can ship.

1. Call `lifeblood_dependants` on the canonical id. Empty result means
   no source code references the symbol.
2. Call `lifeblood_blast_radius` with `summarize: true` to fold popular
   transitive consumers. Pay attention to the `directDependants` field:
   non-zero means at least one symbol still touches it.
3. Call `lifeblood_dead_code` filtered to the containing type and
   inspect the `bucket` + `declarationOnly` triage fields. `Production`
   bucket + `declarationOnly: false` is a real deletion candidate;
   `Test`, `Editor`, `Generated`, or `declarationOnly: true` are
   special-case classes the deletion checklist must address.
4. Cross-check with `lifeblood_find_references` to catch any reference
   the graph dropped (incoming-edge filtering at graph build time is
   documented in `INV-EXTRACT-SYNTHESIZED-CTOR-001` — the synthesized
   `.cctor` surfacing closes the dispatch-table-delegate class).

## 5. Validate a snippet before a commit

You are about to commit a non-trivial change. Lifeblood can compile-check
the file in-place against the module's actual reference set, including
`<LangVersion>` / `<Nullable>` / `<NoWarn>` / `<DefineConstants>` /
`InternalsVisibleTo`.

1. Call `lifeblood_compile_check` with `filePath: <relative-path>` and
   `code: <new-content>`. The tool resolves the owning module, parses
   the replacement with the module's own `CSharpParseOptions`, swaps
   the existing tree, and reports diagnostics that are NEW relative to
   the unchanged baseline.
2. Read the response. `success: true` + zero diagnostics is the ship
   gate.
3. If the response surfaces `CS1701` / `CS1702` / `CS0122` / similar
   parity-class diagnostics, the gap is on Lifeblood's side (not
   yours) — see `INV-DIAGNOSTIC-PARITY-001`. File a bug; in the
   meantime cross-check with `dotnet build` and proceed.

## 6. Recover from a merge conflict

A merge / rebase produced a `<<<<<<<` conflict. You want to know how
much surface area each side affects before resolving by hand.

1. Open the conflicted file. Note the symbol names mentioned in both
   sides of the conflict.
2. Call `lifeblood_resolve_short_name` (mode `exact` first, fall back
   to `contains` then `fuzzy`) for each bare name to get the canonical
   id.
3. Call `lifeblood_find_references` on each canonical id with
   `includeDeclarations: true`. The result names every consumer of
   each side's surface — choose the side whose surface has the higher-
   value consumers (production over test, public API over private).
4. After resolving, run `lifeblood_compile_check` (workflow §5) on the
   merged file and `lifeblood_analyze incremental:true` to refresh the
   graph.

## 7. Find a dead method (correctly)

`lifeblood_dead_code` ships in advisory mode (`confidence: Advisory`)
because static analysis cannot see every reflection / Unity-dispatched
caller. The triage workflow:

1. Call `lifeblood_dead_code` with `ExcludePublic: false, ExcludeTests:
   false` to see the full advisory list.
2. Filter to `bucket: Production` + `declarationOnly: false` —
   interface members and abstract methods are NOT routine deletion
   candidates.
3. Cross-check each finding with `lifeblood_find_references`. If
   Roslyn surfaces a reference the graph missed, it's a true false
   positive — file it against the relevant INV class in `LB-INBOX-*`.
4. For Unity workspaces, the Unity reachability roster
   (`INV-UNITY-001`, extended by `LB-FP-003`) already filters
   `[SettingsProvider]`, `[Shortcut]`, `[OnOpenAsset]`, `[BurstCompile]`,
   `[MonoPInvokeCallback]`, full NUnit fixture lifecycle, and Unity
   magic methods. Anything left in the findings list is a real
   candidate.

## 8. Build a generated DSP or math probe

You need measured output evidence for a numerical or DSP change, but the test
matrix must not become a second copy of the product's ranges, defaults, or
sentinels. Lifeblood helps verify the source contract and the resulting test
file; the user's test framework owns execution.

1. Name one case authority in the repository: a versioned contract manifest,
   range/provider table, enum/catalog, or another production API that exposes
   the supported domain. Do not copy that authority into a Lifeblood-specific
   manifest merely to generate tests.
2. Inspect the authority with the smallest semantic tool that fits. Use
   `lifeblood_contract_audit` when a consumer manifest already declares the
   value/shape contract, `lifeblood_static_tables` for operation-backed tables,
   or `lifeblood_lookup` plus source reading for a provider/catalog API.
   Treat missing or truncated evidence as a gap, not an empty domain.
3. Make the user's fixture derive its parameterized cases from that same
   authority. The representative set normally includes the declared default,
   neutral value, musical minimum/maximum, midpoint, active zero or storage
   sentinel, and one or two high-resolution neighborhoods. Arbitrary global
   extremes belong only when the source contract declares them.
4. Keep setup and scheduling explicit in the fixture: run length, sample rate
   or iteration count, input schedule, controls changed, static and swept
   scenarios, gate/tail windows, and repeat seed/order. These are consumer test
   semantics; Lifeblood must not guess them from product names.
5. Select output invariants that match the contract. Common independent checks
   are finite output (including no NaN/Inf/denormals), bounded peak or adjacent
   delta, RMS/envelope continuity, silence-window noise floor, declared-tail
   silence, static-versus-swept response, and repeated-run determinism. A
   finding-free static contract audit does not replace these runtime checks.
6. Once the file belongs to the user's generated project descriptors, call
   `lifeblood_compile_check filePath:"<test-file>"`; use bounded `filePaths[]`
   when the scaffold and its authority-facing fixture changed together. A
   successful compile check proves project-context structure only.
7. Run the generated cases in the repository's normal test runner and record
   the source revision, case authority, selected cases, and test receipt. Only
   that runner can prove the measured output invariants. If a new test file is
   not yet in a Unity compilation, import/regenerate descriptors and re-analyze
   before repeating the compile check.

This workflow deliberately adds no audio runner, probe generator command,
retained fact cache, or second range database to Lifeblood. Generation stays in
the consumer test project where its runtime, framework, and product contracts
are available.

## Envelope cheat sheet

| Field | When to act |
|-------|-------------|
| `truthTier: Semantic` + `confidence: Proven` | Ship it. |
| `truthTier: Semantic` + `confidence: Advisory` | Human review pass required. |
| `truthTier: Derived` | The result was inferred from a different surface — name the inference in the PR description. |
| `truthTier: Heuristic` / `Inferred` | Sanity-check with a second tool before acting. |
| `Limitations[]` contains "stale" | Re-run `lifeblood_analyze`. |
| `Limitations[]` contains "tracked file(s) changed" | Re-run `lifeblood_analyze`. |
| `Limitations[]` contains "Unregistered tool" | File a Lifeblood bug — the tool registry has a gap. |

## What this playbook does NOT cover

- Lifeblood's full tool reference is in `docs/TOOLS.md` — read that for
  every flag, every cell-kind, every option.
- The architectural invariants live in `docs/invariants/**.md`; every
  tool's behavior is pinned by at least one INV.
- The deprecation policy for wire shapes is `docs/SCHEMA_DEPRECATION_POLICY.md`.
- The full release history is `CHANGELOG.md`.

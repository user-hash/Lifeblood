# Improvement Inbox

State of Lifeblood going forward. Four things:

1. A current-state verdict compiled from the external reviews we run against the repo.
2. A pointer to shipped work (tracked via git tags and `CHANGELOG.md`).
3. Forward-looking entries: title, observation, suggested fix shape, why it matters.
4. Shipped entries marked **SHIPPED** with closing-commit / INV-ID refs preserved verbatim. We keep them so a future regression has a trace path back to the fix that was supposed to prevent it — the inbox doubles as a regression catalogue, not a release blog. Anything speculative that nobody is about to work on is still deleted.

---

## Status snapshot (post-v0.7.3 doc-refresh + comment-cleanup wave, 2026-05-14 — preparing v0.7.4)

The post-v0.7.3 wave landed in three phases. Phase one shipped five new tool-facing capabilities (`lifeblood_test_impact`, `lifeblood_enum_coverage`, cycles taxonomy, dead_code triage fields, preprocessor scope on `diagnose` / `compile_check` envelopes), four follow-on bug fixes (BUG-1..4 from the 2026-05-14 fake-stuff audit), and the five-part FOLLOWUP series threading `LangVersion` / `Nullable` / `NoWarn` / `DefineConstants` into Roslyn options + extracting `PathBucketClassifier` as a Domain-layer SSoT. Phase two was a pre-release doc + comment refresh: doc tree updated everywhere (counts 26 → 28 tools, 776 → 893 tests, 80 → 87 invariants / 43 → 50 categories), one missing INV bullet authored (`INV-PATHBUCKET-SHARED-001` — was referenced inline + across source + tests but had no own body), six-lego eternal-comment sweep across src/ + tests/ stripping Phase-X / Stage-N / dated "Added 2026-04-11" / finding-ID journey prose (~33 files, net ~70 LOC trimmed, every INV-ID + architectural rationale preserved), three Phase-named test files renamed (AnalysisToolsPhase6Tests → AnalysisToolsTests, FindReferencesPhase4Tests → FindReferencesTests, ResolverPhase3Tests → ResolverCapabilityTests). Phase three closed a release blocker the pre-tag audit caught: `lifeblood_compile_check` threw `ArgumentException: Inconsistent language versions (Parameter 'syntaxTrees')` on every DAWG file-mode + snippet-mode call because the replacement / wrapper trees were parsed without the owning module's `CSharpParseOptions` (regression from FOLLOWUP-001 in the same wave). Fix threads `GetModuleParseOptions` through `RoslynCompilationHost.CompileCheckFile`, `RoslynCompilationHost.CompileCheckSnippet`, and `SnippetWrapper.Prepare`; pinned by 3-fact `CompileCheckParseOptionsParityTests` against a CSharp11-LangVersion compilation. **896 passed + 1 skipped / 897 total green at the end of phase three.** The skip is a regression pin for `LB-INBOX-010` (target-typed `new(MethodGroup)` dead_code FP) — authored on this wave; ship the extractor fix and drop the Skip to convert into a ratchet. Inbox SHIPPED markers added on LB-INBOX-001 + LB-INBOX-002 + LB-INBOX-008 + LB-INBOX-009 with closing-commit + INV-ID refs preserved for regression-trace.

## Status snapshot (post field-report 2026-05-11 polish wave, 2026-05-12 — v0.7.3 SHIPPED)

The post-v0.7.2 polish wave landed eight commits closing one high-severity silent-data-loss bug + three structured-wire-shape asks from a real-world Unity workspace field report (2026-05-11) + an eternal-prose cleanup across src + tests + shipped docs. **Tests 751 → 776 (+25). MCP tools 25 → 26 (read-side 15 → 16). Authored invariants +4: `INV-INCREMENTAL-XREF-001`, `INV-EDGE-CALLSITE-001`, `INV-RESOLVE-MEMBER-001`, `INV-BLAST-RADIUS-GROUP-001`.** Closes `LB-BUG-020` (incremental analyze drops cross-module edges silently — false-positive dead-code class) plus the field-report 2026-05-11 P1 set (CallSite provenance on every expression-derived edge, type-scoped member resolution, blast-radius bucket / per-module grouping). Live-MCP dogfood on a real Unity workspace confirms all three new wire shapes — CallSite carried by 60.8% of edges (133,523 / 219,548; the remainder are graph-derived edges with no authoring location by design); `resolve_member` returns typed `Unique` outcome; `blast_radius groupBy=both` returns `byBucket: {Production, Test}` + `byModule: {<asmdef>}` populated with `previewPerGroup`-capped entries. Eternal-prose cleanup: 36 files / +141 −130 lines / 0 behavior change — Lifeblood now reads as a generic Roslyn semantic tool, no longer coupled to any one consumer project's symbol names or paths. **No tag yet — v0.7.3 is a candidate awaiting 3× verification per legacy-project policy.**

## Status snapshot (post G1+G2+G4+R2-3 wave, 2026-04-28)

The dogfood plan landed in six phases on top of v0.6.5 (Tests 569 → 632 / Invariants 63 → 70 / Ports 22 → 26 / MCP tools 22 → 25). A follow-up polish session shipped six dogfood findings on top of v0.6.7, taking tests 632 → 661 (+29). The G1+G2+G4+R2-3 wave shipped a Unity-shaped audit session's findings as a single coherent landing: tests 664 → 751 (+87), authored invariants 65 → 71 (+6: `INV-EXTRACT-ENUMMEMBER-001`, `INV-RESOLVER-007`, `INV-ANALYZE-FALLBACK-001`, `INV-SEARCH-MATCHKIND-001`, `INV-JSON-IMPORT-BOM-001`, `INV-MCP-STDIO-UTF8-001`), parser-reported invariants 66 → 76 (+10 — the parser-multi-segment-id fix unblocked four pre-existing invariants that had been silently missing from the audit). Dogfood re-verification: edge count grew +18% (180,818 → 214,097) because enum-member references the dangling-edge filter was silently dropping `R2-3` now resolve. Invariants restructured into `docs/invariants/` tree (slim CLAUDE.md as coordinator); the dynamic tree-walker now picks them up alongside `<root>/CLAUDE.md` + `<root>/AGENTS.md`. Lifeblood self at that wave: 76 typed invariants across 39 categories.

Shipped against the roadmap below: `LB-INBOX-001` (truth envelope, `INV-ENVELOPE-001`), `LB-INBOX-002` (`INV-DEADCODE-001` close-out + Unity reachability port `INV-UNITY-001` + Editor reflection roster `LB-FP-003`). Plus six post-v0.6.7 fixes: `LB-FR-021` (cycles pagination), `LB-BUG-017` + `LB-BUG-018` + `LB-FR-023` (invariant parser shapes C/D/E + dynamic source discovery), `LB-BUG-019` (compile_check file-mode owning-module resolution + tree replacement), `LB-FR-022` (context smart-dynamic shaping), `LB-FP-003` (dead_code Unity Editor reflection roster + type-via-child propagation). Plus `INV-INVARIANT-001` v2 — invariants moved out of CLAUDE.md to `docs/invariants/` tree, validated by self-tests aggregating across CLAUDE.md + every tree file. Plus the G1+G2+G4+R2-3 wave: enum-member extraction (`INV-EXTRACT-ENUMMEMBER-001`) + resolver type-aware Rule 4 (`INV-RESOLVER-007`) close `R2-3` both-sided; caller-owned scope policy on incremental analyze (`INV-ANALYZE-FALLBACK-001`) replaces silent widening with structured Rejected / FullFallback shapes carrying `mode` / `requestedMode` / `fallbackReason` / `canRetryFull` / `suggestedRetry` (`G1`); search results structurally typed by source bucket (`INV-SEARCH-MATCHKIND-001`, `G2`); authority_report description broadened beyond ABG-only framing (`G4`). Partially landed: `LB-INBOX-004` (large-workspace wedge — authority report + forwarder classifier shipped; `cycles` + `context` + `compile_check` + `dead_code` polish shipped). Open: `LB-INBOX-003` (contract freeze), `LB-INBOX-005` (public proof), `LB-INBOX-006` (consolidated smoke script), `LB-INBOX-008` (per-diagnostic preprocessor scope), `LB-INBOX-009` (enum-member ref queries — partially addressed by `INV-EXTRACT-ENUMMEMBER-001` since enum members now resolve, but kind-tagged ref filtering still pending).

---

## Current state (review snapshot from v0.6.3, 2026-04-11)

Consolidated from three independent external reviews on the day of the v0.6.3 release. The reviewers ran the audit against the live commit-pinned repo state, verified the architecture against the actual project-reference graph and ratchet tests, and in one case performed a real MCP round-trip against the built server to confirm end-to-end operability.

### Combined ratings

| Dimension                         | Average | Range       |
|-----------------------------------|---------|-------------|
| Idea / thesis                     | **9.2** | 9.0 to 9.5  |
| Architecture                      | **9.0** | 9.0 to 9.0  |
| Execution                         | **8.3** | 8.1 to 8.5  |
| Practical usefulness              | **8.7** | 8.5 to 8.8  |
| Connectability / operability      | **8.7** | 8.5 to 8.7  |
| Trustworthiness / credibility     | **8.3** | 8.3 to 8.5  |
| External proof / market proof     | **5.0** | 5.0 only    |
| **Overall release quality**       | **8.6** | 8.5 to 8.7  |

### Green flags the reviewers all independently landed on

- The dependency shape is real, not marketing prose. `Lifeblood.Domain` is a genuine zero-dependency leaf. Application only references Domain. Adapters only reference Application. Connectors only reference Application. Composition roots are constrained by an allowlist test.
- Architecture is ratcheted, not described. `ArchitectureInvariantTests` enforces dependency direction on every build. `DocsTests` enforces the port and tool counts in `docs/STATUS.md`. Architecture invariants in `CLAUDE.md` are runtime-queryable via `lifeblood_invariant_check`, which is itself self-auditing.
- Single-source-of-truth discipline is the dominant engineering instinct at every drift-prone site: `CanonicalSymbolFormat`, `CsprojPaths`, `McpProtocolSpec`, and `CLAUDE.md` via the invariant-check tool.
- New features arrive through ports, not random direct wiring. `ISymbolResolver`, `ISemanticSearchProvider`, `IDeadCodeAnalyzer`, `IPartialViewBuilder`, and `IInvariantProvider` were all added in v0.6.3 as proper port interfaces in Application with connector-side implementations.
- The project is comfortable surfacing limitations honestly. `lifeblood_dead_code` is marked experimental in every surface (tool description prefix, response envelope with `status: "experimental"` and `warning` field, CLAUDE.md invariant `INV-DEADCODE-001`, README, CHANGELOG, TOOLS.md, STATUS.md, architecture.html) rather than oversold.
- End-to-end operability verified: a reviewer launched the built `Lifeblood.Server.Mcp.dll`, completed the MCP `initialize` handshake, called `lifeblood_analyze` on the repo itself, and got a valid response with 1863 symbols, 5777 edges, 11 modules, 0 violations, and a structured `usage` block.

### Yellow flags worth tracking

- External adoption proof is still early. Internal coherence is strong; the outside-world verdict is ahead of the repo, not behind it. Phase 5 of the roadmap below exists to address this.
- Platform surface is growing quickly: 22 tools, 22 ports, multiple adapters, invariant introspection, Unity sidecar. Informal consistency will stop being enough well before v0.7. Phase 3 addresses this.
- Maturity split across adapters is real and honest. C# / Roslyn is Proven; TypeScript is High; Python is Structural. Practical value is concentrated in the C# wedge today. Phase 4 doubles down on that wedge rather than trying to level the other adapters prematurely.
- Documentation volume is high enough that drift risk is real even with ratchets. Two of the three reviewers called out "self-conscious repo" stylistic tells (heavy invariant naming, dense explanatory prose, polished narrative around every seam). Not disqualifying, but a warning that the discipline now needs to keep the same quality bar as the code.
- Workspace noise: `bin/` and `obj/` artifacts appear in the visible repo tree at review time. These are gitignored but the local state is visible to tool-assisted reviewers. Not a repo-truth problem; a cosmetic UX problem during review.
- One reviewer noted full-test verification could not be independently re-run during their review pass because the Release test suite is heavy and the session was interrupted. Internal verification is green (539/539 passing, confirmed by the CI workflow runs on `1017a3b` and `2511321` plus the publish workflow on `v0.6.3`).

### Red flags

None. No reviewer flagged anything that suggested the repo is fundamentally incoherent, hallucinated, or misrepresenting its architecture.

---

## Post-v0.6.3 roadmap: five-phase tightening plan

The strategic question for v0.6.4 and beyond is not "what is broken" but "what do we tighten next". The reviewers converged on a specific answer: amplify the existing strengths rather than broaden the thesis.

The strict ordering is:

**truth envelope → derived correctness → contract freeze → dominant wedge → public proof**

Doing these out of order creates waste. Phase 3 (contract freeze) before Phase 1 (truth envelope) would freeze an incomplete response shape. Phase 5 (public proof) before Phase 2 (derived correctness) would lock external expectations to work that still has a known-false-positive tail. Phase 4 (dominant wedge) before Phase 3 (contract freeze) would create churn for early integrators.

None of this is release-blocking. v0.6.3 is live on NuGet and GitHub Releases. This is the direction for v0.6.4 through v0.7.

The five phases are tracked below as `LB-INBOX-001` through `LB-INBOX-005`. A small execution caveat from review 3 is tracked as `LB-INBOX-006`.

---

## LB-INBOX-001. Phase 1. Uniform truth envelope across every read-side tool — **SHIPPED v0.6.5 (INV-ENVELOPE-001)**

**Resolution.** Truth envelope landed as `INV-ENVELOPE-001`: every read-side response carries `truthTier` (`Semantic` / `Derived` / `Heuristic` / `Inferred`), `confidence` (`Proven` / `Advisory` / `Speculative`), `evidenceSource`, `stalenessSeconds`, `filesChangedSinceAnalyze`, and per-tool `limitations[]`. Classification table lives on `ToolDefinition.EnvelopeClassification` in the registry, projected into `LifebloodResponseDecorator` at composition time. Adding a new read-side tool without a classification fails the registry ratchet test. Original entry preserved below for regression-trace.

**Observed.** `lifeblood_dead_code` ships with a `status: "experimental"` marker and a `warning` field that describes its known false-positive classes in-band. Every other tool returns results without a comparable metadata shape. A caller receiving a `find_references` hit has no way to tell from the payload alone whether that hit is compiler-resolved, parser-structural, or graph-derived. The existing separation of syntax / semantic / derived truth in `docs/ARCHITECTURE.md` is not projected to the wire.

**Suggested fix shape.** Define one typed response-metadata contract shared across MCP tools:

- `truthTier`: one of `syntax` / `semantic` / `derived`
- `confidence`: `proven` / `high` / `structural` / `advisory`
- `evidenceSource`: where the result came from (`Roslyn`, `GraphBuilder`, `InferredByGraphWalk`, etc.)
- `staleness`: optional timestamp or commit indicator when the result depends on a cached graph
- `limitations`: optional free-form caveat when the tool knows it is operating outside its confident zone

Every read-side tool declares its default tier. Advisory tools (today only `lifeblood_dead_code`, tomorrow possibly others) emit limitations in-band, not only in docs. Add response-shape golden tests across all 26 tools (15 read + 10 write) so no new tool can ship without the envelope.

**Why it matters.** It amplifies the repo's strongest architectural idea (the syntax / semantic / derived distinction) without changing any engine underneath. Shortest path from "internally disciplined" to "externally trustworthy". Work compounds with Phase 3: once every response carries the envelope, versioning the envelope once covers every tool.

---

## LB-INBOX-002. Phase 2. Close out `INV-DEADCODE-001` and the shared extraction gap — **SHIPPED v0.6.5 + v0.6.7 (INV-UNITY-001, LB-FP-003)**

**Resolution.** Three extractor classes closed in v0.6.5 (constructor `Calls` edge, field-initializer containing method, property-accessor body context). Unity reachability port (`INV-UNITY-001`) added in v0.6.7 closes the MonoBehaviour magic-method false-positive class. `LB-FP-003` (v0.7.0) extends Unity reflection to `[SettingsProvider]`, `[Shortcut]`, `[OnOpenAsset]`, `[BurstCompile]`, `[MonoPInvokeCallback]`, full NUnit fixture lifecycle, plus type-via-child propagation. Post-v0.7.3 the `INV-DEADCODE-TRIAGE-001` triage fields (`directDependants` / `bucket` / `declarationOnly`) shipped on top, with `bucketBreakdown` summary. Live dogfood on an 87-module Unity workspace: 1095 → 729 findings (-33%), MonoBehaviour-magic FPs 378 → 13 (-97%), type-level findings 6 → 4 post-`LB-FP-003`. Original entry preserved below for regression-trace.

**Observed.** After the v0.6.4 extraction pass (interface dispatch, member-access granularity, null-conditional property, lambda context) and the implicit-global-usings compilation fix, the `lifeblood_dead_code` self-analysis tail stabilized at 10 findings. A follow-up pass closed three more structural gaps that the original "by design / known gap" framing had enshrined:

1. **Ctor `Calls` edge.** `ObjectCreationExpressionSyntax` now emits both a type-level `References` edge AND a method-level `Calls` edge to the `.ctor`. `find_references` on a constructor returns its construction sites. The dead-code analyzer sees invoked ctors as reachable.
2. **Field-initializer containing method.** `FindContainingMethodOrLocal` resolves a reference inside `static T _x = Bar()` or `T _x = Bar()` to the type's synthesized `.cctor` / first `.ctor`. Closes the `new Lazy<>(Load)` FP class.
3. **Property accessor context.** `FindContainingMethodOrLocal` now returns the accessor `IMethodSymbol` instead of bailing out. `GetMethodId` routes accessors through `AssociatedSymbol` so the emitted edge source is the property id - the graph node the dead-code analyzer actually walks. Covers both bodied `get { return _field; }` and expression-bodied `=> _field` properties/indexers.

**Remaining expected after re-scan.** Runtime entry points (`Program.Main` × 6) and any genuine unused surface. Everything else listed in prior inbox tables (ctor "by design", accessor "known gap", field-initializer "no containing method") is now closed.

**Suggested follow-on.**
- Rescan Lifeblood self-analysis + confirm dead-code tail shrinks from 10 toward the entry-point floor.
- Update `INV-DEADCODE-001` wording in `CLAUDE.md` (remove obsolete "static field initializer method-groups" + "constructor by design" bullets from the remaining-known-FPs list).
- Graduate `lifeblood_dead_code` out of experimental once the rescan is stable for at least one minor release AND the Phase-1 truth envelope is defined (so the graduated tool ships with the right `confidence` tier).

**Why it matters.** Every call-graph tool (`find_references`, `dependants`, `blast_radius`, `file_impact`, `dead_code`) inherits the new edges automatically. `find_references` on a ctor now works - previously silently returned zero. Property-held constants used from their own getter are no longer flagged. Method-group refs from static field initializers are attributed correctly.

---

## LB-INBOX-003. Phase 3. Contract freeze before the platform surface grows further

**Observed.** Lifeblood's wire surface now includes 25 MCP tools, 26 ports, the semantic graph JSON schema, and a growing set of architectural invariants. Several single-source-of-truth sites already exist: `CanonicalSymbolFormat`, `CsprojPaths`, `McpProtocolSpec`, `CLAUDE.md` + `docs/invariants/` tree. But there is no formal versioning story for the tool schemas or the graph schema. A future minor that accidentally renames a response field would break every external integrator silently.

**Suggested fix shape.**

1. Publish versioned tool input and output schemas under `schemas/tools/<version>/*.json`. Start with a v1 snapshot of the current 25 tool shapes.
2. Add compatibility tests that replay a recorded v1 client session against a new server build and assert no field is missing, renamed, or changed in type.
3. Version the graph JSON schema more aggressively. Today `schemas/graph.schema.json` is unversioned. Switch to `schemas/graph/v1.schema.json` and add evolution rules: what may be appended, what may not be renamed, what constitutes a major-version break.
4. Introduce a deprecation policy for tools, fields, and invariants. Tools that are retired enter a deprecation window with a `deprecated: true` and `replacedBy: "..."` marker in `tools/list` for at least one minor-version release before removal.
5. Extend `lifeblood_invariant_check` with compatibility assertions: "these invariants were added in v0.6.3 and are load-bearing for v0.6.4 clients".

**Why it matters.** External integrators cannot build confidently on a surface that rewires without warning. The repo has enough surface now that informal consistency is no longer enough. Freezing contracts before v0.7 will be much cheaper than doing it after more adapters and clients appear. Sequencing: Phase 1 lands first so the truth envelope is part of the v1 schema, not an add-on.

---

## LB-INBOX-004. Phase 4. Double down on the C# / Roslyn / Unity wedge

**Observed.** The `README.md` maturity table is refreshingly honest: C# / Roslyn is "Proven", TypeScript is "High", Python is "Structural", generic JSON import varies. The strongest use case today is serious C# / Unity codebases with large module counts where AI needs compiler-grounded answers, and the repo has production verification on a 75-module, 400k+ LOC Unity workspace. But the external value story is not yet legible to someone who has not read `docs/STATUS.md` end to end.

**Suggested fix shape.**

1. Write a "large C# workspace playbook" in `docs/PLAYBOOK_CSHARP.md` with concrete workflows: triage a breakage, audit module boundaries, safe rename, inspect blast radius, validate a snippet, recover from a merge conflict, find a dead method. Every workflow names the specific tools and the exact argument shapes.
2. Publish benchmarked demos on real-sized repos (anonymized) with before/after value for an AI agent. Show the wall clock, the round-trip count, and the specific wrong answer a naive agent would have given without Lifeblood.
3. Harden incremental analyze, stale refresh, and workspace lifecycle ergonomics. Make the "edit a file, then run compile_check" loop sub-second on warm state for the common cases.
4. Treat Unity / C# dogfood as the premium path. Everything else is secondary until it catches up. Strategic posture, not deprecation of other adapters.

**Why it matters.** "Universal connector" is a good long-term architecture. "Best tool for compiler-grounded C# AI workflows" is the much stronger near-term product position. Phase 4 makes Lifeblood undeniable in the lane where it is already proven. Sequencing: depends on Phase 1 (so the playbook can cite `truthTier` / `confidence` values) and Phase 2 (so the benchmarked demos do not stumble into `INV-DEADCODE-001` false positives mid-demo).

---

## LB-INBOX-005. Phase 5. Turn internal credibility into public credibility

**Observed.** Internally, Lifeblood is disciplined: architecture ratchets, runtime-queryable invariants, a real changelog, a status page that matches the code, CI spanning build / TS adapter / Python adapter / dogfood, and a published NuGet release with a real GitHub Release page. Externally, the repo is still early. A stranger landing on the GitHub page cannot immediately answer "what is this for", "why should I trust it", or "where should I not trust it yet".

**Suggested fix shape.**

1. Publish 3 to 5 deep case studies with exact user tasks and exact Lifeblood wins. Target formats: a short README per case study in `docs/case-studies/` with the anonymized task, the tool calls, the responses, and the outcome.
2. Add comparison demos against generic MCP / code tools on the same repo tasks. Same task, three tools, three outputs.
3. Open a small public issue backlog and resolve it transparently. External proof is currently the weakest dimension in the consolidated review ratings (5.0 / 10).
4. Add a "trust dashboard" in docs: supported maturity by adapter (citing the README maturity table), known limitations (citing `INV-DEADCODE-001` and similar), benchmark results (citing Phase 4 demos), and compatibility guarantees (citing Phase 3 contract freeze).

**Why it matters.** Internal discipline is solved. The missing piece is portable proof. Phase 5 stops Lifeblood looking like a clever internal framework and makes it look like a credible external product. Sequencing: depends on everything else landing first. Do not publish a trust dashboard citing benchmark results that do not yet exist, or a compatibility guarantee for a contract that has not been frozen, or a case study that runs into known-advisory tool output.

---

## LB-INBOX-006. `smoke-mcp-analyze.ps1` default `ServerDll` path is brittle on Windows — **SHIPPED [Unreleased] (Wave W6)**

**Resolution.** Closed by replacing the hardcoded default with auto-discovery (`Resolve-ServerDll` helper at the top of `smoke-mcp-analyze.ps1`). The helper walks Debug first then Release under `$PSScriptRoot/src/Lifeblood.Server.Mcp/bin/<Config>/net8.0/`; if neither exists, the one-line diagnostic names every path tried AND the exact `dotnet build` command the operator can run to populate one. Passing an explicit `-ServerDll` short-circuits discovery but still validates the file exists before launching. The fix shape #1 (auto-discovery), #2 (existence assertion + named-path diagnostic), and #3 (operator-facing one-liner) all shipped in one atom; #3's standalone README was rolled into the inline diagnostic so a reviewer with no documentation context still sees the build command at first-run failure. Original entry preserved below for regression-trace.

**Observed.** Review 3 launched the v0.6.3 smoke script on Windows and the default `ServerDll` path construction failed at first run. The actual server worked correctly once the script was pointed at the right DLL location explicitly. Not a code defect in the MCP server; a path-construction bug in the smoke wrapper script. The script exists to make it trivial for an external reviewer to verify a live MCP round-trip against a freshly cloned repo, so a broken default path hits exactly the audience it was written for.

**Suggested fix shape.**

1. Change the default `ServerDll` parameter to auto-discover via `Join-Path $PSScriptRoot '..' 'src' 'Lifeblood.Server.Mcp' 'bin' 'Debug' 'net8.0' 'Lifeblood.Server.Mcp.dll'` or the Release equivalent, and fall through to Release if Debug is missing.
2. On startup, assert the file exists. If it does not, print a one-line diagnostic that names the exact path it tried and suggests either passing `-ServerDll <path>` or running `dotnet build -c Debug` first.
3. Add a short README note in the smoke folder explaining the default resolution order so reviewers do not have to read the script to understand what went wrong.

**Why it matters.** This is the exact script an external auditor uses to convince themselves Lifeblood actually works end-to-end. A path-construction fail on first run is cosmetically small but materially damaging for trust: it is the moment where a reviewer decides whether the repo is operational or theoretical. Fixing this is small work with outsized credibility impact.

---

## LB-INBOX-007. `lifeblood_diagnose` resolves `System.Math` against a colliding workspace `Math` namespace — **SHIPPED v0.7.4 (INV-MODULE-REFS-001)**

**Resolution.** Closed by reframing the root cause one layer up from the resolver. The Roslyn binding under Lifeblood was always spec-correct: C# §11.7.2 namespace-or-type lookup states a sibling-namespace child of the parent namespace shadows an outer-using BCL type, so once a workspace `Math` namespace is visible on the compile classpath, bare `Math.X` legitimately binds to the workspace namespace. The actual fault was Lifeblood's reference graph pulling sibling-namespace assemblies onto a Unity-asmdef module's classpath even when the asmdef declared no such reference — i.e. Lifeblood applied SDK-style transitive-reference semantics uniformly to every module, while Unity asmdef-generated csprojs follow MSBuild 2003-schema direct-only reference semantics. Fix shape #1 (resolver-side `using` directive walk) NOT shipped — would have only papered over the symptom. Fix shape #2 (`resolutionAmbiguous` envelope field) NOT shipped — eternal solution removes the ambiguity at compilation time so the envelope field has nothing to surface. Fix shape #3 (regression workspace fixture) shipped as `ReferenceClosureCompilationTests` + `ReferenceClosureModeDiscoveryTests`. Real fix lifted reference closure to a discovered module fact: `ReferenceClosureMode { Transitive, DirectOnly }` + `ModuleInfo.ReferenceClosure`, where `RoslynModuleDiscovery.ParseProject` reads the csproj root xmlns + `Sdk` attribute to decide which mode applies (old-format MSBuild 2003-schema csprojs → `DirectOnly`; SDK-style → `Transitive`, preserving pre-fix behavior for Lifeblood self + NuGet workspaces). `ModuleCompilationBuilder.ProcessInOrder` branches dep-ref resolution on the discovered mode. `INV-MODULE-REFS-001` added in `docs/invariants/csharp-adapter.md`; `INV-CANONICAL-001` tightened to scope-to-Transitive. Eternal: any future BCL-vs-workspace-namespace collision (`Color`, `Time`, `Random`, …) is covered by the same closure-mode branch with no per-name special casing. Closed in Lifeblood `e1acbe3` (`fix(refs): mirror csproj-tool closure semantics per module (LB-TRACK-001)`); shipped in v0.7.4, carried into v0.7.5. DAWG round-trip verified 2026-05-14 against the post-redeploy MCP: 0 CS0234 namespace-collision findings on files mixing `using System;` + bare `Math.X` calls. Original entry preserved below for regression-trace.

**Observed.** On a workspace whose root namespace contains a child `Math` namespace
(e.g. `Foo.Bar.Math` with sub-types like a custom math/utility module), running
`lifeblood_diagnose` against files that use plain `using System;` + `Math.PI` /
`Math.Min` / `Math.Max` / `Math.Sin` produces diagnostics of the shape:

```text
CS0234: The type or namespace name 'PI' does not exist in the namespace 'Foo.Bar.Math'
```

The same files compile clean under the live IDE / build (Unity console reports
zero errors), confirming the diagnostic is a Lifeblood standalone-resolution
artifact: `Math.PI` is being bound to the workspace-local `Math` namespace
instead of `System.Math`. Reproduces consistently across multiple files that
exercise the conflict (audio, runtime, snapshot helpers — any file mixing
`using System;` and a local-namespace `Math` reference).

**Suggested fix shape.**

1. Walk `using` directives at the head of each compilation unit when resolving
   bare-identifier `Math.X` references; prefer `System.Math` when `using System;`
   is in scope and the local `Math` namespace does not contain a member matching
   `X` (PI, Min, Max, Sin, etc).
2. If 1 is too intrusive for the resolver, surface the ambiguity in the diagnostic
   payload: `resolutionAmbiguous: ["System.Math", "Foo.Bar.Math"]` with a hint to
   add an explicit `using static System.Math;` or a fully-qualified call. Today
   the diagnostic just claims the local-namespace binding as truth.
3. Add a regression workspace fixture that has a top-level `Math` namespace and
   asserts `Math.PI` in a `using System;`-only file resolves to `System.Math.PI`.

**Why it matters.** Right now this class of false positive forces every Lifeblood
diagnostic on a Unity-shaped workspace to be cross-checked against the Unity
console before the user can act. Reduces the "verified by Lifeblood, ship it"
trust budget by exactly one round-trip. The fix shape above keeps the strict
resolver but makes the answer right when `using System;` is already declared,
which is the overwhelming common case.

---

## LB-INBOX-008. Per-diagnostic preprocessor-scope reporting on the envelope — **SHIPPED [Unreleased] (INV-DIAGNOSTIC-ENVELOPE-DEFINES-001)**

**Resolution.** Every `lifeblood_diagnose` and `lifeblood_compile_check` response now surfaces `definesActive` (the sorted, deduplicated preprocessor symbols Lifeblood bound the scope under) plus `resolvedModule` (the module the scope resolved to; empty for project-wide). Scope rules mirror legacy diagnostics: file-scope routes through `FindOwningCompilation`; module-scope uses the request's module name; project-wide returns the sorted-deduped union across every loaded compilation. Domain `DiagnosticsReport { Diagnostics, DefinesActive, ResolvedModule }` + `DefinesActive` on `CompileCheckResult`. The `defineConstraints` option (fix shape #3) deferred — the live observation pattern is "did this finding bind under Editor or release defines?", answered by `definesActive` alone; an as-if-player one-shot can be added if real sessions demand it. Pinned by `DiagnosticEnvelopeDefinesTests` (7 facts). Closes `LB-TRACK-20260514-002`. Original entry preserved below for regression-trace.

**Observed.** During a player-build readiness audit of a workspace (see related
session note in dogfood-archive feedback doc), Lifeblood's standalone diagnostic
correctly flagged a method as a player-build risk because the caller was outside
`#if UNITY_EDITOR || DEVELOPMENT_BUILD` and the callee was inside it. Unity
Editor tests passed because the Editor symbol was defined; a release IL2CPP
compile would have failed. Good catch — this is one of the highest-value
findings the tool has produced on a real session.

The friction: nothing on the diagnostic envelope tells the user "this finding
was rendered against define set X" (e.g. `["UNITY_EDITOR", "DEVELOPMENT_BUILD"]`
or `["UNITY_STANDALONE_WIN", "ENABLE_IL2CPP"]`). So the user can't distinguish
"Editor-only-noise that is gated correctly" from "player-build-real risk that
will fail on the release pipeline" without manually re-running both contexts
and diff'ing.

**Suggested fix shape.**

1. Add a `definesActive: string[]` field to the truth envelope on every diagnostic
   response (`lifeblood_diagnose`, `lifeblood_compile_check`). Lists the active
   preprocessor symbols Lifeblood used when binding the snippet/file.
2. Add `definesUnused: string[]` (optional, when known) — symbols NOT defined for
   this scope but referenced in the codebase, so a user can trivially see
   "you're checking the Editor define set; the player set would also exclude
   `DEVELOPMENT_BUILD` and `UNITY_INCLUDE_TESTS`."
3. Optionally, accept `defineConstraints: string[]` on `lifeblood_diagnose` /
   `lifeblood_compile_check` so the user can request a one-shot "as-if-player"
   check from a single tool call without running two server passes.

**Why it matters.** Closes the L1-style auditor loop: every Lifeblood diagnostic
becomes a self-classifying finding instead of "is this real or just an Editor-
context artifact?" — the most common follow-up question on real sessions today.
Pairs naturally with `INV-ENVELOPE-001` (the truth envelope contract is the
existing surface to extend).

---

## LB-INBOX-009. Enum-member reference queries return inconclusive results — **SHIPPED [Unreleased] (INV-ENUM-COVERAGE-001)**

**Resolution.** Fix shape #2 shipped: dedicated `lifeblood_enum_coverage` tool takes an enum type id and returns per-member produced / consumedComparison / consumedSwitch / other counts plus `isUnproduced` / `isUnreferenced` flags, all classified by parent syntax in a single O(total_nodes) walk per compilation (cheaper than per-member `find_references`). Top-level `unproducedCount` + `unreferencedCount` summaries answer the dogfood audit ("which state-machine values are checked-for but never assigned?") off one call. Fix shape #1 (the three-way reference-kind classification on `find_references` itself) NOT shipped — `enum_coverage` superseded it as the canonical workflow because per-member find_references doesn't scale on real enums and the classification already lives inside enum_coverage. Fix shape #3 (`lifeblood_dead_code` flagging unproduced enum members) NOT shipped — `enum_coverage`'s `isUnproduced` flag is the answer; dead_code would just rediscover it. Pinned by `EnumCoverageTests` (8 facts). Closes `LB-TRACK-20260514-003`. Original entry preserved below for regression-trace.

**Observed.** Verifying "every declared enum value is produced by source code"
on a real reject-reason / telemetry-bucket enum
(`MyDomain.Audio.SomeRejectReason` with ~22 byte-backed values) required asking
Lifeblood for incoming references on individual enum members like
`MyDomain.Audio.SomeRejectReason.UnsupportedWaveform`. `lifeblood_find_references`
returned hits that pointed back to the enum declaration site or did not
distinguish "value referenced by name" vs "value never produced". The user
fell back to source-grep / Roslyn syntactic walk to verify coverage, which is
the workflow Lifeblood's premise targets.

**Suggested fix shape.**

1. Treat enum members as first-class queryable symbols on `lifeblood_find_references`.
   Differentiate three reference kinds in the response:
    - `MemberAccess` (`SomeRejectReason.UnsupportedWaveform` written explicitly)
    - `Equality` (`x == SomeRejectReason.UnsupportedWaveform`)
    - `SwitchArm` (`case SomeRejectReason.UnsupportedWaveform:`)
   Combined empty result for all three = strong "declared but never produced" signal.
2. Either add a dedicated read-side tool (e.g. `lifeblood_enum_coverage` or
   `lifeblood_unproduced_enum_values`) that takes an enum type id and returns a
   per-member coverage report, or document the find-references kind-filter pattern
   as the canonical workflow for this audit.
3. Tighten `lifeblood_dead_code` to optionally flag enum members with zero
   producing references in the same way it flags methods with no incoming edges.

**Why it matters.** Reject-reason / state-machine / event-bus enums are
architectural surfaces; "declared value drifts from never-produced" is the
class of bug review-passes have to find by hand today. A semantic answer would
collapse a hand audit to one tool call. Pairs with `lifeblood_dead_code`'s
existing advisory-mode discipline — same shape, applied to enum members.

---

## LB-INBOX-010. `dead_code` / `dependants` miss method-group references through target-typed `new(...)` and generic-method calls

**Observed.** Self-dogfood on 2026-05-14 (post-v0.7.3, preparing v0.7.4)
surfaced six `lifeblood_dead_code` method findings on Lifeblood itself.
Three are legitimate runtime entry points (`Program.Main` × 3 across
CLI / Server.Mcp / ScriptHost, documented in the response envelope's
`limitations[]`). The other three are real graph-level false positives
where `lifeblood_find_references` correctly sees the usage but
`lifeblood_dependants` / `lifeblood_dead_code` / `lifeblood_blast_radius`
miss it because the extractor never emitted a `Calls` edge:

1. **Target-typed `new(MethodGroup)`** —
   `src/Lifeblood.Adapters.CSharp/Internal/BclReferenceLoader.cs:20`
   declares `public static readonly Lazy<MetadataReference[]> References = new(Load);`.
   `find_references` returns one hit (Roslyn semantic), `dependants` count is `0`.
   Same pattern at
   `src/Lifeblood.Adapters.CSharp/RoslynCodeExecutor.cs:90` with
   `new(LoadHostBclReferences)`.
2. **Generic method-group / type-inferred call** —
   `src/Lifeblood.Server.Mcp/ToolHandler.cs:162` calls `ApplyCap` five
   times (`var (highValueFiles, filesTrunc) = ApplyCap(pack.HighValueFiles, maxFiles);`
   …), but `method:Lifeblood.Server.Mcp.ToolHandler.ApplyCap(T[],int)`
   shows `directDependants=0`. Suspected canonical-id drift between the
   call-site's instantiated `IMethodSymbol` and the source-declared
   generic definition.

The tool already documents both classes in its response `warning`
field ("methods referenced via method-group conversion (Lazy<T>, event
handlers, delegate arguments); methods with call-site canonical-id
drift in multi-module workspaces (pre-existing extraction gap under
investigation)"), so consumers see the caveat. The audit caught a
second, sharper miss: the test fixture
`tests/Lifeblood.Tests/RoslynExtractorTests.cs:1145` claims to pin
target-typed `new(Load)` coverage in its comment (
"`static Lazy<T> _x = new(Load)` — `Load` must get an incoming edge so
the dead-code analyzer does not flag it") but the actual source under
test uses the EXPLICIT form
(`private static readonly System.Lazy<string> _cache = new System.Lazy<string>(Load);`).
The test pin and the contract diverge — a true regression-ratchet
hole, not just a doc inconsistency.

**Suggested fix shape.**

1. **Close the target-typed gap.** Trace why
   `RoslynEdgeExtractor.ExtractReferenceEdge`'s method-group handler
   (lines 324-335) doesn't fire for the `Load` identifier inside
   `new(Load)`. The handler matches `BaseObjectCreationExpressionSyntax`
   for the ctor edge (correct), but the argument-position `Load`
   `IdentifierNameSyntax` may resolve to a different `SymbolInfo` shape
   under target-typed binding — `model.GetSymbolInfo` could return
   `CandidateReason.OverloadResolutionFailure` until the target type
   resolves the ctor, leaving `referencedSymbol` null at extraction
   time. Likely fix: in target-typed contexts use
   `GetSymbolInfo` on the OUTER `BaseObjectCreationExpressionSyntax` to
   force target-type binding before re-querying the inner identifier,
   or accept `CandidateReason.OverloadResolutionFailure` + walk
   `CandidateSymbols`.
2. **Close the generic-call canonical-id drift.** Investigate whether
   the extractor emits the edge under the instantiated symbol-id
   (e.g. `method:...ApplyCap(string[],int)`) versus the source-declared
   generic id (`method:...ApplyCap(T[],int)`). If so, route through
   `OriginalDefinition` before calling `GetMethodId` so the edge target
   matches the canonical extracted symbol. (Mirrors the
   `INV-CANONICAL-001` discipline applied elsewhere.)
3. **Fix the test hole both ways.** Rename the existing fixture (which
   covers the EXPLICIT-form path) to make its scope honest, then add a
   new fixture that uses target-typed `new(Load)` and asserts the
   `Calls` edge. The new test will fail until #1 ships — that's the
   point. Pin as `[Fact(Skip="known FP — LB-INBOX-010")]` until then,
   or as a regular `[Fact]` once #1 ships.
4. **Promote both gaps into the published `INV-DEADCODE-001`
   remaining-FP list** so the contract carries the constraint in the
   invariant tree, not just in the runtime warning string.

**Why it matters.** Tool-warning self-disclosure is honest; an extractor
fix removes the warning. Three false positives on a 2,755-symbol
codebase is small in absolute terms, but the gap class is exactly the
kind of "AI agent decides a live method is dead" hazard the truth
envelope is supposed to bound. Every percentage point removed from the
advisory tail makes the `dead_code` tool more actionable.

---

## LB-INBOX-011. Static-table facts do not yet feed graph liveness, and implicit array tables can be missed — **SHIPPED [Unreleased] (INV-EXTRACT-STATIC-IMPLICIT-ARRAY-001 + INV-EXTRACT-DISPATCH-TABLE-COVERAGE-001 + INV-EXTRACT-METHOD-GROUP-CANDIDATE-001)**

**Resolution.** Both parts closed in the v0.7.6 prep wave. Part 1 (implicit primitive array tables): `RoslynStaticTableExtractor.ClassifyContainer` gained an `IArrayInitializerOperation` branch — the Roslyn op shape for `static T[] X = { ... }` with no `new T[]` prefix. Element type resolved from the declaring member's `ITypeSymbol` because the op itself carries no type metadata for the array variant. Container kind stays `Array` (`INV-EXTRACT-STATIC-IMPLICIT-ARRAY-001`). Part 2 (graph-edge synthesis for dispatch-table cells): re-investigated as a no-yes-man pushback on the original framing. The proposal to ship a separate "static-table → graph-edge synthesizer" was structurally redundant — the empirical "delegate row methods missing from `dependants`" class was the same target-typed-`new(MethodGroup)` gap LB-INBOX-010 part 1 covered, just observed through a dispatch-table lens. Once `INV-EXTRACT-METHOD-GROUP-CANDIDATE-001` accepts `CandidateSymbols`-bound method groups in `RoslynEdgeExtractor.ExtractReferenceEdge`, every dispatch-table cell (method-group, enum-member, field-reference) emits its canonical edge through the same walker that handles non-table contexts. The wire-color choice (`Calls` from `.cctor` vs `References` from the field, as the original entry suggested) is observationally identical at the live-ness layer because `dead_code` / `dependants` / `port_health` / `blast_radius` all consume both edge kinds via `HasIncomingReference`. Pinned by `ExtractEdges_DispatchTableWithMethodGroupAndEnumCells_FullCoverage` (all three cell classes) + `GetStaticTables_StaticImplicitArrayField_DetectedAsArrayContainer` + `GetStaticTables_StaticImplicitArrayProperty_DetectedAsArrayContainer` (implicit array form). Closes `LB-TRACK-20260515-010`. Original entry preserved below for regression-trace.

**Observed.** Dogfood on a real 90-module Unity workspace after v0.7.4
surfaced two related gaps around table-driven code:

1. `lifeblood_static_tables` extracted object-creation dispatch tables
   correctly, including `MethodGroup` cells and source provenance, but
   returned zero tables for static primitive recipe arrays authored as
   `private static readonly float[] Weights = { ... };` /
   `private static readonly byte[] Ratios = { ... };`. Those implicit
   array-initializer fields are table-shaped and should be queryable by
   the same tool. If Roslyn surfaces this as a different operation shape
   than `IArrayCreationOperation`, the extractor contract needs to include
   that shape explicitly while still staying operation-based, not syntax
   text-based.
2. For extracted dispatch tables, `MethodGroup` cells surface the target
   `MethodGroupId`, and `lifeblood_find_references` can see the method
   group usage at the source line. The graph, however, does not receive a
   corresponding dependency edge. As a result `lifeblood_dependants`,
   `lifeblood_dead_code`, `lifeblood_port_health`, and
   `lifeblood_blast_radius` can treat table-delegate target methods as
   dead or dependency-free even though they are live through the table.

This is distinct from `LB-INBOX-010`: that entry covers method-group
references in target-typed `new(...)` and generic-call canonical-id drift.
This entry covers the static-table extraction surface itself - facts are
already present in `static_tables`, but not reflected into graph liveness.

**Suggested fix shape.**

1. Add a regression fixture with implicit primitive arrays:
   `static readonly float[] Weights = { 0.1f, 0.2f };`,
   `static readonly byte[] Ratios = { 1, 2, 4 };`, and an enum array.
   Assert `lifeblood_static_tables` returns one table per member with
   ordered literal rows. Implement by inspecting the Roslyn operation tree
   shape for implicit array initializers; do not fall back to regex or
   raw syntax-text parsing.
2. When the C# adapter classifies static-table values that carry stable
   symbol ids (`MethodGroupId`, `FieldReferenceId`, `EnumMemberId`,
   `EnumFlagMemberIds`), emit graph `References` edges from the containing
   static field/property symbol to those referenced symbols, with the same
   `CallSite` provenance already attached to expression-derived edges.
   Use `References`, not `Calls`, for method groups: storing a delegate in
   a table is a data reference, not an invocation.
3. Verify the derived tools automatically improve from those edges:
   `dependants(targetMethod)` reports the table field/property,
   `dependencies(tableField)` reports the delegate target,
   `port_health(tableOwnerType)` stops marking delegate row methods dead,
   and `dead_code` no longer emits table-only delegate targets as classic
   zero-incoming findings.
4. Add a focused skipped regression if the edge emission is not fixed in
   the same pass, named around `LB-INBOX-011`, so future releases cannot
   accidentally present green coverage for table-delegate liveness.

**Why it matters.** Serious C# systems often encode behavior in capability
matrices, recipe registries, dispatch tables, analyzer tables, and kernel
policy rows. Lifeblood already exposes those facts through
`lifeblood_static_tables`; the graph must see the same references or the
read-side tools split reality in two. This is exactly the kind of gap that
makes an AI agent believe a table-driven method is unused when it is
actually load-bearing.

---

## How entries land here

If you find a friction point during a real session:

1. Reproduce it once with a minimal query against a workspace you trust.
2. Write the entry as: title, observation, suggested fix, why it matters.
3. Anonymize all consumer-specific names. The Lifeblood repo carries no leakage from downstream workspaces. Describe shapes (`FooType`, `BarMethod`, `OuterType.PropertyName`) instead of real identifiers.
4. Keep entries narrow. One observation, one fix shape per entry. Cross-reference with `LB-INBOX-NNN` ids when entries are related.
5. When the fix ships, **mark the entry SHIPPED at the heading** with the closing-commit / tag / INV-ID refs, preserve the original `Observed` / `Suggested fix shape` / `Why it matters` body verbatim, and keep the entry in this file. The CHANGELOG records what shipped from the release angle; this file records the original observation alongside its resolution so a future regression has a single page that links the symptom, the fix, and the contract that should be preventing recurrence.

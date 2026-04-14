# Improvement Inbox

State of Lifeblood going forward. Not a graveyard of shipped items and not a blog. Three things only:

1. A current-state verdict compiled from the external reviews we run against the repo.
2. A pointer to shipped work (tracked via git tags and `CHANGELOG.md`, not duplicated here).
3. A small number of active forward-looking entries, each in a consistent format: title, observation, suggested fix shape, why it matters.

Anything that has shipped is deleted from this file. Anything speculative that nobody is about to work on is also deleted. What remains is direction.

---

## Current state (as of v0.6.3, 2026-04-11)

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

## LB-INBOX-001. Phase 1. Uniform truth envelope across every read-side tool

**Observed.** `lifeblood_dead_code` ships with a `status: "experimental"` marker and a `warning` field that describes its known false-positive classes in-band. Every other tool returns results without a comparable metadata shape. A caller receiving a `find_references` hit has no way to tell from the payload alone whether that hit is compiler-resolved, parser-structural, or graph-derived. The existing separation of syntax / semantic / derived truth in `docs/ARCHITECTURE.md` is not projected to the wire.

**Suggested fix shape.** Define one typed response-metadata contract shared across MCP tools:

- `truthTier`: one of `syntax` / `semantic` / `derived`
- `confidence`: `proven` / `high` / `structural` / `advisory`
- `evidenceSource`: where the result came from (`Roslyn`, `GraphBuilder`, `InferredByGraphWalk`, etc.)
- `staleness`: optional timestamp or commit indicator when the result depends on a cached graph
- `limitations`: optional free-form caveat when the tool knows it is operating outside its confident zone

Every read-side tool declares its default tier. Advisory tools (today only `lifeblood_dead_code`, tomorrow possibly others) emit limitations in-band, not only in docs. Add response-shape golden tests across all 22 tools so no new tool can ship without the envelope.

**Why it matters.** It amplifies the repo's strongest architectural idea (the syntax / semantic / derived distinction) without changing any engine underneath. Shortest path from "internally disciplined" to "externally trustworthy". Work compounds with Phase 3: once every response carries the envelope, versioning the envelope once covers every tool.

---

## LB-INBOX-002. Phase 2. Close out `INV-DEADCODE-001` and the shared extraction gap

**Observed.** After the v0.6.4 extraction pass (interface dispatch, member-access granularity, null-conditional property, lambda context) and the implicit-global-usings compilation fix, the `lifeblood_dead_code` self-analysis tail stabilized at 10 findings. A follow-up pass closed three more structural gaps that the original "by design / known gap" framing had enshrined:

1. **Ctor `Calls` edge.** `ObjectCreationExpressionSyntax` now emits both a type-level `References` edge AND a method-level `Calls` edge to the `.ctor`. `find_references` on a constructor returns its construction sites. The dead-code analyzer sees invoked ctors as reachable.
2. **Field-initializer containing method.** `FindContainingMethodOrLocal` resolves a reference inside `static T _x = Bar()` or `T _x = Bar()` to the type's synthesized `.cctor` / first `.ctor`. Closes the `new Lazy<>(Load)` FP class.
3. **Property accessor context.** `FindContainingMethodOrLocal` now returns the accessor `IMethodSymbol` instead of bailing out. `GetMethodId` routes accessors through `AssociatedSymbol` so the emitted edge source is the property id — the graph node the dead-code analyzer actually walks. Covers both bodied `get { return _field; }` and expression-bodied `=> _field` properties/indexers.

**Remaining expected after re-scan.** Runtime entry points (`Program.Main` × 6) and any genuine unused surface. Everything else listed in prior inbox tables (ctor "by design", accessor "known gap", field-initializer "no containing method") is now closed.

**Suggested follow-on.**
- Rescan Lifeblood self-analysis + confirm dead-code tail shrinks from 10 toward the entry-point floor.
- Update `INV-DEADCODE-001` wording in `CLAUDE.md` (remove obsolete "static field initializer method-groups" + "constructor by design" bullets from the remaining-known-FPs list).
- Graduate `lifeblood_dead_code` out of experimental once the rescan is stable for at least one minor release AND the Phase-1 truth envelope is defined (so the graduated tool ships with the right `confidence` tier).

**Why it matters.** Every call-graph tool (`find_references`, `dependants`, `blast_radius`, `file_impact`, `dead_code`) inherits the new edges automatically. `find_references` on a ctor now works — previously silently returned zero. Property-held constants used from their own getter are no longer flagged. Method-group refs from static field initializers are attributed correctly.

---

## LB-INBOX-003. Phase 3. Contract freeze before the platform surface grows further

**Observed.** Lifeblood's wire surface now includes 22 MCP tools, 22 ports, the semantic graph JSON schema, and a growing set of architectural invariants. Several single-source-of-truth sites already exist: `CanonicalSymbolFormat`, `CsprojPaths`, `McpProtocolSpec`, `CLAUDE.md`. But there is no formal versioning story for the tool schemas or the graph schema. A v0.6.4 that accidentally renames a response field would break every external integrator silently.

**Suggested fix shape.**

1. Publish versioned tool input and output schemas under `schemas/tools/<version>/*.json`. Start with a v1 snapshot of the current 22 tool shapes.
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

## LB-INBOX-006. `smoke-mcp-analyze.ps1` default `ServerDll` path is brittle on Windows

**Observed.** Review 3 launched the v0.6.3 smoke script on Windows and the default `ServerDll` path construction failed at first run. The actual server worked correctly once the script was pointed at the right DLL location explicitly. Not a code defect in the MCP server; a path-construction bug in the smoke wrapper script. The script exists to make it trivial for an external reviewer to verify a live MCP round-trip against a freshly cloned repo, so a broken default path hits exactly the audience it was written for.

**Suggested fix shape.**

1. Change the default `ServerDll` parameter to auto-discover via `Join-Path $PSScriptRoot '..' 'src' 'Lifeblood.Server.Mcp' 'bin' 'Debug' 'net8.0' 'Lifeblood.Server.Mcp.dll'` or the Release equivalent, and fall through to Release if Debug is missing.
2. On startup, assert the file exists. If it does not, print a one-line diagnostic that names the exact path it tried and suggests either passing `-ServerDll <path>` or running `dotnet build -c Debug` first.
3. Add a short README note in the smoke folder explaining the default resolution order so reviewers do not have to read the script to understand what went wrong.

**Why it matters.** This is the exact script an external auditor uses to convince themselves Lifeblood actually works end-to-end. A path-construction fail on first run is cosmetically small but materially damaging for trust: it is the moment where a reviewer decides whether the repo is operational or theoretical. Fixing this is small work with outsized credibility impact.

---

## How entries land here

If you find a friction point during a real session:

1. Reproduce it once with a minimal query against a workspace you trust.
2. Write the entry as: title, observation, suggested fix, why it matters.
3. Anonymize all consumer-specific names. The Lifeblood repo carries no leakage from downstream workspaces. Describe shapes (`FooType`, `BarMethod`, `OuterType.PropertyName`) instead of real identifiers.
4. Keep entries narrow. One observation, one fix shape per entry. Cross-reference with `LB-INBOX-NNN` ids when entries are related.
5. When the fix ships, **delete the entry from this file**. The record of what shipped lives in `CHANGELOG.md` and the git history, not here. This file is an active roadmap, not a historical log.

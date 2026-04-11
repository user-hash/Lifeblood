# Phase 8 — `lifeblood_invariant_check` Design Spike

**Status:** Phase 8A + 8B shipped in v0.6.3. Phase 8C (enforcement taxonomy) and 8D (graph-walking enforcement) still deferred. Phase 8E (git-diff mode) explicitly out of scope. This doc is the architectural record of what shipped plus the open work list.

**Scope:** design record + follow-up roadmap. Sections 1-6 describe the contract as shipped. Section 7 (rollout phases) tracks which phases have landed and which remain. Section 8 (open questions) notes the answers picked during 8A+8B implementation inline.

**Implementation divergence from the original spike.** The spike proposed Option C in §4 ("Hybrid: CLAUDE.md canonical for body; `.lifeblood/invariants.yaml` holds the machine-readable fields"). The shipped implementation picked a simpler variant: CLAUDE.md is the *only* source of truth, parsed at runtime by `ClaudeMdInvariantParser`, and there is no companion file at all. A future Phase 8C can still ship a companion file for `appliesTo` / `enforcement` metadata without breaking this contract — the parser returns a structured shape, the provider orchestrates, adding a second loader is additive. Single-source-of-truth is simpler today and the ratchet-test pair the spike proposed is unnecessary when there is no second source to drift against.

**What shipped in v0.6.3.** Three value types (`Invariant`, `InvariantAudit`, `CategoryCount`), one port (`IInvariantProvider`), one parser (`ClaudeMdInvariantParser` — pure text → records, handles shapes A and B, multi-line titles, multi-paragraph bodies, duplicate detection, category inference), one cache (`InvariantParseCache<T>` — generic, timestamp-invalidated, reusable for any future source), one provider (`LifebloodInvariantProvider` — thin orchestrator), one tool handler (`HandleInvariantCheck` with three modes: `id`, `audit`, `list`), one registration in `ToolRegistry`, and 43 new tests across three test files including a dogfood self-test that parses Lifeblood's own CLAUDE.md. The new tool documents its own contract as `INV-INVARIANT-001` in CLAUDE.md — the first governance rule in the repository that the lifeblood_* toolchain can audit itself. Live dogfood: the tool parses Lifeblood's 57 invariants across 25 categories with zero duplicates and zero parse warnings.

**Audience:** the next Lifeblood session, and any external reader inspecting the architectural design record. Read top-to-bottom once; come back to §4 (tool surface) and §7 (rollout) when implementing Phase 8C.

---

## 1. Problem

Lifeblood's `CLAUDE.md` declares ~45 architectural invariants of the form

> **INV-CANONICAL-001. Roslyn compilations receive the full transitive dependency closure, not just direct `ModuleInfo.Dependencies`.** …

Some are ratchet-tested (`ArchitectureInvariantTests`, `DocsTests`, `CanonicalSymbolFormatTests`). Most are documented-only. A few carry explicit `Ratchet-tested by X` citations; the rest require an engineer to remember the connection. Three concrete failure modes today:

1. **AI agents don't read the wall of text.** When an agent is about to edit `RoslynCompilationHost.cs` it cannot easily see which invariants constrain its change. The invariants are in a monolithic markdown file, not queryable from the workspace.
2. **Drift is invisible.** An invariant that loses its enforcing test silently becomes documentation. The v0.6.1 `tools/list` serialization regression is a textbook example: `INV-TOOLREG-001` existed in the CLAUDE.md, the conflated wire+internal type shipped anyway, the ratchet test that would have caught it was only added *after* the bug.
3. **No cross-reference.** Given `INV-CANONICAL-001`, there's no command that returns "the test file that pins this is `tests/.../CanonicalSymbolFormatTests.cs`" or "the code site this lives on is `ModuleCompilationBuilder.ProcessInOrder`". The linkage is maintained only by careful prose in the invariant's body.

## 2. User value

The tool turns CLAUDE.md's invariants from a **wall of prose** into a **queryable structured index** that every AI agent can consult without reading the full file. Three primary use cases:

**U1. Pre-edit check ("what applies here?").** Agent is about to edit `src/Lifeblood.Adapters.CSharp/RoslynCompilationHost.cs`. It calls

```
lifeblood_invariant_check { scope: "file", path: "src/Lifeblood.Adapters.CSharp/RoslynCompilationHost.cs" }
```

and receives the subset of invariants whose declared `appliesTo` globs match the file. The agent now knows INV-CANONICAL-001, INV-BCL-004, INV-STREAM-001, and INV-VIEW-001 all constrain the file before writing a single line.

**U2. Invariant detail lookup.** Agent reads a test with comment `// INV-RESOLVER-005` and wants the full rationale. It calls

```
lifeblood_invariant_check { id: "INV-RESOLVER-005" }
```

and gets the prose body, appliesTo globs, every enforcement site, the commit SHA that introduced the invariant, and the list of tests that currently pin it.

**U3. Drift detection.** Build pipeline or human reviewer calls

```
lifeblood_invariant_check { mode: "audit" }
```

and gets a report of every invariant whose status is not `Enforced` — either because its documented test file is missing, the test name doesn't exist, or no enforcement kind was declared at all. This is the v0.6.1 `tools/list` failure mode caught automatically.

A secondary use case — U4. **Fast machine-check of "all things on a diff"** — uses U1 with a list of changed files (from `git diff --name-only`) and flags every applicable invariant as a pre-commit reminder. Out of scope for v1 but the data model supports it free.

## 3. Invariant data model

Every invariant is one record. Schema (proposed — open for tuning in Phase 8A):

```
Invariant {
  id             : string    // "INV-CANONICAL-001"
  title          : string    // short single-line summary
  body           : markdown  // full rationale as written in CLAUDE.md
  category       : enum      // DomainPurity | Application | Graph | Adapter | Connector
                             // Analysis | Test | Pipeline | Resolver | CompFact
                             // Canonical | View | Usage | BCL | Mcp | ToolReg
                             // ScriptHost | CompRoot | Docs | Changelog
  appliesTo      : Glob[]    // file-path globs where the invariant constrains changes
                             // e.g. [ "src/Lifeblood.Adapters.CSharp/**/*.cs" ]
  enforcement    : Enforcement[]   // see §5; one invariant can have many
  introducedIn   : string    // commit SHA or version tag (optional)
  replacedBy     : string?   // forward pointer if retired
}

Enforcement {
  kind           : enum      // RatchetTest | IntegrationTest | CsprojCheck
                             //   | GraphValidator | RuntimeAssert | ManualReview
  target         : string    // e.g. "tests/Lifeblood.Tests/CanonicalSymbolFormatTests.cs#ComputeTransitiveDependencies_FlatChain_ReturnsFullClosure"
                             // or a csproj path, or a source file:line
  status         : enum      // Present | Missing | Unverifiable
  lastCheckedAt  : ISO8601   // populated by the checker
}
```

**Status enum semantics.** `Present` = the named target exists and the assertion is active. `Missing` = the target file or test name isn't in the repo — documentation-only or broken. `Unverifiable` = the kind is `ManualReview` so the checker can only report "not machine-checkable" (which is still useful: U3's audit filters for these to surface the hope-enforced ones).

## 4. Where invariants live (source of truth)

**Option A — Inline frontmatter in CLAUDE.md.** Parse CLAUDE.md for `- **INV-FOO-NNN. Title.** Body...` markers, extract via a markdown parser plus regex. Advantage: no duplication, CLAUDE.md stays the single authoring surface. Disadvantage: the tool has to parse markdown on every call, AND the machine-readable fields (appliesTo, enforcement) have to be inferred from prose or encoded as trailing HTML comments.

**Option B — Separate `invariants.yaml` file.** Authored once per invariant with full structure. CLAUDE.md gets a link reference per invariant. Advantage: schema-pure, diffable, queryable without parsing markdown. Disadvantage: two sources of truth — the body in the YAML and the same body (or a summary) in CLAUDE.md. Drift risk.

**Option C — Hybrid: CLAUDE.md is canonical for body/title; `invariants.yaml` holds the machine-readable fields.** Tool joins them by ID. CLAUDE.md stays readable and authoring-friendly for the prose. YAML holds the appliesTo/enforcement metadata that the tool needs. Drift risk: an invariant in CLAUDE.md with no matching YAML entry (or vice versa). A ratchet test closes the drift: "every `INV-` marker in CLAUDE.md has a YAML entry, and every YAML entry has a CLAUDE.md marker".

**Decision: Option C.** It keeps the prose close to the architectural decisions it describes (a future reader walking CLAUDE.md gets the full rationale inline) AND gives the tool a clean schema without markdown parsing. The ratchet test is trivial and eliminates the drift risk. Phase 8A ships both the YAML schema and the ratchet test.

Location: `.lifeblood/invariants.yaml` (new file) at the repo root. Prefixed directory signals "Lifeblood metadata" to the tool without polluting `docs/` or `.claude/` — both are used for other purposes.

## 5. Tool surface

One tool, one name, three modes (dispatched on parameter shape). The tool is **read-side** per INV-TOOLREG-001: no workspace mutations, no write-side compilation cost.

```
lifeblood_invariant_check
  ( // mode 1: scope-by-file (U1)
    scope?: "file" | "module" | "workspace",
    path?: string,

    // mode 2: lookup-by-id (U2)
    id?: string,

    // mode 3: audit (U3)
    mode?: "audit",

    // common
    categoryFilter?: string[],
    statusFilter?: ("Present" | "Missing" | "Unverifiable")[]
  )
  -> InvariantCheckResult
```

**Result shape:**

```
InvariantCheckResult {
  mode            : "file" | "module" | "workspace" | "id" | "audit"
  invariants      : InvariantRecord[]
  summary         : {
    total          : int
    enforcedCount  : int
    missingCount   : int
    unverifiableCount: int
  }
  executionMs     : int
}

InvariantRecord {
  id              : string
  title           : string
  body            : string   // present when mode="id", elided otherwise to keep responses lean
  category        : string
  appliesTo       : string[]
  enforcement     : EnforcementStatus[]
  overallStatus   : "Enforced" | "PartiallyEnforced" | "DocumentationOnly" | "Broken"
}
```

**Parameter semantics.** Exactly one of `{scope+path}`, `{id}`, or `{mode:"audit"}` is required. Extra params are a validation error (no silent precedence games). `categoryFilter` and `statusFilter` apply to the result set, not to routing.

**`overallStatus`** is a derived field:
- **Enforced** — every enforcement with kind != `ManualReview` has status `Present`.
- **PartiallyEnforced** — some enforcements are `Present`, others `Missing`.
- **DocumentationOnly** — every enforcement has kind `ManualReview`, or no enforcements declared.
- **Broken** — one or more enforcements are `Missing` and nothing else is `Present`.

## 6. Enforcement taxonomy — how the checker verifies each kind

| Kind | Checker strategy | Example |
|---|---|---|
| **RatchetTest** | Parse the target `tests/**/*.cs` file, verify a method with the named identifier exists, marked `[Fact]` or `[Theory]`. Present iff parseable + method exists. | `ArchitectureInvariantTests.ScriptHost_HasZeroProjectReferences` |
| **IntegrationTest** | Same as RatchetTest but the target is typically a `SkippableFact` needing golden repo. The checker only verifies the method exists; it doesn't RUN the test. Running is `dotnet test`'s job. | `WriteSideIntegrationTests.FindImplementations_IGreeter_FindsGreeterAndFormalGreeter` |
| **CsprojCheck** | Parse the target csproj via `Internal.CsprojPaths` (the shared helper we already have), check the declared property/reference is present OR absent as the invariant requires. | `INV-DOMAIN-001` — Domain csproj has zero `ProjectReference`. |
| **GraphValidator** | Load the graph via `IGraphImporter` from a snapshot or run a fast re-analyze, walk the nodes/edges for the invariant's predicate. | `INV-GRAPH-003` — every edge carries Evidence. |
| **RuntimeAssert** | No checker step — the invariant is self-checking during runtime. The tool reports status = `Unverifiable` but marks the kind so U3 audit knows this is intentional, not forgotten. | `INV-USAGE-PORT-002` — `Stop` is idempotent; verified only by integration tests against the concrete probe. |
| **ManualReview** | Always status = `Unverifiable`. U3 audit surfaces the count. | `INV-APP-001` — general coding conventions. |

A single invariant can declare MULTIPLE enforcements. E.g. `INV-CANONICAL-001` might have:

```yaml
- id: INV-CANONICAL-001
  enforcement:
    - kind: RatchetTest
      target: "tests/Lifeblood.Tests/CanonicalSymbolFormatTests.cs#ComputeTransitiveDependencies_FlatChain_ReturnsFullClosure"
    - kind: RatchetTest
      target: "tests/Lifeblood.Tests/CanonicalSymbolFormatTests.cs#ComputeTransitiveDependencies_Diamond_ReturnsDeduplicatedClosure"
    - kind: IntegrationTest
      target: "tests/Lifeblood.Tests/CanonicalSymbolFormatTests.cs#AnalyzeWorkspace_ThreeModuleTransitiveChain_ProducesCanonicalMethodIds"
```

The checker reports every enforcement individually; `overallStatus` aggregates.

## 7. Rollout — phased, each phase independently landable

**Phase 8A. Data model + ratchet.** One week of work, zero user-facing tool.
- Create `.lifeblood/invariants.yaml` with a starter schema: `id`, `title`, `category`, `appliesTo`. No enforcement yet.
- Populate entries for every existing `INV-*` in CLAUDE.md (~45 entries). Title and category are copy-paste; `appliesTo` is new.
- Ratchet test in `DocsTests`: `Invariants_EveryClaudeMdMarkerHasYamlEntry` and the inverse. Closes the two-source drift.
- **Deliverable:** machine-readable inventory. No tool yet.

**Phase 8B. `lifeblood_invariant_check` minimum viable.** Mode `id` and mode `file`. Enforcement field is still empty, so the tool returns category + appliesTo + body-from-CLAUDE.md. No status determination yet.
- New port `Lifeblood.Application.Ports.Right.IInvariantProvider` with `LoadInvariants() → Invariant[]` and `FindApplicable(filePath) → Invariant[]`.
- New connector `LifebloodInvariantProvider` in `Lifeblood.Connectors.Mcp` that reads `.lifeblood/invariants.yaml` via `IFileSystem`.
- New tool handler `HandleInvariantCheck` in `ToolHandler.cs`. Read-side. No compilation cost.
- **Deliverable:** U1 (pre-edit check) and U2 (lookup by id) work end to end.

**Phase 8C. Enforcement taxonomy.** Add `enforcement[]` to YAML entries. Implement checkers for `RatchetTest`, `IntegrationTest`, `CsprojCheck` (the three mechanically verifiable kinds). `GraphValidator`, `RuntimeAssert`, `ManualReview` ship as "not checked here" status for now.
- New internal helper `EnforcementChecker` in `Lifeblood.Connectors.Mcp` that walks the kinds and returns per-enforcement status.
- Integration test against the repo's own invariants.yaml proving at least one of each kind is `Present`.
- **Deliverable:** U3 (audit) works for the majority of invariants. Drift detection is real.

**Phase 8D. Graph-walking enforcements.** Wire `GraphValidator` kind to an actual graph walk — for invariants like `INV-GRAPH-003` that need semantic-graph data. Requires the session to have a loaded graph (falls back to `NotLoaded` error if not).
- **Deliverable:** structural graph invariants join the audit.

**Phase 8E (optional).** Git-aware mode. `scope: "diff"` takes `base` and `head` parameters, computes `git diff --name-only`, returns applicable invariants for every changed file. Powers a pre-commit hook.

Phases 8A→8C are the core deliverable. 8D is nice-to-have. 8E is explicitly optional and only if a consumer asks for it.

## 8. Open questions / risks

**OQ-1. Where does `.lifeblood/invariants.yaml` live in the repo hierarchy?** Root vs `docs/`? Root is the cleanest for tooling discovery (the checker doesn't have to know about `docs/`). Docs subdirectory hides it from casual browsing. Going with root unless someone objects.

**OQ-2. YAML or JSON?** YAML is more authorable (comments, multi-line body strings). JSON is schema-enforced by every tool on the planet. Precedent: the existing `schemas/graph.schema.json` uses JSON. Going with **YAML** because the body fields are multi-paragraph prose and YAML handles that better. A JSON schema can still validate the YAML via `yamllint`/`ajv`.

**OQ-3. Fuzzy matching of `appliesTo`.** Agent asks for invariants that apply to `src/Lifeblood.Adapters.CSharp/Internal/Foo.cs`. The YAML declares `src/Lifeblood.Adapters.CSharp/**/*.cs`. Standard glob match — use `DotNet.Globbing` or `Microsoft.Extensions.FileSystemGlobbing`. Microsoft.Extensions.FileSystemGlobbing is already in the BCL family.

**OQ-4. Should the tool also check *runtime* invariants by loading and running a graph?** No — at the cost of adding graph-load dependency to a read-side tool. Keep the tool cheap. Graph-walking enforcements (Phase 8D) opt in when a graph is already loaded.

**OQ-5. What if an invariant's `appliesTo` glob matches nothing in the repo?** Return status `Broken` for the RatchetTest if the test file doesn't exist; the unmatched glob is a separate audit-time warning ("INV-FOO-001 declares appliesTo `src/OldModule/**/*.cs` but no such files exist — stale?").

**OQ-6. Should we auto-generate the initial YAML from CLAUDE.md?** Yes, as a one-shot migration script in Phase 8A. The script is throwaway: it parses every `INV-` marker, extracts title and body, writes the YAML entry with empty `appliesTo` and `enforcement`. Human curates from there.

**Risk R-1. The YAML becomes a ghost file.** Engineers add INV-* markers to CLAUDE.md and forget the YAML. Mitigation: the ratchet test from Phase 8A blocks the commit.

**Risk R-2. `appliesTo` globs become stale as files move.** When a directory is renamed, the YAML glob needs updating. Not detected by the ratchet. Mitigation: Phase 8C's audit flags `Missing` enforcements, which will fire when the test path drifts. The invariant entry will still appear unstale until someone runs audit.

**Risk R-3. Too granular.** If every tiny class-level assertion gets its own INV entry the file explodes. Mitigation: keep the bar high — INV is for architectural decisions that cross a seam, not for "this method returns non-null".

---

## Summary for the next session

1. **Read §1-3** once for context.
2. **Start at Phase 8A.** The migration script + ratchet test are ~1 day of work and unlock every later phase.
3. **Skip 8E.** Don't build git-aware mode until someone needs it.
4. **Confirm OQ-1..OQ-6 decisions with the user before Phase 8A.** Especially OQ-2 (YAML vs JSON) — it's the most reversible point.
5. **Invariant-count ratchet.** `DocsTests` already pins tool/port counts via HTML comments in `docs/STATUS.md` (INV-DOCS-001). Extend the same pattern to invariant count: `<!-- invariantCount: N -->` + a parse-and-compare test. Cheap and closes the "forgot to update docs" class.

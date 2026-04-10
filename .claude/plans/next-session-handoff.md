# Next-Session Handoff — Lifeblood post-v4

**Date written:** 2026-04-10 (handoff from previous session — Claude Opus 4.6 1M context)
**Repo:** `D:\Projekti\Lifeblood`
**Branch:** `main`
**Last commit:** `54c0b22` — `feat(roslyn): post-BCL architectural fixes — three seams + BCL ownership`
**Test count:** 329/329 passing
**Dist published:** `D:\Projekti\Lifeblood\dist\` (13:25 build, all v4 work + multi-parent GraphBuilder fix)
**No release tag** — Matic explicitly said "no tag yet"

---

## Why this handoff exists

The previous session shipped a large two-phase refactor (v2 BCL ownership fix + v4 three-seam framing) that closed five reviewer-reported bugs and added a new MCP tool. The next session should:

1. **Sanity-check the shipped work** end-to-end against DAWG and against Lifeblood-self.
2. **Run Lifeblood on itself** ("dogfood" / hexagonal-architecture self-audit). This is the most valuable thing the next session can do — Lifeblood now ships an `execute` tool with a typed semantic view, which means it can introspect its own architecture through its own surface.
3. **Identify any further hexagonal-architecture tightening** the codebase should adopt.
4. **Plan v5** for the three remaining reviewer-flagged gaps (one of which has actually been transitively fixed already — see §6 below).

The user's instruction was: *"checkup of existing work, checking if we can enforce hexagonal architecture more, check if everything works and if we can spot anything if we frame lifeblood on itself. Then I also need a plan for remaining work with actual research in lifeblood end-to-end."*

---

## §1. State of the world

### What shipped this session (one commit, `54c0b22`)

**Two phases, both in `54c0b22`:**

#### Phase 1 — v2 BCL ownership fix
- Closes the silent zero-result class for `find_references` / `dependants` / call-graph extraction on **any workspace that ships its own BCL** (Unity, .NET Framework, Mono).
- Empirical impact on DAWG: `Nebulae.BeatGrid.Audio` module 29,523 CS0433/CS0518 errors → 3 unused-field warnings; `Voice.SetPatch` find_references 0 → 18.
- Pieces:
  - `ModuleInfo.BclOwnership` (HostProvided | ModuleProvided enum) decided in `RoslynModuleDiscovery.ParseProject` and consumed in `ModuleCompilationBuilder.CreateCompilation`.
  - `Internal.CanonicalSymbolFormat` — single pinned `SymbolDisplayFormat` for parameter type display strings used by every method-ID builder. Lifeblood's symbol ID grammar is now owned by Lifeblood, not Roslyn's default.
  - `RoslynCompilationHost.FindReferences` matches by canonical symbol id (`BuildSymbolId`), not by `ToDisplayString`.
  - `RoslynWorkspaceManager.FindInCompilation` rewritten with documented contract: kind-filtered, name-filtered, signature-strict; never silently substitutes a wrong member.
  - `AnalysisSnapshot.CsprojTimestamps` + `IncrementalAnalyze` csproj-edit detection (closes LB-BUG-006).

#### Phase 2 — v4 three-seam framing
- **Seam #1 — `ISymbolResolver`** (closes LB-BUG-002, LB-BUG-004, LB-FR-002, LB-FR-003)
  - `Lifeblood.Application.Ports.Right.ISymbolResolver` + single `SymbolResolutionResult` DTO. Routes every read-side MCP handler through one resolver. Resolution order: exact canonical → truncated method (lenient single-overload) → bare short name. Returns `Outcome` + `Candidates` + `Diagnostic` for ambiguous and not-found cases.
  - Partial-type unification is a **read model** on the resolution result, NOT a graph schema change. The graph stays raw; the resolver walks file→type Contains edges to discover all partial declaration files. The deterministic primary file path picker (filename match → prefix match → lexicographic) lives in `LifebloodSymbolResolver.ChoosePrimaryFilePath`.
  - `GraphBuilder` multi-parent fix — partial-type Contains edges were lost under last-write-wins; now every observed `ParentId` is tracked per symbol id and `Build()` synthesizes one Contains edge per unique parent. This is what makes `lookup AdaptiveBeatGrid` return all 160 partial files instead of one. Domain-layer change, no schema bump.
  - `FindReferencesOptions.IncludeDeclarations` — operation-level policy on `ICompilationHost.FindReferences`, NOT a side-effect of resolver merging. Two-overload signature preserves backward compat. When true, the result includes one synthetic `(declaration)` entry per partial declaration file via Roslyn's `ISymbol.Locations`.
  - New MCP tool `lifeblood_resolve_short_name`.
- **Seam #2 — Csproj-driven compilation facts as a documented convention** (closes LB-BUG-005)
  - `ModuleInfo.AllowUnsafeCode` field, parsed from `<AllowUnsafeBlocks>` by discovery, consumed via `WithAllowUnsafe` in `CreateCompilation`. Same shape as `BclOwnership`.
  - Convention documented as `INV-COMPFACT-001..003` in `CLAUDE.md`.
  - Closes Minis CS0227 false positives in DAWG (2 errors → 0).
- **Seam #3 — `RoslynSemanticView`** (closes LB-BUG-003)
  - Read-only typed accessor for the C# adapter's loaded semantic state (`Compilations`, `Graph`, `ModuleDependencies`). Constructed once per `GraphSession.Load` and shared by reference across consumers.
  - `RoslynCodeExecutor` primary constructor takes the view; the view IS the script-host globals object, passed via `CSharpScript.RunAsync<RoslynSemanticView>(...)`. Scripts reach the loaded state via top-level identifiers `Graph`, `Compilations`, `ModuleDependencies`.
  - Backward-compat secondary constructor takes only `compilations` for tests and standalone callers.

### Race-condition hotfix shipped mid-session
`RoslynCodeExecutor.Execute` redirects `Console.Out` globally. xUnit's default test-class parallelism would clobber concurrent `Execute()` callers. Added `[CollectionDefinition("ScriptExecutorSerial", DisableParallelization = true)]` in `RoslynSemanticViewTests.cs` and applied `[Collection("ScriptExecutorSerial")]` to `HardeningTests`, `WriteSideToolTests`, `RoslynSemanticViewTests`.

### CLAUDE.md gained 9 new invariants
- `INV-RESOLVER-001..004` (identifier resolution is a port; resolver accepts every input format; partial-type unification is a read model; primary file path is deterministic)
- `INV-COMPFACT-001..003` (csproj is authoritative; each fact lives as a typed `ModuleInfo` field; csproj edits invalidate cached facts via `CsprojTimestamps`)
- `INV-VIEW-001..003` (each adapter publishes a typed semantic view; tools consume the view; the view is constructed once and shared by reference)

### CHANGELOG entry under `[Unreleased]`
Three-seam framing documented with the full architectural reasoning, the bug closures table, and the graph delta evidence.

### Plan documents (in `.claude/plans/`, NOT committed)
- `bcl-ownership-fix.md` — v2 plan
- `post-bcl-fixes.md` — v4 plan with three-seam framing
- `next-session-handoff.md` — this file

The `.claude/` directory is NOT in `.gitignore` but is also NOT committed. Decide whether to commit it as design records.

---

## §2. Verification snapshot (DAWG, dist 13:25)

Final acceptance numbers from the previous session, against `D:/Projekti/DAWG`:

```
lifeblood_analyze projectPath=D:/Projekti/DAWG incremental=false
  → Loaded: 44566 symbols, 87233 edges, 75 modules, 0 violations

lifeblood_lookup type:Nebulae.BeatGrid.AdaptiveBeatGrid
  → filePath: AdaptiveBeatGrid.cs (deterministic primary, filename match)
  → filePaths: 160 partial declaration files (sorted lexicographically)

lifeblood_diagnose moduleName=Minis
  → 0 CS0227 errors (was 2); only unrelated CS1701 netstandard warning remains

lifeblood_execute "return Graph.Symbols.Count;"     → 44566
lifeblood_execute "return Compilations.Count;"      → 75

lifeblood_dependants method:Nebulae.BeatGrid.Audio.DSP.Voice.SetPatch
  (truncated, no parens)
  → 8 callers via resolver canonicalization (was [])

lifeblood_resolve_short_name MidiLearnManager
  → type:Nebulae.BeatGrid.MidiLearnManager + file path + kind

lifeblood_find_references type:Nebulae.BeatGrid.AdaptiveBeatGrid
  includeDeclarations=true
  → 199 results including ~165 (declaration) entries for every partial
```

Graph delta from session start to session end (DAWG, 75 modules):
- Symbols: pre-v2 ≈ 44418 → post-v4 final 44566 (+148)
- Edges:   pre-v2 ≈ 78126 → post-v4 final 87233 (+9107)

The +9107 edges come from two sources stacked:
1. **+8230** from the v2 BCL fix (clean semantic models in Unity modules → call-graph extraction stops returning null at every call site)
2. **+874** from the v4 multi-parent GraphBuilder fix (partial types now produce one Contains edge per partial file instead of one per type)

---

## §3. The reviewer's most-recent backlog table (carried over)

From the parallel reviewer session, after my fixes shipped to dist:

| Bug | Reviewer status | Reality after final dist (13:25) |
|---|---|---|
| **LB-BUG-001** (?.invoke variant) | ✅ FIXED | ✅ Confirmed. Closed transitively by v2 BCL fix because Roslyn's `GetSymbolInfo` walks `ConditionalAccessExpressionSyntax → InvocationExpression`. **Drop from Plan v5.** |
| **LB-BUG-002** (struct method dependants) | ✅ FIXED | ✅ Confirmed via Seam #1 resolver. Reviewer's earlier "still broken" report was a misdiagnosis — they used a truncated symbol id; the resolver now canonicalizes it. |
| **LB-BUG-003** (execute Workspace global) | ❌ Carry-over | 🔁 **Partial-correct, deliberate scope choice.** v4 exposes `Graph` / `Compilations` / `ModuleDependencies` on `RoslynSemanticView` (the script globals). The reviewer's literal `Workspace.CurrentSolution.Projects.Count()` test still fails because we deliberately didn't expose `Workspace` (per Plan v4 §8 out-of-scope). If they need it, add it as a fourth property on `RoslynSemanticView` — one-line change. **Verify with reviewer whether the documented `Graph` / `Compilations` surface is sufficient or if literal `Workspace` is needed.** |
| **LB-BUG-004** partial (`filePaths` array) | 🟡 API only | ✅ **Fully closed in the final 13:25 dist.** Returns 160 partial files for `AdaptiveBeatGrid` via the GraphBuilder multi-parent fix. Reviewer tested an earlier dist that had the API surface but only one entry. Re-verify with reviewer against the latest dist. |
| **LB-FR-002** (resolve_short_name) | ✅ SHIPPED | ✅ Tool registered and routed. |
| **LB-BUG-006** (incremental analyze) | ✅ FIXED | ✅ This is the v2 `CsprojTimestamps` work — `IncrementalAnalyze` now detects csproj edits and forces re-discovery. |

**Net: 5 bugs fully closed (after re-verifying #4 with reviewer), 1 deliberate scope choice (#3) pending alignment.**

---

## §4. Tasks for the next session

These are listed in priority order. The user asked for "checkup of existing work, hexagonal enforcement check, lifeblood-on-lifeblood self-audit, plan for remaining work with actual research."

### Task A — Verify shipped work end-to-end

1. Re-run the 8 DAWG verification queries from §2. Confirm numbers match.
2. Run `dotnet test tests/Lifeblood.Tests/Lifeblood.Tests.csproj` — must be 329/329 passing.
3. Open `D:\Projekti\DAWG\.mcp.json` and confirm Lifeblood is pointed at `D:/Projekti/Lifeblood/dist/Lifeblood.Server.Mcp.dll`.
4. If anything diverges, the dist may need a fresh `dotnet publish src/Lifeblood.Server.Mcp/Lifeblood.Server.Mcp.csproj -c Release -o dist`. Note that publishing requires killing any running Lifeblood MCP processes first (they hold the dist DLLs locked). Find them via `tasklist /FI "IMAGENAME eq dotnet.exe"` and kill the high-memory ones (typically 500MB+).

### Task B — Lifeblood self-audit ("dogfood")

This is the highest-leverage task and the one Matic explicitly asked for. The new `lifeblood_execute` tool with `RoslynSemanticView` globals lets you introspect Lifeblood's own architecture from inside Lifeblood itself.

1. **Analyze Lifeblood with Lifeblood:**
   ```
   lifeblood_analyze projectPath="D:/Projekti/Lifeblood" incremental=false
   ```
   Expected: ~10 modules (Domain, Application, Adapters.CSharp, Adapters.JsonGraph, Connectors.Mcp, Connectors.ContextPack, Analysis, Server.Mcp, ScriptHost, CLI).

2. **Hexagonal-architecture invariants — run via `lifeblood_analyze` with the hexagonal rule pack:**
   ```
   lifeblood_analyze projectPath="D:/Projekti/Lifeblood" rulesPath="hexagonal" incremental=false
   ```
   Expected: 0 violations. If violations appear, that's a real architectural finding to fix.

3. **Specific architecture probes via `lifeblood_execute`** (use the `Graph` global):
   - `Lifeblood.Domain` should have ZERO outgoing dependencies (it's the pure leaf).
     ```csharp
     return Graph.Symbols
       .Where(s => s.Kind == SymbolKind.Module && s.Name == "Lifeblood.Domain")
       .Select(m => Graph.GetOutgoingEdgeIndexes(m.Id).Length)
       .FirstOrDefault();
     ```
     Expected: 0 (ignoring Contains edges), or whatever the dependency count actually is. Investigate if non-zero.
   - `Lifeblood.Application` should depend ONLY on `Lifeblood.Domain`.
   - `Lifeblood.Adapters.*` should depend on Application + Domain, never on each other or on connectors.
   - `Lifeblood.Connectors.*` should depend on Application + Domain, never on adapters or on each other.
   - `Lifeblood.Server.Mcp` is the composition root and may depend on everything except other adapters' internals.

4. **Identifier resolution invariant — verify INV-RESOLVER-001 is enforced:** every read-side handler in `ToolHandler.cs` and `WriteToolHandler.cs` should call `_resolver.Resolve(...)` before any graph or workspace lookup. Run:
   ```
   lifeblood_find_references symbolId=method:Lifeblood.Application.Ports.Right.ISymbolResolver.Resolve(...)
   ```
   And manually verify the call sites cover every `Handle*` method that takes a `symbolId`.

5. **Compilation-facts invariant — verify INV-COMPFACT-002 is enforced:** every csproj-driven compilation option should appear as a typed field on `ModuleInfo`, parsed in `RoslynModuleDiscovery.ParseProject`, consumed in `ModuleCompilationBuilder.CreateCompilation`. Today there are two: `BclOwnership` and `AllowUnsafeCode`. Search the codebase for any `<AllowUnsafe`-style XML element parsing OUTSIDE `RoslynModuleDiscovery.ParseProject` — should find none.

6. **Adapter view invariant — verify INV-VIEW-002 is enforced:** `RoslynCodeExecutor` should be the only consumer of `RoslynSemanticView` today. Future consumers should be added by reference, never by re-threading raw `Compilations` / `Graph`. Run:
   ```
   lifeblood_find_references type:Lifeblood.Adapters.CSharp.RoslynSemanticView
   ```
   Expected: construction sites in `GraphSession.Load` and `LoadIncremental`, consumption site in `RoslynCodeExecutor` constructor and `Execute()`, plus tests. Anything else is suspicious.

7. **Look for hexagonal violations the rule pack might miss:**
   - Any `using Microsoft.CodeAnalysis*` in `Lifeblood.Domain` or `Lifeblood.Application` is a violation. Grep for it.
   - Any direct reference to `_compilations` or `compilation.X` outside the C# adapter is a violation.
   - Any `TextResult(...)` or MCP-shaped return outside `Lifeblood.Server.Mcp` is a violation.
   - Anywhere `string.Join(",", method.Parameters.Select(p => p.Type.ToDisplayString()))` appears outside `CanonicalSymbolFormat` is a violation of the canonical-format invariant — that consolidation is the whole point of `CanonicalSymbolFormat`.

8. **Check graph hotspots and tier classification on Lifeblood itself.** Use `lifeblood_context` to get the AI context pack. Look for:
   - Files with high in-degree (many dependants) — these are the architectural pillars.
   - Files with high out-degree (many dependencies) — these are coordination points.
   - Cycles, if any. There shouldn't be any.

9. **Report findings.** Anything you find that's a real hexagonal violation gets a Plan v5 entry. Anything that's a design tension worth discussing gets a note for Matic.

### Task C — Plan v5 for remaining work (with research)

Plan v5 covers everything that's NOT in v4 scope. Three known items + whatever Task B turns up.

#### Known v5 items

1. **Field read/write edges** (LB-FR or new bug — confirmed by reviewer)
   - `RoslynEdgeExtractor` doesn't currently emit any edges for field accesses. `SettingsPanelController._midiDiscovery` is "assigned and read from same partial" — neither produces a graph edge.
   - Fix shape: add a visitor for `IdentifierNameSyntax` whose resolved symbol is `IFieldSymbol`. Emit a `References` (or new `ReadsField` / `WritesField`) edge from the containing method to the field.
   - Research questions:
     - What's the current shape of edges around fields? Does `dependants` on a field return anything? (Probably not.)
     - Should this be a new `EdgeKind.AccessesField` or fold into existing `References`?
     - How does the Roslyn `IOperation` model distinguish reads vs writes? (`IFieldReferenceOperation` parent context: `IAssignmentOperation` left side = write, anywhere else = read.)
     - Does Lifeblood currently use Roslyn's `IOperation` API, or just syntax + `GetSymbolInfo`? Audit `RoslynEdgeExtractor` end-to-end.

2. **Interface dispatch propagation** (NEW finding from reviewer — but **partially closed**)
   - Reviewer report: calls to interface methods (e.g. `IMidiDiscoveryService.RetryNow`) only emit edges to the interface symbol, not to implementations (`MidiDiscoveryCoordinator.RetryNow`). So `find_references` / `dependants` on the implementation returns 0 even though the call site exists.
   - **Update from this session**: the reviewer confirmed `LB-BUG-001 (?.invoke variant) ✅ FIXED` — `ListDevices` cross-assembly via `?.invoke` now resolves at `Midi.cs:405 → MidiInputAdapter`. This means at least SOME interface dispatch already works after the v2 BCL fix. Verify whether the original "interface dispatch" complaint is now closed too, or only the `?.invoke` syntax variant.
   - Fix shape (if still needed): in `RoslynEdgeExtractor.ExtractCallEdge`, when `target` is an interface method, ALSO emit edges to every known implementor. The implementation index already exists via `RoslynCompilationHost.FindImplementations` — extract that walk into a shared helper and call it from the edge extractor too.
   - Research questions:
     - Run `find_references` on `MidiDiscoveryCoordinator.RetryNow` against DAWG. Does it find the call site through the interface? If yes, this whole item is closed.
     - If not, look at `RoslynEdgeExtractor.ExtractCallEdge`. The `target` is the interface method symbol. Walk implementations and emit additional edges.

3. **Null-conditional invocation `obj?.Method()`** (NEW finding from reviewer — but **closed**)
   - Reviewer's first report: ` voices?.SetPatch(patch)` shape misses edges.
   - Reviewer's latest report: `LB-BUG-001 (?.invoke variant) ✅ FIXED`. Closed transitively by v2 BCL fix.
   - **Drop from Plan v5.** Add a regression test instead — verify `obj?.Method()` produces the same call edge as `obj.Method()` against a synthetic compilation. Lives in `FindReferencesCrossModuleTests.cs`.

4. **`lifeblood_execute` literal `Workspace` global** (LB-BUG-003 reviewer carry-over)
   - Today exposes `Compilations` / `Graph` / `ModuleDependencies`. Reviewer's literal test was `Workspace.CurrentSolution.Projects.Count()` which still fails.
   - Decision: add a `Workspace` property to `RoslynSemanticView` (one-line addition, plus update `GraphSession.Load` to construct an `AdhocWorkspace` and pass it). OR document that `Compilations` is the canonical surface and `Workspace` is deliberately not exposed.
   - Recommend asking Matic which way before changing it.

5. **Other backlog items from `feedback_lifeblood_mandatory`** in DAWG memory (`C:\Users\Matic\.claude\projects\D--Projekti-DAWG\memory\project_lifeblood_improvement_backlog.md`):
   - LB-FR-003 separate `find_writes` / `find_reads` from `find_references` (depends on field-edge work above).
   - LB-FR-004 example scripts under `Lifeblood/examples/` for the documented INV-LIFEBLOOD-003 use cases (DSP invariants, architecture metrics, refactoring validation).
   - LB-NICE-001 `lifeblood_compile_check` should return diagnostic line numbers — verify whether already implemented.
   - LB-NICE-002 cache warm/cold indicator on `lifeblood_analyze` — easy add.

### Task D — Test suite verification

Run `dotnet test tests/Lifeblood.Tests/Lifeblood.Tests.csproj --nologo -v q`. Expected: **329 passed**. If any fail, do not start v5 work — fix the failing tests first.

The test suite uses xUnit collections to handle the `Console.Out` thread-unsafety in `RoslynCodeExecutor.Execute`. Three test classes are in the `ScriptExecutorSerial` collection: `HardeningTests`, `WriteSideToolTests`, `RoslynSemanticViewTests`. If you add a new test class that calls `executor.Execute(...)`, you MUST add `[Collection("ScriptExecutorSerial")]` to it.

### Task E — Decide whether to commit `.claude/plans/`

The previous session left `bcl-ownership-fix.md`, `post-bcl-fixes.md`, and `next-session-handoff.md` (this file) in `.claude/plans/`, untracked. They're valuable architectural records. Two options:
- (a) Commit them under `docs/` or `.claude/` so future contributors can read the design rationale.
- (b) Leave them as working docs and write a smaller `docs/architecture/RESOLVER.md` etc. that captures the durable parts.

Recommend (a) — the plans already contain the full reasoning and the regression test matrix. Move them to `docs/architecture/plans/` if `.claude/` feels wrong for a public repo.

---

## §5. Files and paths the next session will need

### Source files of interest
- **Domain layer:**
  - `src/Lifeblood.Domain/Graph/SemanticGraph.cs` — has the new short-name index and `FindByShortName` method.
  - `src/Lifeblood.Domain/Graph/GraphBuilder.cs` — has the multi-parent tracker and the modified `Build()` Contains synthesis.
- **Application port:**
  - `src/Lifeblood.Application/Ports/Left/IModuleDiscovery.cs` — has `BclOwnershipMode` enum and `ModuleInfo.AllowUnsafeCode`.
  - `src/Lifeblood.Application/Ports/Left/ICompilationHost.cs` — has the two `FindReferences` overloads + `FindReferencesOptions`.
  - `src/Lifeblood.Application/Ports/Right/ISymbolResolver.cs` — the resolver port + `SymbolResolutionResult` DTO + `ResolveOutcome` enum + `ShortNameMatch`.
- **C# adapter:**
  - `src/Lifeblood.Adapters.CSharp/RoslynModuleDiscovery.cs` — parses `<Reference>` for BCL ownership and `<AllowUnsafeBlocks>` for unsafe code.
  - `src/Lifeblood.Adapters.CSharp/Internal/ModuleCompilationBuilder.cs` — consumes both fields.
  - `src/Lifeblood.Adapters.CSharp/Internal/CanonicalSymbolFormat.cs` — single source of truth for parameter type display strings.
  - `src/Lifeblood.Adapters.CSharp/Internal/AnalysisSnapshot.cs` — has `CsprojTimestamps`.
  - `src/Lifeblood.Adapters.CSharp/Internal/RoslynWorkspaceManager.cs` — `FindInCompilation` rewritten with documented contract.
  - `src/Lifeblood.Adapters.CSharp/RoslynCompilationHost.cs` — `FindReferences` matches by canonical id; has the `IncludeDeclarations` overload.
  - `src/Lifeblood.Adapters.CSharp/RoslynCodeExecutor.cs` — takes `RoslynSemanticView`; passes view as script globals.
  - `src/Lifeblood.Adapters.CSharp/RoslynSemanticView.cs` — the typed read-only view POCO.
  - `src/Lifeblood.Adapters.CSharp/RoslynWorkspaceAnalyzer.cs` — populates `CsprojTimestamps`; `IncrementalAnalyze` checks csproj edits.
- **MCP server:**
  - `src/Lifeblood.Server.Mcp/GraphSession.cs` — constructs `RoslynSemanticView` once per load.
  - `src/Lifeblood.Server.Mcp/ToolHandler.cs` — every read-side handler routes through `_resolver`.
  - `src/Lifeblood.Server.Mcp/WriteToolHandler.cs` — every symbol-id-bearing write-side handler routes through `_resolver`.
  - `src/Lifeblood.Server.Mcp/ToolRegistry.cs` — has `lifeblood_resolve_short_name` registration.
- **Connector:**
  - `src/Lifeblood.Connectors.Mcp/LifebloodSymbolResolver.cs` — reference implementation of `ISymbolResolver` with the four-stage resolver and partial-type merge.

### Test files of interest
- `tests/Lifeblood.Tests/SymbolResolverTests.cs` — 9 resolver tests including the LB-BUG-002 truncated-id regression and the partial-type multi-parent regression.
- `tests/Lifeblood.Tests/RoslynSemanticViewTests.cs` — 4 script-globals tests in the `ScriptExecutorSerial` collection.
- `tests/Lifeblood.Tests/BclOwnershipCompilationTests.cs` — BCL ownership + AllowUnsafeCode end-to-end.
- `tests/Lifeblood.Tests/FindReferencesCrossModuleTests.cs` — 9 cross-module find_references regressions including the silent-fallback bug class.
- `tests/Lifeblood.Tests/HardeningTests.cs` — discovery tests for BCL ownership casing and AllowUnsafeBlocks casing.
- `tests/Lifeblood.Tests/IncrementalAnalyzeTests.cs` — csproj-edit invalidation tests.

### Plans (`.claude/plans/`, NOT committed)
- `bcl-ownership-fix.md` — v2 plan
- `post-bcl-fixes.md` — v4 plan with three-seam framing
- `next-session-handoff.md` — this file

### Documentation
- `CLAUDE.md` — has the new `INV-RESOLVER-001..004`, `INV-COMPFACT-001..003`, `INV-VIEW-001..003` invariants and the "Symbol ID Grammar (C# Adapter)" section.
- `CHANGELOG.md` — `[Unreleased]` section documents the three-seam framing.

### MCP wiring (DAWG-side, not Lifeblood-side)
- `D:\Projekti\DAWG\.mcp.json` — points at `D:/Projekti/Lifeblood/dist/Lifeblood.Server.Mcp.dll`.

---

## §6. Open questions for Matic

1. **Should `.claude/plans/*.md` be committed to the repo?** They're valuable architectural records. Recommend yes, possibly under `docs/architecture/`.
2. **Should `Workspace` (literal `AdhocWorkspace`) be added to `RoslynSemanticView`?** The reviewer's `Workspace.CurrentSolution.Projects.Count()` test still fails because we deliberately exposed `Compilations` instead. One-line addition if Matic wants it.
3. **Should v5 ship as a single commit or split per item?** Plan v4 was one commit covering both v2 BCL + v4 three-seam framing because they were tightly coupled. Plan v5 items are mostly independent — splitting would let each ship/revert independently.
4. **Should the next session create a release tag?** Matic explicitly said "no tag yet" when the previous session asked. Worth re-asking after the self-audit completes.

---

## §7. Suggested opening prompt for the next session

Paste this at the start of the next session:

> Hey Claude. I'm continuing the Lifeblood post-BCL refactor from a previous Opus 4.6 session.
>
> The previous session shipped commit `54c0b22` with the v2 BCL ownership fix + v4 three-seam framing (ISymbolResolver, AllowUnsafeCode compilation fact, RoslynSemanticView). Test suite is 329/329 passing. Dist is published at `D:/Projekti/Lifeblood/dist/`. No release tag yet.
>
> Before you start, please read `D:\Projekti\Lifeblood\.claude\plans\next-session-handoff.md` end-to-end. It has the full state, the verification snapshot, and the four prioritized tasks for this session (sanity check, lifeblood-self-audit, plan v5 with research, test verification).
>
> Your first job is **Task B — the Lifeblood self-audit**. Use the new `lifeblood_execute` tool with the `Graph` / `Compilations` / `ModuleDependencies` script globals to introspect Lifeblood's own architecture from inside Lifeblood itself. Look for hexagonal violations the rule pack might miss, identifier-resolution gaps, compilation-facts gaps, and any other architectural tightening opportunities.
>
> Then **Task C — Plan v5** for the remaining work (field edges, possibly interface dispatch propagation if not already closed, the literal `Workspace` global question). Research the current edge extractor end-to-end before proposing fixes. Same plan-first discipline as v4: research → seam framing → external review → execution.
>
> Same rules as before: NO hot patches, proper architectural fixes for eternity, plan before code, full test suite green between commits, and use lifeblood and grep to verify every claim. Do not commit `.claude/plans/*.md` unless I explicitly say so.

---

## §8. What the previous session learned (worth preserving)

These are insights worth carrying into the next session, ordered by leverage:

1. **Fixing the underlying semantic model fixes every consumer of that model simultaneously.** The v2 BCL fix transitively closed `find_references`, `dependants`, AND null-conditional invocation handling AND interface dispatch (partially) — all without changing those code paths. Future bug reports that look like "different code paths broken in the same way" should first verify the semantic model is healthy via `lifeblood_diagnose` before assuming the surfaces are independently broken.

2. **The reviewer's diagnoses are sometimes wrong about which layer is broken.** LB-BUG-002 was reported as "different code paths, fix wasn't applied" but the actual root cause was a truncated symbol id format mismatch. Always reproduce the bug yourself with the canonical input format before assuming the reviewer's framing is correct.

3. **Three-seam framing beats five-fix piecemeal framing.** v3 of the post-BCL plan listed five fixes with their own rollouts. v4 collapsed them into three seams (resolver / compfact / view) covering all five findings. The seam framing produces lasting architectural contracts (`INV-RESOLVER-001..004`, `INV-COMPFACT-001..003`, `INV-VIEW-001..003`) that prevent the same bug class from recurring.

4. **Schema changes are expensive — read models on the resolution result are cheap.** v3 proposed adding `Symbol.FilePaths[]` to the Domain layer. v4 kept the Domain layer untouched and put the merged read model on `SymbolResolutionResult` instead. The graph stays raw; the resolver computes the merge on read. Less risk, less coupling, same UX.

5. **`Console.Out` redirection is thread-unsafe by design.** The `RoslynCodeExecutor.Execute` comment already flagged it ("Safe only because MCP server is single-threaded"). xUnit's default test-class parallelism violates that assumption. The fix is `[Collection("ScriptExecutorSerial")]` on every test class that calls `Execute()`. Don't try to fix the global state — pin the test execution to be serial.

6. **External review catches things you'd miss.** The two reviewer corrections that mattered most for v4: (a) "don't add `Symbol.FilePaths[]` — keep partial unification on the resolution result"; (b) "include csproj-change invalidation in the same fix, not as a follow-up". Both came from a fresh Claude session reviewing the v3 plan. The v4 plan is better for it. **Get external review before shipping anything non-trivial.**

7. **Matic explicitly wants "proper architectural fixes for eternity, no hot patches."** The race-condition `Console.Out` fix is the closest thing to a hot patch in this PR — but even that uses xUnit's sanctioned `[CollectionDefinition]` mechanism, not a `Thread.Sleep` or a global mutex. When in doubt, write a plan, get review, then execute.

8. **The MCP harness auto-respawns Lifeblood server processes after kill.** Publishing the dist requires killing locked processes; the next tool call brings them back from the new dist binaries automatically. Plan publishes to land in a quiet moment and accept that the MCP tools will drop briefly.

9. **The DAWG `Nebulae.BeatGrid.Audio` and `Nebulae.BeatGrid.Audio.DSP` modules are the canary.** They had 29,523 errors before the v2 fix and 3 unused-field warnings after. They're the highest-impact validation surface for any future Lifeblood semantic-model fix. Always test against them.

10. **`Voice.SetPatch` is the canonical regression query.** Before fix: 0 references / 0 dependants. After: 18 references / 8 dependants. Use this query to verify any future Lifeblood change doesn't regress the cross-assembly struct method case.

---

End of handoff.

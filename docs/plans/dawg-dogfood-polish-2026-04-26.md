# Lifeblood Polish Plan — DAWG Dogfood Backlog Triage (2026-04-26)

**Source.** `D:/Projekti/DAWG/.claude/projects/D--Projekti-DAWG/memory/project_lifeblood_improvement_backlog.md` — 1242 lines of hands-on findings spanning 2026-04-10 → 2026-04-26 across ~10 DAWG sessions (DSP audits, ABG extraction waves, dead-code passes, master unification).

**Repo state.** v0.6.5 shipped. CHANGELOG closed BUG-004/005/006 (interface dispatch, member access granularity, null-conditional property) + ctor `Calls` edge + field-initializer containing method + property accessor context + ctor resolver regression. Inbox roadmap = LB-INBOX-001..006 (truth envelope → close DEADCODE → contract freeze → wedge → public proof → smoke script).

**Goal.** Translate the DAWG backlog into a sized, ordered work plan that lands in the Lifeblood repo as proper hexagonal features (port → adapter → handler) with tests. Drop noise. Keep value.

---

## 1. Triage — what's already shipped vs still open

| DAWG ID | Subject | Status in v0.6.5 |
|---------|---------|------------------|
| LB-FR-001 | resolve_short_name | **DONE** (heavy use, in production) |
| LB-BUG-004 (orig) | dead_code interface dispatch | **DONE v0.6.4** (Implements edges) |
| LB-BUG-005 (orig) | dead_code field.property chains | **DONE v0.6.4** (member-access granularity) |
| LB-BUG-006 (orig) | dead_code null-conditional `?.` | **DONE v0.6.4** (MemberBindingExpressionSyntax) |
| LB-INBOX-002 | INV-DEADCODE-001 close-out | **DONE v0.6.5** (3 more gaps + ctor regression) |
| LB-BUG-001 | struct method via array indexer | **OPEN** — re-verify against v0.6.5 walker (member-access fix may have closed it) |
| LB-BUG-010 | implicit interface-impl in find_references | **OPEN** — same family as BUG-001; needs re-verify post v0.6.4 Implements edges |
| LB-BUG-002 | `method:` rejects properties | **LIKELY OPEN** — resolver dispatches by prefix, not kind-aware |
| LB-BUG-009 | `[RuntimeInitializeOnLoadMethod]` entrypoints | **OPEN** — Unity attribute roster missing |
| LB-FP-001 | MonoBehaviour msg dispatch (Awake/Update/…) | **OPEN** |
| LB-FP-002 | UnityEvent / Button.onClick YAML bindings | **OPEN** |
| LB-BUG-014 | `execute` cannot resolve Unity asmdef DLLs | **OPEN** — confirmed reading `RoslynCodeExecutor.cs`: only host BCL + in-memory compilations referenced, no probe of `Library/Bee/artifacts/` |
| LB-BUG-015 | `compile_check` requires `code` even with filePath | **OPEN** — confirmed: `WriteToolHandler.HandleCompileCheck` errors `"code is required"` and tool schema declares `required: ["code"]` |
| LB-BUG-016 | `diagnose` ignores `filePath`, returns 311k dump | **OPEN** — confirmed: schema doesn't even declare `filePath` parameter; `HandleDiagnose` only reads `moduleName`. Caller's `filePath` is silently dropped → falls back to whole-project dump |
| LB-OBS-004 | no staleness signal on read-side responses | **OPEN** — graph age never returned |
| LB-FR-021 | new-type invisible after incremental analyze | **OPEN** — index keying suspected on `.cs.meta` GUID |
| LB-FR-018 | authority report (host iface count + members) | **OPEN** — high leverage for ABG endgame |
| LB-FR-020 | forwarder detection | **OPEN** — automates "thin-wrapper" classification |
| LB-FR-014 | vestigial port detection | **OPEN** — superset of FR-018 |
| LB-FR-015 | enum identity drift | **OPEN** |
| LB-FR-016 | dead_code confidence tiers | **OPEN** — overlaps LB-INBOX-001 (truth envelope) |
| LB-FR-006 / FR-011 | benchmark harness helper | **OPEN** — opinionated wrapper around execute |
| LB-FR-012 / BUG-007 | target-runtime capability drift | **OPEN** — execute uses host .NET, ships compile breaks |
| LB-NICE-003 | analyze incremental misses .asmdef edits | **OPEN** — three sessions reporting; promote to BUG |
| LB-NICE-005 | blast_radius oversize → summarize mode | **OPEN** |
| LB-NICE-007 | cycle detail tool | **OPEN** |
| LB-FR-013 | `Graph.Help()` sandbox introspection | **OPEN** — easy win |
| LB-BUG-006 (dup) / BUG-008 | Edge.Kind enum requires ToString | **OPEN** — sandbox ergonomics |

**Drop / archive (already in production or addressed):** LB-FR-001 (DONE), LB-BUG-004/005/006 originals (DONE v0.6.4), LB-WIN-001..009 (positive observations, no action).

---

## 2. Themes — collapse 25 open items into 5 work streams

The open items cluster cleanly:

| Stream | DAWG IDs | Architectural shape |
|--------|----------|---------------------|
| **A. Tool-handler UX papercuts** | BUG-015, BUG-016, OBS-004, NICE-005 | Schema + handler edits in `WriteToolHandler.cs` / `ToolHandler.cs`. No port changes. ≤ 200 LOC each. |
| **B. Unity-aware reachability** | BUG-009, FP-001, FP-002, NICE-003 (asmdef) | New `IUnityReachabilityProvider` port + adapter. Feeds `IDeadCodeAnalyzer` an additional liveness signal. |
| **C. Execute robustness** | BUG-014, BUG-007/FR-012, FR-013, BUG-008 (Edge.Kind), FR-006/FR-011 | `RoslynCodeExecutor` adapter changes: probe path resolution, target-profile awareness, sandbox introspection helpers. New `ICodeExecutionProfile` port. |
| **D. Authority / forwarder analysis** | FR-014, FR-018, FR-020, FR-015 | New analyzers in `Lifeblood.Analysis` + new ports in `Application.Ports.Right`. Feeds the C# / Unity wedge (LB-INBOX-004). |
| **E. Resolver completeness** | BUG-001 (re-verify), BUG-002, BUG-010, FR-021 | Walker + resolver edits in `Lifeblood.Adapters.CSharp`. Same family as v0.6.4/v0.6.5 closures. |

LB-INBOX-001 (truth envelope) is **prerequisite** for Stream A's OBS-004 (staleness goes in the envelope) and Stream D (confidence tier per result). Sequencing reflects this.

---

## 3. Sequenced rollout

Five-phase ladder. Each phase is one tagged release. No phase ships without tests + CHANGELOG entry + relevant invariant updates.

### Phase P1 — v0.6.6: Re-verification + UX papercuts (1–2 days)

**Why first.** Three of the bugs may already be partially closed by v0.6.4/v0.6.5 walker changes; re-verify before reopening surface area. The UX papercuts are independent and trivial.

**Work:**

1. **Re-verify against v0.6.5:**
   - LB-BUG-001 — write a regression test for `voices[i].SetPatch(patch)` on partial struct. If it now passes, close. If not, fix the `IInvocationOperation` walker to resolve `IArrayElementReferenceOperation` receivers (5–10 LOC in `RoslynEdgeExtractor.ExtractInvocationEdge`).
   - LB-BUG-010 — write a regression test for implicit interface-impl `find_references`. The v0.6.4 `Implements` edge work most likely already covers this from the dead-code direction; check that `find_references` traverses Implements when the symbol is on the concrete type. Likely 10–20 LOC in `LifebloodSymbolResolver` (canonicalize concrete-impl IDs to the interface declaration when looking up call sites) **or** in the live walker.
   - LB-BUG-002 — `method:` should accept properties (or add `member:` umbrella). Add to `LifebloodSymbolResolver.Resolve`: when prefix is `method:` and short-name lookup matches a property, return the property symbol. Update CLAUDE.md grammar doc accordingly.

2. **LB-BUG-015** — make `code` optional in `compile_check` when `filePath` is supplied. Read file from disk via `IFileSystem` port. Update tool schema to `oneOf({required: ["code"]}, {required: ["filePath"]})`. ~30 LOC in `WriteToolHandler.HandleCompileCheck`.

3. **LB-BUG-016** — add `filePath` parameter to `lifeblood_diagnose`. When provided, scope diagnostics to that file's compilation unit. Add `scope: "file" | "project"` enum. Default to `file` when `filePath` set. ~50 LOC + schema update.

4. **LB-NICE-005** — add `summarize: boolean` and `maxResults: int` to `lifeblood_blast_radius`. When summarize=true, return `{ count, directDependants, transitiveCount, top: [first-N] }`. Prevents 84KB ThreadGuard overflow. ~40 LOC.

5. **LB-FR-010** — combine direct-dependants and transitive count in `blast_radius` response. Add `directDependants: N` field. Doc tweak distinguishing dependants vs blast_radius. Trivial.

**Tests.** One regression test per item under `tests/Lifeblood.Tests/`. Synthetic fixtures for re-verify items.

**CHANGELOG.** v0.6.6 entry under each fix with DAWG-ID cross-reference.

---

### Phase P2 — v0.6.7: Truth envelope foundation (LB-INBOX-001) (2–3 days)

**Why second.** Phase 3 of P1's blast_radius work and every later phase wants a uniform `{ truthTier, confidence, staleness, evidenceSource, limitations }` envelope. Lay it once.

**Work:**

1. Add `Lifeblood.Domain.Results.ResponseEnvelope` record (or attach as `meta` field on existing result types).
2. Add `IResponseDecorator` port in Application that every read-side handler routes through after producing its raw result.
3. Implement adapter `LifebloodResponseDecorator` in Connectors.Mcp that fills in the envelope based on tool kind.
4. Wire all 12 read-side tool handlers to emit envelope. Default `truthTier`/`confidence` per tool (most are `semantic`/`proven`; `dead_code` is `derived`/`advisory`; `find_references` carries `staleness` indicating analyze age).
5. Add response-shape golden tests across all 22 tools.
6. **LB-OBS-004 lands here for free** — staleness is the envelope's `staleness` field. Server compares `Graph.AnalyzedAt` vs file mtimes, returns `secondsOld` and optional `filesChangedSinceAnalyze`.

**Tests.** Golden tests across all read-side tool handlers. Negative test: new tool added without envelope fails to compile (use marker interface).

**CLAUDE.md.** Add `INV-ENVELOPE-001`: every read-side tool response carries the envelope.

---

### Phase P3 — v0.6.8: Unity-aware reachability (Stream B) (3–5 days)

**Why third.** Lifeblood's biggest external use case is large Unity codebases (per LB-INBOX-004). Five DAWG sessions report Unity-specific FPs as the top friction. Fixing this unlocks accurate `dead_code` for Unity workspaces — which is the highest-leverage signal Lifeblood produces for hexagonal-arch refactor sessions.

**Work:**

1. **New port:** `Lifeblood.Application.Ports.Right.IUnityReachabilityProvider` returning `bool IsAttributeEntrypoint(Symbol)` + `bool IsMonoBehaviourMessage(Symbol)` + `bool IsYamlBound(Symbol)`.
2. **Adapter:** `Lifeblood.Adapters.CSharp.UnityReachabilityAdapter`:
   - Hardcoded entrypoint-attribute roster: `RuntimeInitializeOnLoadMethod`, `InitializeOnLoadMethod`, `MenuItem`, `ContextMenu`, `PostProcessBuild`, `PostProcessScene`, `CustomEditor`.
   - MonoBehaviour magic-method roster: `Awake`, `Start`, `OnEnable`, `OnDisable`, `OnDestroy`, `Update`, `FixedUpdate`, `LateUpdate`, `OnApplicationPause`, `OnApplicationFocus`, `OnApplicationQuit`, `OnGUI`, `OnDrawGizmos`, `OnDrawGizmosSelected`, `OnValidate`, `Reset`, `OnTriggerEnter`, etc. (full Unity list).
   - YAML scanner: opt-in via analyze option `scanUnityYaml: true`. Parses `*.prefab`, `*.unity`, `*.asset` for `m_PersistentCalls.m_Calls[].m_MethodName + m_TargetAssemblyTypeName`. Emits synthetic reachability edges into the symbol graph.
3. **Wire into `LifebloodDeadCodeAnalyzer`:** when port is registered, exclude symbols flagged by any of the three checks. Reason field reflects which check fired (so callers see "alive via [RuntimeInitializeOnLoadMethod]" not "dead").
4. **Promote LB-NICE-003 to BUG.** Add `*.asmdef` to `IncrementalAnalyzer`'s watched-file set. When `.asmdef` content changes, fall back to full re-analyze for that round. ~30 LOC.

**Tests.** Synthetic Unity-shaped fixture (MonoBehaviour subclass + RuntimeInitializeOnLoadMethod method + UnityEvent-bound method in YAML asset) → dead_code returns 0 results when port registered, returns 3 when not.

**CLAUDE.md.** Update `INV-DEADCODE-001` to remove "Unity reflection-based dispatch" from remaining FP list. Add `INV-UNITY-001` documenting the reachability port contract.

---

### Phase P4 — v0.6.9: Execute robustness (Stream C) (3–5 days)

**Why fourth.** `lifeblood_execute` is load-bearing for DSP work (LB-WIN-001, WIN-002, WIN-006, WIN-007 — four wins across four sessions). Three bugs here block runtime verification. Fixes make execute trustworthy for DAWG-scale projects.

**Work:**

1. **LB-BUG-014** — Unity asmdef DLL probe paths.
   - Add `IRuntimeAssemblyResolver` port returning probe directories for the loaded workspace.
   - `RoslynCodeExecutor` calls into the resolver; adapter scans `<projectPath>/Library/Bee/artifacts/**/*.dll` (Unity 2022+) and `<projectPath>/Library/ScriptAssemblies/*.dll` (Unity legacy / domain-reload artifacts) and adds matching DLLs as `MetadataReference`.
   - Subscribe to `AssemblyLoadContext.Resolving` so script types loaded reflectively (`Type.GetType("Nebulae.…")`) also resolve.
   - Document explicitly: requires at least one Unity build before `execute` can use Unity types. The probe surfaces a friendly diagnostic when the artifact dir is empty (`"No Unity build artifacts found at Library/Bee/artifacts. Run a Unity build first."`).

2. **LB-BUG-007 / LB-FR-012** — target runtime capability flag.
   - Add `targetProfile: "host" | "net-standard-2.1" | "net-6.0"` to `lifeblood_execute` schema.
   - When non-default, `RoslynCodeExecutor` swaps `HostBclReferences` for the matching reference assembly pack.
   - Pre-flight diagnostic: walk script syntax, flag any `MathF.Log2`, `float.IsFinite`, etc. that aren't in target profile **before** running. Returns `targetRuntimeWarnings: [{ api, missingIn }]`.
   - Default `targetProfile` auto-detected from project's csproj `<TargetFramework>` if loaded.

3. **LB-FR-013** — `Graph.Help()` sandbox introspection.
   - Add a `GraphHelp` property on `RoslynSemanticView` that returns a documented string ("Available enum values: EdgeKind = [Calls, References, Contains, Implements, Inherits, …]; Symbol properties: Id, Name, Kind, FilePath, Line, Visibility, Properties; Edge properties: SourceId, TargetId, Kind, Evidence; Common queries: …").
   - Optional: add `Graph.EdgesOfKind(string)` and `Graph.SymbolsOfKind(string)` string-accepting helpers (closes LB-BUG-008).

4. **LB-FR-006 / LB-FR-011** — microbench template.
   - **Defer to v0.7.x.** Useful but additive; can ship as a `.csx` example under `Lifeblood/examples/` first, promote to a tool only if external usage demands it. Track in IMPROVEMENT_INBOX, don't block this phase.

**Tests.** Round-trip test: load a real Unity project sample (use one of the existing GoldenRepos or add a thin Unity-shaped fixture), call `execute` with `using SomeUnityType;`, expect success. Target-profile test: script using `MathF.Log2` against `net-standard-2.1` profile fails with structured `targetRuntimeWarnings` not raw CS error.

**CLAUDE.md.** `INV-EXECUTE-001` documenting probe-path discovery contract. Update `INV-VIEW-003` (script-globals) to mention `GraphHelp`.

---

### Phase P5 — v0.7.0: Authority / forwarder analysis (Stream D) (4–6 days)

**Why fifth.** This is the LB-INBOX-004 wedge ("large C# workspace playbook"). FR-018 (authority report) and FR-020 (forwarder detection) directly automate the manual triage work that ate ~30 min per stage of every ABG extraction session. Ships best after envelope (P2), Unity reachability (P3), and execute robustness (P4) so the analyzers can cite confidence tiers, exclude Unity FPs, and be queried interactively.

**Work:**

1. **LB-FR-018 — `IAuthorityReporter` port:**
   ```csharp
   AuthorityReport Analyze(SemanticGraph graph, string typeId);
   ```
   Returns `{ implementedInterfaces: int, ownedPublicSurface: int, perInterfaceMembers: [{ iface, count, consumers }], forwarderRatio: double }`.
   - Adapter walks `Implements` edges from the type, then `Contains` edges on each interface, counts consumers per interface via incoming `Calls` edges.
   - Tool: `lifeblood_authority_report symbolId=type:…`.

2. **LB-FR-020 — forwarder detection.**
   - Add `MethodClassification` enum: `PureForwarder` / `ThinWrapper` / `RealLogic`.
   - `RoslynEdgeExtractor` already walks method bodies; emit `Symbol.Properties["classification"] = …` based on body-shape heuristic:
     - 1 statement, expression-bodied, target = single `IInvocationOperation` on field/property → PureForwarder
     - ≤ 5 statements, contains 1 invocation + null-guard or simple cast → ThinWrapper
     - else → RealLogic
   - Filter exposed via `lifeblood_dead_code(onlyClassification: "PureForwarder")` and `lifeblood_authority_report` (forwarderRatio).

3. **LB-FR-014 — `lifeblood_port_health symbolId=type:IFoo`:**
   - Walks members, calls `find_references` on each, returns `{ memberCount, liveMembers, deadMembers, livenessPct, verdict, live[], dead[] }`.
   - Verdict: ≥ 75% live → "healthy"; 25–75% → "mixed"; < 25% → "vestigial".
   - Thin tool — composes existing `find_references` + `Contains` walk. Could be a derived analyzer rather than new port.

4. **LB-FR-015 — enum identity drift.**
   - `lifeblood_enum_drift enum1=type:A enum2=type:B` returns shared names with mismatched ordinal values.
   - Pure read-side analyzer over `Symbol.Properties["enumValue"]` (extractor must emit this; check current state — likely not emitted yet, so adds a small extractor edit).

5. **LB-NICE-007 — cycle detail tool.**
   - `lifeblood_cycles` lists the SCC participants from the existing `CircularDependencyDetector`. Already computed; just expose.

**Tests.** Authority report against synthetic interface with N members and known consumer set. Forwarder classification against known-shape methods. Port health on a vestigial-by-construction interface.

**CLAUDE.md.** `INV-AUTHORITY-001` (authority-report contract). `INV-FORWARDER-001` (classification heuristic). Update `INV-DEADCODE-001` to mention the `onlyClassification` filter.

---

### Phase P6 — Resolver completeness mop-up (Stream E remainder)

After P5, sweep any remaining items from Stream E that re-verification in P1 didn't close.

- **LB-FR-021** — new-type visibility after incremental. Likely root cause: index keys on `.cs.meta` GUID. Fix: key on file path for discovery, GUID for identity. Or: warn in analyze response when new `.cs` files have no `.meta` siblings.
- **LB-OBSERVATION-003** — split `changedFileCount` into `changedSourceFiles` + `touchedGraphFiles`. One-line rename + telemetry split.

---

## 4. Out-of-scope / archive

- **LB-FR-007** (graph-delta queries between commits) — interesting but adds git dependency to a workspace tool. Defer until external demand surfaces.
- **LB-FR-009** (simulate_move) — high value but high complexity (in-memory project graph mutation + recompile). Park as v0.7.x candidate.
- **LB-FR-006/011** (microbench harness) — ship as example .csx first, promote later.
- **LB-NICE-002** (cache warm/cold indicator) — covered by P2 envelope's `staleness` field.
- **LB-NICE-001** (compile_check diagnostic line numbers) — already returned per `CompileCheckResult`; verify and close.
- **LB-NICE-006** (CSE pre-check heuristic) — interesting; not on critical path.
- **LB-OBSERVATION-001** (dependencies(type:) returns 0 outbound type-level edges) — document semantics in tool description; either populate aggregated edges or doc-only.

---

## 5. Process invariants

Per the user's "eternal hexagonal-architecture directive" (DAWG backlog line 11):

- Every fix lands as port (Domain/Application) + adapter (Adapters/Connectors) + handler (Server.Mcp). No direct handler→Roslyn calls without a port.
- No patches, no workarounds, no special cases. If a fix needs a per-tool flag, the flag is the port surface, not a handler-local conditional.
- Each phase ships with: tests (regression + synthetic fixture), CHANGELOG entry, CLAUDE.md invariant where structural, IMPROVEMENT_INBOX delete-or-update.
- Re-run `lifeblood_invariant_check mode:audit` after each phase. Zero parse warnings, zero duplicates.
- Self-analyze drift watch: `LiveSelfAnalyzeDriftTests` should stay green across each phase.

## 6. Sizing summary

| Phase | Tag | Days | LOC | Tests added |
|-------|-----|------|-----|-------------|
| P1 | v0.6.6 | 1–2 | ~300 | 5–8 |
| P2 | v0.6.7 | 2–3 | ~400 | ~15 (golden across all read-side) |
| P3 | v0.6.8 | 3–5 | ~600 | 8–12 |
| P4 | v0.6.9 | 3–5 | ~500 | 6–10 |
| P5 | v0.7.0 | 4–6 | ~800 | 15–20 |
| P6 | v0.7.x | 1–2 | ~150 | 3–5 |
| **Total** | — | **14–23** | **~2,750** | **~60** |

Each phase is independently shippable. P1 and P2 are prerequisites; P3/P4 are parallel-able if there's a second contributor; P5 wants P3+P4 landed first.

---

## 7. First action

If approving this plan: start P1 by writing the three re-verification tests against v0.6.5 and reading the verdicts. Half the open Stream E items may already be closed — pruning the backlog before opening new surface area is the cheapest possible win.

If not approving: the smallest standalone valuable ship is **P1 alone** (UX papercuts + re-verification, 1–2 days, no architectural commitment).

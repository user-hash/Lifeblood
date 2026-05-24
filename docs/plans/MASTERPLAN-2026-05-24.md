# Lifeblood Masterplan 2026-05-24

**Author:** synthesized 2026-05-24 from DAWG-side memory + Lifeblood `docs/IMPROVEMENT_INBOX.md` + `docs/STATUS.md` + `CHANGELOG.md` (v0.7.5 → v0.7.8 + [Unreleased]) + Lifeblood `docs/plans/*` + DAWG `reference_lifeblood_known_limitations.md` (refreshed 2026-05-24).
**Revision:** v2 2026-05-24 (post-reviewer-pass — applies fixes to 8 findings on the v1 plan).
**Baseline:** Lifeblood v0.7.8 (`docs/STATUS.md` reports 29 MCP tools / 27 ports / 1098 tests / **invariant count ~119–122, re-verify on Lifeblood-self analyze before use as sign-off gate — STATUS.md anchor says 122, one live probe reported 119**; self-analyze 3506 sym / 21113 edges / 0 violations / 0 cycles).
**DAWG dogfood reference:** 2026-05-24 full analyze 67068 sym / 247350 edges / 90 modules / 97 cycles / 0 violations.

> **v2 reviewer-pass changes (read this section before the rest if you read v1):**
> - **Wave 1**: added controlled-changed-file repro for L-LIM-002 (Atom 1.1 was a no-op repeat, too weak).
> - **Wave 2**: rewritten — `DefineConstants` permutations alone do NOT close L-LIM-001 because Unity-generated csprojs contain UNITY_ANDROID + UNITY_EDITOR in the same list. Plan now synthesizes Player profiles from asmdef `includePlatforms`/`excludePlatforms`/`versionDefines` + Unity's platform-symbol matrix.
> - **Wave 3**: dropped invented `ITestImpactAnalyzer` port (current code is `public static class TestImpactAnalyzer` in `Lifeblood.Analysis`). Now extends existing `TestClassImpact` record without breaking callers. Tightened heuristic — FQN-first, `nameof(T)` via operation tree, short-name only with namespace context or uniqueness.
> - **Wave 4**: moved `GetAssignmentCoverage` onto `ICompilationHost` (Left) to mirror `GetStaticTables` placement. Confidence tier split: direct same-method = Proven, interprocedural/aliased = Advisory with `limitations[]`.
> - **Wave 5**: dropped `IVersionedToolRegistry` port (contradicted "no new ports"). Replay-compatibility infrastructure ships without a new port.
> - **Wave 1 atom 1.5 added**: settle invariant count (STATUS says 122, live probe said 119 — pick a number with evidence before any sign-off gate quotes it).

This masterplan supersedes the per-area tracking by giving one coherent wave sequence across:
1. DAWG-side `L-LIM-001..006` (known limitations callers compensate for).
2. Lifeblood-side `LB-INBOX-003..005` (open phases from the post-v0.6.3 five-phase tightening plan).
3. Re-probe + closure of items shipped since DAWG memory last refreshed.

The plan is hexagonal in spirit per [`Lifeblood/CLAUDE.md`](../../CLAUDE.md): every new capability lands as Domain DTOs + Application port + Adapter + Connector handler. Never inline. Never patch-shaped.

---

## 0. Cross-validation matrix — DAWG ↔ Lifeblood

The 2026-05-24 baseline refresh revealed three items that shipped between the DAWG-side memory write date (2026-05-20) and now. Status reconciled per source.

| DAWG-side ID | Lifeblood-side INV / INBOX | Shipped in | Status |
|---|---|---|---|
| **L-LIM-001** (preprocessor blindness) | — *(not in Lifeblood inbox; structurally hard)* | NOT shipped | **OPEN** — Wave 2 |
| **L-LIM-002** (incremental edge drop) | `INV-ANALYZE-FALLBACK-001` (v0.7.8 caller-owned policy) | v0.7.6+ | **NEEDS RE-PROBE** — Wave 1 |
| **L-LIM-003** (authority stale post-incremental) | `INV-DIAGNOSE-FRESHNESS-001` (`analysisGeneration` counter) + composite-aware `authority_report` | v0.7.8 | **NEEDS RE-PROBE** — Wave 1 |
| **L-LIM-004** (`lifeblood_execute` IL2CPP DLL injection + missing BCL refs) | `INV-EXECUTE-001` upgrade + `ScriptReferenceSetBuilder` + `targetProfile` honesty | **v0.7.8** | **PROBABLY CLOSED** — confirm under DAWG repro — Wave 1 |
| **L-LIM-005** (`test_impact` undercounts reflection tests) | — *(documented limitation; not yet addressed)* | NOT shipped | **OPEN** — Wave 3 |
| **L-LIM-006** (`static_tables` cannot inspect closure-body assignments) | — *(out-of-scope for `static_tables` by design)* | NOT shipped | **OPEN** — Wave 4 (new tool `lifeblood_assignment_coverage`) |

| Lifeblood roadmap ID | Status as of v0.7.8 | Plan placement |
|---|---|---|
| `LB-INBOX-001` truth envelope | SHIPPED (v0.6.5 `INV-ENVELOPE-001`) | closed |
| `LB-INBOX-002` `INV-DEADCODE-001` closure + Unity reachability | SHIPPED (v0.6.7 `INV-UNITY-001`, v0.7.0 `LB-FP-003`) | closed |
| `LB-INBOX-003` contract freeze | **PARTIAL** — `INV-WIRE-CONTRACT-001` + `SCHEMA_DEPRECATION_POLICY.md` shipped (v0.7.6 W4); per-tool `schemas/tools/v1/*.json` snapshot files + replay-compatibility tests + invariant version-bind assertions OPEN | Wave 5 |
| `LB-INBOX-004` C# / Roslyn / Unity wedge | **PARTIAL** — `PLAYBOOK_CSHARP.md` + first case study shipped (v0.7.6 W5); sub-second incremental loop polish + more benchmarked demos OPEN | Wave 6 |
| `LB-INBOX-005` public proof | **PARTIAL** — first case study shipped; 2–4 more case studies + comparison demos vs generic MCP + trust dashboard OPEN | Wave 6 |
| `LB-INBOX-006` smoke-mcp-analyze auto-discovery | SHIPPED ([Unreleased] Wave W6) | closed |
| `LB-INBOX-007` `lifeblood_diagnose` Math-namespace collision | SHIPPED (v0.7.4 `INV-MODULE-REFS-001`) | closed |
| `LB-INBOX-008` per-diagnostic preprocessor scope | SHIPPED ([Unreleased] `INV-DIAGNOSTIC-ENVELOPE-DEFINES-001`) | closed |
| `LB-INBOX-009` enum-member reference queries | SHIPPED ([Unreleased] `INV-ENUM-COVERAGE-001` + v0.7.8 dispatch-table extension) | closed |
| `LB-INBOX-010` target-typed `new(MethodGroup)` + generic canonical-id drift | SHIPPED (v0.7.6 `INV-EXTRACT-METHOD-GROUP-CANDIDATE-001` + `INV-EXTRACT-METHOD-ORIGINAL-DEFINITION-001`) | closed |
| `LB-INBOX-011` static-table → graph-edge synthesis | SHIPPED (v0.7.6 — turned out to fold into the same fix as LB-INBOX-010) | closed |

**Wave priority is driven by DAWG's eternal architecture need, not Lifeblood-internal sequencing.** Each wave declares its DAWG-side trigger (the dogfood pain that motivates it) and its Lifeblood-side closure (the INV pin that prevents regression).

---

## 1. Triage table — every open item, sized

Sized by *Lifeblood implementation effort*, not DAWG-side adoption effort. Adoption is Wave 7. Effort is in working sessions (one session ≈ 4–6 focused hours), with risk band.

| Item | Wave | Effort | Risk | DAWG trigger | Lifeblood port / tool (v2 revised) |
|---|---|---|---|---|---|
| Re-probe L-LIM-002 (no-change AND changed-file repros) | 1 | 0.75 | Low | refresh known-limitations tracking | (no code) |
| Re-probe L-LIM-003 authority stale | 1 | 0.5 | Low | same | (no code) |
| Confirm L-LIM-004 closed under DAWG IL2CPP | 1 | 0.5 | Low | unblock `lifeblood_execute` for Bindings-coverage authoring | (no code; verify shipped fix) |
| Settle invariant-count baseline (Atom 1.5 — new in v2) | 1 | 0.25 | Low | sign-off gate integrity | (no code; reconcile STATUS.md vs live audit) |
| Multi-define-set semantic queries (L-LIM-001) — Player profile synthesis | 2 | **7–10** (revised up from 5–8) | **High** | mobile/iOS/WebGL #if-guarded callsites invisible to `find_references` / `dependants` / `dead_code` | `IConditionalCompilationProvider` (new port, Left) + `IPlayerDefineProfileSynthesizer` (new port, Left, Unity-only) + `UnityPlayerDefineProfileSynthesizer` adapter + `PlayerProfileMatrix` data + edge-union pipeline. **Port count +2 (27 → 29).** |
| Reflection-aware `test_impact` (L-LIM-005) | 3 | 2–3 | Medium | ratchet test suite invisible to `test_impact` | **Additive extension of EXISTING `public static class TestImpactAnalyzer`** + opt-in `TestImpactOptions { IncludeReflectionHeuristic }` + `Kind` field on existing `TestClassImpact` record. **No new port.** |
| Closure / Bindings assignment coverage (L-LIM-006) | 4 | 4–5 | Medium | DAWG `BindingsClosureCoverageRatchetTests` blocker (Polish-1 P4) | **Extend EXISTING `ICompilationHost` (Left port) with `GetAssignmentCoverage`** + `RoslynAssignmentCoverageExtractor` adapter + new tool `lifeblood_assignment_coverage`. **No new port. Tool count 29 → 30.** |
| Contract-freeze completion (LB-INBOX-003) | 5 | 4–6 | Low | external integrator confidence | per-tool `schemas/tools/v1/*.json` + `ReplayCompatibilityTests` + additive nullable fields on existing `ToolDefinition`. **No new port** (corrected from v1). |
| Public-proof polish (LB-INBOX-004 + LB-INBOX-005) | 6 | 4–8 | Low | external credibility | doc-side only |
| DAWG-side integration | 7 | 1–2 | Low | DAWG mandatory-tool table accuracy | (DAWG repo only) |

**Total estimated effort:** ~22–32 working sessions (v2 revised — Wave 2 bumped +2 sessions for Player profile synthesis). Calendar: 4–6 weeks daily, ~3 months intermittent.

**Net port-count delta across the masterplan (v2 corrected):** +2 (both from Wave 2: `IConditionalCompilationProvider` + `IPlayerDefineProfileSynthesizer`). Final: 27 → 29 ports + 29 → 30 tools.

---

## 2. Wave 1 — Re-probe shipped fixes (verify, don't assume)

**Goal:** Reconcile DAWG-side tracking with Lifeblood post-v0.7.8 reality. Convert "needs re-probe" entries into "closed" or "still observed" with concrete evidence.

**Effort:** ~1.5 sessions. **Risk:** Low (read-side queries only).

### Atom 1.1 — L-LIM-002 re-probe (incremental edge drop) — TWO repros, not one

**v1 plan had a no-change repeat only. That doesn't test the bug class** — the original evidence (DAWG memory L-LIM-002 §"Evidence") was 657 changed files producing a 7.6% edge drop. A no-change incremental could match no-change full without the bug being fixed. Need a controlled changed-file repro.

**Method A — no-change baseline (smoke).**
1. Full analyze DAWG, record `summary.edges` and `analysisGeneration`.
2. No file changes; incremental analyze with `incremental:true, allowFullFallback:true`.
3. Compare edge counts. Expected post-fix: equal (or structured rejection, which is also a valid closure).

**Method B — controlled changed-file repro (the real test).**
1. Full analyze DAWG, record `summary.edges`.
2. Touch (no semantic change, just mtime bump) a single `.cs` file in a leaf module — e.g. `Assets/_Project/Scripts/Systems/Shared/DebugLogger.cs`.
3. Incremental analyze. Record `summary.edges`.
4. Touch a `.cs` file in a hub module that other modules reference — e.g. an ABG partial.
5. Incremental analyze again. Record `summary.edges`.
6. Compare against a final full re-analyze (`incremental:false`) as ground truth.
7. **Pass criteria:** edge count after the two incrementals MATCHES the final full re-analyze. Pre-fix the cross-module edges where the *other* endpoint lived in unchanged modules silently dropped.

**Expected outcome.** v0.7.8 caller-owned scope policy (`INV-ANALYZE-FALLBACK-001`) means incremental REJECTS on detected drift unless `allowFullFallback:true`. Two distinct closures both count:
- (a) Edges match across the three runs → incremental is now lossless on cross-module edges.
- (b) Incremental returns `mode:'rejected'` with `fallbackReason:'ModuleDescriptorChanged'` on the hub touch → caller is responsibly informed and re-runs full. Either is a valid closure of the original trap.

**Closure criteria.** Both Method A AND Method B pass under one of the two valid outcomes → L-LIM-002 CLOSED with v0.7.8 evidence. If Method B still shows the silent edge drop class → L-LIM-002 stays OPEN with refreshed evidence + new Lifeblood inbox entry naming Method B as repro recipe.

### Atom 1.2 — L-LIM-003 re-probe (authority stale post-incremental)

**Method.** Re-run the 2026-05-13 ABG slot-authority host-adapter pilot scenario:
1. Modify a partial in DAWG that changes ABG's implemented-interface count.
2. Incremental analyze (with `allowFullFallback:true`).
3. `lifeblood_authority_report type:Nebulae.BeatGrid.AdaptiveBeatGrid`.
4. Read `implementedInterfaceCount` and verify against ground truth from Unity/Roslyn reflection.
5. Read `analysisGeneration` counter — confirm it incremented.

**Expected outcome.** Post-v0.7.8 the `analysisGeneration` counter (`INV-DIAGNOSE-FRESHNESS-001`) surfaces on every read-side envelope. Authority report results carry the same counter. If the value lags the actual analyze, the stale-data trap is still real; if it matches the most recent analyze, L-LIM-003 is at least observable (the caller can detect drift) even if the value itself is computed from a stale graph.

**Closure criteria.** Document whichever shape we observe in DAWG tracking. If authority report is now reliably fresh after incremental → L-LIM-003 CLOSED. If still stale but counter exposes it → L-LIM-003 DOWNGRADED to "stale-but-observable" with stable workaround (`analysisGeneration` check before trusting).

### Atom 1.3 — L-LIM-004 confirm closed (`lifeblood_execute` IL2CPP)

**Method.** Re-run the 2026-05-19 Polish-1 P4 repro:
1. Full analyze DAWG @ current HEAD.
2. `lifeblood_execute` with trivial probe `var x = 1+1; Console.WriteLine(x);` — host profile.
3. Expect: `runtimeAssemblyWarnings` lists `GameAssembly.dll` filtered as native PE; script runs; output = `"2"`.
4. Re-run with `targetProfile: "net-standard-2.1"`. Expect: `targetRuntimeWarnings` says the profile was downgraded to host (per v0.7.8 honesty contract).
5. Author a 5-line probe against a real DAWG semantic question (e.g. `Graph.Symbols.Count(s => s.Kind == SymbolKind.Method)`). Expect: works.

**Expected outcome.** L-LIM-004 a/b shipped per v0.7.8 changelog — the managed-PE gate + `ScriptReferenceSetBuilder` + `targetProfile` honesty should let DAWG's IL2CPP workspace use `execute` without manual intervention.

**Closure criteria.** Probe runs clean → L-LIM-004 CLOSED. Failure → re-open with v0.7.8-era evidence (likely separate bug).

### Atom 1.4 — Update DAWG tracking file

Update [`reference_lifeblood_known_limitations.md`](C:/Users/Matic/.claude/projects/D--Projekti-DAWG/memory/reference_lifeblood_known_limitations.md) with per-L-LIM closure status from atoms 1.1–1.3. Preserve original `Evidence` / `Workaround` paragraphs verbatim per the tracking file's stated convention; append a `## Resolution 2026-MM-DD` section with the new evidence.

If L-LIM-002 / L-LIM-003 close: also update [`Nebulae Beatcraft CLAUDE.md`](D:/Projekti/DAWG/CLAUDE.md) `INV-LIFEBLOOD-002b/c` block to drop the workaround mandate.

**Deliverable.** Refreshed DAWG tracking + (conditionally) CLAUDE.md update. No Lifeblood-side code changes.

### Atom 1.5 — Settle the invariant count

**v1 plan baseline quoted "122 typed invariants across 83 categories" from `docs/STATUS.md`.** Reviewer ran live `lifeblood_invariant_check` on Lifeblood-self and got **119**. Three-invariant gap. STATUS.md is ratchet-pinned by `DocsTests` so a structural drift should fail the build — but counts can also be cited correctly under one shape and miscounted under another. Settle the truth before any sign-off gate quotes it.

**Method.**
1. `cd D:/Projekti/Lifeblood; dotnet test -c Release --filter "FullyQualifiedName~DocsTests"` — assert STATUS.md anchors still pass under the current commit.
2. Live: `lifeblood_analyze projectPath:"D:/Projekti/Lifeblood"; lifeblood_invariant_check mode:"audit"`. Record `totalCount`, `categories`, `duplicates`, `parseWarnings`.
3. Reconcile: (a) DocsTests green AND live count = 122 → STATUS.md correct, reviewer's 119 was a stale-graph result. (b) DocsTests green AND live count = 119 → DocsTests is checking a different number (e.g. parsed shapes A/B only, missing C/D/E). (c) DocsTests fails → drift, fix STATUS.md anchor.
4. Whatever the real number is, document it in the v2 plan baseline before any wave uses it as a sign-off gate.

**Closure criteria.** Plan baseline reads the verified live count, not the STATUS.md cite. STATUS.md anchor updated if drift confirmed. Sign-off gates in §12 quote the verified number, not the v1 placeholder.

---

## 3. Wave 2 — Multi-define-set semantic queries (L-LIM-001)

**Goal.** Close the preprocessor-blindness trap. Today Lifeblood compiles each module under ONE define set (`UNITY_EDITOR` active, `UNITY_ANDROID` / `UNITY_IOS` / `UNITY_WEBGL` / `UNITY_STANDALONE` inactive). Callsites guarded by `#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR` are invisible to `find_references` / `dependants` / `dead_code` / `blast_radius`. An AI agent running `lifeblood_dead_code` on a mobile-only entry symbol declares it removable and breaks the mobile boot path.

**DAWG-side reference callsite.** `AdaptiveBeatGrid.Bootstrap.Services.cs:46` calls `IAudioConfigurationApplyPort.RequestConfiguration` + `AudioRuntimeProfilePolicy.Resolve` + `AudioRuntimeProfilePersistenceLocator.Current` + `UnityAudioPlatformResolver.Resolve` + `UnityAudioConfigurationApplyOwner.EnsureRegistered` under `#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR`. All five symbols undercount by 1 prod caller in current Lifeblood.

**Effort:** 5–8 sessions. **Risk:** **High** (touches compilation host, edge extraction, every read-side tool that consumes the graph, AND the test fixtures that pin the graph).

### Architecture decision — Unity csproj is Editor-pinned; Player profiles must be SYNTHESIZED

**Reviewer caught a critical flaw in v1.** Unity generates ONE csproj per asmdef, and that csproj's `<DefineConstants>` always contains the Editor define set (UNITY_EDITOR + UNITY_EDITOR_WIN + the active build-target's symbols mixed together). Confirmed by inspecting `D:/Projekti/DAWG/Assembly-CSharp.csproj`: a single `<DefineConstants>` list contains `UNITY_ANDROID`, `PLATFORM_ANDROID`, `UNITY_EDITOR`, `UNITY_EDITOR_64`, `UNITY_EDITOR_WIN`, `NET_STANDARD_2_1`, `ENABLE_MONO`, `EDITION_NEON` all together. So a `#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR` block is unreachable under naive csproj-DefineConstants parsing — `!UNITY_EDITOR` is always false in the only permutation the csproj declares.

**Therefore: deriving permutations from `<DefineConstants>` alone CANNOT close L-LIM-001.** Lifeblood must SYNTHESIZE Player permutations from a Unity-platform-symbol matrix + asmdef metadata. This is significantly more work than v1 framed.

**Revised shape — UNION across (Editor + N synthesized Player) permutations per module.**

For each Unity workspace module, compile under:
1. **Editor permutation** = the csproj's literal `<DefineConstants>` (today's behavior; preserved).
2. **Player permutations** = synthesized one-per-relevant-build-target by:
   a. Removing every `UNITY_EDITOR*` symbol.
   b. Removing every editor-only platform symbol (`UNITY_EDITOR_WIN`, `UNITY_EDITOR_OSX`, `UNITY_EDITOR_LINUX`).
   c. Keeping the build-target platform symbols (e.g. `UNITY_ANDROID`, `PLATFORM_ANDROID`).
   d. Keeping or rewriting Unity-version + platform-capability symbols per the synthesized target's known matrix (this is the part that needs a maintained data file — see "Cost of being correct" below).
3. **Skip permutations excluded by asmdef.** A module with asmdef `excludePlatforms: ["Editor"]` does NOT compile under the Editor permutation. A module with `includePlatforms: ["Android", "iOS"]` ONLY compiles under those Player permutations. Asmdef `versionDefines` add per-Unity-version conditional defines.

Union the resulting edges into one graph. Each edge carries `Properties["definePermutations"]` = sorted CSV. Truth envelope gains `definesActiveAcrossPermutations: string[][]`.

### Cost of being correct

The Unity platform-symbol matrix is large but not infinite. Reasonable v1 scope:
- Three Player profiles synthesized: `PlayerStandaloneWin`, `PlayerAndroid`, `PlayerIOS`.
- Optional fourth: `PlayerWebGL`.
- Editor profile preserved as-is.

Matrix data lives in `src/Lifeblood.Adapters.CSharp/Unity/PlayerProfileMatrix.cs` (Lifeblood's responsibility — anyone using Lifeblood on Unity gets the matrix). Source: Unity Manual "Platform-dependent compilation" page, version-bracketed.

Scope it tight on v1: only cover the symbols DAWG-class workspaces actually use (per a `#if`-grep audit of the DAWG repo as a representative sample). Symbols the matrix doesn't know about fall through unchanged. Caller can see the matrix's effective define set on the new envelope field.

**Cost upper bound:** 3 Player profiles × ~90 modules in DAWG = 270 extra compilations. v0.7.8 wall time = 44s; conservatively 2× = ~90s. Acceptable per the v1 throughput budget. If profile cost exceeds the budget, switch from union to lazy-per-profile-on-query (caller opts in via `includePermutations: ["PlayerAndroid"]`).

### Port spec (revised — TWO ports)

```csharp
namespace Lifeblood.Application.Ports.Left;

/// <summary>
/// Discovers preprocessor-define permutations the workspace cares about. For a
/// Unity workspace this is the Editor permutation (from csproj) plus N
/// synthesized Player permutations. For a non-Unity workspace this is just the
/// csproj's declared configurations.
/// </summary>
public interface IConditionalCompilationProvider
{
    /// <summary>
    /// Per-module permutations. At least one (the default / Editor permutation).
    /// </summary>
    IReadOnlyList<DefinePermutation> GetPermutations(string moduleName);
}

/// <summary>
/// Unity-specific: synthesizes Player profiles by transforming an Editor define
/// set via the known platform-symbol matrix. Implemented only by the Unity-aware
/// adapter; non-Unity adapters can provide an empty implementation.
/// </summary>
public interface IPlayerDefineProfileSynthesizer
{
    /// <summary>
    /// Given a module's Editor-permutation define set and asmdef metadata,
    /// return zero or more synthesized Player permutations.
    /// </summary>
    /// <param name="editorPermutation">The csproj's declared define set.</param>
    /// <param name="asmdef">Parsed asmdef facts (includePlatforms,
    /// excludePlatforms, versionDefines) — null for non-Unity csprojs.</param>
    IReadOnlyList<DefinePermutation> Synthesize(
        DefinePermutation editorPermutation,
        AsmdefMetadata? asmdef);
}

public sealed record DefinePermutation(
    string Name,                              // "Editor", "PlayerAndroid", "PlayerIOS", "PlayerStandaloneWin", "PlayerWebGL"
    IReadOnlyList<string> DefineSymbols,      // sorted, deduplicated
    bool IsEditor,                            // distinguishes Editor from any Player
    bool IsDefault);                          // exactly one per module is default (typically Editor on Unity, csproj-Release elsewhere)

public sealed record AsmdefMetadata(
    IReadOnlyList<string> IncludePlatforms,   // [] = all
    IReadOnlyList<string> ExcludePlatforms,   // [] = none
    IReadOnlyList<AsmdefVersionDefine> VersionDefines);

public sealed record AsmdefVersionDefine(
    string Name,                              // e.g. "Unity"
    string Expression,                        // e.g. "(0,2022.3)"
    string Define);                           // e.g. "UNITY_PRE_2022_3"
```

**Adapter shape (`RoslynConditionalCompilationProvider` + `UnityPlayerDefineProfileSynthesizer`, C# adapter):**

- `RoslynConditionalCompilationProvider` reads each module's csproj `<DefineConstants>` for the Editor permutation. Delegates Unity Player synthesis to `IPlayerDefineProfileSynthesizer` (DI; only the Unity-aware adapter implements it).
- `UnityPlayerDefineProfileSynthesizer.Synthesize`:
  1. Reads the module's `.asmdef` JSON adjacent to the csproj (probe `<csproj-dir>/*.asmdef`).
  2. Filters Player profiles excluded by `excludePlatforms`.
  3. Removes editor-pinned symbols (`UNITY_EDITOR*`, `UNITY_EDITOR_WIN/OSX/LINUX`).
  4. Keeps platform-target symbols matching the synthesized target.
  5. Applies `versionDefines` per Unity-version comparison.
  6. Returns deduplicated, sorted define list.
- `PlayerProfileMatrix.cs` — internal static data, one entry per known Player target naming the symbols to ADD on synthesis. Version-bracketed by Unity major version.

**Sanity check fixture (mandatory before shipping):** Run the synthesizer on DAWG's Assembly-CSharp.csproj. Assert the synthesized `PlayerAndroid` permutation contains `UNITY_ANDROID` but NOT `UNITY_EDITOR`. Assert `#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR` is reachable under the `PlayerAndroid` permutation.

### Pipeline integration

Touch points in `RoslynCompilationHost` + `ModuleCompilationBuilder`:

1. **`ModuleCompilationBuilder.CreateCompilation`** — currently produces one `CSharpCompilation` per module. Change to produce one per declared permutation (`Dictionary<string permutationName, CSharpCompilation>`).
2. **`RoslynEdgeExtractor`** — walks each compilation per module. Each edge carries `Properties["definePermutation"]` = permutation name.
3. **`GraphBuilder.Build`** — union edges across permutations; collapse `(sourceId, targetId, kind)` duplicates; on collapse, union the permutation set into `Properties["definePermutations"]` = sorted CSV.
4. **Truth envelope (`LifebloodResponseDecorator`)** — for `find_references` / `dependants` / `blast_radius` / `dead_code` responses, surface `definesActiveAcrossPermutations: string[][]` listing the permutation sets that contributed any edge to the result.
5. **`dead_code` analyzer** — a symbol is dead ONLY if zero incoming non-`Contains` edges across ALL permutations.

### Invariants to author

- **`INV-MULTI-DEFINE-001`** — `IConditionalCompilationProvider` is the SSoT for Editor-permutation enumeration; `IPlayerDefineProfileSynthesizer` is the SSoT for Player permutations on Unity workspaces. Two consumers MUST NOT independently parse `<DefineConstants>`, `defineConstraints`, or Unity platform-symbol matrices. Ratchet: architecture test refuses any direct csproj-DefineConstants read OR any direct `UNITY_*` symbol-set construction outside the two adapter classes.
- **`INV-MULTI-DEFINE-002`** — graph edges carry `Properties["definePermutations"]` after `GraphBuilder.Build`. Ratchet: `GraphBuilderTests.Build_EdgeWithMultiplePermutations_UnionsIntoProperties`.
- **`INV-MULTI-DEFINE-003`** — truth envelope on `find_references` / `dependants` / `blast_radius` / `dead_code` lists `definesActiveAcrossPermutations`. Ratchet: `ResponseEnvelopeMultiDefineTests`.
- **`INV-MULTI-DEFINE-004`** — synthesized Player permutations NEVER contain `UNITY_EDITOR*` symbols. Ratchet: `UnityPlayerDefineProfileSynthesizerTests.Synthesize_AnyPlayerProfile_ExcludesEditorSymbols`.
- **`INV-MULTI-DEFINE-005`** — `PlayerProfileMatrix` is version-bracketed by Unity major version. A workspace's resolved Unity version drives which matrix entries apply. Ratchet: `PlayerProfileMatrixVersionBracketTests`.

### Test pins

- `RoslynConditionalCompilationProviderTests` — csproj `<DefineConstants>` parsing, multi-`<Configurations>` parsing, default-permutation fallback for non-Unity csprojs.
- `UnityPlayerDefineProfileSynthesizerTests` — Editor symbol stripping (covers v0.7.8 + post-fix shape), asmdef `excludePlatforms: ["Editor"]` filtering, asmdef `includePlatforms: ["Android"]` narrowing, `versionDefines` application.
- `AsmdefParsingTests` — round-trip `includePlatforms` / `excludePlatforms` / `versionDefines` from real Unity asmdef JSON.
- `ModuleCompilationBuilderMultiDefineTests` — one module compiles N times (Editor + N Player); cross-permutation symbol identity preserved (`SymbolId` matches across permutations).
- `EdgeUnionAcrossPermutationsTests` — symbol declared in shared region with callers in Editor-only AND PlayerAndroid-only permutations gets unioned edge set with both `definePermutations` listed.
- `DeadCodeMultiDefineTests` — symbol with callers ONLY in PlayerAndroid permutation is NOT flagged dead under default (Editor + all Players) analyze. This is the canonical L-LIM-001 trap-class regression.
- **DAWG dogfood fixture (mandatory pre-ship):** `AdaptiveBeatGridBootstrap_AudioPort_ReachableUnderMobilePermutation` — pins the canonical L-LIM-001 callsite class. Specifically: `find_references` on `IAudioConfigurationApplyPort.RequestConfiguration` returns BOTH the settings-panel call (Editor permutation) AND the bootstrap mobile-only call at `AdaptiveBeatGrid.Bootstrap.Services.cs:46` (PlayerAndroid + PlayerIOS permutations).
- **DAWG dogfood: throughput acceptance fixture.** Full DAWG analyze with all profiles under ≤ 2× the v0.7.8 wall time (44s → ≤ 90s). If exceeded, downgrade default to `includePermutations: ["Editor"]` and require callers to opt in to Player profiles.

### Dependencies

- Wave 1 atom 1.2 (L-LIM-002 re-probe) — multi-define ships per-permutation compilation; incremental-mode behavior under permutation set changes must be settled first.

### Closure criteria

- L-LIM-001 CLOSED in DAWG tracking with v0.7.9 evidence.
- All three `INV-MULTI-DEFINE-*` ratchets green.
- DAWG dogfood: `find_references` on `IAudioConfigurationApplyPort.RequestConfiguration` returns BOTH the settings-panel call AND the bootstrap mobile-only call.
- Throughput acceptance: full DAWG analyze under ≤ 2× the v0.7.8 wall time (currently 44s → target ≤ 90s) — permutation count is small (≤3 per module) so the cost should be sub-linear.

---

## 4. Wave 3 — Reflection-aware `test_impact` (L-LIM-005)

**Goal.** Make `lifeblood_test_impact` honest about ratchet / reflection-style tests. Today the BFS over `Calls` / `References` incoming edges misses tests that reach the target via Roslyn reflection, `typeof(T)` strings, qualified-name string literals, `lifeblood_*` MCP queries — i.e. every architecture ratchet test class in DAWG.

**DAWG-side reference.** `lifeblood_test_impact target:"type:Nebulae.BeatGrid.AdaptiveBeatGrid"` returns 3 test methods. Ground truth: dozens of ABG-related ratchets (`AbgBoundaryFinalRatchetTests`, `MinisPatchRatchetTests`, `MultiResolutionLayoutRatchetTests`, `AudioSourceComponentCountRatchetTests`, ...). The current tool is an unsafe lower bound.

**Effort:** 2–3 sessions. **Risk:** Medium (extends an advisory tool — wrong design choices propagate into how callers reason about ratchet coverage).

### Architecture decision — extend the EXISTING static class + record, do NOT invent a port

**v1 plan invented `ITestImpactAnalyzer` (Right port).** Reviewer caught: today's code is `public static class TestImpactAnalyzer` in `src/Lifeblood.Analysis/TestImpactAnalyzer.cs:16`. Output is `TestImpactReport { AffectedTestClasses: TestClassImpact[] }` (per `Lifeblood.Domain/Results/AnalysisResult.cs:123` + `ToolHandler.cs:960`). Inventing a port changes the wire shape and breaks existing callers.

**Revised approach: additive extension.** Keep the static class. Keep the `AffectedTestClasses[]` grouped wire shape. Add NEW additive fields (`Kind` on `TestClassImpact` rows, top-level `SemanticEdgeHits` + `ReflectionHeuristicHits` counts, opt-in option). All existing callers continue to work unchanged; new callers read the new fields.

**Heuristic shape — tighter than v1, no false-positive parade.**

Tested string-literal matching with the workspace-class-name pattern fails: `"AdaptiveBeatGrid"` is a short, frequently-mentioned substring that would match in test docstrings, comments-converted-to-strings (which `nameof` resolves to literals at compile), nested-type references, and so on. Reviewer is right. Three discipline rules:

1. **FQN-first.** Match the FULL qualified name (`"Nebulae.BeatGrid.AdaptiveBeatGrid"`) as a literal first. High signal, low FP.
2. **Short-name with namespace context only.** Match the bare name (`"AdaptiveBeatGrid"`) ONLY when ALSO the containing namespace (`Nebulae.BeatGrid`) appears as a literal in the SAME test method body, OR when the short name is globally unique across the workspace's symbol-name index.
3. **`nameof(T)` via operation tree, NOT string-literal scan.** Roslyn lowers `nameof(T)` to a constant-string `IConstantValueOperation`; the operation tree carries the captured symbol on `INameOfOperation.Argument` (via the syntax binding). Walk the operation tree for `INameOfOperation` whose `Argument` resolves to `targetSymbolId` — this gives a high-confidence symbol-level match, not a fragile literal match.
4. **`typeof(T)` already emits a Calls/References edge** in current Lifeblood (covered by extractor since v0.6.4). It's a Semantic hit, not a heuristic one. Verify under the extractor first; only fall back to heuristic when no semantic edge exists.
5. **`Type.GetType("...")` / `Assembly.GetType("...")` with a LITERAL argument** — match the FQN literal via rule 1. Computed-string `Type.GetType(someVar)` is genuinely unreachable; surface as a limitation per `INV-ADVISORY-LIMITATIONS-001`.

### Wire-shape change — minimal & additive

```csharp
namespace Lifeblood.Domain.Results;

public sealed record TestImpactReport
{
    public required TestClassImpact[] AffectedTestClasses { get; init; }   // EXISTING — wire shape preserved

    // NEW v2 plan additions (all default-initialized so existing serialization stays byte-stable for callers reading only the existing field):
    public int SemanticEdgeHits { get; init; }              // count of TestClassImpact rows sourced via Calls/References BFS
    public int ReflectionHeuristicHits { get; init; }       // count of TestClassImpact rows sourced via Wave-3 heuristic
    public IReadOnlyList<string> Limitations { get; init; } // surfaced via existing envelope; field promoted for direct caller use
        = Array.Empty<string>();
}

public sealed record TestClassImpact
{
    // EXISTING fields preserved...
    public required string TestClassId { get; init; }
    public required string[] TestMethodIds { get; init; }
    public int Distance { get; init; }

    // NEW v2 plan addition (additive — defaults to Semantic for back-compat callers who don't read this field):
    public TestImpactHitKind Kind { get; init; } = TestImpactHitKind.Semantic;
}

public enum TestImpactHitKind
{
    Semantic,               // sourced via Calls/References incoming BFS (existing behavior)
    ReflectionHeuristic     // sourced via Wave-3 FQN-literal / nameof-operation match
}
```

And the existing static class gains one new options parameter:

```csharp
namespace Lifeblood.Analysis;

public static class TestImpactAnalyzer
{
    // EXISTING overload preserved (callers using positional args don't break).
    public static TestImpactReport Analyze(IGraph graph, string targetSymbolId, int? maxDistance = null);

    // NEW overload (Wave 3).
    public static TestImpactReport Analyze(IGraph graph, string targetSymbolId, TestImpactOptions options);
}

public sealed record TestImpactOptions(
    int? MaxDistance = null,
    bool IncludeReflectionHeuristic = false,    // OPT-IN default — preserves v0.7.8 wire shape exactly for callers who don't request it
    ISymbolNameUniquenessIndex? SymbolNameIndex = null); // injected; null disables short-name-with-uniqueness rule (only FQN + nameof match in that case)
```

### Adapter shape (extension of `LifebloodTestImpactAnalyzer`)

Today's BFS over incoming `Calls` / `References`. Wave 3 enhancement:
1. After BFS completes, if `IncludeReflectionHeuristic: true`:
2. Compute target's FQN + short name.
3. For each candidate test method (identified by extractor-recorded `[Test]` / `[TestCase]` / `[Theory]` / `[UnityTest]` / `[Fact]` / `[TestCaseSource]` attribute):
   a. Walk the method's syntax tree for `LiteralExpressionSyntax` with string token text containing the FQN — match.
   b. Walk the method's IOperation tree for `INameOfOperation` whose `Argument` symbol resolves to `targetSymbolId` — match.
   c. Walk for `LiteralExpressionSyntax` matching the short name AND verify the same method body contains a literal containing the namespace OR `SymbolNameIndex.IsUnique(shortName) == true` — match.
4. Emit synthetic `TestClassImpact` with `Kind: ReflectionHeuristic`.
5. Deduplicate against semantic hits (same `TestClassId` collapses; `Kind: Semantic` wins).
6. Update `SemanticEdgeHits` + `ReflectionHeuristicHits` totals.
7. Append `Limitations[]` entry: `"Reflection heuristic uses FQN-literal + nameof-operation + (short-name with namespace context). Tests using Type.GetType(computedString) remain invisible."`

### Invariants to author

- **`INV-TEST-IMPACT-REFLECTION-001`** — when `IncludeReflectionHeuristic: true`, the analyzer surfaces hits sourced from string-literal name matches in test method bodies. Ratchet: `TestImpactReflectionHeuristicTests` fixture with a test that reflectively loads a target by FQN string and is correctly flagged.
- **`INV-TEST-IMPACT-REFLECTION-002`** — heuristic hits carry `Kind: ReflectionHeuristic`; semantic hits stay `Kind: Semantic`. Ratchet: `TestImpactHitKindDistinctnessTests`.
- **`INV-TEST-IMPACT-REFLECTION-003`** — truth envelope `limitations[]` on every `test_impact` response carries an explicit "reflection heuristic uses string-literal matching; tests using `Type.GetType` via a computed string are still invisible". Honest disclosure per the `LB-INBOX-001` truth-envelope contract.

### Test pins

- `TestImpactSemantic_OnlyDirectCalls_ReturnsCallEdgeTests` — regression for the existing BFS behavior.
- `TestImpactReflectionHeuristic_StringLiteralOfQualifiedName_FindsTest` — Shape A coverage for FQN literal.
- `TestImpactReflectionHeuristic_StringLiteralOfShortName_FindsTest` — Shape A coverage for short-name literal.
- `TestImpactReflectionHeuristic_TypeofExpression_FindsTest` — semantic edge already exists for `typeof(T)`, validate the heuristic doesn't double-count.
- `TestImpactReflectionHeuristic_NameofExpression_FindsTest` — `nameof(T)` resolves to a string literal in the syntax model; verify the heuristic catches it.
- **DAWG dogfood fixture (manual):** `lifeblood_test_impact target:"type:Nebulae.BeatGrid.AdaptiveBeatGrid"` returns ≥10 test classes (today: 2). Recall target: ≥50% of the actual ABG ratchet set (`AbgBoundaryFinalRatchetTests` MUST be in the result).

### Dependencies

None (independent of Wave 2).

### Closure criteria

- L-LIM-005 DOWNGRADED in DAWG tracking from "unsafe lower bound" to "advisory with reflection heuristic; verify against ratchet suite for architectural types".
- INV-TEST-IMPACT-REFLECTION-001..003 green.
- DAWG dogfood recall on ABG ratchet set ≥50% (target: 100% but accept 50% as Shape A floor).

---

## 5. Wave 4 — Assignment-coverage tool (L-LIM-006) — **unblocks DAWG Polish-1 P4**

**Goal.** Ship the primitive DAWG's `BindingsClosureCoverageRatchetTests` needs. Today `lifeblood_static_tables` extracts STATIC field/property initializer collection shapes, which is the wrong shape for Bindings — Bindings slots are public mutable Func/Action FIELDS assigned via object-initializer expressions or lambda-body closures inside a Build helper method or ctor. The required primitive walks `IObjectCreationOperation` + subsequent `ISimpleAssignmentOperation` targeting the same local.

**DAWG-side reference.** `BeatGridTickHostBindings` (33 slots, 60Hz), `AbgMidiStackBindings` (47 slots, per-MIDI-event), `MelodicPadVisualHostBindings` (45 slots, per-frame) per `INV-BINDINGS-VALIDATION-001` (DAWG CLAUDE.md). DAWG's Polish-1 P4 has been deferred specifically because this tool does not exist (`L-LIM-006` + `L-LIM-004` compound blocker; L-LIM-004 just closed in v0.7.8, L-LIM-006 still open).

**Effort:** 4–5 sessions. **Risk:** Medium (new tool surface, new operation walker, new wire shape — but conceptually narrow).

### Architecture decision — extend `ICompilationHost`, do NOT add a new Right port

**v1 plan put `IAssignmentCoverageAnalyzer` in Ports/Right.** Reviewer caught: this is the wrong layer. Compilation-dependent extractors live on `ICompilationHost` (the Left port). Sister tool `static_tables` is exactly the model:
- `ICompilationHost.GetStaticTables(typeId, options)` — port method on the Left adapter (per `src/Lifeblood.Application/Ports/Left/ICompilationHost.cs:102`).
- `RoslynCompilationHost.GetStaticTables` — implementation (per `src/Lifeblood.Adapters.CSharp/RoslynCompilationHost.cs:531`).
- `WriteToolHandler.HandleStaticTables` — MCP dispatch (per `src/Lifeblood.Server.Mcp/WriteToolHandler.cs:413`).
- Tool registered as `lifeblood_static_tables` on the WriteSide handler.

**Mirror this exactly for `lifeblood_assignment_coverage`.** Add `GetAssignmentCoverage` to `ICompilationHost`; implementation on `RoslynCompilationHost`; MCP dispatch on `WriteToolHandler`; tool registered on WriteSide. **No new port.** Port count stays 27. Only tool count moves (29 → 30).

Single new tool — no fork of `static_tables`. Reasons:
- `static_tables` is documented as "static initializer extraction" — extending it to method-body assignment would muddy the contract.
- Caller intent differs: `static_tables` answers "what's the static dispatch table?"; the new tool answers "did this consumer fill all the required slots before passing the Bindings on?".
- New tool naming: `lifeblood_assignment_coverage`. WriteSide handler (Compilation-required convention).

### Confidence tiering — Proven for direct, Advisory for everything else

**Reviewer pushback on v1's "Semantic / Proven" envelope:** the tool's accuracy is NOT uniformly Proven. Different construction shapes have different rigor:

| Construction shape | Tier | Confidence | Why |
|---|---|---|---|
| `new XBindings { A = ..., B = ... }` (inline object-initializer only) | Semantic | Proven | Single operation tree, no escape, no aliasing — provably complete |
| `var b = new XBindings(); b.A = ...; b.B = ...; Use(b);` (statement-level on local, single method, no aliasing) | Semantic | Proven | Single method's `IOperation` tree + escape analysis — provably complete |
| `var b = BuildBindings(); b.A = ...;` (constructed via factory) | Derived | Advisory | Cross-method flow — would need interprocedural analysis to know which slots `BuildBindings` populated |
| `var b = new XBindings(); var b2 = b; b2.A = ...;` (aliased local) | Derived | Advisory | Alias analysis would need follow-through; Wave 4 v2 does NOT ship alias tracking |
| `var b = new XBindings(); if (cond) { b.A = ...; }` (branched assignment) | Derived | Advisory | Control-flow gives MAY-be-assigned, not MUST-be-assigned; Wave 4 v2 reports as Absent (conservative) |
| `var b = new XBindings(); Configure(b); b.A = ...;` (post-escape assignment) | Semantic | Proven | Escape boundary is explicit; post-escape assignment doesn't count (per `INV-ASSIGNMENT-COVERAGE-003`) |

**Wire-shape impact.** The tool's response envelope is per-site, not per-tool. Each `AssignmentCoverageSite` carries its OWN `Confidence: Proven | Advisory` based on which shape the constructor / assignments matched. Top-level envelope says `Derived` if ANY site is Advisory; else `Semantic`. `Limitations[]` enumerates the shapes that bumped a site to Advisory.

### Port spec (revised — extends EXISTING Left port `ICompilationHost`)

```csharp
namespace Lifeblood.Application.Ports.Left;

// ICompilationHost.cs — ADD a new method alongside GetStaticTables.
public partial interface ICompilationHost
{
    // EXISTING methods preserved...

    /// <summary>
    /// For each construction site of a target type, report which of the target's
    /// public mutable Func/Action/delegate/object fields are assigned at that
    /// site. Covers both object-initializer expressions and statement-level
    /// assignments on the constructed local before the local escapes.
    /// Per-site confidence tier reflects the constructor / assignment shape's
    /// analysis rigor (see plan §5).
    /// </summary>
    AssignmentCoverageReport? GetAssignmentCoverage(
        string targetTypeId,
        AssignmentCoverageOptions options);
}

public sealed record AssignmentCoverageOptions(
    /// <summary>
    /// Slot kinds to consider as "required". Default: every public mutable
    /// field whose declared type is a delegate (Func / Action / custom delegate
    /// type) and any property whose set accessor is public. Pass null to
    /// include all public mutable members.
    /// </summary>
    SlotKindFilter? SlotKinds = null);

[Flags]
public enum SlotKindFilter
{
    None = 0,
    DelegateField = 1,
    DelegateProperty = 2,
    PublicMutableField = 4,
    PublicMutableProperty = 8,
    AllDelegateSlots = DelegateField | DelegateProperty,
    AllPublicMutable = PublicMutableField | PublicMutableProperty
}

public sealed record AssignmentCoverageReport(
    string TargetTypeSymbolId,
    IReadOnlyList<string> AllSlots,                  // canonical slot order
    IReadOnlyList<AssignmentCoverageSite> Sites,
    ResponseEnvelope Envelope);

public sealed record AssignmentCoverageSite(
    string ContainingMethodSymbolId,                 // where the construction happens
    string FilePath,
    int Line,
    int Column,
    IReadOnlyList<AssignmentCoverageSlot> Slots,
    AssignmentCoverageConfidence Confidence,         // NEW — per-site tier per §5 table
    IReadOnlyList<string> SiteLimitations);          // NEW — names the shape (alias / branched / factory) that bumped the tier

public enum AssignmentCoverageConfidence
{
    Proven,        // direct inline-initializer OR single-method statement-level + no aliasing + no branched MAY-assign
    Advisory       // any of: factory construction, aliased local, branched MAY-assign — see SiteLimitations[]
}

public sealed record AssignmentCoverageSlot(
    string SlotName,                                 // matches AllSlots entry
    AssignmentCoverageStatus Status,
    /// <summary>
    /// Kind of assignment expression. Null when Status == Absent.
    /// </summary>
    AssignmentExpressionKind? ExpressionKind,
    /// <summary>
    /// File:line of the assignment. Null when Absent.
    /// </summary>
    int? Line,
    int? Column);

public enum AssignmentCoverageStatus
{
    Assigned,        // slot was written between construction and escape
    Absent,          // slot was not written
    AssignedNull     // slot was explicitly assigned null literal — distinct from Absent
}

public enum AssignmentExpressionKind
{
    Lambda,          // (a, b) => ...
    MethodGroup,     // owner.SomeMethod
    FieldReference,  // owner._field
    PropertyAccess,  // owner.Property
    NullLiteral,     // explicit null
    Other            // any other expression
}
```

### Adapter shape (`RoslynAssignmentCoverageExtractor`)

Walker structure:
1. Resolve `targetTypeSymbolId` to `ITypeSymbol`. Enumerate slots per `SlotKindFilter`.
2. Walk every `CSharpCompilation` for `IObjectCreationOperation` whose `Type` matches.
3. For each construction site:
   a. Collect inline object-initializer slot writes from `ObjectCreationExpressionSyntax.Initializer`.
   b. Identify the local variable bound to the result (if any — `IVariableDeclaratorOperation` parent).
   c. Walk forward in the enclosing method's control-flow graph until the local escapes (passed as argument, returned, assigned to field/property of another type, or method end). Within that window, collect every `ISimpleAssignmentOperation` whose target is `IMemberReferenceOperation { Instance: ILocalReferenceOperation matching }`.
   d. Union the inline + statement-level slot writes; build per-slot status.
4. Return per-site report.

**Control-flow walk depth.** Use Roslyn's `ControlFlowGraph.Create(IMethodOperation)` for the enclosing method. Walk basic blocks in forward execution order; the local escapes when:
- An `IInvocationOperation` argument matches the local (passed as argument).
- An `IReturnOperation` returns the local.
- An `ISimpleAssignmentOperation` writes the local to a member of a different type (e.g. `this._bindings = b`).
Conservative: when a branch escape state is uncertain, treat the local as escaped (stop walking that branch). Coverage report flags "AssignedBeforeEscape" only; post-escape assignments don't count.

### Pipeline integration (mirrors `static_tables` exactly)

- Extend EXISTING `ICompilationHost` (`Lifeblood.Application/Ports/Left/ICompilationHost.cs`) with `GetAssignmentCoverage` — no new port.
- New `RoslynAssignmentCoverageExtractor` adapter in `Lifeblood.Adapters.CSharp/` (own file per `INV-ADAPTER-THIN-001`; mirrors `RoslynStaticTableExtractor` placement).
- `RoslynCompilationHost.GetAssignmentCoverage` — host orchestrator method, delegates to the extractor (mirrors `RoslynCompilationHost.GetStaticTables`).
- `WriteToolHandler.HandleAssignmentCoverage` — MCP dispatch (mirrors `WriteToolHandler.HandleStaticTables` at line 413).
- New `ToolRegistry` entry `lifeblood_assignment_coverage` on WriteSide. Envelope classification PER-SITE (see §"Confidence tiering"); top-level envelope tier is `Semantic` when all sites are Proven, else `Derived`. Top-level `Limitations[]` enumerates the union of per-site SiteLimitations.

**Port count stays 27. Tool count moves 29 → 30. Sign-off math updates §12 gates accordingly.**

### Invariants to author

- **`INV-ASSIGNMENT-COVERAGE-001`** — every `IObjectCreationOperation` whose Type matches `targetTypeSymbolId` produces exactly one `AssignmentCoverageSite`. Ratchet: `AssignmentCoverageSiteEnumerationTests`.
- **`INV-ASSIGNMENT-COVERAGE-002`** — slot writes from inline object-initializer AND statement-level assignment AND null-literal assignment are all surfaced with `ExpressionKind`. Ratchet: `AssignmentCoverageExpressionKindTests` (one fixture per `AssignmentExpressionKind` value).
- **`INV-ASSIGNMENT-COVERAGE-003`** — `Status: Absent` only when the slot was NOT written between construction and escape. Ratchet: `AssignmentCoverageEscapeBoundaryTests` (assignment-after-escape MUST NOT count).
- **`INV-ASSIGNMENT-COVERAGE-004`** — null-literal assignment surfaces as `Status: AssignedNull`, distinct from `Absent`. Closes the "I forgot to wire this" vs "I deliberately wired null" distinction. Ratchet: `AssignmentCoverageNullDistinctionTests`.

### Test pins

- `AssignmentCoverage_InlineInitializerOnly_AllSlotsAssigned` — `new XBindings { A = ..., B = ... }` with all slots inline.
- `AssignmentCoverage_StatementAssignmentOnly_AllSlotsAssigned` — `var b = new XBindings(); b.A = ...; b.B = ...;` shape.
- `AssignmentCoverage_MixedInlineAndStatement_BothCounted` — inline subset + statement-level subset.
- `AssignmentCoverage_PartialAssignment_AbsentSlotsListed` — only some slots wired; verify Absent list.
- `AssignmentCoverage_LambdaSlot_KindLambda` — `b.OnTick = ts => owner.HandleTick(ts);`.
- `AssignmentCoverage_MethodGroupSlot_KindMethodGroup` — `b.OnTick = owner.HandleTick;`.
- `AssignmentCoverage_FieldReferenceSlot_KindFieldReference` — `b.OnTick = owner._tickHandler;`.
- `AssignmentCoverage_NullLiteralSlot_KindNullLiteral_StatusAssignedNull` — `b.OnTick = null;`.
- `AssignmentCoverage_AssignmentAfterEscape_NotCounted` — escape via `Configure(b)` then `b.A = ...` MUST NOT count.
- `AssignmentCoverage_BranchedEscape_ConservativeCounted` — `if (cond) Configure(b); b.A = ...` MUST NOT count (escape on one branch ⇒ stop).
- **DAWG dogfood fixture (manual):** `lifeblood_assignment_coverage targetTypeSymbolId:"type:Nebulae.BeatGrid.Tick.BeatGridTickHostBindings"` returns all production construction sites with per-slot Status. Then DAWG-side `BindingsClosureCoverageRatchetTests` is authored against the new tool.

### Dependencies

None (independent of Waves 1–3).

### Closure criteria

- L-LIM-006 CLOSED in DAWG tracking.
- INV-ASSIGNMENT-COVERAGE-001..004 green.
- DAWG dogfood: `BindingsClosureCoverageRatchetTests` lands as a semantic ratchet pinning slot coverage on all 3 top high-frequency Bindings (BeatGridTick / AbgMidiStack / MelodicPadVisual). Closes DAWG Polish-1 P4. The DAWG-side ratchet must accept per-site `AssignmentCoverageConfidence` and only fail-closed on `Proven`-tier `Absent` slots — `Advisory`-tier results are surfaced as warn-only.
- Lifeblood STATUS: 29 → 30 MCP tools. **Port count UNCHANGED at 27** (extension of existing `ICompilationHost`, not a new port — corrected from v1 plan).

---

## 6. Wave 5 — Contract freeze completion (LB-INBOX-003 closure)

**Goal.** Finish the contract-freeze work `INV-WIRE-CONTRACT-001` started. Today `ResponseEnvelope` is pinned by reflection and the deprecation policy exists as a doc; the per-tool input/output schemas are still implicit. A future minor that accidentally renames a response field on `lifeblood_dependants` or `lifeblood_authority_report` would break every external integrator silently.

**No DAWG-internal trigger.** This wave is for external integrator confidence — anyone building production tooling on top of Lifeblood's MCP wire.

**Effort:** 4–6 sessions. **Risk:** Low (additive; doesn't change runtime behavior).

### Architecture decision

Three deliverables, sequenced:

1. **Per-tool versioned input/output JSON schemas under `schemas/tools/v1/<tool>.json`.** One file per current 29 (30 post-Wave-4) tools. Schema definition language: JSON Schema 2020-12 (modern, widely supported, has `$ref` for re-use of `ResponseEnvelope` shape).
2. **Replay-compatibility tests** — `Lifeblood.Tests.WireContract.ReplayCompatibilityTests` records a v1 client session (input → output JSON for every tool) and replays it against a new server build, asserting no field is missing, renamed, or changed in type. Recordings live under `tests/Lifeblood.Tests/Resources/wire-recordings/v1/<tool>.json`.
3. **Versioned graph schema** — rename `schemas/graph.schema.json` → `schemas/graph/v1.schema.json`. Add evolution rules already named in `LB-INBOX-003`.

### Port spec — genuinely no new ports

**v1 contradicted itself:** said "no new ports; additive to existing" then defined `IVersionedToolRegistry`. Reviewer caught. v2 drops the port entirely.

The contract-freeze work is **test-side + schema-side, not port-side**:
- Per-tool input/output schemas are JSON files under `schemas/tools/v1/<tool>.json` — no runtime port needed.
- Wire-schema version is a property of the EXISTING `ToolDefinition` record (add a `string WireSchemaVersion = "v1"` field if needed; defaults preserve back-compat).
- Replay-compatibility checking is a test fixture against the existing `McpDispatcher`, not a new runtime surface.
- Deprecation markers (`deprecated: true`, `replacedBy: "..."`) are already part of MCP `tools/list` protocol — `ToolDefinition` adds nullable fields; no port.

**Port count stays 27. Tool count stays at whatever Wave 4 produced (29 → 30 after Wave 4).** Sign-off math in §12 does NOT bump port count for Wave 5.

### Test pins

- `WireContract_AllToolsHaveV1Schema_Test` — fail-closed if any tool in `ToolRegistry` lacks a `schemas/tools/v1/<tool>.json` file.
- `WireContract_AllSchemasValidateAgainstJsonSchema2020` — meta-validation: every schema file is itself valid JSON Schema 2020-12.
- `ReplayCompatibilityTests_AllRecordedSessions_StillValidate` — replay every `<tool>.json` recording, validate the live server response against the v1 schema, fail-closed on any mismatch.
- `WireContract_DeprecatedToolMarker_AppearsInToolsListResponse` — pin the `LB-INBOX-003` `deprecated: true` + `replacedBy: "..."` marker per the policy.

### Invariants to author

- **`INV-WIRE-CONTRACT-002`** — every tool in `ToolRegistry` has a matching `schemas/tools/v1/<tool>.json`. Ratchet: `WireContract_AllToolsHaveV1Schema_Test`.
- **`INV-WIRE-CONTRACT-003`** — graph JSON schema lives at `schemas/graph/v1.schema.json`; un-versioned `schemas/graph.schema.json` is gone. Ratchet: `DocsTests.GraphSchema_HasVersionedPath`.
- **`INV-WIRE-CONTRACT-004`** — `lifeblood_invariant_check` carries `addedInVersion` per invariant, queryable via `mode: "added_in:0.7.6"`. Ratchet: `InvariantVersionBindingTests`.

### Closure criteria

- All 29 (or 30 post-Wave-4) tools have `schemas/tools/v1/<tool>.json`.
- All `INV-WIRE-CONTRACT-002..004` ratchets green.
- `LB-INBOX-003` flipped to **SHIPPED** in `docs/IMPROVEMENT_INBOX.md` with this masterplan's wave reference.

---

## 7. Wave 6 — Public-proof polish (LB-INBOX-004 + LB-INBOX-005 closure)

**Goal.** Close the remaining external-credibility work. Today the repo has the `docs/PLAYBOOK_CSHARP.md` + one case study (`unity-daw-parity-2026-05`); the original phase 4+5 plans called for ≥3 case studies, comparison demos vs generic MCP, and a trust dashboard.

**No DAWG-internal trigger.** This is repo-external work.

**Effort:** 4–8 sessions across documentation + benchmark authoring. **Risk:** Low (doc-side only, no runtime changes).

### Deliverables

1. **2–3 more case studies in `docs/case-studies/`.** Suggested topics from DAWG dogfood history:
   - `unity-burst-port-2026-05.md` — the May 2026 Burst port (Stage U-A → F-SHIMMER) ABG-extraction triage via `lifeblood_authority_report`. Real-world recipe for "convert a 4029-member god-type to ports" with the exact `forwarderRatio` evidence.
   - `unity-multi-define-2026-XX.md` — written after Wave 2 lands. Story: "DAWG mobile bootstrap callsite was invisible to standard `find_references`; multi-define query found it." Shows the L-LIM-001 closure in action.
   - `unity-bindings-coverage-2026-XX.md` — written after Wave 4 lands. Story: "DAWG had 128 Bindings types with 33–47 slots each; manual `ValidateRequiredSlots()` was the only safety net; new `assignment_coverage` tool surfaced ALL construction-site slot omissions in one query." Shows the L-LIM-006 closure.

2. **Comparison demos** under `docs/comparison/`. Three formats:
   - `vs-grep.md` — same task ("find every caller of `Voice.SetPatch`"), three tools (grep, generic MCP, Lifeblood), three outputs, ground-truth column. Demonstrates per-tool accuracy on a real shape.
   - `vs-generic-mcp.md` — generic Roslyn-based MCP (whichever competitor is appropriate) on the same DAWG ABG-extraction triage task; side-by-side `authority_report` equivalent.
   - `vs-claude-code-builtin.md` — Claude Code's built-in Grep + Glob vs Lifeblood semantic tools on a representative task set (3 tasks). Important: this demonstrates *complementarity* (Grep is sufficient for source-text questions; Lifeblood wins on semantic questions), not Lifeblood superiority everywhere.

3. **Trust dashboard at `docs/TRUST.md`.** Four sections:
   - **Adapter maturity** — citing README maturity table (Proven C#, High TypeScript, Structural Python, Beta native-clang).
   - **Known limitations** — pull from `INV-DEADCODE-001` + L-LIM tracking + truth-envelope `limitations[]` strings.
   - **Benchmark results** — citing Wave 6 comparison demos + case studies.
   - **Compatibility guarantees** — citing Wave 5 `INV-WIRE-CONTRACT-*` ratchets + `SCHEMA_DEPRECATION_POLICY.md`.

### Test pins

- `DocsTests.AllCaseStudiesHaveScopeReproducibilityAndAnonymization` — every `docs/case-studies/*.md` carries `## Scope`, `## Reproducibility`, `## What is deliberately not claimed` sections.
- `DocsTests.TrustDashboardLinksAreValid` — every cited file under `docs/TRUST.md` exists.
- `DocsTests.ComparisonDemos_HaveGroundTruthColumn` — every `docs/comparison/*.md` table includes a "ground truth" column.

### Closure criteria

- `LB-INBOX-004` flipped to **SHIPPED** with PLAYBOOK + benchmarks + sub-second incremental loop receipt.
- `LB-INBOX-005` flipped to **SHIPPED** with 3+ case studies + 3 comparison demos + `docs/TRUST.md` published.
- External reviewer (third party, not Codex/Claude) confirms the trust dashboard's claims match the actual repo state.

---

## 8. Wave 7 — DAWG-side integration (Lifeblood adoption)

**Goal.** Update DAWG to use the new Lifeblood capabilities; close the tracking loop on the DAWG side.

**Effort:** 1–2 sessions per wave landed.

### Per-wave DAWG-side work

**After Wave 1 lands (re-probe):**
- Update DAWG [`reference_lifeblood_known_limitations.md`](C:/Users/Matic/.claude/projects/D--Projekti-DAWG/memory/reference_lifeblood_known_limitations.md) with closure status on L-LIM-002 / L-LIM-003 / L-LIM-004.
- Update DAWG CLAUDE.md `INV-LIFEBLOOD-002b/c` block — drop preprocessor / incremental workaround mandate if respective L-LIM closed.

**After Wave 2 lands (multi-define):**
- New DAWG INV: `INV-LIFEBLOOD-005 — Multi-define semantic queries are the default for cross-platform reachability.`
- Update `feedback_lifeblood_mandatory.md` to mandate multi-define for any "is this dead?" decision touching audio / platform / transport code.

**After Wave 3 lands (reflection-aware test_impact):**
- Update DAWG CLAUDE.md `INV-LIFEBLOOD-002a` (counting precision) — extend to test_impact with `Kind` field.
- Update `reference_lifeblood_known_limitations.md` L-LIM-005 closure status.

**After Wave 4 lands (assignment-coverage):**
- Author `Assets/Tests/Editor/Architecture/BindingsClosureCoverageRatchetTests.cs` using new tool. Pin coverage on `BeatGridTickHostBindings` (33 slots), `AbgMidiStackBindings` (47), `MelodicPadVisualHostBindings` (45) at minimum.
- Update DAWG CLAUDE.md `INV-BINDINGS-VALIDATION-001` — note that the semantic ratchet (was deferred per L-LIM-006) is now live.
- Close DAWG Polish-1 P4 in [`project_polish_1_charter_open_2026_05_19.md`](C:/Users/Matic/.claude/projects/D--Projekti-DAWG/memory/project_polish_1_charter_open_2026_05_19.md).

**After Waves 5–6 land:** no DAWG-side work (external integrator credibility, no consumer-side change).

---

## 9. Out-of-scope (explicit non-goals)

Items deliberately NOT in this masterplan:

- **`Workspace` literal global on `execute`** — Lifeblood ships `RoslynSemanticView` (`Graph` / `Compilations` / `ModuleDependencies` / `Help` / `SymbolsOfKind` / `EdgesOfKind`) instead. Decision per Plan v4: literal `Workspace` is too low-level for AI consumers; the typed view is the right abstraction. NOT reopening.
- **Live-instance inspection** (`Workspace.LiveInstances<T>()` per LB-WIN-001) — would require a process bridge to the user's runtime, out of scope for a static-analysis tool. The 2026-04-11 DSP-audit session's success with `execute` reflection over LIVE workspace state is preserved as a one-off pattern, not promoted.
- **Math-benchmark harness** (LB-FR-006) — useful but tangential to Lifeblood's hexagonal "compiler truth in, AI context out" thesis. A separate benchmarking package would be a cleaner architecture; not part of this masterplan.
- **Graph delta queries** (LB-FR-007 `lifeblood_graph_delta(fromCommit, toCommit)`) — requires version-control integration in Domain, which violates the language-agnostic invariant. Deferred indefinitely.
- **`lifeblood_simulate_move`** (LB-FR-009) — asmdef refactor support. Out of scope until LB-INBOX-004's wedge work is fully consolidated; not in this masterplan.
- **JIT-execution patterns for `find_reads` / `find_writes`** (LB-FR-003) — additive to `find_references` but no current DAWG trigger. Tracked in inbox; not in this masterplan.
- **NEW language adapters beyond C#, TS, Python, native-clang** — adapter ecosystem is community-driven per `CLAUDE.md`. Lifeblood ships the framework; adapters are external contributors' work.

---

## 10. CLAUDE.md updates (Lifeblood-side)

After this masterplan lands, [`Lifeblood/CLAUDE.md`](../../CLAUDE.md) needs:

1. **Update port count in §"Port Interfaces" — 27 → 29 after Wave 2 lands** (Wave 4 keeps it at 29 because `GetAssignmentCoverage` extends EXISTING `ICompilationHost`, NOT a new port — corrected from v1 plan).
2. **Add new ports to the path list (Left only) — `IConditionalCompilationProvider`, `IPlayerDefineProfileSynthesizer`** (both Left side). Wave 4 does NOT add a port — `GetAssignmentCoverage` lives on existing `ICompilationHost` (Left). Wave 5 does NOT add a port (corrected from v1).
3. **Update §"MCP Tools" link** — count moves 29 → 30 (Wave 4 adds `lifeblood_assignment_coverage`).
4. **Add `INV-MULTI-DEFINE-*` + `INV-ASSIGNMENT-COVERAGE-*` + `INV-TEST-IMPACT-REFLECTION-*` to invariant tree.** Domain assignment per existing structure:
   - `INV-MULTI-DEFINE-*` (now 5 invariants 001..005, not 3) → `docs/invariants/pipeline.md` (compilation-pipeline concern).
   - `INV-TEST-IMPACT-REFLECTION-*` → `docs/invariants/tools.md` (tool-semantics concern).
   - `INV-ASSIGNMENT-COVERAGE-*` → `docs/invariants/tools.md`.
   - `INV-WIRE-CONTRACT-002..004` → `docs/invariants/mcp-protocol.md`.

## 11. CLAUDE.md updates (DAWG-side)

After per-wave landings, [`DAWG/CLAUDE.md`](D:/Projekti/DAWG/CLAUDE.md) needs:

1. **`INV-LIFEBLOOD-002b/c` closure** — strip when L-LIM-001 / L-LIM-002 close.
2. **New INV: `INV-LIFEBLOOD-005`** — multi-define mandate for cross-platform reachability decisions.
3. **MCP tool count refresh** — 29 → 30 in §"Lifeblood MCP".
4. **L-LIM table refresh** in memory file (NOT in CLAUDE.md — closures live in tracking file).

---

## 12. Sign-off gates per wave

Every wave lands ONLY after:

1. All named INV ratchets green on Lifeblood self.
2. Wave's DAWG dogfood fixture executes successfully.
3. `CHANGELOG.md` `[Unreleased]` entry written (one bullet per shipped INV per the existing convention).
4. `docs/IMPROVEMENT_INBOX.md` cross-referenced (per the "How entries land here" §SHIPPED protocol).
5. `docs/STATUS.md` counts re-anchored if tool / port / test / invariant count moved.
6. **DAWG re-verification:** the wave's DAWG-side L-LIM tracking entry updated with v0.7.X evidence.

No wave ships partial. No wave skips DAWG re-verification.

---

## 13. Final notes

This masterplan is **plan-only** as of 2026-05-24. **v2 revision** applies fixes to 8 findings from a reviewer pass on the v1 draft:

- v1 → v2 port count math: Wave 2 adds 2 ports (not 1), Wave 4 adds 0 (was 1), Wave 5 adds 0 (was 1, contradicted itself). Final: 27 → 29 ports + 29 → 30 tools.
- v1 → v2 Wave 1: changed-file repro added for L-LIM-002. Atom 1.5 added to settle invariant-count baseline before sign-off gates quote it.
- v1 → v2 Wave 2: Player profile SYNTHESIS replaces naive `<DefineConstants>` permutation iteration. New `IPlayerDefineProfileSynthesizer` port + Unity platform-symbol matrix data file.
- v1 → v2 Wave 3: dropped invented port; additive extension of existing `public static class TestImpactAnalyzer` + `TestClassImpact` record. Tightened heuristic — FQN-first, `nameof(T)` via operation tree, short-name only with namespace context or uniqueness.
- v1 → v2 Wave 4: moved `GetAssignmentCoverage` to existing `ICompilationHost` (Left port, mirrors `GetStaticTables`). Confidence tier per-site (Proven for direct, Advisory for interprocedural/aliased/branched).
- v1 → v2 Wave 5: dropped contradictory `IVersionedToolRegistry` port; replay-compatibility infrastructure is test-side + schema-side, no new runtime surface.

No Lifeblood-repo code changes shipped this session.

**Recommended start order (per dependency graph + DAWG-side impact):**

```
Wave 1 (re-probe, 1.5 sessions)
  ↓
Wave 4 (assignment-coverage — unblocks DAWG Polish-1 P4, ~5 sessions)
  ↓ parallel
  ├─ Wave 2 (multi-define — long, ~8 sessions)
  └─ Wave 3 (test_impact reflection — short, ~3 sessions)
  ↓
Wave 5 (contract freeze — independent, can run anytime, ~5 sessions)
  ↓
Wave 6 (public proof — depends on Waves 2+4 case studies, ~6 sessions)
  ↓
Wave 7 (DAWG-side integration — incremental, ~1–2 sessions per upstream wave)
```

**Wave 4 first** after Wave 1 because it's the single shipment that closes a load-bearing DAWG blocker (Polish-1 P4). Wave 2 is the largest investment with the highest-leverage payoff for DAWG's audio / mobile / IL2CPP path. Wave 3 is small and self-contained; can slot in whenever convenient.

If you have one week of focused work and want maximum DAWG-side value: **Wave 1 + Wave 4 + Wave 3**. That's ~10 sessions, closes 4 of 6 L-LIM entries, and unblocks Polish-1 P4 + ratchet-suite recall.

If you have a full month: add **Wave 2** (multi-define) — the highest-impact single capability for the cross-platform reality DAWG actually ships.

If you have a full quarter: complete **Waves 5–6** and Lifeblood crosses from "internally disciplined" to "externally credible" per the original LB-INBOX-005 framing.

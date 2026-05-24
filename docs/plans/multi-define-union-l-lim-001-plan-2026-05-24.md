# L-LIM-001 Multi-Define Union Analyze — Implementation Plan

**Status:** Stage 1 plan, implementation Wave 6 (Phase 2 entry point)
**Tracking:** Closes DAWG L-LIM-001 (preprocessor-guarded callsites invisible to `find_references` / `dependants`)
**Last shipped Stage 0 fixes:** LB-TRACK-20260524-025 / -026 / -027 + INV-LIST-SHAPE-UNIFORM-001
**Authoring date:** 2026-05-24
**Revision:** 2026-05-24 post-reviewer tightening — 5-profile scope replaced with 2-profile MVP (Editor + Player) per L-LIM-001 root-cause analysis; `Edge.Profiles[]` empty-array ambiguity closed by omitting the field entirely on single-profile analyze.
**Estimated scope:** ~1000–1800 LOC + ~60 new test cases (2-profile MVP); 5-profile platform variant is a separate v2 atom that piggybacks on the same port

---

## Problem

Lifeblood currently compiles each project under ONE define-set (the Editor profile on Unity workspaces). Callsites guarded by `#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR` / `#if UNITY_WEBGL` / etc. are excluded from the compilation unit, so semantic call edges to those guarded callsites never form. Downstream tools (`find_references`, `dependants`, `dead_code`, `blast_radius`, `port_health`) systematically undercount mobile / web / standalone-only paths.

**Concrete trap:** an agent running `lifeblood_dead_code` on a symbol that's only consumed from inside `#if UNITY_ANDROID && !UNITY_EDITOR` sees zero callers, declares the symbol safe to remove, and breaks the mobile boot path. DAWG-side `reference_lifeblood_known_limitations.md` documents the canonical instance: `AdaptiveBeatGrid.Bootstrap.Services.cs:46` calls `RequestConfiguration` + `AudioRuntimeProfilePolicy.Resolve` + others under `#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR`.

## Solution shape

Compile each project under N define-set permutations (one per build-target profile), walk edges across ALL variants, UNION the resulting semantic graphs. Existing graph-level edge dedup (`INV-STREAM-005`) makes union safe at the edge layer — same `(sourceId, targetId, kind)` triple emitted twice collapses to one edge.

## Architecture (proposed)

### 1. New port: `IDefineProfileResolver`

Lives in `Lifeblood.Application.Ports.Left`. Returns the list of define-set profiles a project should be analyzed under.

```csharp
public interface IDefineProfileResolver
{
    IReadOnlyList<DefineProfile> ResolveProfiles(string projectRoot);
}

public sealed class DefineProfile
{
    public required string Name { get; init; }              // "Editor" / "Android" / "iOS" / "WebGL" / "Standalone"
    public required string[] AddDefines { get; init; }       // symbols ACTIVE under this profile
    public required string[] RemoveDefines { get; init; }    // symbols REMOVED relative to module's declared set
}
```

Default resolver returns ONE profile (Editor) on every workspace — preserves existing behavior. The resolver is a port so workspace-specific profiles (Unity build targets, Xamarin platforms, .NET TFM matrix) ship as sibling adapters without touching the core analyzer.

### 2. Unity-aware adapter: `UnityDefineProfileResolver` (2-profile MVP)

Lives in `Lifeblood.Adapters.CSharp`. Recognizes Unity workspaces (`Library/` exists at root) and returns the canonical 2-profile set:

| Profile name | AddDefines | RemoveDefines (relative to csproj's PreprocessorSymbols) |
|---|---|---|
| `Editor` | (none — preserves baseline) | (none) |
| `Player` | (none — relies on baseline platform-target defines) | `UNITY_EDITOR`, `UNITY_EDITOR_WIN`, `UNITY_EDITOR_64`, `UNITY_EDITOR_OSX`, `UNITY_EDITOR_LINUX` |

**Rationale.** L-LIM-001's load-bearing discriminator is the `UNITY_EDITOR` axis: callsites guarded by `#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR` are invisible to Lifeblood because the workspace csproj already has UNITY_EDITOR active. The `Player` profile is the Editor baseline minus the Editor-discriminator symbols — every `#if !UNITY_EDITOR` branch becomes visible, every `#if UNITY_EDITOR && X` branch correctly excludes itself, and the `(UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR` canonical witness flips from "inactive" to "active" on Android-targeting Editor workspaces. The platform-target defines (`UNITY_ANDROID` / `UNITY_IOS` / `UNITY_WEBGL` / `PLATFORM_*` / `ENABLE_IL2CPP`) are ALREADY in the baseline csproj DefineConstants on a workspace whose Editor targets that platform — DAWG-side `compile_check` proves it (Editor target `Android` → `PLATFORM_ANDROID` + `UNITY_ANDROID` + `ENABLE_IL2CPP` all already active). The 5-platform variant earlier in this plan was a category error: those `AddDefines` are no-ops because the symbols are already present. The 2-profile MVP closes L-LIM-001's root cause with half the memory cost.

**Pre-fix attempt (rejected, archived for regression-trace).** The earlier plan revision specified a 5-profile set `Editor / Android / iOS / WebGL / Standalone` with per-platform `AddDefines = UNITY_<PLATFORM>, PLATFORM_<PLATFORM>, ENABLE_IL2CPP`. Rejected on review: the platform-target defines are already in baseline on a workspace targeting that platform, so each "add" was a no-op. The REAL discriminator the L-LIM-001 trap fires on is the `UNITY_EDITOR` axis, not the platform-target axis. The 5-profile variant becomes a v2 enhancement once the 2-profile MVP closes the load-bearing root cause and per-platform code-path inspection becomes a separate distinct use-case.

**v2 (not Wave 6).** Platform-specific profile vocabulary (`Android` / `iOS` / `WebGL` / `Standalone` discriminating which platform-target's branches activate) requires hooking Unity's `UnityEditor.PlayerSettings.GetScriptingDefineSymbolsForGroup` API at workspace-discovery time — the platform-target defines vary by Unity version + scripting backend + active player settings, and inferring them statically requires reading + interpreting `ProjectSettings/ProjectSettings.asset`. That's a v2 atom that piggybacks on the same `IDefineProfileResolver` port; v1 ships with the 2-profile MVP because it solves L-LIM-001's root cause without that dependency.

### 3. Multi-profile compilation pipeline

`RoslynWorkspaceAnalyzer.AnalyzeWorkspace` gains an optional `defineProfiles: string[]?` parameter (default null → single Editor profile). When set:

1. For each module, the existing `PreprocessorSymbols[]` set is the BASE.
2. For each requested profile, compute `activeDefines = (BASE - RemoveDefines) ∪ AddDefines` deterministically (ordinal-sort the result for byte-stable provenance).
3. Build ONE `CSharpCompilation` per (module, profile) pair, with `CSharpParseOptions.WithPreprocessorSymbols(activeDefines)`.
4. Cache compilations under composite key `(moduleName, profileName)`.
5. Run the existing edge-extraction pipeline against EACH compilation. Emit edges with provenance tag `Edge.Profiles[]` (new field) — the set of profile names that observed the edge.
6. Edge dedup: same `(sourceId, targetId, kind)` from multiple profiles collapses; `Profiles[]` UNIONS.

### 4. Wire-shape additions

- `Edge.Profiles: string[]?` — profile names that emitted this edge. **Field is OMITTED entirely on single-profile analyze (back-compat: pre-multi-define wire shape).** Populated only under multi-profile analyze; empty array under multi-profile = bug (every edge MUST be observed by at least one profile). Eliminates the "empty array means Editor only" ambiguity — the absence of the field unambiguously signals single-profile analyze.
- `AnalyzeResponse.summary.profileCount: int` — how many profiles were analyzed this round. `1` for single-profile back-compat; `≥ 2` for multi-profile.
- `AnalyzeResponse.summary.perProfileEdgeCounts: Dict<string,int>?` — debug provenance, populated when `defineProfiles` is non-default; null otherwise.
- New optional input arg on `lifeblood_analyze`: `defineProfiles: string[]` (canonical names matching the `IDefineProfileResolver` vocabulary). On Unity workspaces with the 2-profile MVP: `["Editor", "Player"]` is the canonical multi-profile invocation.

### 5. Per-tool consumption

- `find_references` / `dependants` / `dead_code` / `blast_radius` / `port_health` / `authority_report` automatically benefit — they walk graph edges, and the graph now contains union-edges. No tool-side change needed beyond the optional `profileFilter: string[]?` arg that lets callers narrow back to a single profile (e.g. "what are the EDITOR-only callers of X?").
- `enum_coverage`, `static_tables`, `assignment_coverage` — operate on `IOperation` trees, which are per-compilation. Need explicit handling: aggregate across profile compilations OR caller specifies which profile to scan.

### 6. Memory + time budget

For DAWG-sized workspaces (90 modules):
- Single profile (back-compat): ~3.4 GB RSS, 40s wall-time (current baseline).
- 2-profile MVP (Editor + Player): expected ~5–7 GB RSS, ~70–90s wall-time. RAM dominated by retained `CSharpCompilation` objects (each holding syntax trees + semantic models). Memory ~2× single-profile baseline, wall-time ~2× single-profile.
- Sequential-with-eager-disposal default policy: each profile compiled, edges extracted, then compilation disposed before next profile starts → peak RAM stays close to single-profile baseline at the cost of higher wall-time. Opt-in `parallelProfiles:true` for analyze-time-sensitive workflows on workspaces with ≥ 16 GB RAM.
- v2 5-profile platform variant: expected ~10–14 GB RSS / ~150–180s wall-time. Out of Wave 6 scope.

## Implementation phases

### Wave 6.A: Port + default resolver (≈ 200 LOC)
- `IDefineProfileResolver` port in `Lifeblood.Application.Ports.Left`
- `DefaultDefineProfileResolver` adapter returning one Editor profile
- Wire through `RoslynWorkspaceAnalyzer.AnalyzeWorkspace` as optional dependency
- Pinned by `DefineProfileResolverTests` (3 facts: default returns one profile, custom resolver routes correctly, profile-name uniqueness)

### Wave 6.B: Multi-profile compile (≈ 400 LOC)
- `RoslynWorkspaceAnalyzer.AnalyzeWorkspace(defineProfiles: string[]?)`
- Profile-active-define computation (`BASE - Remove ∪ Add`, ordinal-sorted)
- Sequential per-profile compile loop with eager disposal
- Edge.Profiles[] field threaded through extractor → graph
- Pinned by `MultiProfileAnalyzeTests` (6 facts: single-profile back-compat, two-profile edge union, profile-disjoint edge attribution, sequential disposal frees RAM, parallel option, byte-stable profile ordering)

### Wave 6.C: UnityDefineProfileResolver — 2-profile MVP (≈ 150 LOC)
- The 2-profile Unity adapter (Editor + Player)
- Workspace detection (Library/ exists)
- Add/Remove vocabulary per profile, frozen as eternal `internal static readonly` arrays with INV pin
- Pinned by `UnityDefineProfileResolverTests` (6+ facts: Editor profile is identity, Player profile drops UNITY_EDITOR family, non-Unity workspace returns single Editor profile, asmdef-generated csproj is treated identically to SDK-style for define resolution, double-invocation is idempotent)

### Wave 6.D: Edge.Profiles[] wire shape + INV pin (≈ 200 LOC)
- Domain DTO field
- JSON graph importer/exporter round-trip
- `lifeblood_dependants` / `lifeblood_dependencies` / `find_references` surface the field
- Optional `profileFilter` arg on read tools
- INV-MULTI-DEFINE-UNION-001 pinned in `docs/invariants/csharp-adapter.md`
- Pinned by `EdgeProfilesWireShapeTests` (8 facts: empty array default, populated under multi-profile, JSON round-trip, profileFilter narrows correctly)

### Wave 6.E: Per-IOperation tools multi-profile policy (≈ 300 LOC)
- `enum_coverage` / `static_tables` / `assignment_coverage` accept optional `profileScope: string` (default = first profile = Editor)
- Wire shape adds `analyzedUnderProfile: string` so callers see which profile's IOperation tree drove the report
- Pinned by per-tool fixtures (≈ 3 facts each)

### Wave 6.F: DAWG dogfood + L-LIM-001 closure receipt (≈ 100 LOC test + docs)
- Live-MCP probe against DAWG `RequestConfiguration` / `AudioRuntimeProfilePolicy.Resolve` / `AudioRuntimeProfilePersistenceLocator.Current` (the L-LIM-001 canonical witnesses): pre-fix shows 1 caller, post-fix shows 2 callers under Android+iOS+WebGL multi-profile.
- `reference_lifeblood_known_limitations.md` L-LIM-001 section marked CLOSED with re-probe receipt.
- `CHANGELOG.md` entry in `[Unreleased]` Wave 6.

## Trade-offs + open questions

### Q1: How exhaustive should the Unity define vocabulary be?

The conservative subset (UNITY_EDITOR, UNITY_ANDROID, UNITY_IOS, UNITY_WEBGL, UNITY_STANDALONE_*) covers the load-bearing discriminators that gate platform-specific code in real DAWG usage. The complete Unity vocabulary is ~300 symbols per profile, varies by Unity version + scripting backend, and could be hooked by calling Unity's `UnityEditor.PlayerSettings.GetScriptingDefineSymbolsForGroup` at workspace-discovery time. v1 ships conservative; v2 (separate atom) wires the full set via a Unity-Editor sidecar process.

### Q2: Should `find_references` etc. union-by-default or require opt-in?

Default behavior is union: every read-side tool sees the union graph. Single-profile filtering is opt-in via `profileFilter:` arg. The argument: removing the trap is the load-bearing fix; if a caller wants only Editor refs they pass `profileFilter:["Editor"]`. The wire shape stays byte-stable for callers who don't pass `defineProfiles` to analyze.

### Q3: Memory under sequential-disposal — how much do we actually save?

Pre-implementation estimate: peak RAM stays ≤ 1.3× single-profile baseline if compilations are disposed eagerly between profiles. Per-profile edge sets accumulate in the graph (small, ~50 MB for DAWG-sized workspace × N profiles). Need to measure in Wave 6.B with a parametric memory probe.

### Q4: Cross-profile resolver: do incremental analyze + multi-profile compose?

Incremental walks files-changed-since-last-analyze. Under multi-profile, "files changed" applies to all profile-views of the same file equally — the incremental delta multiplies linearly. Edge-preservation policy (`INV-INCREMENTAL-XREF-001`) extends naturally. Wave 6.B includes a multi-profile incremental fixture.

## Anti-goals

- **No per-symbol special casing.** Profile-active-define computation is a pure set operation; no special-case lookups by symbol name.
- **No hardcoded Unity workspace detection beyond `Library/` existence.** The `UnityDefineProfileResolver` is one sibling adapter; the core analyzer does not know it exists.
- **No silent profile selection.** A caller MUST pass `defineProfiles:` explicitly for multi-profile to engage. Default behavior stays single-Editor for back-compat.
- **No half-implementation.** Wave 6 ships when all six phases are green + DAWG L-LIM-001 closure receipt is recorded. No "Editor + Android only" half-step.

## Dependencies

- Lifeblood `v0.7.8+` (current). No upstream Roslyn changes required — `CSharpParseOptions.WithPreprocessorSymbols` is stable.
- DAWG `reference_lifeblood_known_limitations.md` L-LIM-001 — closure receipt depends on Wave 6.F.

## Schedule

Open-ended. Implementation cost is the load-bearing variable: scoping suggests 2–4 weeks of focused work + 1 week of DAWG dogfood validation. Tag landing target: `v0.8.0-multi-define`. Until then, callers continue using the L-LIM-001 workaround (source-text grep cross-check per the L-LIM file's "Workaround for callers" section).

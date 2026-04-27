# Status

Dogfood-verified. 664 tests. **25 MCP tools** (15 read + 10 write). **26 port interfaces**. Truth envelope on every read-side response (`INV-ENVELOPE-001`). Native usage and timing reporting on every `lifeblood_analyze` response. Architectural-invariant introspection via `lifeblood_invariant_check` walks `<root>/CLAUDE.md` + `<root>/AGENTS.md` + any `<root>/docs/invariants/**.md` tree (`LB-FR-023`); five authoring shapes recognised (A/B/C/D/E). Lifeblood self: **65 typed invariants across 31 categories** in `docs/invariants/` tree. Smart-dynamic shaping on `cycles` (`LB-FR-021`) and `context` (`LB-FR-022`) keeps responses inside conservative tool-result budgets even on multi-module Unity workspaces. File-mode `compile_check` (`LB-BUG-019`) resolves the owning compilation and swaps the existing tree, so module-owned files compile-check against their real reference set. CI green on Linux + Windows (4 jobs: build, TypeScript adapter, Python adapter, dogfood). Published on [NuGet](https://www.nuget.org/packages/Lifeblood).

<!-- portCount: 26 --><!-- testCount: 664 --><!-- toolCount: 25 -->

## Components

| Component | State |
|-----------|-------|
| Lifeblood.Domain | Immutable graph model, GraphBuilder (with file-level edge derivation + multi-parent partial-type Contains synthesis), GraphValidator, Evidence, ConfidenceLevel, short-name index, ResponseEnvelope + EnvelopeClassification (truth-envelope contract). |
| Lifeblood.Application | **26 port interfaces** including `IWorkspaceAnalyzer`, `ICompilationHost`, `IRuntimeAssemblyResolver` (Unity DLL probe), `ISymbolResolver`, `IUserInputCanonicalizer`, `IResponseDecorator` (truth envelope), `ISemanticSearchProvider`, `IDeadCodeAnalyzer`, `IUnityReachabilityProvider` (runtime-dispatch reachability), `IAuthorityReporter` (type-level authority report), `IPartialViewBuilder`, `Invariants.IInvariantProvider`, `IUsageProbe` + `IUsageCapture`, `IFileSystem`, `IBlastRadiusProvider`, `IRuleProvider`, `IProgressSink`, plus the left-side workspace ports. Every read-side handler routes through `ISymbolResolver` before any graph or workspace lookup, and through `IResponseDecorator` after producing its raw result so every read-side response carries a truth envelope (truth tier, confidence, staleness, evidence source, limitations). Resolution order: canonical, truncated method, kind correction (`INV-RESOLVER-006`), bare short name, extracted short name from kind-prefixed / qualified input (`INV-RESOLVER-005`). |
| Lifeblood.Adapters.CSharp | Roslyn workspace analyzer with streaming compilation and downgrading. Incremental re-analyze (timestamp-based, per-module, csproj-edit aware, asmdef-edit aware via `INV-UNITY-002`). Cross-assembly edge extraction, HintPath DLL loading. Per-module BCL ownership (HostProvided or ModuleProvided) decided at discovery. `CanonicalSymbolFormat` owns the symbol ID grammar. Per-module `AllowUnsafeCode` + `ImplicitUsings` parsed from csproj. `RoslynSemanticView` publishes typed read-only state plus sandbox helpers (`Help` / `SymbolsOfKind(string)` / `EdgesOfKind(string)`). `SnippetWrapper` auto-wraps bare compile_check snippets for library modules. `CsprojPaths` normalizes path-shaped attributes for cross-platform parsing. `UnityReachabilityAdapter` recognizes Unity entrypoint attributes + MonoBehaviour magic methods. `UnityAssemblyResolver` probes Library/ScriptAssemblies + Library/Bee/artifacts + Library/PackageCache for execute-time DLL injection. Extractor records `Properties["attributes"]`, `Properties["baseType"]`, and `Properties["classification"]` (forwarder shape) on every relevant symbol. |
| Lifeblood.Adapters.JsonGraph | Import and export with full metadata round-trip. |
| Lifeblood.Connectors.ContextPack | Context pack with GraphSummary, instruction file, reading order. |
| Lifeblood.Connectors.Mcp | Graph provider with blast radius delegation and file-level impact. `LifebloodSymbolResolver` is the reference `ISymbolResolver` with a deterministic primary file path picker for partial types, kind correction, and wrong-namespace short-name fallback. `LifebloodResponseDecorator` is the reference `IResponseDecorator`; classification table is injected from the host's tool registry at construction so registry and decorator cannot drift. `LifebloodAuthorityReporter` produces the authority report from a single graph walk. `LifebloodSemanticSearchProvider` tokenizes multi-word queries into ranked-OR scoring. `LifebloodDeadCodeAnalyzer` consults `IUnityReachabilityProvider` when injected. `LifebloodPartialViewBuilder`. `LifebloodInvariantProvider` walks well-known repo conventions dynamically (`<root>/CLAUDE.md` + `<root>/AGENTS.md` + any `<root>/docs/invariants/**.md`) via `IFileSystem`, parses each through `ClaudeMdInvariantParser` (five authoring shapes A/B/C/D/E), aggregates results across all sources with per-id duplicate detection, and caches per-file in `InvariantParseCache<T>`. `McpProtocolSpec` is the single source of truth for JSON-RPC wire constants. |
| Lifeblood.Analysis | Coupling, blast radius, cycles, tiers, rule validation. |
| Lifeblood.Server.Mcp | MCP server with **25 tools** over stdio (15 read-side + 10 write-side). Bidirectional Roslyn. `RoslynSemanticView` constructed once per `GraphSession.Load` and shared by reference across consumers. `ToolDefinition.EnvelopeClassification` is the registry-side source of truth for the truth envelope. McpDispatcher owns the wire protocol; Program.cs is a thin stdio I/O loop. |
| Lifeblood.CLI | analyze, context, export with centralized validation. |
| adapters/typescript | Standalone TS compiler API adapter. Self-analyzing. |
| adapters/python | Standalone ast-based adapter. Zero dependencies. Self-analyzing. |
| Unity bridge | 25 tools via `[McpForUnityTool]`. Sidecar process. Wire constants mirrored from `McpProtocolSpec` with a byte-equal ratchet. |
| Lifeblood.Tests | 664 tests. Extractors, golden repos, round-trip, architecture invariants, MCP server (including an end-to-end stdio-loop test that pins stdout purity), CLI pipeline, WorkspaceSession, security scanner, write-side integration, incremental re-analyze (file + csproj + asmdef), file-level edges, cross-assembly edges, BCL ownership compilation, symbol resolver (truncated id, partial-type multi-parent, wrong-namespace fallback, kind correction), RoslynSemanticView script globals + Help / SymbolsOfKind / EdgesOfKind, ProcessUsageProbe, semantic search, SnippetWrapper, ClaudeMdInvariantParser (all five authoring shapes A/B/C/D/E), tree-walking InvariantProvider (CLAUDE.md + AGENTS.md + docs/invariants/**.md aggregation), Lifeblood-self invariant audit, response-envelope (per-tool classification ratchet + staleness math), Unity reachability (attribute matching incl. SettingsProvider/Shortcut/BurstCompile/NUnit lifecycle, magic-method on direct + transitive subclass, base-via-Properties walk, cycle safety, type-via-child propagation), execute robustness (target-profile selection, Unity DLL probe, sandbox helpers), authority report + forwarder classifier (PureForwarder / ThinWrapper / RealLogic body shapes), `cycles` / `context` smart-dynamic shaping (summarize / per-section caps / sections allowlist), `compile_check` file-mode owning-module resolution + tree replacement. |

## Rule Packs

Built-in architecture rule packs:
- [hexagonal](../packs/hexagonal/rules.json)
- [clean-architecture](../packs/clean-architecture/rules.json)
- [lifeblood](../packs/lifeblood/rules.json) (self-validating)

## Known Limitations

**`lifeblood_dead_code` accuracy.** Eight extractor false-positive classes have been closed (five in v0.6.4: interface dispatch, member access granularity, null-conditional property, lambda context, method-group references; three in v0.6.5: ctor `Calls` edge, field-initializer containing method, property accessor body). Plus the root-cause compilation gap (missing implicit global usings). The Unity reachability port (`INV-UNITY-001`) closes the MonoBehaviour magic-method false-positive class on Unity workspaces. **`LB-FP-003`** extends the Unity reflection roster with `[SettingsProvider]`, `[SettingsProviderGroup]`, `[Shortcut]`, `[OnOpenAsset]`, `[BurstCompile]`, `[MonoPInvokeCallback]`, the full NUnit fixture lifecycle, plus type-via-child propagation (a type is reachable if any directly-contained member carries an entrypoint attribute — closes the `[SettingsProvider]`-on-static-method-with-dead-host-type FP class). Lifeblood self-analysis: 3 type findings (all `Program` composition roots — runtime entry points). DAWG dogfood (87 modules): 1,095 findings → 729 post-P3 (-33%), then 6 → 4 type-level findings post-`LB-FP-003` (XRaySettingsProvider + MpServiceResets cleared). Remaining advisory candidates are structural (UI Toolkit `VisualElement` subclasses, audio callbacks on non-MonoBehaviour bases, reflection-based dispatch via `Type.GetType` + `MethodInfo.Invoke`). See `INV-DEADCODE-001` + `INV-UNITY-001`.

**Call-graph completeness.** Eight extractor gaps closed across v0.6.4 + v0.6.5 raised edge count substantially. `find_references`, `dependants`, `blast_radius`, and `file_impact` all benefit.

**Truth envelope.** Every read-side response ships an `envelope` field with truth tier / confidence / staleness / limitations. Errors deliberately do NOT carry envelopes. Per-tool classification lives on `ToolDefinition.EnvelopeClassification` in the registry (`INV-ENVELOPE-001`).

## Self-Analysis

```
$ lifeblood analyze --project .
Symbols : 2,191
Edges   : 10,194
Modules : 11
Types   : 264
Files   : 150
Violations : 0
Cycles  : 0

── usage (representative; exact numbers on every lifeblood_analyze response) ──
  Wall time : ~7-15 s
  CPU total : ~10-25 s
  CPU utilization : ~120-180% of one core
  Peak working set : ~430 MB (MCP retained, full analyze)
  GC collections : low single digits
──────────────────────────────────────────────────────────────────────────────
```

Lifeblood also audits its own invariants tree via `lifeblood_invariant_check`. The provider walks `<root>/CLAUDE.md`, `<root>/AGENTS.md` (none today), and every `*.md` under `<root>/docs/invariants/`:

```
> lifeblood_invariant_check { mode: "audit" }

totalCount    : 65
categories    : 31  (RESOLVER, BCL, STREAM, ADAPT, GRAPH, ANALYSIS, COMPFACT, …)
duplicates    : 0
parseWarnings : 0
sourcePaths   : [
  "D:/Projekti/Lifeblood/CLAUDE.md",
  "D:/Projekti/Lifeblood/docs/invariants/architecture.md",
  "D:/Projekti/Lifeblood/docs/invariants/csharp-adapter.md",
  "D:/Projekti/Lifeblood/docs/invariants/governance.md",
  "D:/Projekti/Lifeblood/docs/invariants/INDEX.md",
  "D:/Projekti/Lifeblood/docs/invariants/mcp-protocol.md",
  "D:/Projekti/Lifeblood/docs/invariants/pipeline.md",
  "D:/Projekti/Lifeblood/docs/invariants/resolver.md",
  "D:/Projekti/Lifeblood/docs/invariants/tools.md",
  "D:/Projekti/Lifeblood/docs/invariants/usage.md"
]
```

## Production Verification (DAWG)

Tested on a real 87-module Unity workspace (DAWG, ~400k+ LOC). Same workspace, two different call sites, two different memory profiles. Both are correct. Both are by design.

### MCP path (compilations retained for write-side tools)

```
> lifeblood_analyze projectPath="D:/Projekti/DAWG"

mode : full
summary.symbols : 53,882
summary.edges   : 180,814
summary.modules : 87
cycles  : 117 SCCs
```

Authority + classification + dead-code numbers from real DAWG dogfood:
- Methods classified by body shape: 18,985.
- `PureForwarder` count: 3,367 (direct ABG-extraction triage signal).
- Dead-code findings (with Unity reachability injected): 729 (down from 1,095 pre-P3, -33%); 4 type-level findings post-`LB-FP-003` (down from 6 — XRaySettingsProvider + MpServiceResets cleared).
- MonoBehaviour magic-method FPs: 13 (down from 378 pre-P3, -97%).
- Invariant tree discovery: 83 invariants across 25 categories aggregated from CLAUDE.md + AGENTS.md + `docs/invariants/**.md`, 0 parse warnings.

### CLI path (streaming, compilations released)

CLI mode uses streaming compilation and releases each `CSharpCompilation` after extraction (`Emit` to PE metadata reference). Peak working set stays well below the MCP profile because only one full Roslyn `Compilation` is held at a time. Use the CLI when you need a one-shot analyze or a graph export; use the MCP path when you need interactive write-side tools (`execute`, `find_references`, `rename`, ...).

### Why the memory profiles differ

The CLI takes one shot at the workspace, extracts the graph, and streams each compilation out via `Emit` to a lightweight PE metadata reference. Compilations are released after extraction. Peak working set stays moderate because only one full Roslyn `Compilation` is held at a time, and the downgraded references are ~10-100 KB each.

The MCP server retains compilations in memory because the write-side tools (`lifeblood_execute`, `lifeblood_find_references`, `lifeblood_rename`, `lifeblood_diagnose`, `lifeblood_compile_check`, ...) need to query the loaded workspace interactively. No retention, no follow-up queries.

The GC counts confirm this architectural difference. The CLI churns because objects are constantly allocated and released across the streaming pipeline. The MCP server barely collects because its object graph is stable once the workspace is loaded.

**Decision guide for downstream users:**

- Need one-shot analysis, a rules check, or a graph export? Use the CLI path. Sub-1 GB memory budget is enough on most workspaces.
- Need interactive MCP queries (`execute`, `find_references`, ...) after the analyze? Use the MCP server. Budget for the larger profile.
- Memory-constrained MCP session? Pass `readOnly: true` to `lifeblood_analyze` and the server falls back to the CLI streaming profile. Write-side tools are unavailable under `readOnly`.

Measured on AMD Ryzen 9 5950X (16 cores / 32 threads). Both blocks come from the native `usage` field on every `lifeblood_analyze` response, the CLI block to stderr and the MCP block inside the `tools/call` result JSON. No external measurement wrapper.

Multiple dogfood sessions found [50+ real bugs](DOGFOOD_FINDINGS.md). Examples: security bypasses, silent data loss, off-by-one boundaries, resource leaks, missing AST node types, memory architecture, BCL double-load, display-string match across the source/metadata boundary, partial-type last-write-wins, `INV-CANONICAL-001` (transitive dependency closure), `INV-RESOLVER-005` (wrong-namespace short-name fallback), `INV-RESOLVER-006` (kind correction), `INV-TOOLREG-001` (wire/internal split that unblocked MCP reconnect), `INV-DEADCODE-001` (extractor gap classes fixed across v0.6.4 + v0.6.5), `INV-UNITY-001` (Unity reachability port), `INV-ENVELOPE-001` (truth envelope), `INV-AUTHORITY-001` + `INV-FORWARDER-001` (authority report + forwarder classifier), `INV-EXECUTE-001` (Unity DLL probe + target-profile + sandbox introspection), `LB-FR-021` (cycles summarize/pagination), `LB-BUG-017` + `LB-BUG-018` + `LB-FR-023` (invariant parser shapes C/D/E + dynamic source discovery), `LB-BUG-019` (compile_check file-mode tree replacement), `LB-FR-022` (context smart-dynamic shaping), `LB-FP-003` (dead_code Unity Editor reflection roster + type-via-child propagation). Every fix carries a regression test.

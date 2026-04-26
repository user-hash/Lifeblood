# Status

Dogfood-verified. 632 tests. **25 MCP tools** (15 read + 10 write). **26 port interfaces**. Truth envelope on every read-side response (`INV-ENVELOPE-001`). Native usage and timing reporting on every `lifeblood_analyze` response. Architectural-invariant introspection via `lifeblood_invariant_check` against 70 typed invariants in CLAUDE.md. CI green on Linux + Windows (4 jobs: build, TypeScript adapter, Python adapter, dogfood). Published on [NuGet](https://www.nuget.org/packages/Lifeblood).

<!-- portCount: 26 --><!-- testCount: 632 --><!-- toolCount: 25 -->

## Components

| Component | State |
|-----------|-------|
| Lifeblood.Domain | Immutable graph model, GraphBuilder (with file-level edge derivation + multi-parent partial-type Contains synthesis), GraphValidator, Evidence, ConfidenceLevel, short-name index, ResponseEnvelope + EnvelopeClassification (truth-envelope contract). |
| Lifeblood.Application | **26 port interfaces** including `IWorkspaceAnalyzer`, `ICompilationHost`, `IRuntimeAssemblyResolver` (Unity DLL probe), `ISymbolResolver`, `IUserInputCanonicalizer`, `IResponseDecorator` (truth envelope), `ISemanticSearchProvider`, `IDeadCodeAnalyzer`, `IUnityReachabilityProvider` (runtime-dispatch reachability), `IAuthorityReporter` (type-level authority report), `IPartialViewBuilder`, `Invariants.IInvariantProvider`, `IUsageProbe` + `IUsageCapture`, `IFileSystem`, `IBlastRadiusProvider`, `IRuleProvider`, `IProgressSink`, plus the left-side workspace ports. Every read-side handler routes through `ISymbolResolver` before any graph or workspace lookup, and through `IResponseDecorator` after producing its raw result so every read-side response carries a truth envelope (truth tier, confidence, staleness, evidence source, limitations). Resolution order: canonical, truncated method, kind correction (`INV-RESOLVER-006`), bare short name, extracted short name from kind-prefixed / qualified input (`INV-RESOLVER-005`). |
| Lifeblood.Adapters.CSharp | Roslyn workspace analyzer with streaming compilation and downgrading. Incremental re-analyze (timestamp-based, per-module, csproj-edit aware, asmdef-edit aware via `INV-UNITY-002`). Cross-assembly edge extraction, HintPath DLL loading. Per-module BCL ownership (HostProvided or ModuleProvided) decided at discovery. `CanonicalSymbolFormat` owns the symbol ID grammar. Per-module `AllowUnsafeCode` + `ImplicitUsings` parsed from csproj. `RoslynSemanticView` publishes typed read-only state plus sandbox helpers (`Help` / `SymbolsOfKind(string)` / `EdgesOfKind(string)`). `SnippetWrapper` auto-wraps bare compile_check snippets for library modules. `CsprojPaths` normalizes path-shaped attributes for cross-platform parsing. `UnityReachabilityAdapter` recognizes Unity entrypoint attributes + MonoBehaviour magic methods. `UnityAssemblyResolver` probes Library/ScriptAssemblies + Library/Bee/artifacts + Library/PackageCache for execute-time DLL injection. Extractor records `Properties["attributes"]`, `Properties["baseType"]`, and `Properties["classification"]` (forwarder shape) on every relevant symbol. |
| Lifeblood.Adapters.JsonGraph | Import and export with full metadata round-trip. |
| Lifeblood.Connectors.ContextPack | Context pack with GraphSummary, instruction file, reading order. |
| Lifeblood.Connectors.Mcp | Graph provider with blast radius delegation and file-level impact. `LifebloodSymbolResolver` is the reference `ISymbolResolver` with a deterministic primary file path picker for partial types, kind correction, and wrong-namespace short-name fallback. `LifebloodResponseDecorator` is the reference `IResponseDecorator`; classification table is injected from the host's tool registry at construction so registry and decorator cannot drift. `LifebloodAuthorityReporter` produces the authority report from a single graph walk. `LifebloodSemanticSearchProvider` tokenizes multi-word queries into ranked-OR scoring. `LifebloodDeadCodeAnalyzer` consults `IUnityReachabilityProvider` when injected. `LifebloodPartialViewBuilder`. `LifebloodInvariantProvider` parses CLAUDE.md at runtime via `ClaudeMdInvariantParser` + `InvariantParseCache<T>`. `McpProtocolSpec` is the single source of truth for JSON-RPC wire constants. |
| Lifeblood.Analysis | Coupling, blast radius, cycles, tiers, rule validation. |
| Lifeblood.Server.Mcp | MCP server with **25 tools** over stdio (15 read-side + 10 write-side). Bidirectional Roslyn. `RoslynSemanticView` constructed once per `GraphSession.Load` and shared by reference across consumers. `ToolDefinition.EnvelopeClassification` is the registry-side source of truth for the truth envelope. McpDispatcher owns the wire protocol; Program.cs is a thin stdio I/O loop. |
| Lifeblood.CLI | analyze, context, export with centralized validation. |
| adapters/typescript | Standalone TS compiler API adapter. Self-analyzing. |
| adapters/python | Standalone ast-based adapter. Zero dependencies. Self-analyzing. |
| Unity bridge | 25 tools via `[McpForUnityTool]`. Sidecar process. Wire constants mirrored from `McpProtocolSpec` with a byte-equal ratchet. |
| Lifeblood.Tests | 632 tests. Extractors, golden repos, round-trip, architecture invariants, MCP server (including an end-to-end stdio-loop test that pins stdout purity), CLI pipeline, WorkspaceSession, security scanner, write-side integration, incremental re-analyze (file + csproj + asmdef), file-level edges, cross-assembly edges, BCL ownership compilation, symbol resolver (truncated id, partial-type multi-parent, wrong-namespace fallback, kind correction), RoslynSemanticView script globals + Help / SymbolsOfKind / EdgesOfKind, ProcessUsageProbe, semantic search, SnippetWrapper, ClaudeMdInvariantParser, InvariantProvider, Lifeblood-self invariant audit, response-envelope (per-tool classification ratchet + staleness math), Unity reachability (attribute matching, magic-method on direct + transitive subclass, base-via-Properties walk, cycle safety), execute robustness (target-profile selection, Unity DLL probe, sandbox helpers), authority report + forwarder classifier (PureForwarder / ThinWrapper / RealLogic body shapes). |

## Rule Packs

Built-in architecture rule packs:
- [hexagonal](../packs/hexagonal/rules.json)
- [clean-architecture](../packs/clean-architecture/rules.json)
- [lifeblood](../packs/lifeblood/rules.json) (self-validating)

## Known Limitations

**`lifeblood_dead_code` accuracy.** Eight false-positive classes have been closed (five in v0.6.4: interface dispatch, member access granularity, null-conditional property, lambda context, method-group references; three in v0.6.5: ctor `Calls` edge, field-initializer containing method, property accessor body). Plus the root-cause compilation gap (missing implicit global usings). The Unity reachability port (`INV-UNITY-001`) closes the MonoBehaviour magic-method false-positive class on Unity workspaces. Lifeblood self-analysis: 8 findings (most are runtime entry points). DAWG dogfood (87 modules): 1095 findings reduced to 729 (-33%), MonoBehaviour-magic FPs from 378 to 13 (-97%). Remaining FPs are structural (UI Toolkit `VisualElement` subclasses, audio callbacks on non-MonoBehaviour bases, reflection-based dispatch). See `INV-DEADCODE-001` + `INV-UNITY-001`.

**Call-graph completeness.** Eight extractor gaps closed across v0.6.4 + v0.6.5 raised edge count substantially. `find_references`, `dependants`, `blast_radius`, and `file_impact` all benefit.

**Truth envelope.** Every read-side response ships an `envelope` field with truth tier / confidence / staleness / limitations. Errors deliberately do NOT carry envelopes. Per-tool classification lives on `ToolDefinition.EnvelopeClassification` in the registry (`INV-ENVELOPE-001`).

## Self-Analysis

```
$ lifeblood analyze --project .
Symbols : 2,132
Edges   : 9,908
Modules : 11
Types   : 262
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

Lifeblood also audits its own CLAUDE.md via `lifeblood_invariant_check`:

```
> lifeblood_invariant_check { mode: "audit" }

totalCount    : 70
duplicates    : 0
parseWarnings : 0
(per-category breakdown emitted in the full response;
 summary trimmed for brevity)
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
- Dead-code findings (with Unity reachability injected): 729 (down from 1,095 pre-P3, -33%).
- MonoBehaviour magic-method FPs: 13 (down from 378 pre-P3, -97%).

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

Multiple dogfood sessions found [50+ real bugs](DOGFOOD_FINDINGS.md). Examples: security bypasses, silent data loss, off-by-one boundaries, resource leaks, missing AST node types, memory architecture, BCL double-load, display-string match across the source/metadata boundary, partial-type last-write-wins, `INV-CANONICAL-001` (transitive dependency closure), `INV-RESOLVER-005` (wrong-namespace short-name fallback), `INV-RESOLVER-006` (kind correction), `INV-TOOLREG-001` (wire/internal split that unblocked MCP reconnect), `INV-DEADCODE-001` (extractor gap classes fixed across v0.6.4 + v0.6.5), `INV-UNITY-001` (Unity reachability port), `INV-ENVELOPE-001` (truth envelope), `INV-AUTHORITY-001` + `INV-FORWARDER-001` (authority report + forwarder classifier), `INV-EXECUTE-001` (Unity DLL probe + target-profile + sandbox introspection). Every fix carries a regression test.

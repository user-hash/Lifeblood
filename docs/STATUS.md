# Status

Dogfood-verified. 557 tests. **22 MCP tools** (12 read + 10 write). **22 port interfaces**. Native usage and timing reporting on every `lifeblood_analyze` response. Architectural-invariant introspection via `lifeblood_invariant_check` against 58 typed invariants in CLAUDE.md. CI green on Linux + Windows (4 jobs: build, TypeScript adapter, Python adapter, dogfood). Published on [NuGet](https://www.nuget.org/packages/Lifeblood).

<!-- portCount: 22 --><!-- testCount: 557 --><!-- toolCount: 22 -->

## Components

| Component | State |
|-----------|-------|
| Lifeblood.Domain | Immutable graph model, GraphBuilder (with file-level edge derivation + multi-parent partial-type Contains synthesis), GraphValidator, Evidence, ConfidenceLevel, short-name index. |
| Lifeblood.Application | **22 port interfaces** including `IWorkspaceAnalyzer`, `ICompilationHost`, `ISymbolResolver`, `ISemanticSearchProvider`, `IDeadCodeAnalyzer`, `IPartialViewBuilder`, `Invariants.IInvariantProvider`, `IUsageProbe` + `IUsageCapture`, `IFileSystem`, `IBlastRadiusProvider`, `IRuleProvider`, and the left-side adapter ports. Every read-side handler routes through `ISymbolResolver` before any graph or workspace lookup. Resolution order: canonical → truncated method → bare short name → extracted short name from kind-prefixed / qualified input (v0.6.3, `INV-RESOLVER-005`). |
| Lifeblood.Adapters.CSharp | Roslyn workspace analyzer with streaming compilation and downgrading. Incremental re-analyze (timestamp-based, per-module, csproj-edit aware). Cross-assembly edge extraction, HintPath DLL loading. Per-module BCL ownership (HostProvided or ModuleProvided) decided at discovery, closes the silent zero-result class on Unity, .NET Framework, and Mono workspaces. `CanonicalSymbolFormat` owns the symbol ID grammar. Per-module `AllowUnsafeCode` parsed from csproj. `RoslynSemanticView` publishes typed read-only state for tools. `SnippetWrapper` auto-wraps bare compile_check snippets for library modules (v0.6.3). `CsprojPaths` shared helper normalizes csproj path-shaped attributes so the architecture ratchet test and production discovery never drift on Linux (v0.6.3). Bidirectional compiler-as-a-service. |
| Lifeblood.Adapters.JsonGraph | Import and export with full metadata round-trip. |
| Lifeblood.Connectors.ContextPack | Context pack with GraphSummary, instruction file, reading order. |
| Lifeblood.Connectors.Mcp | Graph provider with blast radius delegation and file-level impact. `LifebloodSymbolResolver` is the reference `ISymbolResolver` implementation with a deterministic primary file path picker for partial types and the v0.6.3 wrong-namespace short-name fallback. `LifebloodSemanticSearchProvider` tokenizes multi-word queries into ranked-OR scoring. `LifebloodDeadCodeAnalyzer` (experimental / advisory; see `INV-DEADCODE-001`). `LifebloodPartialViewBuilder`. `LifebloodInvariantProvider` parses CLAUDE.md at runtime via `ClaudeMdInvariantParser` + `InvariantParseCache<T>`. `McpProtocolSpec` is the single source of truth for JSON-RPC wire constants. |
| Lifeblood.Analysis | Coupling, blast radius, cycles, tiers, rule validation. |
| Lifeblood.Server.Mcp | MCP server with **22 tools** over stdio (12 read-side + 10 write-side). Bidirectional Roslyn. `RoslynSemanticView` constructed once per `GraphSession.Load` and shared by reference across consumers. McpDispatcher owns the wire protocol; Program.cs is a thin stdio I/O loop. |
| Lifeblood.CLI | analyze, context, export with centralized validation. |
| adapters/typescript | Standalone TS compiler API adapter. Self-analyzing. |
| adapters/python | Standalone ast-based adapter. Zero dependencies. Self-analyzing. |
| Unity bridge | 22 tools via `[McpForUnityTool]`. Sidecar process. Wire constants mirrored from `McpProtocolSpec` with a byte-equal ratchet. |
| Lifeblood.Tests | 557 tests. Extractors, golden repos, round-trip, architecture invariants, MCP server (including an end-to-end stdio-loop test that pins stdout purity so future `Console.WriteLine` regressions are caught before shipping), CLI pipeline, WorkspaceSession, security scanner, write-side integration, incremental re-analyze (file + csproj), file-level edges, cross-assembly edges, BCL ownership compilation, symbol resolver (truncated id, partial-type multi-parent, wrong-namespace fallback), RoslynSemanticView script globals, ProcessUsageProbe, semantic search (including multi-token ranked-OR + xmldoc), SnippetWrapper (compile_check auto-wrap), ClaudeMdInvariantParser, InvariantProvider, Lifeblood-self invariant audit. |

## Rule Packs

Built-in architecture rule packs:
- [hexagonal](../packs/hexagonal/rules.json)
- [clean-architecture](../packs/clean-architecture/rules.json)
- [lifeblood](../packs/lifeblood/rules.json) (self-validating)

## Known Limitations (v0.6.3)

**`lifeblood_dead_code` accuracy.** v0.6.4 closed five false-positive classes and the root-cause compilation gap (missing implicit global usings). Self-analysis: 150 to 10 findings (93% reduction). Verified on a real 75-module Unity workspace: 80% true-positive rate on spot-checked candidates. Remaining false positives are structural: Unity reflection-based dispatch (`[RuntimeInitializeOnLoadMethod]`, lifecycle callbacks), runtime entry points, static field initializer method-groups. See `INV-DEADCODE-001`.

**Call-graph completeness.** v0.6.4 raised edge count by 42% (self-analysis: 5,777 to 8,223). `find_references`, `dependants`, `blast_radius`, and `file_impact` all benefit from the same extraction and compilation fixes.

## Self-Analysis

```
$ lifeblood analyze --project .
Symbols: 1834
Edges: 5708
Modules: 11
Types: 235

── usage (representative; exact numbers on every lifeblood_analyze response) ──
  Wall time : ~14 s (MCP retained) / ~5 s (CLI streaming)
  CPU total : ~23 s
  CPU utilization : ~180% of one core
  Peak working set : ~570 MB (CLI) / ~2,800 MB (MCP retained)
  GC collections : low single digits (MCP) / hundreds (CLI streaming)
──────────────────────────────────────────────────────────────────────────────
```

Lifeblood also audits its own CLAUDE.md via `lifeblood_invariant_check`:

```
> lifeblood_invariant_check { mode: "audit" }

totalCount : 57
categoryCounts :
  BCL        : 5    RESOLVER   : 5    STREAM     : 5
  ADAPT      : 4    GRAPH      : 4
  ANALYSIS   : 3    COMPFACT   : 3    CONN       : 3    MCP  : 3    VIEW : 3
  APP        : 2    DOMAIN     : 2    TEST       : 2    USAGE: 2
  CANONICAL  : 1    CHANGELOG  : 1    COMPROOT   : 1    ... (+ 8 more)
duplicates    : 0
parseWarnings : 0
```

## Production Verification

Tested on a real 75-module Unity workspace (400k+ LOC). Same workspace, two different call sites, two different memory profiles. Both are correct. Both are by design. Know which one applies to your use case.

### CLI path (streaming, compilations released)

```
$ lifeblood analyze --project D:/path/to/UnityProject

Symbols: 45,546
Edges: 89,449
Modules: 79
Types: 2,524
Cycles: 92

── usage ─────────────────────────────────────────────────
  Wall time : ~14 s
  CPU total : ~23 s
  CPU utilization : ~160% of one core
  Peak working set : ~570 MB
  GC collections : gen0=high, gen1=moderate, gen2=a few
  Phases : analyze : ~14 s / validate : ~90 ms
──────────────────────────────────────────────────────────
```

### MCP path (compilations retained for write-side tools)

```
> lifeblood_analyze projectPath="D:/path/to/UnityProject"
  (returns JSON with summary + usage)

mode : full
summary.symbols : 45,546
summary.edges : 89,449
summary.modules : 79
summary.types : 2,524
wallTimeMs : ~32,000
cpuTimeTotalMs : ~58,000
cpuUtilization% : ~180
peakWsMb : ~2,800
peakPrivateMb : ~2,950
hostCores : 32
gc gen0/1/2 : low single digits
phases:
  analyze : ~32 s
  validate : ~90 ms
```

Exact numbers are surfaced live on every `lifeblood_analyze` response via the `usage` field, so citations stay honest across releases without manual measurement chores.

### Why the memory profiles differ by ~4x

The CLI takes one shot at the workspace, extracts the graph, and streams each compilation out via `Emit` to a lightweight PE metadata reference. Compilations are released after extraction. Peak working set stays under 600 MB because only one full Roslyn `Compilation` is held at a time, and the downgraded references are ~10–100 KB each.

The MCP server retains compilations in memory because the write-side tools (`lifeblood_execute`, `lifeblood_find_references`, `lifeblood_rename`, `lifeblood_diagnose`, `lifeblood_compile_check`, etc.) need to query the loaded workspace interactively. No retention, no follow-up queries. Peak working set lands around 2.5-3 GB on a 75-module workspace because every compilation stays referenced for the life of the session.

The GC counts confirm this architectural difference. The CLI churns (hundreds of gen0) because objects are constantly allocated and released across the streaming pipeline. The MCP server barely collects (low single digits) because its object graph is stable once the workspace is loaded.

**Decision guide for downstream users:**

- Need one-shot analysis, a rules check, or a graph export? Use the CLI path. Sub-1 GB memory budget is enough.
- Need interactive MCP queries (`execute`, `find_references`, etc.) after the analyze? Use the MCP server. Budget for 3 GB on a 75-module workspace, 4 GB on larger.
- Memory-constrained MCP session? Pass `readOnly: true` to `lifeblood_analyze` and the server falls back to the CLI streaming profile. Write-side tools are unavailable under `readOnly`. You get graph-only queries in exchange for the memory savings.

Measured on AMD Ryzen 9 5950X (16 cores / 32 threads). Both blocks come from the native `usage` field on every `lifeblood_analyze` response, the CLI block to stderr and the MCP block inside the `tools/call` result JSON. No external measurement wrapper.

Seven+ dogfood sessions found [50+ real bugs](DOGFOOD_FINDINGS.md). Examples: security bypasses, silent data loss, off-by-one boundaries, resource leaks, missing AST node types, memory architecture, BCL double-load, display-string match across the source/metadata boundary, partial-type last-write-wins, `INV-CANONICAL-001` (transitive dependency closure), `INV-RESOLVER-005` (wrong-namespace short-name fallback), `INV-TOOLREG-001` (wire/internal split that unblocked MCP reconnect), `INV-DEADCODE-001` (five false-positive classes fixed in v0.6.4, 150 to 10 findings). Every fix carries a regression test.

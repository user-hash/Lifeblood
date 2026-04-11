# Status

Dogfood-verified. 405 tests. 21 MCP tools (11 read + 10 write). 21 port interfaces. Native usage and timing reporting on every `lifeblood_analyze` response. CI green (4 jobs: build, TypeScript adapter, Python adapter, dogfood). Published on [NuGet](https://www.nuget.org/packages/Lifeblood).

<!-- portCount: 21 --><!-- testCount: 405 --><!-- toolCount: 21 -->

## Components

| Component | State |
|-----------|-------|
| Lifeblood.Domain | Immutable graph model, GraphBuilder (with file-level edge derivation + multi-parent partial-type Contains synthesis), GraphValidator, Evidence, ConfidenceLevel, short-name index. |
| Lifeblood.Application | 17 port interfaces (including `IUsageProbe` + `IUsageCapture` + `ISymbolResolver`), AnalyzeWorkspaceUseCase, GenerateContextUseCase. ISymbolResolver port routes every read-side handler through one resolver. Resolution order: canonical, then truncated method, then bare short name. |
| Lifeblood.Adapters.CSharp | Roslyn workspace analyzer with streaming compilation and downgrading. Incremental re-analyze (timestamp-based, per-module, csproj-edit aware). Cross-assembly edge extraction, HintPath DLL loading. Per-module BCL ownership (HostProvided or ModuleProvided) decided at discovery. Fixes the silent zero-result class on Unity, .NET Framework, and Mono workspaces. CanonicalSymbolFormat owns the symbol ID grammar. Per-module AllowUnsafeCode parsed from csproj. RoslynSemanticView publishes typed read-only state for tools. Bidirectional compiler-as-a-service. |
| Lifeblood.Adapters.JsonGraph | Import and export with full metadata round-trip. |
| Lifeblood.Connectors.ContextPack | Context pack with GraphSummary, instruction file, reading order. |
| Lifeblood.Connectors.Mcp | Graph provider with blast radius delegation and file-level impact. `LifebloodSymbolResolver` is the reference `ISymbolResolver` implementation, with a deterministic primary file path picker for partial types. |
| Lifeblood.Analysis | Coupling, blast radius, cycles, tiers, rule validation. |
| Lifeblood.Server.Mcp | MCP server with 21 tools over stdio (9 read-side + 10 write-side + lifeblood_search + lifeblood_dead_code + lifeblood_partial_view). Bidirectional Roslyn. RoslynSemanticView constructed once per GraphSession.Load and shared by reference across consumers. |
| Lifeblood.CLI | analyze, context, export with centralized validation. |
| adapters/typescript | Standalone TS compiler API adapter. Self-analyzing. |
| adapters/python | Standalone ast-based adapter. Zero dependencies. Self-analyzing. |
| Unity bridge | 21 tools via [McpForUnityTool]. Sidecar process. |
| Lifeblood.Tests | 344 tests. Extractors, golden repos, round-trip, architecture invariants, MCP server, CLI pipeline, WorkspaceSession, security scanner, write-side integration, incremental re-analyze (file + csproj), file-level edges, cross-assembly edges, BCL ownership compilation, symbol resolver (truncated id, partial-type multi-parent), RoslynSemanticView script globals, ProcessUsageProbe (12 probe tests + 3 use-case integration tests for the native usage reporting). |

## Rule Packs

Built-in architecture rule packs:
- [hexagonal](../packs/hexagonal/rules.json)
- [clean-architecture](../packs/clean-architecture/rules.json)
- [lifeblood](../packs/lifeblood/rules.json) (self-validating)

## Self-Analysis

```
$ lifeblood analyze --project .
Symbols: 1376
Edges: 3822
Modules: 11
Types: 174

── usage ─────────────────────────────────────────────────
  Wall time : 5,075 ms (5.1 s)
  CPU total : 7,296 ms
  user mode : 6,406 ms
  kernel mode : 890 ms
  CPU utilization : 143.8% of one core
  CPU avg per core : 4.5% across 32 logical cores
  Peak working set : 212 MB
  Peak private bytes : 148 MB
  GC collections : gen0=11 gen1=6 gen2=2
  Phases :
  analyze : 5,071 ms
  validate : 3 ms
──────────────────────────────────────────────────────────
```

## Production Verification

Tested on a real 75-module Unity workspace (400k+ LOC). Same workspace, two different call sites, two different memory profiles. Both are correct. Both are by design. Know which one applies to your use case.

### CLI path (streaming, compilations released)

```
$ lifeblood analyze --project D:/path/to/UnityProject

Symbols: 44569
Edges: 87238
Modules: 75
Types: 2439
Cycles: 91

── usage ─────────────────────────────────────────────────
  Wall time : 32,644 ms (32.6 s)
  CPU total : 53,687 ms
  user mode : 47,109 ms
  kernel mode : 6,578 ms
  CPU utilization : 164.5% of one core
  CPU avg per core : 5.1% across 32 logical cores
  Peak working set : 571 MB
  Peak private bytes : 484 MB
  GC collections : gen0=197 gen1=108 gen2=34
  Phases :
  analyze : 32,570 ms
  validate : 73 ms
──────────────────────────────────────────────────────────
```

### MCP path (compilations retained for write-side tools)

```
> lifeblood_analyze projectPath="D:/path/to/UnityProject"
  (returns JSON with summary + usage)

mode : full
summary.symbols : 44,607
summary.edges : 87,306
summary.modules : 75
summary.types : 2,443
wallTimeMs : 34,305
cpuTimeTotalMs : 59,203
  user : 53,250
  kernel : 5,953
cpuUtilization% : 172.6
peakWsMb : 2,512
peakPrivateMb : 2,576
hostCores : 32
gc gen0/1/2 : 2 / 1 / 1
phases:
  analyze : 34,200 ms
  validate : 104 ms
```

### Why the memory profiles differ by ~4x

The CLI takes one shot at the workspace, extracts the graph, and streams each compilation out via `Emit` to a lightweight PE metadata reference. Compilations are released after extraction. Peak working set stays under 600 MB because only one full Roslyn `Compilation` is held at a time, and the downgraded references are ~10–100 KB each.

The MCP server retains compilations in memory because the write-side tools (`lifeblood_execute`, `lifeblood_find_references`, `lifeblood_rename`, `lifeblood_diagnose`, `lifeblood_compile_check`, etc.) need to query the loaded workspace interactively. No retention, no follow-up queries. Peak working set lands around 2.5 GB on a 75-module workspace because every compilation stays referenced for the life of the session.

The GC counts confirm this architectural difference. The CLI churns (`gen0=197, gen1=108, gen2=34`) because objects are constantly allocated and released across the streaming pipeline. The MCP server barely collects (`gen0=2, gen1=1, gen2=1`) because its object graph is stable once the workspace is loaded.

**Decision guide for downstream users:**

- Need one-shot analysis, a rules check, or a graph export? Use the CLI path. Sub-1 GB memory budget is enough.
- Need interactive MCP queries (`execute`, `find_references`, etc.) after the analyze? Use the MCP server. Budget for 3 GB on a 75-module workspace, 4 GB on larger.
- Memory-constrained MCP session? Pass `readOnly: true` to `lifeblood_analyze` and the server falls back to the CLI streaming profile. Write-side tools are unavailable under `readOnly`. You get graph-only queries in exchange for the memory savings.

Measured on AMD Ryzen 9 5950X (16 cores / 32 threads). Both blocks come from the native `usage` field on every `lifeblood_analyze` response, the CLI block to stderr and the MCP block inside the `tools/call` result JSON. No external measurement wrapper.

The wall time is an order of magnitude better than the figures older docs quoted (around 90 s). The CLI peak memory is an order of magnitude better than the older 4 GB figure. The MCP retained peak is close to the older 4 GB figure, which is consistent: the old measurement was almost certainly taken against the MCP server, not the CLI, and was recorded without distinguishing the two paths. The `usage` block is now how the project tracks these numbers against the codebase, so they stay honest without per-release measurement chores.

Edge count grew by more than 9,000 after the v0.6.0 BCL ownership and multi-parent GraphBuilder fixes. Call-graph extraction stops returning null at every System usage in workspaces that ship their own BCL (Unity, .NET Framework, Mono), and partial types now produce one Contains edge per declaration file.

Seven dogfood sessions found [50+ real bugs](DOGFOOD_FINDINGS.md). Examples: security bypasses, silent data loss, off-by-one boundaries, resource leaks, missing AST node types, memory architecture, BCL double-load, display-string match across the source/metadata boundary, and partial-type last-write-wins. All fixed in-session.

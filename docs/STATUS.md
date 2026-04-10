# Status

Dogfood-verified. 344 tests. 18 MCP tools (8 read + 10 write). 15 port interfaces. Native usage and timing reporting on every `lifeblood_analyze` response. CI green (4 jobs: build, TypeScript adapter, Python adapter, dogfood). Published on [NuGet](https://www.nuget.org/packages/Lifeblood).

## Components

| Component | State |
|-----------|-------|
| Lifeblood.Domain | Immutable graph model, GraphBuilder (with file-level edge derivation + multi-parent partial-type Contains synthesis), GraphValidator, Evidence, ConfidenceLevel, short-name index. |
| Lifeblood.Application | 15 port interfaces, AnalyzeWorkspaceUseCase, GenerateContextUseCase. ISymbolResolver port routes every read-side handler through one resolver. Resolution order: canonical, then truncated method, then bare short name. |
| Lifeblood.Adapters.CSharp | Roslyn workspace analyzer with streaming compilation and downgrading. Incremental re-analyze (timestamp-based, per-module, csproj-edit aware). Cross-assembly edge extraction, HintPath DLL loading. Per-module BCL ownership (HostProvided or ModuleProvided) decided at discovery. Fixes the silent zero-result class on Unity, .NET Framework, and Mono workspaces. CanonicalSymbolFormat owns the symbol ID grammar. Per-module AllowUnsafeCode parsed from csproj. RoslynSemanticView publishes typed read-only state for tools. Bidirectional compiler-as-a-service. |
| Lifeblood.Adapters.JsonGraph | Import and export with full metadata round-trip. |
| Lifeblood.Connectors.ContextPack | Context pack with GraphSummary, instruction file, reading order. |
| Lifeblood.Connectors.Mcp | Graph provider with blast radius delegation and file-level impact. `LifebloodSymbolResolver` is the reference `ISymbolResolver` implementation, with a deterministic primary file path picker for partial types. |
| Lifeblood.Analysis | Coupling, blast radius, cycles, tiers, rule validation. |
| Lifeblood.Server.Mcp | MCP server with 18 tools over stdio. Bidirectional Roslyn. RoslynSemanticView constructed once per GraphSession.Load and shared by reference across consumers. |
| Lifeblood.CLI | analyze, context, export with centralized validation. |
| adapters/typescript | Standalone TS compiler API adapter. Self-analyzing. |
| adapters/python | Standalone ast-based adapter. Zero dependencies. Self-analyzing. |
| Unity bridge | 18 tools via [McpForUnityTool]. Sidecar process. |
| Lifeblood.Tests | 329 tests. Extractors, golden repos, round-trip, architecture invariants, MCP server, CLI pipeline, WorkspaceSession, security scanner, write-side integration, incremental re-analyze (file + csproj), file-level edges, cross-assembly edges, BCL ownership compilation, symbol resolver (truncated id, partial-type multi-parent), RoslynSemanticView script globals. |

## Rule Packs

Built-in architecture rule packs:
- [hexagonal](../packs/hexagonal/rules.json)
- [clean-architecture](../packs/clean-architecture/rules.json)
- [lifeblood](../packs/lifeblood/rules.json) (self-validating)

## Self-Analysis

```
$ lifeblood analyze --project .
Symbols: 1376
Edges:   3822
Modules: 11
Types:   174

── usage ─────────────────────────────────────────────────
  Wall time          :      5,075 ms  (5.1 s)
  CPU total          :      7,296 ms
    user mode        :      6,406 ms
    kernel mode      :        890 ms
  CPU utilization    :     143.8% of one core
  CPU avg per core   :       4.5% across 32 logical cores
  Peak working set   :        212 MB
  Peak private bytes :        148 MB
  GC collections     : gen0=11  gen1=6  gen2=2
  Phases             :
    analyze            :      5,071 ms
    validate           :          3 ms
──────────────────────────────────────────────────────────
```

## Production Verification

Tested on a real 75-module Unity workspace (400k+ LOC):

```
Symbols: 44569
Edges:   87238
Modules: 75
Types:   2439
Cycles: 91

── usage ─────────────────────────────────────────────────
  Wall time          :     32,644 ms  (32.6 s)
  CPU total          :     53,687 ms
    user mode        :     47,109 ms
    kernel mode      :      6,578 ms
  CPU utilization    :     164.5% of one core
  CPU avg per core   :       5.1% across 32 logical cores
  Peak working set   :        571 MB
  Peak private bytes :        484 MB
  GC collections     : gen0=197  gen1=108  gen2=34
  Phases             :
    analyze            :     32,570 ms
    validate           :         73 ms
──────────────────────────────────────────────────────────
```

Measured on AMD Ryzen 9 5950X (16 cores / 32 threads). All numbers come from the native `usage` block emitted by `lifeblood analyze` itself, not from an external measurement wrapper.

The wall time and peak memory are an order of magnitude better than the figures older docs quoted (which were around 90 s and 4 GB). The `usage` block is now how the project tracks these numbers against the codebase, so they stay honest without per-release measurement chores.

Edge count grew by more than 9,000 after the v0.6.0 BCL ownership and multi-parent GraphBuilder fixes. Call-graph extraction stops returning null at every System usage in workspaces that ship their own BCL (Unity, .NET Framework, Mono), and partial types now produce one Contains edge per declaration file.

Seven dogfood sessions found [50+ real bugs](DOGFOOD_FINDINGS.md). Examples: security bypasses, silent data loss, off-by-one boundaries, resource leaks, missing AST node types, memory architecture, BCL double-load, display-string match across the source/metadata boundary, and partial-type last-write-wins. All fixed in-session.

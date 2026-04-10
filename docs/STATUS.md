# Status

Dogfood-verified. 329 tests. 18 MCP tools (8 read + 10 write). 15 port interfaces. CI green (4 jobs: build, TypeScript adapter, Python adapter, dogfood). Published on [NuGet](https://www.nuget.org/packages/Lifeblood).

## Components

| Component | State |
|-----------|-------|
| Lifeblood.Domain | Immutable graph model, GraphBuilder (with file-level edge derivation + multi-parent partial-type Contains synthesis), GraphValidator, Evidence, ConfidenceLevel, short-name index. |
| Lifeblood.Application | 15 port interfaces, AnalyzeWorkspaceUseCase, GenerateContextUseCase. ISymbolResolver port routes every read-side handler through one resolver (canonical → truncated method → bare short name). |
| Lifeblood.Adapters.CSharp | Roslyn workspace analyzer with streaming compilation + downgrading. Incremental re-analyze (timestamp-based, per-module, csproj-edit aware). Cross-assembly edge extraction, HintPath DLL loading. Per-module BCL ownership (HostProvided / ModuleProvided) decided at discovery — fixes silent zero-result class on Unity / .NET Framework / Mono workspaces. CanonicalSymbolFormat owns the symbol ID grammar. Per-module AllowUnsafeCode parsed from csproj. RoslynSemanticView publishes typed read-only state for tools. Bidirectional compiler-as-a-service. |
| Lifeblood.Adapters.JsonGraph | Import and export with full metadata round-trip. |
| Lifeblood.Connectors.ContextPack | Context pack with GraphSummary, instruction file, reading order. |
| Lifeblood.Connectors.Mcp | Graph provider with blast radius delegation and file-level impact. LifebloodSymbolResolver — reference ISymbolResolver implementation with deterministic primary file path picker for partial types. |
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
$ lifeblood analyze --project . --rules lifeblood
Symbols: 1291
Edges:   3620
Modules: 11
Types:   165
Violations: 0
```

## Production Verification

Tested on a real 75-module Unity project (400k+ LOC, 2,404 types):

```
Symbols: 44,566
Edges:   87,233
Modules: 75
Types:   2,411
Cycles:  90
```

Edge count grew +9,107 after the v0.6.0 BCL ownership + multi-parent GraphBuilder fixes — call-graph extraction stops returning null at every System usage in workspaces that ship their own BCL (Unity, .NET Framework, Mono), and partial types now produce one Contains edge per declaration file.

Seven dogfood sessions found [50+ real bugs](DOGFOOD_FINDINGS.md) — security bypasses, silent data loss, off-by-one boundaries, resource leaks, missing AST node types, memory architecture, BCL double-load, display-string match across source/metadata boundary, partial-type last-write-wins. All fixed in-session.

# Status

Dogfood-verified. 281 tests. 17 MCP tools (7 read + 10 write). CI green (4 jobs: build, TypeScript adapter, Python adapter, dogfood). Published on [NuGet](https://www.nuget.org/packages/Lifeblood).

## Components

| Component | State |
|-----------|-------|
| Lifeblood.Domain | Immutable graph model, GraphBuilder (with file-level edge derivation), GraphValidator, Evidence, ConfidenceLevel. |
| Lifeblood.Application | 14 port interfaces, AnalyzeWorkspaceUseCase, GenerateContextUseCase. |
| Lifeblood.Adapters.CSharp | Roslyn workspace analyzer with streaming compilation + downgrading. Incremental re-analyze (timestamp-based, per-module). Bidirectional compiler-as-a-service. |
| Lifeblood.Adapters.JsonGraph | Import and export with full metadata round-trip. |
| Lifeblood.Connectors.ContextPack | Context pack with GraphSummary, instruction file, reading order. |
| Lifeblood.Connectors.Mcp | Graph provider with blast radius delegation and file-level impact. |
| Lifeblood.Analysis | Coupling, blast radius, cycles, tiers, rule validation. |
| Lifeblood.Server.Mcp | MCP server with 17 tools over stdio. Bidirectional Roslyn. |
| Lifeblood.CLI | analyze, context, export with centralized validation. |
| adapters/typescript | Standalone TS compiler API adapter. Self-analyzing. |
| adapters/python | Standalone ast-based adapter. Zero dependencies. Self-analyzing. |
| Unity bridge | 17 tools via [McpForUnityTool]. Sidecar process. |
| Lifeblood.Tests | 281 tests. Extractors, golden repos, round-trip, architecture invariants, MCP server, CLI pipeline, WorkspaceSession, security scanner, write-side integration, incremental re-analyze, file-level edges. |

## Rule Packs

Built-in architecture rule packs:
- [hexagonal](../packs/hexagonal/rules.json)
- [clean-architecture](../packs/clean-architecture/rules.json)
- [lifeblood](../packs/lifeblood/rules.json) (self-validating)

## Self-Analysis

```
$ lifeblood analyze --project . --rules lifeblood
Symbols: 1148
Edges:   3196
Modules: 11
Types:   150
Violations: 0
```

## Production Verification

Tested on a real 75-module Unity project (400k+ LOC, 2,404 types):

```
Symbols: 43,800
Edges:   70,600+
Modules: 75
Types:   2,404
Cycles:  34
```

Six dogfood sessions found [45 real bugs](DOGFOOD_FINDINGS.md) — security bypasses, silent data loss, off-by-one boundaries, resource leaks, missing AST node types, memory architecture. All fixed in-session.

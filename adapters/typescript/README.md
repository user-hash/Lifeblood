# TypeScript Adapter

**Status:** Implemented. Second language adapter proving Lifeblood's universal protocol.

Uses the TypeScript compiler API (`ts.createProgram` + `TypeChecker`) to extract a semantic graph and output it as `graph.json` conforming to `schemas/graph.schema.json`.

## Usage

```bash
cd adapters/typescript
npm install
npm run build

# Analyze a TypeScript project
node dist/index.js /path/to/ts-project > graph.json

# Feed into Lifeblood
dotnet run --project ../../src/Lifeblood.CLI -- analyze --graph graph.json
```

Self-analysis (the adapter analyzes itself):
```bash
node dist/index.js . > graph.json
dotnet run --project ../../src/Lifeblood.CLI -- analyze --graph graph.json
# Symbols: 49, Edges: 51, zero violations
```

## What It Extracts

- **Symbols:** modules, files, classes, interfaces, enums, type aliases, methods, properties, constructors
- **Edges:** inherits, implements, calls, references, contains
- **Evidence:** semantic (from TypeChecker), confidence: high

## Capability Profile

```json
{
  "discoverSymbols": true,
  "typeResolution": "high",
  "callResolution": "high",
  "implementationResolution": "high",
  "crossModuleReferences": "high",
  "overrideResolution": "none"
}
```

## Architecture

```
src/types.ts            Schema types matching graph.schema.json
src/symbol-extractor.ts Symbol extraction from AST + TypeChecker
src/edge-extractor.ts   Edge extraction with IsFromSource filtering
src/index.ts            Entry point, tsconfig parsing, deterministic output
```

Follows the same patterns as the Roslyn reference adapter:
- `IsFromSource` filter (no edges to node_modules / lib types)
- Deterministic output (sorted symbols and edges)
- Last-write-wins dedup for symbols
- Contains edges synthesized from parentId

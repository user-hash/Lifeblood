# TypeScript Adapter

**Status:** Not started. This is a contribution guide, not existing code.

## Approach

TypeScript has a full compiler API (`ts.createProgram`, `CompilerHost`, `SourceFile`). This is the closest thing to Roslyn outside of .NET. A strong adapter would:

1. Use `ts.createProgram` to get the full type-checked program
2. Walk `SourceFile` nodes for symbols
3. Use the `TypeChecker` for semantic resolution
4. Output `graph.json` conforming to `schemas/graph.schema.json`

## Capability Profile

```json
{
  "discoverSymbols": true,
  "typeResolution": "high",
  "callResolution": "high",
  "crossModuleReferences": "high",
  "overrideResolution": "high"
}
```

TypeScript's compiler API is mature enough for `"high"` across the board. With careful implementation, some capabilities could reach `"proven"`.

## Getting Started

See [docs/ADAPTERS.md](../../docs/ADAPTERS.md) for the full guide.

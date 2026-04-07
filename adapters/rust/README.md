# Rust Adapter

**Status:** Not started. This is a contribution guide, not existing code.

## Approach

Rust has `syn` for syntax parsing and `rust-analyzer` as a library for semantic analysis. A good adapter would:

1. Use `syn` for fast syntax-level parsing (types, functions, imports)
2. Optionally integrate `rust-analyzer` for semantic resolution
3. Use `cargo metadata` for module/crate discovery
4. Output `graph.json` conforming to `schemas/graph.schema.json`

## Capability Profile

Without rust-analyzer:
```json
{
  "discoverSymbols": true,
  "typeResolution": "bestEffort",
  "callResolution": "bestEffort",
  "crossModuleReferences": "bestEffort"
}
```

With rust-analyzer:
```json
{
  "discoverSymbols": true,
  "typeResolution": "high",
  "callResolution": "high",
  "crossModuleReferences": "high"
}
```

## Getting Started

See [docs/ADAPTERS.md](../../docs/ADAPTERS.md) for the full guide.

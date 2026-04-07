# Python Adapter

**Status:** Not started. This is a contribution guide, not existing code.

## Approach

Python has `ast` (built-in) for syntax and `mypy` for optional type checking. A good adapter would:

1. Use `ast` to discover files, classes, functions, imports
2. Optionally use `mypy` for type resolution (higher confidence)
3. Output `graph.json` conforming to `schemas/graph.schema.json`

## Capability Profile

```json
{
  "discoverSymbols": true,
  "typeResolution": "bestEffort",
  "callResolution": "bestEffort",
  "crossModuleReferences": "bestEffort",
  "overrideResolution": "none"
}
```

With mypy integration, `typeResolution` could reach `"high"`.

## Getting Started

See [docs/ADAPTERS.md](../../docs/ADAPTERS.md) for the full guide.

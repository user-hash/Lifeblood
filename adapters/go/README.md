# Go Adapter

**Status:** Not started. Looking for contributors.

## Approach

Go has `go/analysis`, `go/types`, and `go/packages`. Clean, modular analyzer framework. A good adapter would:

1. Use `go/packages` to discover modules and files
2. Use `go/types` for type resolution and symbol lookup
3. Use `go/analysis` patterns for structured passes
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

Go's tooling is mature. Interface implementation detection is implicit (structural typing) which makes `implementationResolution` an interesting challenge.

## Getting Started

See [docs/ADAPTERS.md](../../docs/ADAPTERS.md) for the full guide.

# Architecture

Lifeblood uses hexagonal architecture. The core is pure.

## Dependency Flow

```
Lifeblood.CLI ──→ Lifeblood.Core (pure, zero deps)
       │
       ├──→ Lifeblood.Adapters.CSharp ──→ Roslyn
       │
       └──→ Lifeblood.Reporters
```

**Lifeblood.Core** is a leaf. It references nothing. If it ever gets a PackageReference, the architecture is broken.

## Three Layers of Truth

Every piece of information in the graph has an origin and a confidence level.

### 1. Syntax Truth
What the parser proves directly from source text. File structure, declarations, imports, inheritance syntax, raw call expressions. Every adapter can provide this.

### 2. Semantic Truth
What the language toolchain resolves. Symbol identity, actual target of a reference, overload resolution, type resolution, cross-module edges. Only adapters with compiler-grade backends (Roslyn, TypeScript compiler) provide this at high confidence.

### 3. Derived Architectural Truth
What Lifeblood computes from the graph. Forbidden dependency violations, tier classifications, blast radius, dead code candidates, hub detection. This is pure core logic, language-independent.

These layers must stay separate. Blurring them kills trust.

## Capability-Aware Analysis

Not all adapters are equal. A Roslyn adapter produces proven type resolution. A Python ast-based adapter produces best-effort guesses for dynamic code.

Every adapter declares its capabilities:

```json
{
  "language": "python",
  "capabilities": {
    "discoverSymbols": true,
    "typeResolution": "bestEffort",
    "callResolution": "bestEffort",
    "crossModuleReferences": "bestEffort"
  }
}
```

Every analysis result carries the confidence level it was computed at. Consumers know if they are getting proof or a guess.

## The JSON Protocol

Language adapters do NOT need to be written in C#. Any tool that outputs a JSON file conforming to `schemas/graph.schema.json` is a valid adapter.

```
[Your language parser]  →  graph.json  →  lifeblood analyze --graph graph.json
```

This means:
- Python adapter: written in Python
- Go adapter: written in Go
- Rust adapter: written in Rust

The C# core reads the JSON graph and runs all analysis. The adapter and the core never share a process if you do not want them to.

## Invariants

See [CLAUDE.md](../CLAUDE.md) for the full list. Key ones:

- **INV-CORE-001**: Core has zero external dependencies
- **INV-CORE-002**: Core has zero references to any adapter
- **INV-CORE-003**: All analysis operates on SemanticGraph only
- **INV-GRAPH-001**: SymbolKind enum is language-agnostic
- **INV-ADAPTER-001**: Adapters implement ICodeParser, nothing else
- **INV-ANALYSIS-001**: All analyzers are stateless

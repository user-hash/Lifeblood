# Native Clang Adapter

**Status:** Planned. Stage 0 architectural charter only; no implementation code
has shipped yet.

This adapter will translate C/C++ projects into Lifeblood's universal semantic
graph using Clang/LLVM as the parser and semantic engine.

## Why This Exists

Lifeblood's current proven adapter is C# / Roslyn. Native C/C++ projects need a
different compiler brain. The native adapter provides that brain while keeping
the existing Lifeblood core language-agnostic.

The adapter is deliberately external:

```text
C/C++ project + compile_commands.json
  -> adapters/native-clang
  -> graph.json
  -> lifeblood analyze --graph graph.json
  -> existing Lifeblood read-side tools
```

No LLVM or Clang dependency belongs in `Lifeblood.Domain`,
`Lifeblood.Application`, `Lifeblood.Analysis`, or the existing Roslyn adapter.

## Planned Engine

Stage 1 targets a C++ LibTooling command-line tool. It will read a
`compile_commands.json` compilation database and run Clang over each translation
unit with the same include paths, defines, language mode, and generated config
headers that the real build uses.

Other native-code analysis engines can become sidecars later:

- CodeQL for dataflow/security-shaped queries.
- Joern/CPG for richer code-property-graph overlays.
- Project-specific table extractors where a codebase has known registration
  schemas.

They are not the Stage 1 parser.

## Planned Capability Ladder

| Stage | Extracts | Expected confidence |
| --- | --- | --- |
| 1 | files, modules, functions, structs/enums, globals, includes, direct calls | BestEffort to Proven per edge |
| 2 | direct semantic references with call-site provenance | Proven where Clang resolves |
| 3 | build profiles, preprocessor scope, skipped translation-unit reporting | Proven per profile |
| 4 | callback/registration tables and function-pointer storage | Mixed; table facts Proven, indirect dispatch Advisory |
| 6 | constrained FFmpeg CLI-to-code traces | Mixed; explicitly limited |

The adapter must never claim `Proven` for a guessed edge.

## Graph Mapping

The native adapter uses the existing Lifeblood graph kinds:

- C/C++ functions become `method:` symbols.
- Structs, unions, enums, and meaningful typedefs become `type:` symbols.
- Global variables, enum members, and deliberately surfaced macros become
  `field:` symbols.
- Direct calls become `Calls` edges.
- Includes, type uses, global uses, and table-held callbacks become
  `References` edges.

Native-specific details live in `properties`, usually under a `native.*` key.

See the full
[Native Clang stage plan](../../docs/plans/native-clang-adapter-masterplan-2026-05-16.md).

## Stage 1 Prerequisites

This machine currently has `.NET SDK 8.0.419`, but no `clang` or `cmake` on
PATH. Before implementation begins, Stage 1 must establish one supported local
toolchain path and document it here.

Expected tools:

- LLVM/Clang with LibTooling headers and libraries.
- CMake.
- A C++ compiler compatible with the selected LLVM distribution.
- Lifeblood CLI from this repo for graph validation.

## First Fixture Goal

The first fixture should be tiny and boring:

```c
struct Packet { int size; };

static int clamp(int value);
int decode(struct Packet *packet) {
    return clamp(packet->size);
}
```

The first graph should prove:

- file symbols exist;
- `Packet`, `clamp`, and `decode` have stable IDs;
- `decode -> clamp` is a `Calls` edge;
- `decode -> Packet` is a `References` edge;
- all expression-derived edges carry call-site provenance where available;
- `lifeblood analyze --graph graph.json` validates the output.

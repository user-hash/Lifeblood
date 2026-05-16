# Native Clang Adapter

**Status:** Stage 4 callback-table bootstrap. The `libclang` executable emits
graphs for direct-call, direct-reference, profile-shaped, and callback-table C
fixtures.

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

Stage 1-4 uses a small native command-line tool over Clang's API. On the
current Windows machine, the official LLVM installer provides `libclang`
headers/libs (`clang-c/Index.h`, `clang-c/CXCompilationDatabase.h`,
`libclang.lib`, `libclang.dll`) but not the full C++ LibTooling development
surface. So the bootstrap extractor uses `libclang`; C++ LibTooling stays
available as a richer later path if we add a heavier LLVM development package or
source-build story.

The adapter will read a `compile_commands.json` compilation database and run
Clang over each translation unit with the same include paths, defines, language
mode, and generated config headers that the real build uses.

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

Toolchain details are recorded in [TOOLCHAIN.md](TOOLCHAIN.md).

Expected tools:

- LLVM/Clang with `libclang` headers and libraries.
- CMake.
- A C++ compiler compatible with the selected LLVM distribution.
- Lifeblood CLI from this repo for graph validation.

## Build And Run

On Windows, launch the build through the Visual Studio developer environment so
Clang can find the MSVC and Windows SDK libraries:

```powershell
cmd /c 'call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64 && "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" -S adapters/native-clang -B artifacts/native-clang-build -G Ninja -DCMAKE_MAKE_PROGRAM="C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja/ninja.exe" -DCMAKE_CXX_COMPILER="C:/Program Files/LLVM/bin/clang++.exe" -DCMAKE_RC_COMPILER="C:/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64/rc.exe" && "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" --build artifacts/native-clang-build'
```

Run the tiny fixture extraction:

```powershell
artifacts/native-clang-build/lifeblood-native-clang.exe `
  --project adapters/native-clang/test-fixtures/tiny-c `
  --profile tiny-debug `
  --out artifacts/native-clang-build/tiny.graph.json

dotnet run --project src/Lifeblood.CLI -- analyze --graph artifacts/native-clang-build/tiny.graph.json
```

Run the richer direct-reference fixture:

```powershell
artifacts/native-clang-build/lifeblood-native-clang.exe `
  --project adapters/native-clang/test-fixtures/direct-refs-c `
  --profile direct-refs-debug `
  --out artifacts/native-clang-build/direct-refs.graph.json

dotnet run --project src/Lifeblood.CLI -- analyze --graph artifacts/native-clang-build/direct-refs.graph.json
```

Run the profile-shaped fixture:

```powershell
artifacts/native-clang-build/lifeblood-native-clang.exe `
  --project adapters/native-clang/test-fixtures/profile-c `
  --compilation-database adapters/native-clang/test-fixtures/profile-c/profiles/video `
  --profile video `
  --out artifacts/native-clang-build/profile-video.graph.json

artifacts/native-clang-build/lifeblood-native-clang.exe `
  --project adapters/native-clang/test-fixtures/profile-c `
  --compilation-database adapters/native-clang/test-fixtures/profile-c/profiles/audio `
  --profile audio `
  --out artifacts/native-clang-build/profile-audio.graph.json
```

Run the callback-table fixture:

```powershell
artifacts/native-clang-build/lifeblood-native-clang.exe `
  --project adapters/native-clang/test-fixtures/callback-table-c `
  --profile callback-debug `
  --out artifacts/native-clang-build/callback.graph.json
```

## Source Layout

The Stage 1 executable keeps adapter responsibilities separated:

- `main.cpp` is the primary adapter: command-line parsing, orchestration, and
  file output only.
- `LibClangExtractor.*` is the driven adapter over `libclang` and
  `compile_commands.json`.
- `GraphModel.h` is the local graph DTO set matching Lifeblood's JSON graph
  schema shape.
- `JsonGraphWriter.*` serializes the graph boundary without bringing a JSON
  dependency into the tiny bootstrap.

LLVM remains isolated under `adapters/native-clang`; Lifeblood core continues
to consume only the emitted graph JSON.

## First Fixture Contract

The first fixture is tiny and boring by design:

```c
struct Packet { int size; };

static int clamp(int value);
int decode(struct Packet *packet) {
    return clamp(packet->size);
}
```

Its expected graph lives at `test-fixtures/tiny-c/expected.graph.json`, pinned by
`NativeClangAdapterContractTests`. The graph proves:

- file symbols exist;
- `Packet`, `clamp`, and `decode` have stable IDs;
- `decode -> clamp` is a `Calls` edge;
- `decode -> Packet` is a `References` edge;
- all expression-derived edges carry call-site provenance where available;
- `lifeblood analyze --graph graph.json` validates the output.

## Direct Reference Fixture

The richer `direct-refs-c` fixture adds the next valuable native facts:

- enum types and enum members;
- typedef symbols and underlying-type references;
- struct field type references;
- global variable symbols;
- function references to globals and enum members.

The generated graph currently imports as 14 symbols, 22 normalized edges,
1 module, and 3 types after Lifeblood synthesizes containment and derived file
edges.

## Profile Fixture

The `profile-c` fixture proves that build flags shape native graphs. The same
source files are analyzed through two compilation databases:

- `profiles/video` defines `ENABLE_VIDEO=1` and emits `decode_video`.
- `profiles/audio` defines `ENABLE_AUDIO=1` and emits `decode_audio`.

Command-line defines are surfaced on the module symbol through
`native.defines`, and both command-line and source macros are represented as
`field:macro:<name>` symbols. Macro expansions are file-level `References`
edges with `native.referenceKind=macroExpansion`.

## Callback Table Fixture

The `callback-table-c` fixture proves the first Stage 4 callback-registration
shape. A global registration table initialized with function names becomes a
`field:` symbol with `native.kind=callbackTable`, and each table-held function
gets a proven `References` edge from the table with
`native.referenceKind=callbackTarget`.

This is intentionally narrower than arbitrary indirect-call resolution. It only
claims function targets that appear directly in static initializer syntax.

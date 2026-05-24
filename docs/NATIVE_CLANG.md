# Native C Support (libclang)

Beta in v0.7.7. First non-C# adapter for Lifeblood. Parses C code through `libclang`, emits a Lifeblood-shape `graph.json`, lets the existing read-side tools answer the same questions they answer for C# workspaces.

## Status

| Surface | Status |
|---------|--------|
| C translation units | Beta. Parsed through `libclang` with a real `compile_commands.json`. |
| C++ translation units | Deferred. Same boundary will extend; templates, classes, member functions not implemented. |
| Whole-build coverage on FFmpeg-class projects | Deferred. Current scout works on focused file slices. |
| Cross-translation-unit edges | Working in the cross-TU fixture. Symbol IDs are stable across TUs in the same workspace. |
| Partial-parse tolerance | Working. Diagnostic health counts surface instead of fail-closed behavior. |
| Read-side MCP tools against C graphs | Working through `JsonGraphImporter`. `lookup`, `dependencies`, `dependants`, `find_references`, `blast_radius`, `file_impact`, `dead_code`, `cycles` all consume the emitted graph. |
| Write-side MCP tools against C graphs | Out of scope. Write-side tools (`rename`, `compile_check`, `execute`, `diagnose`) are Roslyn-backed and remain C#-only. |

## Opt-in execution lane

The native-clang adapter is an **opt-in build target**. The executable is NOT bundled with the published NuGet packages (`Lifeblood.dll` / `Lifeblood.Server.Mcp.dll`); it ships as a separate `lifeblood-native-clang.exe` built from `adapters/native-clang/` via CMake + libclang. See `TOOLCHAIN.md` for the build recipe.

11 `[SkippableFact]` ratchets pin the executable's contract:
- **Default suite (no env var set)**: ratchets skip silently when the executable is absent. Suite reports `1180 passed + 11 skipped / 1191 total` — green.
- **Opt-in audit (`LIFEBLOOD_REQUIRE_NATIVE_CLANG=1`)**: ratchets fail loudly when the executable is missing. This is the CI-gate path; the env var asserts "the executable IS expected to be here" and the failure is correct behavior when the assertion is false.

To build the executable for local opt-in audit: follow `TOOLCHAIN.md` (requires LLVM dev headers + libclang shared library + CMake + a C++ compiler). Built artifacts land at `artifacts/native-clang-build/lifeblood-native-clang.exe` — the path the ratchets look up.

For each release tag, the C# core (`Lifeblood` + `Lifeblood.Server.Mcp`) ships **without** a bundled native-clang executable. Consumers who need C-language analysis build the executable from source against their own LLVM toolchain. This separation keeps the C# package consumer-toolchain-clean (no LLVM dependency for a Lifeblood install) and lets the native-clang adapter evolve at its own cadence.

## How it works

```
C project + compile_commands.json
  -> adapters/native-clang/lifeblood-native-clang.exe
  -> graph.json
  -> dotnet run --project src/Lifeblood.CLI -- analyze --graph graph.json
  -> read-side MCP tools
```

The extractor is a small native command-line tool over Clang's C API. It opens the project's `compile_commands.json`, walks each translation unit with the exact include paths, defines, language mode, and generated config headers the real build uses, then emits a graph that conforms to `schemas/graph.schema.json`. Lifeblood ingests the graph through the same `JsonGraphImporter` boundary the TypeScript and Python adapters already use.

LLVM, Clang, and CMake stay outside `Lifeblood.Domain`, `Lifeblood.Application`, `Lifeblood.Analysis`, and every connector. The hexagonal core remains language-agnostic.

## What the graph carries

| Surface | Graph shape |
|---------|-------------|
| C functions | `method:` symbols with parameter types and return-type pressure metrics |
| Structs, unions, enums, surfaced typedefs | `type:` symbols |
| Globals, enum members, surfaced macros | `field:` symbols (`field:macro:<name>` for command-line and source macros) |
| Direct calls | `Calls` edges |
| Includes, type references, global references, callback-table targets | `References` edges with `native.referenceKind` distinguishing the role |
| Build profiles | `native.defines` on the module symbol |
| Callback registration tables | Table symbol with `native.kind=callbackTable`, one `References` edge per table-held target |
| Translation-unit parse health | Diagnostic counts on the file symbol; partial parses surface without aborting |

Native-specific details live under `properties`, usually keyed `native.*`, so the universal graph schema stays language-agnostic and downstream tools that do not know about C ignore them safely.

## Fixtures

Nine fixture families pin the contract under `adapters/native-clang/test-fixtures/`. Four carry a checked-in `expected.graph.json` consumed by `NativeClangAdapterContractTests` (`tiny-c`, `direct-refs-c`, `callback-table-c`, and `profile-c` which has both `expected.audio.graph.json` and `expected.video.graph.json`). The other five are executable ratchets only, pinned by `NativeClangExecutableRatchetTests`: `multi-tu-c`, `cross-tu-c`, `partial-parse-c`, `warning-c`, `return-type-c`.

| Fixture | Pins |
|---------|------|
| `tiny-c` | Smallest possible graph: file symbols, struct, two functions, one Calls edge, one References edge, call-site provenance. |
| `direct-refs-c` | Enum types and members, typedef symbols, underlying-type references, struct field type references, global symbols, function-to-global references. |
| `multi-tu-c` | Two translation units in the same workspace, shared header, stable IDs across TUs. |
| `cross-tu-c` | Cross-TU function definitions and call resolution. |
| `callback-table-c` | Static registration tables with function-pointer targets. Row plus cell facts surface on the graph. |
| `profile-c` | Two compilation databases over the same source. `ENABLE_VIDEO=1` and `ENABLE_AUDIO=1` produce different graphs. Command-line defines surface on the module; macros surface as `field:macro:<name>` symbols. |
| `partial-parse-c` | Extractor tolerates a translation unit with parse errors. Emits diagnostic health counts rather than failing closed. |
| `warning-c` | Non-zero warning diagnostic health surfaces correctly. |
| `return-type-c` | Return-type symbol pressure metrics; type-flow coverage per function. |

Test classes: `NativeClangAdapterContractTests` and `NativeClangExecutableRatchetTests` under `tests/Lifeblood.Tests/`.

## Build

The build details live in [`adapters/native-clang/TOOLCHAIN.md`](../adapters/native-clang/TOOLCHAIN.md). Short form:

- LLVM with `libclang` headers and libraries.
- CMake.
- A C++ compiler compatible with the LLVM distribution (MSVC on Windows).
- Lifeblood CLI from this repo for graph validation.

The extractor is built into `artifacts/native-clang-build/lifeblood-native-clang.exe`.

## Run

```powershell
artifacts/native-clang-build/lifeblood-native-clang.exe `
  --project <c-project-root> `
  --profile <profile-name> `
  --out <output.graph.json>

dotnet run --project src/Lifeblood.CLI -- analyze --graph <output.graph.json>
```

`--profile` becomes the module label on the graph. `--out` writes the graph JSON directly to a file path to avoid PowerShell UTF-16 redirection breaking the BOM-aware importer.

## FFmpeg scout

`adapters/native-clang/tools/ffmpeg-scout/` is the repeatable reconnaissance path for stress-testing the extractor against a real C codebase. It clones FFmpeg, configures a minimal LLVM-clang build, generates a focused `compile_commands.json` for a representative file slice, and runs the extractor end-to-end.

Default file slice targets registration-heavy and table-heavy translation units:

- `libavfilter/allfilters.c`
- `libavcodec/allcodecs.c`
- `libavformat/allformats.c`
- `libavutil/pixdesc.c`
- `libswscale/utils.c`

First local result, 2026-05-16:

- 9264 native graph symbols
- 1067 methods
- 14494 Lifeblood-imported edges after graph synthesis
- 87 files
- 0 architecture violations
- 1 likely real cycle in libswscale context initialization

The scout is explicit reconnaissance, not whole-build coverage. The machine used for the first scout did not have WSL, Make, Bear, or `compiledb` available, so the helper creates a focused compile database for selected files. The next maturity step is one of:

- WSL plus `bear -- ./configure && make`
- MSYS2 or MinGW plus Make and bear
- A project-specific FFmpeg compile-database generator that reads `config.mak` and library make fragments

See [`adapters/native-clang/FFMPEG_SCOUT.md`](../adapters/native-clang/FFMPEG_SCOUT.md) for prerequisites, defaults, rerun shortcuts, and current limits.

## Hexagonal boundary

`adapters/native-clang/` is an external JSON producer. The Lifeblood core stays language-agnostic:

- `Lifeblood.Domain` carries the graph model. Zero native dependencies.
- `Lifeblood.Application` carries the ports. Zero native dependencies.
- `Lifeblood.Adapters.JsonGraph` ingests the emitted `graph.json`.
- `Lifeblood.Connectors.Mcp` exposes the graph to AI agents through the same MCP tool surface that C# workspaces use.

A native-specific MCP tool would belong in a future native-aware connector, not in the existing Roslyn-shaped right side.

## Tracking

The full rollout plan lives in [`docs/plans/native-clang-adapter-masterplan-2026-05-16.md`](plans/native-clang-adapter-masterplan-2026-05-16.md). The adapter-side technical README is [`adapters/native-clang/README.md`](../adapters/native-clang/README.md).

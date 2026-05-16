# Native Clang Adapter Masterplan

**Status:** Stage 1 bootstrap underway. The adapter boundary is chartered, a
tiny C fixture contract exists, and a minimal `libclang` executable now emits a
graph for that fixture.

**Owner:** Native C/C++ track. This document is the contract for adding
Clang-backed analysis without weakening Lifeblood's existing hexagonal
boundaries.

**Repo state at authoring:** Lifeblood self-analysis is clean: 3,175 symbols,
16,316 edges, 11 modules, 0 violations, 0 cycles. `lifeblood_invariant_check`
reports 101 invariants, 0 duplicates, 0 parse warnings.

**Local native toolchain state:** `.NET SDK 8.0.419` is installed. LLVM 22.1.5,
CMake 3.31.6-msvc6, Ninja 1.12.1, MSVC Build Tools, and Windows SDK `rc.exe`
are available by absolute path. The official LLVM installer exposes `libclang`
but not the C++ LibTooling development surface.

---

## 1. Product Thesis

FFmpeg is a good long-term flagship target, but a bad first implementation
target. The native track should prove itself on a tiny C fixture, then a
medium C project, then FFmpeg.

The goal is not to build a C parser. The goal is to put Lifeblood's existing
architecture workflow layer on top of compiler-grade native-code facts:

- what depends on this function, file, or library;
- what breaks under this build profile if a function changes;
- which registration table makes a callback live;
- which CLI option path reaches a codec, muxer, filter, or scaler;
- which C implementation maps to which SIMD or assembly variant;
- where macro/build-profile limits make an answer incomplete.

## 2. Architectural Decisions

### Decision 1. External JSON adapter first

The native adapter starts as an out-of-process graph producer under
`adapters/native-clang/`. It emits `schemas/graph.schema.json` and is consumed
through `lifeblood analyze --graph`.

This follows `INV-ADAPT-003`: external adapters communicate via JSON graph
schema only. It keeps LLVM/Clang dependencies out of `Lifeblood.Domain`,
`Lifeblood.Application`, `Lifeblood.Analysis`, `Lifeblood.Connectors.*`, and
the existing C# adapter.

### Decision 2. Clang is the semantic engine

Use Clang/LLVM tooling, not regex and not a hand-written parser. The primary
source of truth is `compile_commands.json`, because C and C++ parsing is not
meaningful without the real include paths, defines, language mode, and config
headers for each translation unit.

The first implementation target is a small native executable over Clang's API.
On the verified Windows toolchain, the official LLVM installer exposes
`libclang` (`clang-c/Index.h`, `clang-c/CXCompilationDatabase.h`,
`libclang.lib`, `libclang.dll`) but not C++ LibTooling headers/libs. Bootstrap
with `libclang`; move to C++ LibTooling only if the C API proves insufficient
and we intentionally add the heavier LLVM development package/source-build
story. Python `libclang` bindings, CodeQL, and Joern can be useful sidecars,
but they are not the core v1 extraction path.

### Decision 3. One graph per build profile

Native code is build-profile-shaped. A symbol can exist in one configuration
and disappear in another because of `#if`, generated config headers, target
architecture, or configure flags.

The native adapter emits one graph per explicit build profile first. Merging
profiles is a later derived view, not a Stage 1 requirement.

### Decision 4. No Domain enum expansion in Stage 1

Lifeblood's graph kinds stay language-agnostic:

- C functions use `SymbolKind.Method`.
- Structs, unions, enums, and typedef-backed public shapes use
  `SymbolKind.Type`.
- Globals, enum members, macros, and table entries use `SymbolKind.Field`
  when they need a symbol.
- Header groups and libraries use existing Module/File symbols and
  properties.

Native-specific detail goes into `Symbol.Properties` and `Edge.Properties`.
Changing `SymbolKind` or `EdgeKind` requires a separate design decision.

### Decision 5. Capability honesty before demos

Every native graph declares adapter capability honestly. A syntax-only or
partial semantic stage must not look like Roslyn-Proven truth.

Early Stage 0/1 work must include a trust-envelope audit for imported graphs:
read-side tool envelopes need to respect adapter capability and edge evidence
so a BestEffort native graph is not presented as compiler-proven Roslyn output.

### Decision 6. FFmpeg is Stage 6, not Stage 1

FFmpeg brings configure-generated headers, macro-heavy code, registration
tables, platform assembly, architecture dispatch, and CLI option routing all
at once. Those are exactly why it is a good flagship, and exactly why it must
come after the extractor contract is proven.

## 3. Reuse Points In Lifeblood

The native track should reuse these existing components as-is:

- `schemas/graph.schema.json` for the adapter output contract.
- `Lifeblood.Adapters.JsonGraph` for import/export.
- `GraphBuilder` for deterministic sorting, contains-edge synthesis, dangling
  edge filtering, deduplication, and derived file edges.
- `AnalysisPipeline` for coupling, cycles, tiers, blast-radius candidate
  summaries, and rule validation.
- MCP read tools for lookup, dependencies, dependants, blast radius, file
  impact, context, search, cycles, and invariant checks.
- Existing docs/invariants governance for every load-bearing contract.

The native track should not reuse or reference `Lifeblood.Adapters.CSharp`.
Roslyn remains the C# reference adapter, not a shared adapter base.

## 4. Native Symbol ID Convention

Stage 1 uses a conservative canonical ID grammar:

| Native construct | Lifeblood kind | ID shape |
| --- | --- | --- |
| Static/shared library boundary | Module | `mod:<logical-library>` |
| Source/header file | File | `file:<repo-relative-path>` |
| C namespace surrogate | Namespace | optional `ns:<module-or-prefix>` |
| Struct | Type | `type:<qualified-struct-name>` |
| Union | Type | `type:<qualified-union-name>` |
| Enum | Type | `type:<qualified-enum-name>` |
| Typedef | Type | `type:<qualified-typedef-name>` when semantically important |
| Function | Method | `method:<qualified-function-name>(<canonical-param-types>)` |
| Global variable | Field | `field:<qualified-global-name>` |
| Enum member | Field | `field:<qualified-enum-name>.<member>` |
| Macro | Field | `field:macro:<macro-name>` only when tracked deliberately |

Properties carry native detail:

- `native.kind`: `function`, `struct`, `union`, `enum`, `typedef`, `global`,
  `enumMember`, `macro`, `callbackTable`, `asmVariant`.
- `native.linkage`: `external`, `internal`, `none`, `unknown`.
- `native.storageClass`: source storage class when available.
- `native.signature`: Clang spelling of the function signature.
- `native.buildProfile`: profile name that produced the symbol.
- `native.header`: primary declaration header when available.

## 5. Edge Mapping

Stage 1 maps only edges that fit the existing graph:

| Native relation | Lifeblood edge |
| --- | --- |
| Module/library dependency | `DependsOn` |
| File includes header | `References` file-to-file or file-to-symbol |
| Function calls function | `Calls` |
| Function references global/type | `References` |
| Struct/typedef field uses type | `References` |
| Enum member contained by enum | `Contains` via `ParentId` |
| Callback table stores function pointer | `References` from table/global symbol to function |

Function-pointer call resolution is Stage 4 unless Clang can prove the target
locally. Until then, indirect calls are represented as explicit advisory
properties, not fake `Calls` edges.

## 6. Stage Plan

### Stage 0. Charter and boundary

Deliverables:

- This masterplan.
- `adapters/native-clang/README.md` with the adapter contract.
- Adapter guide note pointing to the native track.
- No runtime behavior changes.

Gate:

- Docs are coherent.
- Existing tests still pass, or the touched doc tests pass.
- Commit contains only Stage 0 files.

### Stage 1. Tiny C graph producer

Deliverables:

- Minimal `adapters/native-clang` source tree.
- Build instructions for LLVM/Clang + CMake.
- Tiny C fixture with `compile_commands.json`.
- Extract modules, files, functions, structs/enums, globals, includes, direct
  calls, direct type references.
- Emit deterministic `graph.json`.

Gate:

- `lifeblood analyze --graph <native graph>` succeeds.
- Graph validates: no dangling edges, duplicate symbols, or duplicate edges.
- Adapter declares only the capabilities it actually proves.

### Stage 2. Clang semantic completeness for direct code

Deliverables:

- Proven direct call resolution for normal calls.
- Proven direct type/global references where Clang resolves them.
- Header/source ownership policy.
- CallSite provenance for expression-derived edges.

Gate:

- `dependencies`, `dependants`, `blast_radius`, and `file_impact` produce
  useful answers on the tiny fixture and one medium fixture.

### Stage 3. Build profile and preprocessor scope

Deliverables:

- Build profile metadata.
- Multiple compile database/profile support.
- Explicit reporting for skipped translation units and unsupported compiler
  flags.
- Preprocessor/macro facts where Clang exposes them cleanly.

Gate:

- Two profiles of the same fixture produce intentionally different graphs with
  visible `native.buildProfile` metadata.

### Stage 4. Dispatch tables and function pointers

Deliverables:

- Static initializer/table extraction for callback registries.
- Edges from registration tables to handler functions.
- Advisory indirect-call facts for unresolved function-pointer calls.

Gate:

- Dead-code and blast-radius behavior sees table-registered callbacks as live.

### Stage 5. Native read-model polish

Deliverables:

- Native-aware resolver/canonicalizer rules if generic resolver behavior is not
  enough.
- Optional `native_static_tables` or table-facts read tool only if generic graph
  queries are insufficient.
- Capability-aware response envelopes for imported graphs.

Gate:

- No read-side response over a native graph overstates confidence.

### Stage 6. FFmpeg pilot

Deliverables:

- One FFmpeg build profile.
- Module mapping for `libavcodec`, `libavformat`, `libavfilter`,
  `libswscale`, `libavutil`, and selected tools.
- First CLI-to-code trace for a constrained command shape.

Gate:

- Lifeblood can answer a narrow query like "what path handles `-vf scale` in
  this profile?" with files, functions, and limitations.

### Stage 7. ASM/SIMD variant mapping

Deliverables:

- C implementation to optimized kernel/assembly variant mapping.
- Architecture-specific profile metadata.

Gate:

- For one scaler/codec path, Lifeblood can show possible C and architecture
  optimized implementations under the selected profile.

### Stage 8. Optional CodeQL/Joern overlays

Deliverables:

- Explicit sidecar import paths for dataflow/security questions if needed.
- No dependency from core Lifeblood assemblies to CodeQL or Joern.

Gate:

- Sidecars add evidence without becoming the primary native parser.

### Stage 9. Public proof

Deliverables:

- Tiny fixture case study.
- Medium C project case study.
- FFmpeg case study once Stage 6 is honest.

Gate:

- The docs show exact commands, graph counts, useful answers, and limitations.

## 7. Stage 1 Task Breakdown

The next commit after Stage 0 should be one of these, not all at once:

1. Add a tiny C fixture and a hand-authored expected graph to pin the mapping.
2. Add the CMake/LibTooling skeleton that can parse `compile_commands.json`.
3. Add only file/module/function symbol extraction.
4. Add direct call edges.
5. Add type/global reference edges.
6. Add graph determinism and JSON schema validation to the adapter's own tests.

Each task should be independently reviewable and commit-sized.

## 8. Risks

- **LLVM dependency weight.** Keep it out of Lifeblood core. The adapter is a
  separate tool.
- **Windows toolchain friction.** Record exact setup once Stage 1 begins.
  Current machine has no `clang`/`cmake` on PATH.
- **False confidence.** Native graphs must not inherit Roslyn-Proven envelopes
  by accident.
- **Header duplication.** A header can appear in many translation units. Stage
  1 needs deterministic ownership and deduplication rules.
- **Macro truth.** Macro-expanded code must be represented as build-profile
  truth, not universal truth.
- **FFmpeg scope explosion.** Do not start there.

## 9. Non-Goals For V1

- No custom C parser.
- No C/C++ write-side refactoring.
- No attempt to model every preprocessor branch in one merged graph.
- No whole-program pointer analysis claim.
- No security/dataflow engine unless a sidecar is explicitly added later.
- No Domain dependency on LLVM, Clang, CodeQL, Joern, or adapter-specific types.

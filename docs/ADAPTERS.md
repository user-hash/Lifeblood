# Building a Language Adapter

Lifeblood adapters translate language-specific code intelligence into the universal semantic graph. Two paths exist: JSON (any language) or C# (in-process).

## Option A: JSON Adapter (any language, recommended)

Write a parser in your language. Output JSON conforming to `schemas/graph.schema.json`. Lifeblood reads it via `JsonGraphImporter`. No C# needed.

```bash
your-parser ./project > graph.json
dotnet run --project src/Lifeblood.CLI -- analyze --graph graph.json
```

The TypeScript adapter (`adapters/typescript/`) is a working example of this approach.
The Native Clang adapter (`adapters/native-clang/`) is a beta C extractor that
follows the same external JSON boundary, so LLVM and Clang dependencies stay
outside the Lifeblood core.

### Required JSON structure

```json
{
  "version": "1.0",
  "language": "your-language",
  "adapter": {
    "name": "your-adapter",
    "version": "1.0.0",
    "capabilities": {
      "discoverSymbols": true,
      "typeResolution": "bestEffort",
      "callResolution": "bestEffort",
      "implementationResolution": "none",
      "crossModuleReferences": "none",
      "overrideResolution": "none"
    }
  },
  "symbols": [...],
  "edges": [...]
}
```

### Minimum viable adapter

Your JSON must contain:
- `version`: `"1.0"`
- `language`: your language name
- `adapter`: with honest capability declarations
- `symbols[]`: at least File and Type symbols with `id`, `name`, `kind`
- `edges[]`: at least `dependsOn` edges between modules/files

That is enough for coupling analysis, cycle detection, and architecture rules.

### Full adapter

Add these for richer analysis:
- Method and Field symbols with `parentId` for containment hierarchy
- `calls`, `references`, `implements`, `inherits` edges
- `evidence` on every edge (kind, adapterName, confidence)
- All symbols sorted by ID, all edges sorted by source+target+kind (deterministic output)

## Option B: C# Adapter (in-process)

Implement `IWorkspaceAnalyzer` from `Lifeblood.Application.Ports.Left`:

```csharp
public class MyAdapter : IWorkspaceAnalyzer
{
    public AdapterCapability Capability => new AdapterCapability
    {
        Language = "python",
        AdapterName = "python-ast",
        AdapterVersion = "1.0.0",
        CanDiscoverSymbols = true,
        TypeResolution = ConfidenceLevel.BestEffort,
        CallResolution = ConfidenceLevel.BestEffort,
    };

    public SemanticGraph AnalyzeWorkspace(string projectRoot, AnalysisConfig config)
    {
        var builder = new GraphBuilder();
        // Add symbols with ParentId - GraphBuilder synthesizes Contains edges
        return builder.Build();
    }
}
```

The Roslyn adapter (`src/Lifeblood.Adapters.CSharp/`) is the reference implementation.

## Adapter Quality Levels

| Level | What you provide | What it unlocks | Confidence |
|-------|-----------------|----------------|------------|
| **Syntax** | Files, imports, basic structure | Coupling, cycles, architecture rules | bestEffort |
| **Structural** | Types, inheritance, interfaces | Tier classification, boundary checks | bestEffort |
| **Semantic** | Methods, calls, references | Blast radius, dead code detection | proven |
| **Compiler-grade** | Type resolution, overloads | Everything, full trust | proven |

Start with Syntax. It is already useful. Upgrade confidence claims as you add capabilities.

## Native C/C++ Guidance

C and C++ adapters must use a real compilation database and compiler-grade
front end for semantic claims. A `compile_commands.json` file is the normal
input because it records the exact command line for each translation unit:
include paths, defines, language mode, generated config headers, and target
flags.

Native adapters are external JSON producers. LLVM, Clang, CodeQL, Joern, and
CMake dependencies must not enter `Lifeblood.Domain`, `Lifeblood.Application`,
`Lifeblood.Analysis`, or any connector. The adapter lives under `adapters/`,
emits `graph.json`, and `JsonGraphImporter` brings the graph into Lifeblood.

`adapters/native-clang/` is the reference C extractor and ships as beta in
v0.7.7. It reads a `compile_commands.json`, parses each translation unit
through `libclang`, and emits a Lifeblood-shape graph. Surfaces: translation
units, functions, globals, fields, type shells, enum members, macros,
includes, callback-table rows and cells, and per-module, per-file, per-symbol
pressure metrics. Partial-parse tolerant. The extractor emits diagnostic
health counts rather than failing closed when a translation unit has parse
errors.

Fixture coverage under `adapters/native-clang/test-fixtures/` spans nine
families. Four carry a checked-in `expected.graph.json` consumed by
`NativeClangAdapterContractTests`: `tiny-c`, `direct-refs-c`,
`callback-table-c`, and `profile-c` (the profile fixture carries both
`expected.audio.graph.json` and `expected.video.graph.json`). The other five
are executable ratchets only, pinned by `NativeClangExecutableRatchetTests`:
`multi-tu-c`, `cross-tu-c`, `partial-parse-c`, `warning-c`, `return-type-c`.

FFmpeg reconnaissance lives at `adapters/native-clang/tools/ffmpeg-scout/`
with its workflow documented in `adapters/native-clang/FFMPEG_SCOUT.md`. The
scout is explicit about scope. It generates a focused compile database for a
selected file slice, not a whole-repo build. First 5-file slice produced 9264
symbols, 1067 methods, 14494 imported edges, 0 architecture violations, 1
likely real cycle in libswscale context initialization. Whole-build coverage
requires one of WSL with bear, MSYS2 with bear, or a project-specific
compile-database generator and is named as the next maturity step.

See [`docs/NATIVE_CLANG.md`](NATIVE_CLANG.md) for the dedicated capability
page. The [Native Clang stage plan](plans/native-clang-adapter-masterplan-2026-05-16.md)
tracks the rollout.

## Adapter Checklist

Before shipping an adapter, verify:

- [ ] Output conforms to `schemas/graph.schema.json`
- [ ] `version` is `"1.0"`
- [ ] `language` and `adapter` metadata are present
- [ ] Capability claims are honest (do not claim `proven` for things you guess)
- [ ] Every edge has `evidence` with `kind`, `adapterName`, and `confidence`
- [ ] No dangling edges (every sourceId and targetId exists in symbols)
- [ ] No duplicate symbols (unique IDs)
- [ ] No duplicate edges (unique source+target+kind)
- [ ] Symbols with `parentId` reference existing symbols
- [ ] Output is deterministic (same input produces same JSON)
- [ ] External type filtering applied (no edges to BCL/stdlib types - only edges to source-defined or workspace-tracked symbols)
- [ ] Self-analysis works (adapter can analyze its own source code)
- [ ] Output passes through `dotnet run --project src/Lifeblood.CLI -- analyze --graph your-output.json`

## Symbol ID Convention

Adapters should use these ID prefixes for consistency:

| Kind | Prefix | Example |
|------|--------|---------|
| Module | `mod:` | `mod:MyApp` |
| File | `file:` | `file:src/auth.ts` |
| Namespace | `ns:` | `ns:MyApp.Auth` |
| Type | `type:` | `type:MyApp.Auth.AuthService` |
| Method | `method:` | `method:MyApp.Auth.AuthService.login` |
| Field | `field:` | `field:MyApp.Auth.AuthService._token` |
| Property | `property:` | `property:MyApp.Auth.AuthService.Name` |

## Evidence Guidelines

Every edge should carry evidence. The confidence level must be honest:

| Confidence | Meaning | Example |
|-----------|---------|---------|
| `none` | Not supported by this adapter | Override detection in a syntax-only parser |
| `bestEffort` | Inferred from patterns, may be wrong | Import-based dependency in Python |
| `proven` | Verified by compiler/type system, guaranteed correct | Roslyn semantic model, TypeScript TypeChecker |

## Testing Against Golden Repos

The `tests/GoldenRepos/` directory contains fixture projects:
- **HexagonalApp/** - 3-layer hexagonal architecture (Domain, Application, Infrastructure)
- **CycleRepo/** - Two services with circular dependencies

Run your adapter against these and verify:
- Expected types are discovered
- Inheritance and implementation edges are correct
- Circular dependencies are detectable in the output
- Graph validates cleanly through GraphValidator

# Changelog

All notable changes to Lifeblood are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added — Plan v4 (three-seam framing for the post-BCL findings)

After v2's BCL fix, two reviewer reports surfaced five remaining findings.
v4 closes them via three architectural seams (one resolver port, one
csproj-driven compilation-fact convention, one adapter semantic view) instead
of five piecemeal patches.

#### Seam #1 — `ISymbolResolver` (closes LB-BUG-002, LB-BUG-004, LB-FR-002, LB-FR-003)

- **`Lifeblood.Application.Ports.Right.ISymbolResolver`** + single `SymbolResolutionResult` DTO. Every read-side MCP tool that takes a `symbolId` (lookup, dependants, dependencies, blast_radius, file_impact, find_references, find_definition, find_implementations, documentation, rename) routes through the resolver before doing graph or workspace lookups.
- **`Lifeblood.Connectors.Mcp.LifebloodSymbolResolver`** — reference implementation. Resolution order: exact canonical match → truncated method form (lenient single-overload) → bare short name. Returns `Outcome` + `Candidates` + `Diagnostic` for ambiguous and not-found cases.
- **Partial-type unification as a read model.** The graph stores raw symbols (one record per partial declaration; last-write-wins remains the storage policy). The resolver computes a deterministic primary file path + the full `DeclarationFilePaths` array by walking the existing `file:X Contains type:Y` edges. **Zero schema change to `Lifeblood.Domain.Graph.Symbol`.** Primary picker rule: filename matches type name → filename starts with `"<TypeName>."` (shortest first) → lexicographic first.
- **Short-name index** added to `SemanticGraph.GraphIndexes` (case-insensitive bucket) + public `FindByShortName(name)` accessor. Built lazily alongside existing indexes.
- **New MCP tool `lifeblood_resolve_short_name`** — discover canonical IDs from a bare short name when you don't know the namespace.
- **`FindReferencesOptions.IncludeDeclarations`** — explicit operation policy on `ICompilationHost.FindReferences`. When true, the result includes one synthetic `(declaration)` entry per source declaration site (one per partial for partial types). Two-overload signature preserves backward compat.
- **9 regression tests in `SymbolResolverTests.cs`** including the LB-BUG-002 truncated-id misdiagnosis pin-down (`method:Voice.SetPatch` → resolver canonicalizes → caller graph lookup succeeds).
- **`lifeblood_lookup` response now includes `filePaths[]`** — the full sorted list of partial declaration files. The single `filePath` field is now the deterministic primary, no longer a non-deterministic last-write-wins pick.

#### Seam #2 — Csproj-driven compilation facts as a documented convention (closes LB-BUG-005)

- **`ModuleInfo.AllowUnsafeCode`** — new typed bool field, default false, set during `RoslynModuleDiscovery.ParseProject` from `<AllowUnsafeBlocks>` element (case-insensitive on the value to handle Unity's `True` casing). Consumed by `ModuleCompilationBuilder.CreateCompilation` via `WithAllowUnsafe(...)`.
- **`INV-COMPFACT-001..003` documented in CLAUDE.md** — csproj is the source of truth for module-level compilation options; each fact lives as a typed `ModuleInfo` field set at discovery and consumed at compilation; csproj-edit invalidation flows for free through the existing v2 `AnalysisSnapshot.CsprojTimestamps` infrastructure (re-discovery rebuilds the entire `ModuleInfo`, not just one field).
- Closes the **Minis CS0227 false positives** in DAWG and the broader bug class: any csproj that uses unsafe blocks no longer poisons its semantic model with CS0227, restoring `find_references` / dependants / call-graph extraction for symbols inside `unsafe { ... }` regions.
- **5 regression tests** (3 discovery casing + 2 compilation contract).

#### Seam #3 — `RoslynSemanticView` (closes LB-BUG-003)

- **`Lifeblood.Adapters.CSharp.RoslynSemanticView`** — read-only typed accessor for the C# adapter's loaded semantic state (`Compilations`, `Graph`, `ModuleDependencies`). Constructed once per `GraphSession.Load` and shared by reference across consumers.
- **`RoslynCodeExecutor` refactor** — primary constructor takes a `RoslynSemanticView`. The view IS the script-host globals object: passed to `CSharpScript.RunAsync<RoslynSemanticView>(code, options, view, ...)` so `lifeblood_execute` scripts can read `Graph`, `Compilations`, `ModuleDependencies` as bare top-level identifiers. Backward-compatible secondary constructor takes only `compilations` for tests and standalone callers.
- **Default script imports extended** with `Lifeblood.Adapters.CSharp`, `Lifeblood.Domain.Graph`, `Microsoft.CodeAnalysis`, `Microsoft.CodeAnalysis.CSharp`. **Default script references extended** to include the assemblies that define the script-globals types so `Graph` / `Compilations` resolve at compile time.
- **`INV-VIEW-001..003` documented in CLAUDE.md** — each language adapter publishes a typed read-only view of its loaded state; tools that need read access consume the view (not raw fields); the view is shared by reference across consumers.
- **4 regression tests** (1 view-shape sanity + 3 script-globals end-to-end including backward compat for pure-C# scripts).

### Changed

- **All read-side MCP handlers route through `ISymbolResolver`.** Truncated method ids and bare short names now resolve correctly across `lookup`, `dependants`, `dependencies`, `blast_radius`, `find_references`, `find_definition`, `find_implementations`, `documentation`, `rename`. The reviewer's LB-BUG-002 (`method:Voice.SetPatch` returning `[]`) is closed.
- **`find_references` adds optional `includeDeclarations` parameter.** Default false preserves prior behavior.
- **`lifeblood_lookup` response includes `filePaths[]`** alongside the existing single `filePath` (now deterministic for partial types).
- **MCP tool count: 17 → 18** (added `lifeblood_resolve_short_name`).
- **Tests: 310 → 328** (+18 across the three seams).

### Fixed

- **`find_references` / call-graph extraction silently returned 0 results for every method in any Unity / .NET Framework / Mono workspace.** Type-only references (`type:Foo`) worked, method-level references (`method:Foo.Bar()`, `dependants`, call edges) returned empty. Three-layer root cause:
  1. **Resolver silent fallback**: `RoslynWorkspaceManager.FindInCompilation` enumerated *all* methods on a type when no overload matched, returning `methods[0]` — so asking for a method that didn't exist returned an unrelated method's call sites. The wrong target then matched no nodes and the query came back empty.
  2. **Display-string match across the source/metadata boundary**: `RoslynCompilationHost.FindReferences` compared `ISymbol.ToDisplayString()` against the resolved target. Different parameter formatters across source and metadata symbols (driven by Roslyn's default `CSharpErrorMessageFormat` and version drift) silently dropped legitimate call sites.
  3. **BCL double-load** (the dominant cause): `ModuleCompilationBuilder.CreateCompilation` always prepended the host .NET 8 BCL bundle, even for modules that already shipped their own BCL via csproj `<Reference Include="netstandard|mscorlib|System.Runtime">` (Unity ships .NET Standard 2.1; .NET Framework / Mono ship mscorlib). Result: every System type existed in two assemblies. Roslyn emitted CS0433 (ambiguous type) and CS0518 (predefined type missing) on every System usage, the semantic model became unusable, `GetSymbolInfo` returned null at every call site, and every walker tool silently produced empty results.

  Empirical impact on a real Unity workspace (DAWG, 75 modules): `Nebulae.BeatGrid.Audio` module → 29,523 errors before, 3 unused-field warnings after. `find_references` for `method:Voice.SetPatch(VoicePatch)` → 0 → 18 references including the previously-invisible `voices[i].SetPatch(patch)` array-indexer call sites. Total graph edges: 78,126 → 86,334 (+8,208 edges that the broken compilation was silently dropping).

  Fix:
  - **Layer 1 (resolver)**: kind-filtered, name-filtered, signature-strict member lookup with documented contract — never silently substitute an unrelated member. Lenient escape valves (single overload, no signature given) are explicit.
  - **Layer 2 (matcher)**: replace display-string comparison with canonical Lifeblood symbol-ID comparison via `BuildSymbolId(resolved) == targetCanonicalId`. The graph and the walker now share one builder.
  - **Layer 3 (canonical format)**: `Internal.CanonicalSymbolFormat.ParamType` is the single pinned `SymbolDisplayFormat` for parameter type display strings. Every method-ID builder in the adapter (`RoslynSymbolExtractor`, `RoslynEdgeExtractor`, `RoslynCompilationHost.BuildSymbolId`, `RoslynWorkspaceManager.FindInCompilation`) routes through it. Lifeblood's symbol ID grammar is now owned by Lifeblood, not implicitly inherited from Roslyn's default.
  - **Layer 4 (BCL ownership, INV-BCL-001..005)**: new `BclOwnershipMode` enum on `ModuleInfo`. `RoslynModuleDiscovery` decides ownership ONCE during csproj parsing by inspecting `<Reference Include>` (parsed as assembly identity, handles both bare names and strong-name shapes) and `<HintPath>` basename. `ModuleCompilationBuilder` reads the field and gates host BCL injection — no detection logic at the compilation layer. Decision is single-source-of-truth.
  - **Layer 5 (incremental csproj invalidation, INV-BCL-005)**: `AnalysisSnapshot.CsprojTimestamps` tracks csproj timestamps. `IncrementalAnalyze` checks csproj timestamps before the .cs file loop and forces re-discovery + recompile when a csproj edits — closes the silent-stale-BclOwnership hole that would otherwise re-introduce the bug under incremental mode.

### Added

- `BclOwnershipMode` enum and `ModuleInfo.BclOwnership` field on `Lifeblood.Application.Ports.Left.IModuleDiscovery`.
- `Internal.CanonicalSymbolFormat` — single source of truth for parameter type display strings in Lifeblood method symbol IDs.
- `AnalysisSnapshot.CsprojTimestamps` for incremental csproj-edit detection.
- 9 regression tests in `FindReferencesCrossModuleTests.cs` (cross-module class methods, struct methods, partial structs, array-indexer receivers, library-defined parameter types, overload disambiguation, silent-fallback bug class).
- 4 BCL ownership discovery tests in `HardeningTests.cs` (bare Include, strong-name Include, HintPath-only, plain SDK).
- 3 BCL ownership compilation/integration tests in new `BclOwnershipCompilationTests.cs` (ModuleProvided contract, HostProvided contract, end-to-end two-module find_references).
- 2 incremental csproj-invalidation tests in `IncrementalAnalyzeTests.cs`.
- New invariants documented in `CLAUDE.md`: INV-BCL-001 through INV-BCL-005, plus the canonical symbol ID grammar.
- Architectural plan at `.claude/plans/bcl-ownership-fix.md` (v2 with reviewer corrections folded in).

### Changed

- Tests: 293 → 310 (+17 regression tests across discovery, compilation, incremental, and integration layers).

## [0.5.1] - 2026-04-09

### Fixed

- **CS0518 "System.Object is not defined" on multi-module workspaces** (#1): `lifeblood_execute` failed on any code against workspaces with many modules (e.g., Unity/DAWG with 75 modules). Three-layer root cause:
  1. `ScriptOptions.Default` contains 25 "Unresolved" named references — placeholders that never resolve to actual DLLs in a published app.
  2. Adding `compilation.References` (target project's transitive deps) injected Unity's netstandard BCL stubs, conflicting with the host .NET 8 runtime.
  3. Two competing `System.Object` definitions from different BCL flavors caused the script compiler to fail.
  
  Fix: Explicitly load the host .NET runtime's BCL assemblies from the runtime directory (17 core assemblies). Use `WithReferences` (replace) instead of `AddReferences` to drop the useless "Unresolved" defaults. Only add `CompilationReference` per project module — no transitive deps.

### Added

- 5 regression tests for the CS0518 bug class (downgraded multi-module topology).
- `LoadHostBclReferences()` — resolves host BCL once per process via `Lazy<T>`.

### Changed

- **MCP server deployment**: `.mcp.json` now points to `dist/` (published output) instead of `dotnet run --project`. Decouples running server from build locks.
- Tests: 288 → 293.

## [0.5.0] - 2026-04-09

Cross-assembly semantic analysis. The gap that required grep fallback for cross-module consumer counting is gone.

### Fixed

- **Cross-assembly edges were silently dropped**: `IsFromSource` rejected metadata symbols from other analyzed modules. Renamed to `IsTracked` — now accepts symbols whose `ContainingAssembly.Name` matches a known workspace module. Resolves F3 (empty cross-module dependency matrix).
- **FindReferences returned empty for cross-assembly symbols**: `SymbolFinder.FindReferencesAsync` doesn't work across AdhocWorkspace project boundaries. Rewritten to direct compilation scan (same proven pattern as `FindImplementations`).
- **FindDefinition/GetDocumentation returned empty for cross-assembly symbols**: `ResolveSymbol` could return a metadata copy (no source location, no XML docs). New `ResolveFromSource` prefers source-defined symbols across all compilations.
- **Dead `IsFromSource` in RoslynCompilationHost**: removed and replaced with wired `IsFromSource` used by `ResolveFromSource` for source preference.
- **Module discovery merge**: filesystem scan now merges with csproj `<Compile Include>` items instead of choosing one path.

### Changed

- **CrossModuleReferences capability**: upgraded from `BestEffort` to `Proven`.
- **MinVer auto-versioning**: version derived from git tags via MinVer. No manual bumping — just tag and push.
- **MCP server version**: `initialize` response now reports actual assembly version instead of hardcoded `"1.0.0"`.
- Tests: 281 → 288.

### Added

- **Cross-assembly edge extraction**: `RoslynEdgeExtractor.KnownModuleAssemblies` enables edges between symbols in different assemblies (References, Implements, Inherits, Calls, Overrides).
- **HintPath DLL loading**: `ModuleInfo.ExternalDllPaths` + `RoslynModuleDiscovery` parses `<HintPath>` from csproj `<Reference>` elements. Unity engine assemblies loaded as metadata references via `SharedMetadataReferenceCache`, resolving thousands of CS0246 false diagnostics.
- **Module dependency threading**: `RoslynWorkspaceAnalyzer.ModuleDependencies` propagated through `RoslynCompilationHost`/`RoslynWorkspaceRefactoring` → `RoslynWorkspaceManager` for ProjectReference-linked workspace (used by Rename).
- 7 new tests: 4 cross-assembly edge extraction (type ref, method call, BCL filter, backward compat), 2 cross-assembly graph integration, 1 cross-assembly FindReferences.
- `.gitattributes` for consistent line endings.
- `global.json` to pin .NET 8 SDK.
- Silent test skips replaced with explicit `Skip` output.
- Bare catches narrowed to typed exceptions in tests and Unity bridge.

## [0.3.0] - 2026-04-08

Incremental re-analyze, file-level impact, Unity bridge, built-in rule packs.

### Added

- **Incremental re-analyze**: `lifeblood_analyze` with `incremental: true` only recompiles modules with changed files. Seconds instead of minutes. Falls back to full analysis if no previous snapshot exists or if modules were added/removed.
- **File-level edge derivation**: `GraphBuilder.Build()` derives `file:X → file:Y References` edges from symbol-level edges with `edgeCount` property. Evidence: Inferred.
- **lifeblood_file_impact tool**: "If I change this file, what other files break?" — derived from file-level edges.
- **Unity Editor bridge**: `unity/Editor/LifebloodBridge/` — sidecar MCP server auto-discovered via `[McpForUnityTool]`. All 17 tools available in Unity Editor.
- **Built-in rule packs**: `hexagonal`, `clean-architecture`, `lifeblood` — resolve by name (`--rules hexagonal`).
- **MCP setup docs**: copy-paste configs for Claude Code, Cursor, VS Code, Claude Desktop, Unity.
- **Editorconfig**: `.editorconfig` with C# conventions.
- **Graceful shutdown**: Ctrl+C / SIGTERM handling in MCP server.
- **ProcessIsolatedCodeExecutor tests**: integration tests with build guard + timeout kill test.
- **10 regression tests**: security scanner (constructors, expression compile), edge deduplication, streaming downgrade.

### Fixed

- **ProcessIsolatedCodeExecutor path with spaces**: quoted project path in dotnet CLI arguments.
- **CI golden repo restore**: WriteSideIntegrationTests skip gracefully when golden repo unavailable.

### Changed

- Tool count: 12 → 17 (7 read + 10 write).
- Self-analysis: 878 symbols → 1148 symbols, 2139 edges → 3196 edges.
- Tests: 214 → 281.
- Architecture screenshot regenerated.

## [0.2.2] - 2026-04-08

### Added

- **DAWG production verification**: 43,800 symbols, 70,600 edges, 75 modules, 2,404 types, 34 cycles, ~4GB peak.

## [0.2.1] - 2026-04-08

### Fixed

- **Edge deduplication in GraphBuilder**: partial classes caused duplicate Overrides/Inherits/Implements edges. `Build()` now deduplicates all edges by `(sourceId, targetId, kind)`.
- **Unity csproj support**: detect `<Compile Include>` items in old-format csproj. If present, use those instead of filesystem scan. Prevents 75-project recursive scan hang.

## [0.2.0] - 2026-04-08

Bidirectional Roslyn, streaming compilation, 45-pass hardening, Python adapter.

### Added

- **Bidirectional Roslyn**: 10 write-side MCP tools — execute, diagnose, compile-check, find references, find definition, find implementations, symbol at position, documentation, rename, format.
- **Streaming compilation with downgrading**: compile one module at a time in topological order, then `Emit()` → `MetadataReference.CreateFromImage()`. Memory: 32GB → ~4GB for 75-module project.
- **SharedMetadataReferenceCache**: NuGet MetadataReferences deduplicated across modules.
- **NuGet package resolution**: compilations resolve packages from `obj/project.assets.json`.
- **Domain result types**: DiagnosticInfo, CompileCheckResult, CodeExecutionResult, ReferenceLocation, TextEdit.
- **3 new port interfaces**: ICodeExecutor, ICompilationHost, IWorkspaceRefactoring.
- **3 Roslyn implementations**: RoslynCodeExecutor, RoslynCompilationHost, RoslynWorkspaceRefactoring.
- **Compilation preservation**: `AnalysisConfig.RetainCompilations` controls mode (streaming CLI vs retained MCP).
- **Cross-module Roslyn resolution**: compilations built in dependency order via topological sort.
- **Python adapter**: standalone `ast`-based analyzer. Zero dependencies. Self-analyzing.
- **IFileSystem wired**: expanded interface, `PhysicalFileSystem` implementation, injected everywhere.
- **IRuleProvider wired**: `RulesLoader` converted from static class.
- **IProgressSink wired**: `ConsoleProgressSink` writes to stderr.
- **Use case tests**: 8 tests for AnalyzeWorkspaceUseCase and GenerateContextUseCase.
- **Edge extraction tests**: generics, typeof, attributes, return types, C# 9 target-typed new.
- **Golden repo tests**: mini-app fixtures for TypeScript and Python adapters.
- **Dotnet tool packaging**: `Directory.Build.props`, CLI as `lifeblood`, MCP Server as `lifeblood-mcp`.
- **CHANGELOG.md**: version history.

### Fixed

- **Native DLL metadata error**: `LoadBclReferences` filters non-.NET DLLs via `PEReader.HasMetadata`.
- **Session corruption on failed analyze**: `GraphSession.Load` commits state atomically.
- **Symbol resolution from wrong compilation**: FindReferences and Rename resolve from workspace projects.
- **CompileCheck false negatives**: pre-existing diagnostics filtered out.
- **Timeout bypass**: `Task.Run` + `Task.Wait` for hard timeout enforcement.
- **Notification null leak**: `Program.cs` no longer serializes `null` for notifications.
- **RS1024 warning**: Roslyn symbol comparisons use `SymbolEqualityComparer.Default`.
- **Return type edges**: fixed MethodDeclarationSyntax filter that blocked return type references.

### Changed

- Port count: 10 → 14 (3 write-side + 1 blast radius).
- Tests: 109 → 241.
- Self-analysis: 634 symbols → 1057 edges, 888 edges → 2594.
- CI: 4 parallel jobs (build, TypeScript adapter, Python adapter, dogfood).
- Build: 0 warnings, 0 errors.

## [0.1.1] - 2026-04-08

Deep hardening pass. 10 bugs fixed, architecture granulated, AST security scanner, 180 tests.

### Fixed

- **Property symbol ID collision**: `SymbolIds.Property` generated `field:` prefix. Now uses `property:` prefix.
- **GetDiagnostics silent fallback**: non-existent module returned ALL diagnostics. Now returns empty.
- **File.Exists bypassing IFileSystem port**: NuGet resolver used raw `File.Exists`.
- **Property accessor dangling edges**: edge extractor used `get_X`/`set_X` with no matching symbol.
- **MCP parse error response**: malformed JSON-RPC caused no response. Now returns -32700.
- **Notification null leak**: `Dispatch()` returned `null!` for notifications. Now typed as `JsonRpcResponse?`.
- **NuGet catch-all too broad**: bare `catch {}` narrowed to typed exceptions.
- **BCL loader bare catches**: narrowed to typed exceptions.

### Added

- **ScriptSecurityScanner**: Roslyn AST-based security layer with two-layer defense.
- **RoslynWorkspaceManager**: shared workspace infrastructure (~80 LOC dedup).
- **BclReferenceLoader, NuGetReferenceResolver, ModuleCompilationBuilder**: extracted internal components.
- **WriteToolHandler**: extracted 6 write-side MCP handlers.
- **AnalysisPipeline**: moved to Analysis assembly — single source of truth.
- **59 new tests**: write-side tools, AST security, symbol ID parsing, pipeline, architecture.

### Changed

- Tests: 121 → 180.
- Source files: 58 → 63.
- Average LOC/file: 84 → 80.
- Zero bare catches remaining in source.

## [0.1.0] - 2026-04-07

First public release. Framework is dogfood-verified and CI-green.

### Added

- **Domain**: immutable semantic graph model (Symbol, Edge, Evidence, ConfidenceLevel, GraphBuilder, GraphValidator, GraphDocument).
- **Application**: 10 port interfaces, AnalyzeWorkspaceUseCase, GenerateContextUseCase.
- **Adapters.CSharp**: Roslyn-based workspace analyzer, module discovery, symbol/edge extractors.
- **Adapters.JsonGraph**: universal JSON import/export with full metadata round-trip.
- **Analysis**: CouplingAnalyzer, BlastRadiusAnalyzer, CircularDependencyDetector, TierClassifier, RuleValidator.
- **Connectors.ContextPack**: AI context pack generator, instruction file generator, reading order.
- **Connectors.Mcp**: MCP graph provider with blast radius delegation.
- **Server.Mcp**: MCP server with 6 tools over stdio (JSON-RPC 2.0).
- **CLI**: `analyze`, `context`, `export` commands with centralized validation and exit codes.
- **TypeScript adapter**: standalone Node.js adapter using `ts.createProgram` + TypeChecker.
- **Rule packs**: hexagonal, clean-architecture, lifeblood (self-validating).
- **JSON schemas**: `graph.schema.json`, `rules.schema.json`.
- **Tests**: 109 xUnit tests.
- **CI**: 3 parallel jobs (build, TypeScript adapter, dogfood with cross-language proof).
- **Adapter contribution guides**: Go, Python, Rust (contract and checklist, no implementation code).
- **Documentation**: architecture docs, 11 frozen ADRs, adapter guide, dogfood findings, CLAUDE.md.

[Unreleased]: https://github.com/user-hash/Lifeblood/compare/v0.5.1...HEAD
[0.5.1]: https://github.com/user-hash/Lifeblood/compare/v0.5.0...v0.5.1
[0.5.0]: https://github.com/user-hash/Lifeblood/compare/v0.3.0...v0.5.0
[0.3.0]: https://github.com/user-hash/Lifeblood/compare/v0.2.2...v0.3.0
[0.2.2]: https://github.com/user-hash/Lifeblood/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/user-hash/Lifeblood/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/user-hash/Lifeblood/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/user-hash/Lifeblood/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/user-hash/Lifeblood/releases/tag/v0.1.0

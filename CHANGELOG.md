# Changelog

All notable changes to Lifeblood are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- **Module discovery merge**: filesystem scan now merges with csproj `<Compile Include>` items instead of choosing one path.

### Changed

- **MinVer auto-versioning**: version derived from git tags via MinVer. No manual bumping — just tag and push.
- **MCP server version**: `initialize` response now reports actual assembly version instead of hardcoded `"1.0.0"`.

### Added

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

[Unreleased]: https://github.com/user-hash/Lifeblood/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/user-hash/Lifeblood/compare/v0.2.2...v0.3.0
[0.2.2]: https://github.com/user-hash/Lifeblood/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/user-hash/Lifeblood/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/user-hash/Lifeblood/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/user-hash/Lifeblood/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/user-hash/Lifeblood/releases/tag/v0.1.0

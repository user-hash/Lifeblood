# Changelog

All notable changes to Lifeblood are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.1] - 2026-04-08

Deep hardening pass. 10 bugs fixed, architecture granulated, AST security scanner, 180 tests.

### Fixed

- **Property symbol ID collision**: `SymbolIds.Property` generated `field:` prefix — properties silently failed on FindReferences/Rename. Now uses separate `property:` prefix.
- **GetDiagnostics silent fallback**: Requesting non-existent module returned ALL diagnostics instead of empty.
- **File.Exists bypassing IFileSystem port**: NuGet resolver used raw `File.Exists` instead of the injected port.
- **Property accessor dangling edges**: Edge extractor used `get_X`/`set_X` as edge sources with no matching symbol. Now skips to enclosing method.
- **MCP parse error response**: Malformed JSON-RPC input caused no response (client hang). Now returns JSON-RPC -32700 error.
- **Notification null leak**: `Dispatch()` returned `null!` for notifications. Now properly typed as `JsonRpcResponse?`.
- **NuGet catch-all too broad**: Bare `catch {}` in NuGet resolution masked JSON schema changes. Narrowed to `IOException | JsonException | UnauthorizedAccessException`.
- **BCL loader bare catches**: Two remaining bare catches in DLL loading narrowed to typed exceptions.

### Added

- **ScriptSecurityScanner**: Roslyn AST-based security layer. Detects reflection (`GetMethod`, `Invoke`, `SetValue`, `DynamicInvoke`), `unsafe` blocks, pointer types. Two-layer defense: string blocklist + AST scan.
- **RoslynWorkspaceManager**: Shared workspace infrastructure extracted from CompilationHost and WorkspaceRefactoring (~80 LOC dedup).
- **BclReferenceLoader**: Extracted static BCL assembly loading + `IsNativeDll`.
- **NuGetReferenceResolver**: Extracted `project.assets.json` parsing.
- **ModuleCompilationBuilder**: Extracted compilation creation + topological sort.
- **WriteToolHandler**: Extracted 6 write-side MCP handlers with shared guard.
- **AnalysisPipeline**: Moved to `Lifeblood.Analysis` — single source of truth for CLI and MCP server.
- **Architecture invariant test**: Analysis depends only on Domain (was untested).
- **59 new tests**: Write-side tools (19), AST security scanner (11), symbol ID parsing (7), pipeline integration (3), blocked patterns (5), architecture (1), edge cases (13).

### Changed

- Blocked patterns expanded: 5 → 18 (`File.Write*`, `Assembly.Load*`, `Reflection.Emit`, etc.).
- Tests: 121 → 183.
- Source files: 58 → 63.
- Average LOC/file: 84 → 80.
- Files > 200 LOC: 6 → 4.
- Dogfood: 797 symbols / 1971 edges → 878 symbols / 2139 edges.
- Zero bare catches remaining (was 4).

## [0.3.1] - 2026-04-08

Dogfood: code execution. 7 bugs fixed, NuGet resolution, 30/30 MCP integration tests.

### Fixed

- **Native DLL metadata error**: `LoadBclReferences` now filters non-.NET DLLs via `PEReader.HasMetadata`. Was causing CS0009 on all write-side tools.
- **Session corruption on failed analyze**: `GraphSession.Load` now commits state atomically — failed loads no longer destroy the active session.
- **Symbol resolution from wrong compilation**: `FindReferences` and `Rename` now resolve symbols from workspace projects, not standalone compilations. Fixes 0-result find-references and rename exceptions.
- **CompileCheck false negatives**: Pre-existing compilation diagnostics are now filtered out. Only snippet-introduced errors affect the success flag.
- **Timeout bypass**: Script execution now uses `Task.Run` + `Task.Wait` for hard thread-level timeout enforcement. `Thread.Sleep` / `while(true)` can no longer escape the timeout.
- **Notification null leak**: `Program.cs` no longer serializes `null` to stdout for `initialized` notifications.
- **RS1024 warning**: All Roslyn symbol comparisons use `SymbolEqualityComparer.Default.Equals()`.

### Added

- **NuGet package resolution**: Compilations now resolve NuGet packages from `obj/project.assets.json`. Diagnostics dropped from 1569 → 1143.
- **DiagnosticInfo.Module**: Diagnostics now include the source module name.
- **Dogfood doc**: `docs/DOGFOOD_CODE_EXECUTION.md` — third dogfood milestone with 30 integration tests.
- **Architecture screenshot**: Regenerated 2x DPI, updated stats (797 symbols, 1971 edges, 13 ports).

### Changed

- Dogfood: 791 symbols / 1920 edges → 797 symbols / 1971 edges.
- Architecture diagram: updated port count (10 → 13), tool descriptions, footer stats.
- CLAUDE.md and ARCHITECTURE.md: added 3 write-side port interfaces to documentation.
- README.md: updated self-analysis stats.
- Build: 0 warnings, 0 errors (was 2 warnings).

## [0.3.0] - 2026-04-08

Bidirectional Roslyn — compiler-as-a-service via MCP.

### Added

- **Bidirectional Roslyn**: 6 write-side MCP tools — execute C# code, diagnose, compile-check, find references, rename, format. Roslyn exposed as full compiler-as-a-service.
- **Domain result types**: DiagnosticInfo, CompileCheckResult, CodeExecutionResult, ReferenceLocation, TextEdit (pure, zero deps).
- **3 new port interfaces**: ICodeExecutor, ICompilationHost, IWorkspaceRefactoring (language-agnostic).
- **3 Roslyn implementations**: RoslynCodeExecutor (CSharpScript), RoslynCompilationHost (diagnostics/emit/SymbolFinder), RoslynWorkspaceRefactoring (Renamer/Formatter via AdhocWorkspace).
- **Compilation preservation**: RoslynWorkspaceAnalyzer retains compilations after analysis for write-side reuse.
- **Edge extraction tests**: 4 new unit tests for generics, typeof, attributes, return types.
- **Return type edges**: Fixed MethodDeclarationSyntax filter that blocked return type references.
- **Golden repo tests**: mini-app fixtures for TypeScript and Python adapters with pattern assertions in CI.
- **Python .gitignore**: Exclude __pycache__ and build artifacts.

### Changed

- MCP tool count: 6 → 12 (6 read + 6 write).
- Port count: 10 → 13 (3 new write-side ports).
- NuGet: added Microsoft.CodeAnalysis.CSharp.Scripting 4.12.*, Microsoft.CodeAnalysis.CSharp.Workspaces 4.12.*.
- Dogfood: 704 symbols / 1772 edges → 791 symbols / 1920 edges.
- Tests: 117 → 121.

## [0.2.0] - 2026-04-08

Cross-module resolution, Python adapter, hexagonal port sealing.

### Added

- **Cross-module Roslyn resolution**: Compilations built in dependency order via topological sort. `CompilationReference` links projects so Roslyn resolves types across boundaries. `CrossModuleReferences` capability upgraded from `BestEffort` to `Proven`. Edge count jumped from 985 to 1746 on self-analysis (+77%).
- **Python adapter**: Standalone `ast`-based analyzer in `adapters/python/`. Zero external dependencies. Extracts classes, functions, methods, fields, inheritance, imports, type annotations. Self-analyzing.
- **IFileSystem wired**: Expanded interface (added `ReadLines`, `OpenRead`, `DirectoryExists`). `PhysicalFileSystem` implementation in Adapters.CSharp. Injected into RoslynModuleDiscovery, RoslynWorkspaceAnalyzer, GraphSession, RulesLoader, CLI.
- **IRuleProvider wired**: `RulesLoader` converted from static class to `IRuleProvider` implementation with `IFileSystem` injection.
- **IProgressSink wired**: `ConsoleProgressSink` writes progress to stderr. Injected into `AnalyzeWorkspaceUseCase` via CLI.
- **Use case tests**: 8 new tests for `AnalyzeWorkspaceUseCase` and `GenerateContextUseCase` with hand-rolled stubs.
- **Dotnet tool packaging**: `Directory.Build.props` with centralized v0.1.0 versioning. CLI packaged as `lifeblood`, MCP Server as `lifeblood-mcp`. CI pack step producing `.nupkg` artifacts.
- **CHANGELOG.md**: Version history.

### Changed

- All 10 application port interfaces now have concrete implementations (was 7/10).
- BCL references: full runtime directory loaded instead of just `System.Runtime.dll`.
- CI: 4 parallel jobs (build, TypeScript adapter, Python adapter, dogfood with 3-language cross-proof).
- Tests: 109 → 117.
- Dogfood: 634 symbols / 888 edges → 697 symbols / 1746 edges.

## [0.1.0] - 2026-04-07

First public release. Framework is dogfood-verified and CI-green.

### Added

- **Domain**: Immutable semantic graph model (Symbol, Edge, Evidence, ConfidenceLevel, GraphBuilder, GraphValidator, GraphDocument).
- **Application**: 10 port interfaces, AnalyzeWorkspaceUseCase, GenerateContextUseCase.
- **Adapters.CSharp**: Roslyn-based workspace analyzer, module discovery, symbol and edge extractors.
- **Adapters.JsonGraph**: Universal JSON import/export with full metadata round-trip.
- **Analysis**: CouplingAnalyzer, BlastRadiusAnalyzer, CircularDependencyDetector, TierClassifier, RuleValidator.
- **Connectors.ContextPack**: AI context pack generator, instruction file generator, reading order.
- **Connectors.Mcp**: MCP graph provider with blast radius delegation.
- **Server.Mcp**: MCP server with 6 tools over stdio (JSON-RPC 2.0).
- **CLI**: `analyze`, `context`, `export` commands with centralized validation and exit codes.
- **TypeScript adapter**: Standalone Node.js adapter using `ts.createProgram` + TypeChecker.
- **Rule packs**: hexagonal, clean-architecture, lifeblood (self-validating).
- **JSON schemas**: `graph.schema.json`, `rules.schema.json`.
- **Tests**: 109 xUnit tests covering extractors, golden repos, round-trip, architecture invariants, MCP server, CLI pipeline.
- **CI**: 3 parallel jobs (build, TypeScript adapter, dogfood with cross-language proof).
- **Adapter contribution guides**: Go, Python, Rust (contract and checklist, no implementation code).
- **Documentation**: Architecture docs, 11 frozen ADRs, adapter guide, dogfood findings, CLAUDE.md.

[0.1.0]: https://github.com/user-hash/Lifeblood/releases/tag/v0.1.0

# Changelog

All notable changes to Lifeblood are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

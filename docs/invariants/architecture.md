# Architecture Invariants

Hexagonal core: pure Domain, ports-only Application, isolated adapters
and connectors. Every invariant on this page is enforced by ratchet
tests under `tests/Lifeblood.Tests/ArchitectureInvariantTests.cs`.

## Domain Purity

- **INV-DOMAIN-001**: `Lifeblood.Domain` has ZERO dependencies. Not Roslyn, not JSON, not System.IO, not anything. If a PackageReference appears, the architecture is broken.
- **INV-DOMAIN-002**: Domain never references Application, Adapters, Connectors, or CLI.

## Application Layer

- **INV-APP-001**: Application depends only on Domain.
- **INV-APP-002**: Application never references concrete adapters or connectors. Only port interfaces.

## Graph Model

- **INV-GRAPH-001**: SymbolKind enum is language-agnostic. No C#-isms, no Python-isms.
- **INV-GRAPH-002**: Language-specific metadata goes in `Symbol.Properties` dictionary.
- **INV-GRAPH-003**: Every edge carries Evidence (kind, adapter, confidence, source span).
- **INV-GRAPH-004**: Analyzers do NOT modify the graph. Results are separate objects. The graph is read-only after construction.

## Left Side (Language Adapters)

- **INV-ADAPT-001**: Every adapter declares capabilities honestly via AdapterCapability.
- **INV-ADAPT-002**: C# adapter is the reference. Most complete, best tested.
- **INV-ADAPT-003**: External adapters communicate via JSON graph schema only.
- **INV-ADAPT-004**: No adapter code leaks into Domain or Application.
- **INV-NATIVE-CLANG-BOUNDARY-001. Native Clang is an external JSON adapter, not a managed Lifeblood module.** `adapters/native-clang` builds a standalone executable that emits `schemas/graph.schema.json`; Lifeblood imports the graph through `JsonGraphImporter`. Native source/build files must not reference `Lifeblood.Domain`, `Lifeblood.Application`, `Lifeblood.Analysis`, managed adapters, connectors, CLI, or MCP server projects. Ratchet-tested by `NativeClangArchitectureTests.NativeClang_SourceAndBuildFilesDoNotReferenceManagedLifebloodModules`.
- **INV-NATIVE-CLANG-LIBCLANG-001. Raw libclang API usage has an explicit leak budget.** During the beta-to-reference-architecture transition, every file that touches `clang-c`, `CX*`, or `clang_*` is listed in `NativeClangArchitectureTests.NativeClang_LibClangApiUsageStaysInsideExplicitBoundary`. New raw libclang touch points require an intentional allowlist edit. The shrink path is to translate libclang cursors into native fact DTOs at the adapter edge; function, global, and type-member symbol emission are now graph-side leaves behind declaration facts.
- **INV-NATIVE-CLANG-RATCHET-001. Native executable ratchets can be fail-hard on demand.** `NativeClangExecutableRatchetTests` use explicit skip semantics when the native executable is absent, but `LIFEBLOOD_REQUIRE_NATIVE_CLANG=1` turns absence into a hard failure. Native release validation should set this variable so the suite cannot pass by skipping executable coverage.

## Right Side (AI Connectors)

- **INV-CONN-001**: Connectors depend on Application ports, not on adapters.
- **INV-CONN-002**: MCP connector serves the graph read-only.
- **INV-CONN-003**: Context pack generator produces AI-consumable JSON, not human prose.

## Analysis

- **INV-ANALYSIS-001**: All analyzers are stateless. Input: graph + config. Output: typed result.
- **INV-ANALYSIS-002**: No analyzer modifies the graph. Read-only.
- **INV-ANALYSIS-003**: CouplingAnalyzer counts distinct dependants, not edge count.

## Testing

- **INV-TEST-001**: Every adapter passes the same golden repo contract tests.
- **INV-TEST-002**: Every analyzer is tested against golden repos.

## Pipeline

- **INV-PIPE-001**: The pipeline is deterministic. Same input = same output.

## Process Isolation

- **INV-SCRIPTHOST-001. `Lifeblood.ScriptHost` has zero `ProjectReference`.** The script host is a process-isolated child that runs untrusted code; its "no access to parent state" guarantee depends on not taking a ProjectReference to any Lifeblood module. NuGet PackageReferences are allowed (Microsoft.CodeAnalysis.CSharp.Scripting is load-bearing). Ratchet-tested by `ArchitectureInvariantTests.ScriptHost_HasZeroProjectReferences`.

## Composition-Root Allowlist

- **INV-COMPROOT-001. Composition roots use only the allowlist.** `Lifeblood.CLI` and `Lifeblood.Server.Mcp` reference only `{Domain, Application, Analysis, Adapters.CSharp, Adapters.JsonGraph, Connectors.ContextPack, Connectors.Mcp}`. The allowlist is declared once as `private static readonly HashSet<string> CompositionRootAllowedModules` on `ArchitectureInvariantTests`. Single source of truth. Expanding it is a conscious architectural decision requiring a commit that edits the test. Ratchet-tested by `CompositionRoot_CLI_UsesOnlyAllowedModules` and `CompositionRoot_ServerMcp_UsesOnlyAllowedModules`.

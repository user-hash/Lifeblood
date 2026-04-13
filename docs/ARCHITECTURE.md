# Architecture

Hexagonal framework. Two sides. Pure core.

```
LEFT SIDE                     CORE                     RIGHT SIDE
(Language Adapters)        (The Pipe)               (AI Connectors)

Roslyn (C#)       ──┐                            ┌──  AgentContextGenerator
JSON graph        ──┼→  Domain  →  Application  →┤──  InstructionFileGenerator
                  ──┘       ↑                     ├──  LifebloodMcpProvider
                      Analysis (optional)         ├──  LifebloodSymbolResolver
                                                  ├──  LifebloodSemanticSearchProvider
                                                  ├──  LifebloodDeadCodeAnalyzer
                                                  ├──  LifebloodPartialViewBuilder
                                                  ├──  LifebloodInvariantProvider
                                                  └──  CLI / CI
```

## Three Layers of Truth

1. **Syntax truth.** What the parser proves from source text. Every adapter can provide this.
2. **Semantic truth.** What the compiler resolves. Type identity, overload resolution, call targets. Only compiler-grade adapters like Roslyn provide this at Proven confidence.
3. **Derived truth.** What Lifeblood computes from the graph. Coupling, blast radius, boundary violations, tier classification, cycle detection, dead-code candidates, invariant audits.

These layers stay separate. Every edge carries Evidence saying which layer produced it and how confident the adapter is.

## Dependency Flow

```
CLI (composition root)
  → Application → Domain (pure leaf, zero deps)
  → Adapters.CSharp → Application
  → Adapters.JsonGraph → Application
  → Connectors.ContextPack → Application
  → Connectors.Mcp → Application (blast radius via IBlastRadiusProvider port)
  → Analysis → Domain
```

Adapters and Connectors depend inward on Application ports. They never reference each other. Domain is always the leaf with zero dependencies.

## Assemblies

| Assembly | Role | Dependencies |
|----------|------|-------------|
| **Lifeblood.Domain** | Graph model, Evidence, ConfidenceLevel, GraphBuilder, GraphValidator, rules, results | None |
| **Lifeblood.Application** | 22 port interfaces including `IWorkspaceAnalyzer`, `ICompilationHost`, `ISymbolResolver`, `ISemanticSearchProvider`, `IDeadCodeAnalyzer`, `IPartialViewBuilder`, `IInvariantProvider`, `IUsageProbe`, `IUsageCapture`. AnalyzeWorkspaceUseCase, GenerateContextUseCase | Domain |
| **Lifeblood.Adapters.CSharp** | Roslyn reference adapter. Workspace analyzer, module discovery, symbol/edge extraction, RoslynSemanticView, CanonicalSymbolFormat (single source of truth for parameter-type display), CsprojPaths (shared path normalization for csproj parsing), SnippetWrapper (compile_check auto-wrap for bare statement snippets) | Application, Roslyn |
| **Lifeblood.Adapters.JsonGraph** | JSON import/export with round-trip fidelity | Application |
| **Lifeblood.Connectors.ContextPack** | AgentContextGenerator, InstructionFileGenerator, ReadingOrderGenerator | Application |
| **Lifeblood.Connectors.Mcp** | LifebloodMcpProvider (lookup, deps, dependants, blast radius, file impact), LifebloodSymbolResolver (identifier resolution + wrong-namespace short-name fallback), LifebloodSemanticSearchProvider (tokenized ranked-OR search over name + xmldoc), LifebloodDeadCodeAnalyzer (experimental / advisory), LifebloodPartialViewBuilder, LifebloodInvariantProvider (CLAUDE.md runtime parser + cache), ClaudeMdInvariantParser (pure text → records), InvariantParseCache (generic timestamp-invalidated cache), McpProtocolSpec (single source of truth for JSON-RPC wire constants) | Application |
| **Lifeblood.Analysis** | CouplingAnalyzer, BlastRadiusAnalyzer, CircularDependencyDetector, TierClassifier, RuleValidator | Domain |
| **Lifeblood.Server.Mcp** | MCP server host. Stdio JSON-RPC. 22 tools (12 read + 10 write). Bidirectional Roslyn. McpDispatcher owns the wire protocol. | Application, Adapters.CSharp, Connectors |
| **Lifeblood.ScriptHost** | Process-isolated code execution harness. Separate process, no shared memory. Zero ProjectReferences (INV-SCRIPTHOST-001). | Roslyn Scripting only |
| **Lifeblood.CLI** | Composition root: AnalysisPipeline, RulesLoader, thin dispatch | Everything |

## Domain Model

The domain is pure. No Roslyn, no JSON, no System.IO.

- **Symbol.** A node: module, file, namespace, type, method, field, property (including events via `isEvent` and indexers via `isIndexer`), parameter.
- **Edge.** A directed relationship: contains, dependsOn, implements, inherits, calls, references, overrides.
- **Evidence.** Provenance: syntax, semantic, or inferred. Plus the adapter name and a `ConfidenceLevel`.
- **GraphBuilder.** Constructs graphs, synthesizes Contains edges from ParentId, deduplicates symbols, derives file-level edges from symbol-level edges (`INV-FILE-EDGE-001`), sorts output deterministically.
- **GraphValidator.** Rejects malformed graphs: dangling edges, duplicates, orphaned parents, self-references.
- **SemanticGraph.** The immutable graph with thread-safe lazy indexes.
- **AnalysisSnapshot** (adapter-level). Caches per-file extraction results for incremental re-analyze. Tracks csproj timestamps so `<Nullable>` / `<AllowUnsafeBlocks>` / `<Reference>` edits trigger per-module re-discovery (`INV-BCL-005`).

Properties are `IReadOnlyDictionary` on the public surface. The graph is read-only after construction.

## Port Interfaces (22 total)

### Left Side (Language Adapters)
- `IWorkspaceAnalyzer`. Primary entry point. Takes `projectRoot` plus config and returns a `SemanticGraph`.
- `IModuleDiscovery`. Module and project discovery, returns `ModuleInfo[]`. Each `ModuleInfo` carries `BclOwnership` and `AllowUnsafeCode` parsed from csproj (csproj-driven compilation facts, `INV-COMPFACT-001..003`).
- `ICompilationHost`. Diagnostics, compile-checking, reference finding (Roslyn-backed). `FindReferences` has an explicit `IncludeDeclarations` option for partial-type declaration sites. `CompileCheck` auto-wraps bare statement snippets via `Internal.SnippetWrapper` so library modules accept them (v0.6.3).
- `ICodeExecutor`. Executes code snippets against the loaded workspace.
- `IWorkspaceRefactoring`. Rename (returns edits, does NOT apply), and format.
- `IGraphImporter`. Reads a stream into a `SemanticGraph` via the JSON protocol.
- `IGraphExporter`. Writes a `SemanticGraph` to a stream.

### Infrastructure
- `IFileSystem`. Filesystem abstraction for testability.
- `IUsageProbe`. Creates a fresh `IUsageCapture` per analyze run. Every analyze carries a structured `AnalysisUsage` snapshot with wall time, CPU time, peak memory, and GC counts (`INV-USAGE-001..002`, `INV-USAGE-PORT-001..002`, `INV-USAGE-PROBE-001..002`).
- `IUsageCapture`. One-shot usage capture scoped to a single analyze run.

### Analysis
- `IRuleProvider`. Loads architecture rules (mustNotReference, mayOnlyReference) from JSON.
- `IBlastRadiusProvider`. Transitive BFS over the dependency graph.

### Right Side (AI Connectors)
- `IAgentContextGenerator`. Builds an `AgentContextPack` from a graph and analysis result.
- `IInstructionFileGenerator`. Builds a markdown instruction file from a graph and analysis result.
- `IMcpGraphProvider`. Exposes `LookupSymbol`, `GetDependencies`, `GetDependants`, `GetBlastRadius`, `GetFileImpact`.
- `ISymbolResolver`. Resolves a bare short name, a truncated method id, a canonical id, or a **kind-prefixed id with wrong namespace** (v0.6.3, `INV-RESOLVER-005`) into a `SymbolResolutionResult` carrying `Outcome`, `Candidates`, `Diagnostic`, `DeclarationFilePaths`, and `Overloads`. Every read-side MCP handler routes through this resolver before any graph or workspace lookup (`INV-RESOLVER-001`). Partial-type unification is computed as a read model on the resolution result, not a graph schema change (`INV-RESOLVER-003..004`).
- `ISemanticSearchProvider`. Ranked search over symbol names, qualified names, and persisted xmldoc summaries. Tokenizes on whitespace with ranked-OR scoring (v0.6.3).
- `IDeadCodeAnalyzer`. Finds symbols with no incoming semantic references. v0.6.4 closed five false-positive classes (150 to 10 self-analysis, 96% true-positive rate on real workspace). See `INV-DEADCODE-001`.
- `IPartialViewBuilder`. Combines every partial declaration of a type into one view with file headers.
- `Invariants.IInvariantProvider`. Parses `CLAUDE.md` at the loaded project root and exposes architectural invariants as structured data. Three methods: `GetAll`, `GetById`, `Audit` (`INV-INVARIANT-001`).

## Capability-Aware

Not all adapters are equal. Roslyn gives Proven type resolution. A tree-sitter adapter might give BestEffort. Every adapter declares what it can do via `AdapterCapability`. Analysis results carry the confidence level. No fake authority.

Current Roslyn adapter capabilities:
- CanDiscoverSymbols: true
- TypeResolution: Proven
- CallResolution: Proven. v0.6.4 fixed the implicit global usings gap that caused 42% of `GetSymbolInfo` calls to return null.
- ImplementationResolution: Proven (cross-assembly via canonical Lifeblood ids, `INV-FINDIMPL-001`)
- CrossModuleReferences: Proven. The edge extractor tracks metadata symbols from known workspace modules via `KnownModuleAssemblies`. Transitive dependency closure is walked at compilation time (`INV-CANONICAL-001`).
- OverrideResolution: Proven (virtual dispatch chain via IMethodSymbol.OverriddenMethod)

## File-Level Edge Derivation

`GraphBuilder.Build()` derives `file:X → file:Y References` edges from symbol-level edges. For each non-Contains edge between symbols in different files, a file-level edge is emitted with an `edgeCount` property. These edges are derived truth (Evidence: Inferred, adapter: GraphBuilder). This is what answers `lifeblood_file_impact`: "if I change this file, what other files break?" (`INV-FILE-EDGE-001`)

## Incremental Re-Analyze

After a full analysis, subsequent calls with `incremental: true` only recompile modules whose source files changed. The `AnalysisSnapshot` caches per-file extraction results (symbols, edges, timestamps). Changed files are detected via filesystem timestamps and surgically replaced. Csproj timestamp changes trigger per-module re-discovery so compilation facts (`BclOwnership`, `AllowUnsafeCode`, etc.) never go stale. Module additions/removals fall back to full re-analyze.

v1 limitation: does not cascade to dependent modules when an API surface changes.

## Unity Bridge

The Unity bridge lives at `unity/Editor/LifebloodBridge/`. It runs Lifeblood as a sidecar MCP server (separate .NET process), communicating via JSON-RPC 2.0 over stdin/stdout. Unity projects create a directory junction to this path. The bridge auto-discovers via `[McpForUnityTool]` attributes and exposes all 22 tools to Unity MCP. Wire-format constants live in `McpProtocolSpec` (`INV-MCP-003`); the Unity mirror at `unity/Editor/LifebloodBridge/McpProtocolConstants.cs` is byte-compared by a ratchet test so the two sides cannot drift.

```
Unity Editor ──→ Unity MCP (scenes, GameObjects, assets)
                     │
                     └── [McpForUnityTool] ──→ Lifeblood MCP (child process)
                         └── 22 semantic tools over JSON-RPC
```

## Deterministic Output

GraphBuilder sorts symbols by ID and edges by source+target+kind before producing the graph. File discovery is sorted. Same input always produces the same output (`INV-PIPE-001`).

## Architectural Seams

The invariant register in `CLAUDE.md` is organized by seam. Each seam is a contract with a dedicated set of invariants and regression tests. A bug report that looks like "different surfaces broken in the same way" should first check whether one of these seams is the right place to fix it, before adding a per-surface patch.

**Seam 1. `ISymbolResolver` (Application port).** Identifier resolution is a port. Every read-side handler routes through one resolver before any graph or workspace lookup. Resolution order: exact canonical match → truncated method form → bare short name → **extracted short name from qualified input** (v0.6.3). Partial-type unification is a read model. The deterministic primary file path picker lives in `LifebloodSymbolResolver.ChoosePrimaryFilePath`. Invariants: `INV-RESOLVER-001..005`.

**Seam 2. Csproj-driven compilation facts as a documented convention.** Each module-level compilation option that the host needs to honor lives as a typed field on `ModuleInfo`, parsed once during `RoslynModuleDiscovery.ParseProject`, consumed in `ModuleCompilationBuilder.CreateCompilation`. Typed fields today: `BclOwnership`, `AllowUnsafeCode`. Csproj-edit invalidation flows through `AnalysisSnapshot.CsprojTimestamps`. Re-discovery rebuilds the entire `ModuleInfo`. Invariants: `INV-COMPFACT-001..003`.

**Seam 3. `RoslynSemanticView` (typed adapter view).** Each language adapter publishes a typed read-only view of its loaded semantic state. The C# adapter's `RoslynSemanticView` exposes `Compilations`, `Graph`, and `ModuleDependencies`. Constructed once per `GraphSession.Load`, shared by reference. `RoslynCodeExecutor` consumes the view as the script-host globals object. Invariants: `INV-VIEW-001..003`.

**Seam 4. Canonical symbol-id determinism.** Canonical symbol ids must be byte-identical for the same underlying method regardless of which compilation extracts it. The fix is at `ModuleCompilationBuilder.ProcessInOrder` which routes direct dependencies through `ComputeTransitiveDependencies` so every module compilation sees the full transitive closure. Drift manifests as silent zero results in every cross-module lookup tool. Invariants: `INV-CANONICAL-001`. Known remaining gap class tracked in `INV-DEADCODE-001` for the v0.6.4 investigation.

**Seam 5. MCP wire protocol source of truth.** Protocol version, JSON-RPC method names, and notification method names live exclusively in `Lifeblood.Connectors.Mcp.McpProtocolSpec`. Clients that cannot take a project reference (Unity bridge) ship a standalone mirror pinned by byte-equal ratchet tests. Internal registry records and wire-format DTOs are separate types. Invariants: `INV-MCP-001..003`, `INV-TOOLREG-001`.

**Seam 6. Invariant introspection.** `CLAUDE.md` is the single source of truth for architectural invariants. `LifebloodInvariantProvider` parses it at runtime via `ClaudeMdInvariantParser` (pure text → records), caches per-project-root with timestamp invalidation via `InvariantParseCache<T>` (generic, reusable), and exposes the results via `IInvariantProvider`. The `lifeblood_invariant_check` tool wraps the port with three modes (id lookup, audit, list). Invariant: `INV-INVARIANT-001`.

## Invariant Enforcement

Architecture rules are not just documented. They are tested AND queryable:
- `ArchitectureInvariantTests` verifies dependency direction on every build via `CsprojPaths.GetReferencedModuleName` (shared helper so production discovery and the ratchet test never drift; fix class introduced in v0.6.3)
- 11 frozen ADRs in `docs/ARCHITECTURE_DECISIONS.md`
- GraphValidator runs on every graph before analysis
- Rule packs (hexagonal, clean-architecture, lifeblood) validate boundaries
- **58 typed invariants in CLAUDE.md**, queryable at runtime via `lifeblood_invariant_check`: get the full body, title, and source line for any invariant by id; audit for duplicates; list every declared id
- DocsTests ratchets: `portCount`, `toolCount`, `testCount` in `docs/STATUS.md` are compared to the live repository state on every CI run
- CHANGELOG link-reference ratchet: every `## [X.Y.Z]` heading must have a matching `[X.Y.Z]: ...` link reference (`INV-CHANGELOG-001`)

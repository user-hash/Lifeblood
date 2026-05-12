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
| **Lifeblood.Domain** | Graph model, Evidence, ConfidenceLevel, GraphBuilder, GraphValidator, rules, results, ResponseEnvelope + EnvelopeClassification | None |
| **Lifeblood.Application** | 26 port interfaces including `IWorkspaceAnalyzer`, `ICompilationHost`, `IRuntimeAssemblyResolver`, `ISymbolResolver`, `IResponseDecorator`, `ISemanticSearchProvider`, `IDeadCodeAnalyzer`, `IUnityReachabilityProvider`, `IPartialViewBuilder`, `IAuthorityReporter`, `IInvariantProvider`, `IUsageProbe` + `IUsageCapture`. AnalyzeWorkspaceUseCase, GenerateContextUseCase. | Domain |
| **Lifeblood.Adapters.CSharp** | Roslyn reference adapter. Workspace analyzer, module discovery, symbol/edge extraction (records `Properties["attributes"]`, `Properties["baseType"]`, `Properties["classification"]` on every relevant symbol), RoslynSemanticView (with sandbox helpers `Help` / `SymbolsOfKind(string)` / `EdgesOfKind(string)`), CanonicalSymbolFormat (single source of truth for parameter-type display), CsprojPaths (shared path normalization for csproj parsing), SnippetWrapper (compile_check auto-wrap), UnityReachabilityAdapter (entrypoint attributes + MonoBehaviour magic methods + transitive base walk), UnityAssemblyResolver (Library/ScriptAssemblies + Library/Bee/artifacts + Library/PackageCache DLL probe). | Application, Roslyn |
| **Lifeblood.Adapters.JsonGraph** | JSON import/export with round-trip fidelity. | Application |
| **Lifeblood.Connectors.ContextPack** | AgentContextGenerator, InstructionFileGenerator, ReadingOrderGenerator. | Application |
| **Lifeblood.Connectors.Mcp** | LifebloodMcpProvider (lookup, deps, dependants, blast radius, file impact), LifebloodSymbolResolver (identifier resolution + wrong-namespace short-name fallback + kind correction), LifebloodResponseDecorator (truth envelope; classification injected from registry at composition time), LifebloodAuthorityReporter, LifebloodSemanticSearchProvider (tokenized ranked-OR search over name + xmldoc), LifebloodDeadCodeAnalyzer (consults `IUnityReachabilityProvider` when injected), LifebloodPartialViewBuilder, LifebloodInvariantProvider (CLAUDE.md runtime parser + cache), ClaudeMdInvariantParser (pure text to records), InvariantParseCache (generic timestamp-invalidated cache), McpProtocolSpec (single source of truth for JSON-RPC wire constants). | Application |
| **Lifeblood.Analysis** | CouplingAnalyzer, BlastRadiusAnalyzer, CircularDependencyDetector, TierClassifier, RuleValidator. | Domain |
| **Lifeblood.Server.Mcp** | MCP server host. Stdio JSON-RPC. 26 tools (16 read + 10 write). Bidirectional Roslyn. McpDispatcher owns the wire protocol. ToolDefinition.EnvelopeClassification is the registry-side source of truth for the truth envelope. | Application, Adapters.CSharp, Connectors |
| **Lifeblood.ScriptHost** | Process-isolated code execution harness. Separate process, no shared memory. Zero ProjectReferences (INV-SCRIPTHOST-001). | Roslyn Scripting only |
| **Lifeblood.CLI** | Composition root: AnalysisPipeline, RulesLoader, thin dispatch. | Everything |

## Domain Model

The domain is pure. No Roslyn, no JSON, no System.IO.

- **Symbol.** A node: module, file, namespace, type, method, field, property (including events via `isEvent` and indexers via `isIndexer`), parameter.
- **Edge.** A directed relationship: contains, dependsOn, implements, inherits, calls, references, overrides. Carries an optional `CallSite` (`FilePath` / `Line` / `Column` / `EndLine` / `EndColumn` / `ContainingSymbolId`) pinpointing the authoring expression for expression-derived edges; null for graph-derived edges (module DependsOn, type Inherits without a surfaced clause node).
- **CallSite.** Domain value-object lifting authoring provenance into the graph so downstream tools can answer "where in source does X depend on Y?" without re-reading files.
- **Evidence.** Provenance: syntax, semantic, or inferred. Plus the adapter name and a `ConfidenceLevel`.
- **GraphBuilder.** Constructs graphs, synthesizes Contains edges from ParentId, deduplicates symbols, derives file-level edges from symbol-level edges (`INV-FILE-EDGE-001`), sorts output deterministically.
- **GraphValidator.** Rejects malformed graphs: dangling edges, duplicates, orphaned parents, self-references.
- **SemanticGraph.** The immutable graph with thread-safe lazy indexes.
- **AnalysisSnapshot** (adapter-level). Caches per-file extraction results for incremental re-analyze. Tracks csproj timestamps so `<Nullable>` / `<AllowUnsafeBlocks>` / `<Reference>` edits trigger per-module re-discovery (`INV-BCL-005`).

Properties are `IReadOnlyDictionary` on the public surface. The graph is read-only after construction.

## Port Interfaces (26 total)

### Left Side (Language Adapters)
- `IWorkspaceAnalyzer`. Primary entry point. Takes `projectRoot` plus config and returns a `SemanticGraph`.
- `IModuleDiscovery`. Module and project discovery, returns `ModuleInfo[]`. Each `ModuleInfo` carries `BclOwnership`, `AllowUnsafeCode`, `ImplicitUsings`, and `Dependencies` parsed from csproj (csproj-driven compilation facts, `INV-COMPFACT-001..003`).
- `ICompilationHost`. Diagnostics, compile-checking, reference finding (Roslyn-backed). `FindReferences` has an explicit `IncludeDeclarations` option. `GetDiagnostics(DiagnosticsRequest)` accepts a typed scope filter (`FilePath`, `ModuleName`) for file-scoped diagnostics. `CompileCheck` auto-wraps bare statement snippets via `Internal.SnippetWrapper` so library modules accept them.
- `ICodeExecutor`. Executes code snippets against the loaded workspace. Typed `Execute(CodeExecutionRequest)` overload supports `TargetProfile` (`host` / `net-standard-2.1` / `net-6.0`); the existing `Execute(string,string[],int)` overload is preserved.
- `IRuntimeAssemblyResolver`. Returns additional DLL probe paths plus optional diagnostics. Reference adapter `UnityAssemblyResolver` scans Unity's `Library/` directory tree. Closes the 'execute cannot reach UnityEngine types' class.
- `IWorkspaceRefactoring`. Rename (returns edits, does NOT apply), and format.
- `IGraphImporter`. Reads a stream into a `SemanticGraph` via the JSON protocol.
- `IGraphExporter`. Writes a `SemanticGraph` to a stream.

### Infrastructure
- `IFileSystem`. Filesystem abstraction for testability.
- `IUsageProbe`. Creates a fresh `IUsageCapture` per analyze run. Every analyze carries a structured `AnalysisUsage` snapshot with wall time, CPU time, peak memory, and GC counts (`INV-USAGE-001..002`, `INV-USAGE-PORT-001..002`, `INV-USAGE-PROBE-001..002`).
- `IUsageCapture`. One-shot usage capture scoped to a single analyze run.

### Output
- `IProgressSink`. Receives stderr-style progress events from long-running pipelines (per-module compile, validate, complete).

### Analysis
- `IRuleProvider`. Loads architecture rules (mustNotReference, mayOnlyReference) from JSON.
- `IBlastRadiusProvider`. Transitive BFS over the dependency graph.

### Right Side (AI Connectors)
- `IAgentContextGenerator`. Builds an `AgentContextPack` from a graph and analysis result.
- `IInstructionFileGenerator`. Builds a markdown instruction file from a graph and analysis result.
- `IMcpGraphProvider`. Exposes `LookupSymbol`, `GetDependencies`, `GetDependants` (legacy string-id surface), `GetDependencyEdges`, `GetDependantEdges` (typed `EdgeDetail[]` surface carrying `OtherEndId` + `Kind` + nullable `CallSite` so a single call answers "where in source does X depend on Y?"), `GetBlastRadius`, `GetFileImpact`.
- `ISymbolResolver`. Resolves a bare short name, a truncated method id, a canonical id, a kind-prefixed id with wrong namespace (`INV-RESOLVER-005`), or a `method:` prefix on a property/field/event name (kind correction, `INV-RESOLVER-006`) into a `SymbolResolutionResult` carrying `Outcome`, `Candidates`, `Diagnostic`, `DeclarationFilePaths`, and `Overloads`. Type-scoped `ResolveMember(graph, typeName, memberName, paramTypes?)` returns a `MemberResolutionResult` with typed `Outcome` (`Unique` / `MultipleMatches` / `NotFound` / `TypeNotFound` / `AmbiguousContainingType`), resolved containing-type id, matching members, and ambiguous-type candidates — closes the "I know the type, give me ONE specific member with overload disambiguation" gap that `lifeblood_resolve_short_name` flattened. Every read-side MCP handler routes through this resolver before any graph or workspace lookup (`INV-RESOLVER-001`). Partial-type unification is computed as a read model on the resolution result (`INV-RESOLVER-003..004`).
- `IUserInputCanonicalizer`. Step 0 of resolution: language-specific input rewriting (`System.String` to `string`, strip `global::`, etc.).
- `IResponseDecorator`. Builds the truth envelope on every read-side response: truth tier, confidence band, evidence source, wall-clock staleness, files-changed-since-analyze count, per-tool limitations. Classification table is sourced from the host's tool registry at construction (`INV-ENVELOPE-001`).
- `ISemanticSearchProvider`. Ranked search over symbol names, qualified names, and persisted xmldoc summaries. Tokenizes on whitespace with ranked-OR scoring.
- `IDeadCodeAnalyzer`. Finds symbols with no incoming semantic references. Optional `IUnityReachabilityProvider` injection downgrades MonoBehaviour magic methods + Unity entrypoint attributes from 'flagged' to 'live by runtime dispatch' (`INV-UNITY-001`). See `INV-DEADCODE-001`.
- `IUnityReachabilityProvider`. Returns true when a symbol is reachable through Unity runtime dispatch (entrypoint attribute or MonoBehaviour magic method on a Unity-message-receiver-derived type). Walks the inheritance chain via `Properties["baseType"]` so external bases (UnityEngine.dll) still resolve.
- `IAuthorityReporter`. Single graph walk produces an `AuthorityReport`: implementedInterfaceCount, ownedPublicSurface, per-interface usage (member count + consumer count), forwarderRatio (`INV-AUTHORITY-001`).
- `IPartialViewBuilder`. Combines every partial declaration of a type into one view with file headers.
- `Invariants.IInvariantProvider`. Walks well-known repo conventions dynamically — `<root>/CLAUDE.md`, `<root>/AGENTS.md`, and any `<root>/docs/invariants/**.md` tree — via `IFileSystem`, parses each through `ClaudeMdInvariantParser` (five authoring shapes A/B/C/D/E), aggregates results across all sources with per-id duplicate detection, and caches per-file in `InvariantParseCache<T>`. Three methods: `GetAll`, `GetById`, `Audit` (the audit reports every contributing source path on `SourcePaths[]`). The conventions live in the adapter (`LifebloodInvariantProvider`), NOT the port — a repo with a different layout supplies its own provider, reusing the parser + cache without touching Application (`INV-INVARIANT-001`).

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

After a full analysis, subsequent calls with `incremental: true` only recompile modules whose source files changed. The `AnalysisSnapshot` caches per-file extraction results (symbols, edges, timestamps). Changed files are detected via filesystem timestamps and surgically replaced. Csproj timestamp changes trigger per-module re-discovery so compilation facts (`BclOwnership`, `AllowUnsafeCode`, etc.) never go stale.

**Caller-owned scope policy (`INV-ANALYZE-FALLBACK-001`).** The adapter does not silently widen scope when it detects drift it cannot honor cheaply (no prior cache, module set changed, project descriptor edited). It returns a typed `IncrementalAnalyzeResult { Mode, Graph?, ChangedFileCount, Reason?, Detail? }` and the caller chooses what to do via `AnalysisConfig.AllowFullFallback`:
- `false` (default) → adapter returns `Mode = Rejected, Graph = null, Reason = ...`. Caller decides next step.
- `true` → adapter widens to full and returns `Mode = FullFallback, Graph = ..., Reason = ...` so the result lands and the cache miss stays visible.

`FallbackReason` taxonomy is adapter-agnostic (`NoPriorAnalysis` / `ModuleSetChanged` / `ModuleDescriptorChanged`); adapter-specific descriptor names (asmdef, csproj, pyproject, package.json) live in `Detail`. Internal best-effort callers (`GraphSession.MaybeRefreshIfStale`) deliberately set `AllowFullFallback = true` because their contract is "make state fresh"; user-facing callers preserve the user's flag unchanged.

The MCP wire shape carries `mode` (what the adapter DID) + `requestedMode` (what the caller ASKED) + `fallbackReason` + `fallbackDetail`. Rejection responses additionally carry `canRetryFull: true` and a `suggestedRetry` block — the next move is self-documenting.

v1 limitation: does not cascade to dependent modules when an API surface changes; full fallback (when allowed) handles that case.

## Unity Bridge

The Unity bridge lives at `unity/Editor/LifebloodBridge/`. It runs Lifeblood as a sidecar MCP server (separate .NET process), communicating via JSON-RPC 2.0 over stdin/stdout. Unity projects create a directory junction to this path. The bridge auto-discovers via `[McpForUnityTool]` attributes and exposes all 25 tools to Unity MCP. Wire-format constants live in `McpProtocolSpec` (`INV-MCP-003`); the Unity mirror at `unity/Editor/LifebloodBridge/McpProtocolConstants.cs` is byte-compared by a ratchet test so the two sides cannot drift.

```
Unity Editor ──→ Unity MCP (scenes, GameObjects, assets)
                     │
                     └── [McpForUnityTool] ──→ Lifeblood MCP (child process)
                         └── 25 semantic tools over JSON-RPC
```

## Deterministic Output

GraphBuilder sorts symbols by ID and edges by source+target+kind before producing the graph. File discovery is sorted. Same input always produces the same output (`INV-PIPE-001`).

## Architectural Seams

The invariant register lives under [`docs/invariants/`](../docs/invariants/INDEX.md), one file per domain. The slim project-root `CLAUDE.md` keeps the architecture overview, dependency rules, port catalogue, naming conventions, rules-for-adding-features, and what-not-to-do — every formally-numbered `INV-XXX-NNN` rule lives in the tree. Each seam below is a contract with a dedicated set of invariants and regression tests. A bug report that looks like "different surfaces broken in the same way" should first check whether one of these seams is the right place to fix it, before adding a per-surface patch.

**Seam 1. `ISymbolResolver` (Application port).** Identifier resolution is a port. Every read-side handler routes through one resolver before any graph or workspace lookup. Resolution order: exact canonical match, truncated method form, kind correction (`method:` prefix on a property/field/event name, `INV-RESOLVER-006`), bare short name, extracted short name from qualified input (`INV-RESOLVER-005`). Partial-type unification is a read model. The deterministic primary file path picker lives in `LifebloodSymbolResolver.ChoosePrimaryFilePath`. Invariants: `INV-RESOLVER-001..006`.

**Seam 2. Csproj-driven compilation facts as a documented convention.** Each module-level compilation option that the host needs to honor lives as a typed field on `ModuleInfo`, parsed once during `RoslynModuleDiscovery.ParseProject`, consumed in `ModuleCompilationBuilder.CreateCompilation`. Typed fields today: `BclOwnership`, `AllowUnsafeCode`, `ImplicitUsings`. Csproj-edit invalidation flows through `AnalysisSnapshot.CsprojTimestamps`. Asmdef-edit invalidation flows through `AnalysisSnapshot.AsmdefTimestamps` (`INV-UNITY-002`). Re-discovery rebuilds the entire `ModuleInfo`. Invariants: `INV-COMPFACT-001..003`, `INV-BCL-001..005`.

**Seam 3. `RoslynSemanticView` (typed adapter view).** Each language adapter publishes a typed read-only view of its loaded semantic state. The C# adapter's `RoslynSemanticView` exposes `Compilations`, `Graph`, `ModuleDependencies`, `Help` (sandbox cheat sheet), `SymbolsOfKind(string)`, `EdgesOfKind(string)`. Constructed once per `GraphSession.Load`, shared by reference. `RoslynCodeExecutor` consumes the view as the script-host globals object. Invariants: `INV-VIEW-001..003`, `INV-EXECUTE-001`.

**Seam 4. Canonical symbol-id determinism.** Canonical symbol ids must be byte-identical for the same underlying method regardless of which compilation extracts it. The fix is at `ModuleCompilationBuilder.ProcessInOrder` which routes direct dependencies through `ComputeTransitiveDependencies` so every module compilation sees the full transitive closure. Drift manifests as silent zero results in every cross-module lookup tool. Invariants: `INV-CANONICAL-001`.

**Seam 5. MCP wire protocol source of truth.** Protocol version, JSON-RPC method names, and notification method names live exclusively in `Lifeblood.Connectors.Mcp.McpProtocolSpec`. Clients that cannot take a project reference (Unity bridge) ship a standalone mirror pinned by byte-equal ratchet tests. Internal registry records and wire-format DTOs are separate types. Invariants: `INV-MCP-001..003`, `INV-TOOLREG-001`.

**Seam 6. Invariant introspection.** Architectural invariants live across well-known repo conventions discovered dynamically. `LifebloodInvariantProvider` walks `<root>/CLAUDE.md`, `<root>/AGENTS.md`, and any `<root>/docs/invariants/**.md` tree via `IFileSystem`, parses each through `ClaudeMdInvariantParser` (five authoring shapes — A `- **INV-X-N**: body`, B `- **INV-X-N. Title.** Body`, C `**INV-X-N: Title.** body` bare bold paragraph, D `- **INV-X-N** (vX.Y.Z): body` parenthesized version tag, E `- **INV-X-N:** body` colon-inside-bold), aggregates results, caches per-file with timestamp invalidation via `InvariantParseCache<T>` (generic, reusable), and exposes the results via `IInvariantProvider`. The `lifeblood_invariant_check` tool wraps the port with three modes (id lookup, audit, list); the audit reports every contributing source path on `SourcePaths[]`. Invariants: `INV-INVARIANT-001`, parser ratchets cover every shape; closes `LB-BUG-017` + `LB-BUG-018` + `LB-FR-023`.

**Seam 7. Truth envelope (registry-driven).** Every read-side MCP tool response carries a top-level `envelope` field. Per-tool classification (truth tier, confidence band, evidence source, limitations) is declared on `ToolDefinition.EnvelopeClassification` in the registry; the composition root projects that table into `LifebloodResponseDecorator` at startup. Adding a new read-side tool without a classification triggers the registry ratchet (`Registry_EveryReadSideTool_DeclaresEnvelopeClassification`). Staleness is computed from `GraphSession.AnalyzedAtUtc` plus an `IFileSystem` mtime scan over the graph's File symbols, capped at 256 files per call. Invariant: `INV-ENVELOPE-001`.

**Seam 8. Runtime-dispatch reachability.** `IUnityReachabilityProvider` is the right-side port for framework-dispatch detection. The C# reference adapter (`UnityReachabilityAdapter`) handles Unity entrypoint attributes and MonoBehaviour magic methods, walking the inheritance chain via `Properties["baseType"]` so external Unity bases resolve. `LifebloodDeadCodeAnalyzer` consults the provider when injected. Future framework adapters (ASP.NET attribute routing, MAUI handlers, MEF) plug into the same port. Invariant: `INV-UNITY-001`.

**Seam 9. Authority + forwarder analysis.** `IAuthorityReporter` produces a single-walk report (`implementedInterfaceCount`, `ownedPublicSurface`, per-interface usage, `forwarderRatio`). The forwarder ratio is read off `Symbol.Properties["classification"]`, set at extraction time by `RoslynSymbolExtractor.AttachMethodClassification` (`PureForwarder` / `ThinWrapper` / `RealLogic`). `lifeblood_authority_report` and `lifeblood_port_health` consume the report directly. Invariants: `INV-AUTHORITY-001`, `INV-FORWARDER-001`.

## Invariant Enforcement

Architecture rules are not just documented. They are tested AND queryable:
- `ArchitectureInvariantTests` verifies dependency direction on every build via `CsprojPaths.GetReferencedModuleName` (shared helper so production discovery and the ratchet test never drift)
- 11 frozen ADRs in `docs/ARCHITECTURE_DECISIONS.md`
- GraphValidator runs on every graph before analysis
- Rule packs (hexagonal, clean-architecture, lifeblood) validate boundaries
- **76 typed invariants under `docs/invariants/`** (8 domain files + INDEX), queryable at runtime via `lifeblood_invariant_check`: get the full body, title, and source line for any invariant by id; audit for duplicates; list every declared id. The walker also picks up `<root>/CLAUDE.md` and `<root>/AGENTS.md` if they declare additional invariants.
- DocsTests ratchets: `portCount`, `toolCount`, `testCount` in `docs/STATUS.md` are compared to the live repository state on every CI run
- CHANGELOG link-reference ratchet: every `## [X.Y.Z]` heading must have a matching `[X.Y.Z]: ...` link reference (`INV-CHANGELOG-001`)

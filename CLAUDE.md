# Lifeblood. AI Instruction File

## What This Project Is

Lifeblood is a hexagonal framework that pipes compiler-level semantics into AI agents. Language adapters on the left, AI connectors on the right, pure universal graph in the middle.

We do not build Roslyn-grade adapters for every language. We build the framework and the C# reference implementation. The community builds adapters for other languages.

## Architecture

```
Lifeblood.Domain # Pure. ZERO deps. Graph model, rules, results, capabilities.
Lifeblood.Application # Ports + use cases. Depends only on Domain.
  ├── Ports/Left/ # IWorkspaceAnalyzer, IModuleDiscovery
  ├── Ports/Right/ # IAgentContextGenerator, IMcpGraphProvider, IInstructionFileGenerator
  ├── Ports/GraphIO/ # IGraphImporter, IGraphExporter
  ├── Ports/Analysis/ # IRuleProvider
  ├── Ports/Output/ # IProgressSink
  ├── Ports/Infrastructure/ # IFileSystem
  └── UseCases/ # AnalyzeWorkspace, GenerateContext

Lifeblood.Adapters.CSharp # LEFT SIDE. Roslyn. Reference implementation.
Lifeblood.Adapters.JsonGraph # LEFT SIDE. Universal JSON protocol.
Lifeblood.Connectors.ContextPack # RIGHT SIDE. Context pack + CLAUDE.md generator.
Lifeblood.Connectors.Mcp # RIGHT SIDE. MCP graph provider for AI agents.
Lifeblood.Analysis # Optional analyzers (coupling, blast radius, cycles, tiers).
Lifeblood.Server.Mcp # MCP server host. Stdio JSON-RPC. Interactive AI sessions.
Lifeblood.CLI # Composition root. Wires adapters to connectors.
```

## Invariants

### Domain Purity
- **INV-DOMAIN-001**: `Lifeblood.Domain` has ZERO dependencies. Not Roslyn, not JSON, not System.IO, not anything. If a PackageReference appears, the architecture is broken.
- **INV-DOMAIN-002**: Domain never references Application, Adapters, Connectors, or CLI.

### Application Layer
- **INV-APP-001**: Application depends only on Domain.
- **INV-APP-002**: Application never references concrete adapters or connectors. Only port interfaces.

### Graph Model
- **INV-GRAPH-001**: SymbolKind enum is language-agnostic. No C#-isms, no Python-isms.
- **INV-GRAPH-002**: Language-specific metadata goes in `Symbol.Properties` dictionary.
- **INV-GRAPH-003**: Every edge carries Evidence (kind, adapter, confidence, source span).
- **INV-GRAPH-004**: Analyzers do NOT modify the graph. Results are separate objects. The graph is read-only after construction.

### Left Side (Language Adapters)
- **INV-ADAPT-001**: Every adapter declares capabilities honestly via AdapterCapability.
- **INV-ADAPT-002**: C# adapter is the reference. Most complete, best tested.
- **INV-ADAPT-003**: External adapters communicate via JSON graph schema only.
- **INV-ADAPT-004**: No adapter code leaks into Domain or Application.

### Right Side (AI Connectors)
- **INV-CONN-001**: Connectors depend on Application ports, not on adapters.
- **INV-CONN-002**: MCP connector serves the graph read-only.
- **INV-CONN-003**: Context pack generator produces AI-consumable JSON, not human prose.

### Analysis
- **INV-ANALYSIS-001**: All analyzers are stateless. Input: graph + config. Output: typed result.
- **INV-ANALYSIS-002**: No analyzer modifies the graph. Read-only.
- **INV-ANALYSIS-003**: CouplingAnalyzer counts distinct dependants, not edge count.

### Testing
- **INV-TEST-001**: Every adapter passes the same golden repo contract tests.
- **INV-TEST-002**: Every analyzer is tested against golden repos.

### Pipeline
- **INV-PIPE-001**: The pipeline is deterministic. Same input = same output.

## Dependency Rules

```
Lifeblood.CLI
  → Lifeblood.Application
  → Lifeblood.Adapters.CSharp
  → Lifeblood.Adapters.JsonGraph
  → Lifeblood.Connectors.*
  → Lifeblood.Analysis

Lifeblood.Adapters.CSharp
  → Lifeblood.Application (ports only)
  → Lifeblood.Domain
  → Microsoft.CodeAnalysis.CSharp (Roslyn)

Lifeblood.Connectors.Mcp
  → Lifeblood.Application (ports only)
  → Lifeblood.Domain

Lifeblood.Analysis
  → Lifeblood.Domain

Lifeblood.Application
  → Lifeblood.Domain

Lifeblood.Server.Mcp
  → Lifeblood.Application
  → Lifeblood.Adapters.CSharp
  → Lifeblood.Adapters.JsonGraph
  → Lifeblood.Connectors.*
  → Lifeblood.Analysis

Lifeblood.ScriptHost
  → (nothing. Isolated process. Microsoft.CodeAnalysis.CSharp.Scripting only.)

Lifeblood.Domain
  → (nothing. Pure leaf. Forever.)
```

## Port Interfaces

All ports live under `src/Lifeblood.Application/Ports/`. The directory layout is the contract: `Ports/Left/` (language adapters: `IWorkspaceAnalyzer`, `IModuleDiscovery`, `ICompilationHost`, `ICodeExecutor`, `IWorkspaceRefactoring`), `Ports/Right/` (AI connectors: `IAgentContextGenerator`, `IMcpGraphProvider`, `IInstructionFileGenerator`, `ISymbolResolver`, `ISemanticSearchProvider`, `IDeadCodeAnalyzer`, `IPartialViewBuilder`, `Invariants/IInvariantProvider`), `Ports/GraphIO/` (`IGraphImporter`, `IGraphExporter`), `Ports/Analysis/` (`IRuleProvider`, `IBlastRadiusProvider`), `Ports/Infrastructure/` (`IFileSystem`, `IUsageProbe`, `IUsageCapture`). Total count pinned by `docs/STATUS.md` `<!-- portCount -->` ratchet.

## Identifier Resolution (`ISymbolResolver`)

Every read-side MCP tool that takes a `symbolId` parameter must route through
`Lifeblood.Application.Ports.Right.ISymbolResolver` before doing graph or
workspace lookups. The resolver is the single source of truth for "what does
this user-supplied identifier mean". It canonicalizes truncated method IDs,
resolves bare short names, and computes the merged read model for partial types.

- **INV-RESOLVER-001. Identifier resolution is a port.** Every read-side tool
  that takes a `symbolId` parameter (`lifeblood_lookup`, `lifeblood_dependants`,
  `lifeblood_dependencies`, `lifeblood_blast_radius`, `lifeblood_file_impact`,
  `lifeblood_find_references`, `lifeblood_find_definition`,
  `lifeblood_find_implementations`, `lifeblood_documentation`, `lifeblood_rename`)
  routes through `ISymbolResolver` before any graph or workspace lookup. NEVER
  add a read-side tool that calls `graph.GetSymbol` or `graph.GetIncomingEdgeIndexes`
  directly with the user's raw input.

- **INV-RESOLVER-002. The resolver accepts every input format.** Resolution
  order: exact canonical match → truncated method form (`method:NS.Type.Name`
  with no parens, lenient single-overload match) → bare short name (no kind
  prefix and no namespace, looks up the short-name index) → **extracted
  short name from a kind-prefixed or namespaced input** (see INV-RESOLVER-005).
  Returns `SymbolResolutionResult` with `Outcome`, `CanonicalId`, `Symbol`,
  `PrimaryFilePath`, `DeclarationFilePaths`, `Candidates`, and `Diagnostic`.

- **INV-RESOLVER-003. Partial-type unification is a read model.** The graph
  stores raw symbols (one per partial declaration; last-write-wins remains the
  storage policy in `GraphBuilder.AddSymbol`). The resolver walks the type's
  incoming `EdgeKind.Contains` edges from `SymbolKind.File` symbols to discover
  every partial declaration file at read time. Schema unchanged 
  `Lifeblood.Domain.Graph.Symbol.FilePath` stays a single value. The merged
  view lives entirely on `SymbolResolutionResult.PrimaryFilePath` +
  `SymbolResolutionResult.DeclarationFilePaths`.

- **INV-RESOLVER-004. Primary file path for partial types is deterministic.**
  Picker rules in `LifebloodSymbolResolver.ChoosePrimaryFilePath`:
  (1) filename matches the type name exactly, (2) filename starts with
  `"<TypeName>."` (shortest match wins among prefix matches), (3) lexicographic
  first as final fallback. Same input + same graph → same primary, always.

- **INV-RESOLVER-005. Wrong-namespace inputs resolve via the trailing short-name segment.** When the user supplies a kind-prefixed or namespaced id whose namespace is wrong (`type:Old.NS.VoicePatchAdapter` for a symbol that has moved to `New.NS`), the resolver falls through to a Rule 4 "extracted short-name" lookup. `LifebloodSymbolResolver.ExtractLikelyShortName` strips the kind prefix, drops any method parameter list, and returns the final dot-separated segment. That segment is looked up in the short-name index. Single hit → `ResolveOutcome.ShortNameFromQualifiedInput` with a Diagnostic that explains the namespace correction. Multiple hits → `ResolveOutcome.AmbiguousShortNameFromQualifiedInput` surfacing every candidate; the resolver never silently picks. The suggestion ranker `SuggestNearMatchesInternal` also routes through `ExtractLikelyShortName` before scoring, with short-name-index hits scoring at `ShortNameHitScore = 1000` so a real short-name match always sorts above fuzzy accident. Pinned by `ResolverShortNameFallbackTests` (24 tests including both original dogfood cases, ambiguous case, not-found fallthrough, and legacy rules 1-3 staying live on bare/exact inputs).

## Csproj-Driven Compilation Facts

Csproj is the source of truth for module-level compilation options. Each fact
flows through one path: discover at parse time → store on `ModuleInfo` →
consume at compilation time. The pattern is the contract. Every future
csproj-driven option follows the same shape.

- **INV-COMPFACT-001. Csproj is authoritative for module-level compilation
  options.** Examples already shipped: `BclOwnership` (v2), `AllowUnsafeCode`
  (v4). Future additions follow the same pattern: `LangVersion`, `Nullable`,
  `DefineConstants`, `Platform`, `WarningLevel`, etc.

- **INV-COMPFACT-002. Each compilation fact lives as a typed field on
  `ModuleInfo`.** Default value preserves pre-fix behavior. Set during
  `RoslynModuleDiscovery.ParseProject`. Consumed exactly once during
  `Internal.ModuleCompilationBuilder.CreateCompilation`. NEVER re-derive from
  the csproj at the compilation layer; NEVER sniff filenames as a substitute
  for declared options.

- **INV-COMPFACT-003. Csproj edits invalidate cached module facts.** The
  `AnalysisSnapshot.CsprojTimestamps` tracking added in v2 (INV-BCL-005)
  covers every compilation fact for free. `IncrementalAnalyze` rebuilds the
  entire `ModuleInfo` on csproj edit, not just one field. The next compilation
  fact added under this convention ships with zero new incremental work.

## Canonical Symbol ID Determinism (C# Adapter)

Lifeblood symbol IDs must be byte-identical for the same underlying method
regardless of which compilation extracts it. Drift between two instances of
the same method (one source, one metadata; or one declared in module A and
one implementing the same interface in module B) silently corrupts every
downstream feature: `find_references`, `dependants`, blast radius, the
fuzzy short-name resolver, and every cross-module tool that relies on
string equality of canonical IDs.

- **INV-CANONICAL-001. Roslyn compilations receive the full transitive dependency closure, not just direct `ModuleInfo.Dependencies`.** Unlike MSBuild's ProjectReference flow, `CSharpCompilation.Create` does NOT walk references transitively. If C references B and B references A, compiling C must pass BOTH B and A as explicit `MetadataReference`s. Missing transitive A → every type from A appearing through B's public surface becomes a Roslyn error type with empty `ContainingNamespace` → `CanonicalSymbolFormat.BuildParamSignature` emits the short type name without qualifier → canonical ID drifts (`method:C.Impl.M(BarType)` instead of `method:C.Impl.M(A.BarType)`). Drift is silent: compilation succeeds, extraction runs, graph is populated; only cross-module lookups fail. The enforcement site is `Internal.ModuleCompilationBuilder.ProcessInOrder`, which routes each module's direct deps through `ComputeTransitiveDependencies` before collecting PE references. `ModuleInfo.Dependencies` keeps direct-only semantics (feeds module-to-module graph edges + topological sort, both correct on direct deps), but every compilation-reference consumer MUST use the closure helper. Pinned by `CanonicalSymbolFormatTests.ComputeTransitiveDependencies_*` (flat chain, diamond, cycle) plus the end-to-end `AnalyzeWorkspace_ThreeModuleTransitiveChain_ProducesCanonicalMethodIds`.

## Adapter Semantic View

Each language adapter publishes a typed read-only accessor for its loaded
semantic state. Tools that need read access (script host today; debuggers,
visualizers, custom linters tomorrow) consume the view by reference, never
threading raw fields through individual constructors.

- **INV-VIEW-001. Each language adapter publishes a typed semantic view.**
  The C# adapter publishes `Lifeblood.Adapters.CSharp.RoslynSemanticView`
  (`Compilations`, `Graph`, `ModuleDependencies`). Future language adapters
  publish their own view type using their language's semantic model.

- **INV-VIEW-002. Tools consume the view, not raw fields.** The script host
  (`RoslynCodeExecutor`) takes a `RoslynSemanticView` in its constructor.
  Future consumers (debuggers, visualizers, linters, REPLs) take the same
  view. NEVER thread raw `Compilations` / `Graph` / `ModuleDependencies`
  through individual tool constructors.

- **INV-VIEW-003. The view is constructed once per workspace load and
  shared by reference.** `GraphSession.Load` (full and incremental paths)
  builds the view once with three field assignments and passes it by
  reference to consumers. The view never holds locks, never caches anything
  beyond its three references, never mutates state. It is purely a typed
  handle. Sharing avoids accidental divergence between consumers' views of
  the same workspace.

## Usage Reporting (Analyze Pipeline)

Every analyze run can emit a structured `AnalysisUsage` snapshot containing
wall time, CPU time (total, user, kernel), peak working set, peak private
bytes, GC collection counts per generation, host logical core count, and
per-phase durations. The snapshot is populated by an optional
`Lifeblood.Application.Ports.Infrastructure.IUsageProbe` passed to
`AnalyzeWorkspaceUseCase`. Both the CLI and the MCP server ship the probe
on by default; every `lifeblood_analyze` response carries the snapshot.

- **INV-USAGE-001. Usage is inert data.** `AnalysisUsage` and `PhaseTiming` hold only primitive fields and arrays of primitive fields. No `System.Diagnostics` types leak onto the records. Consumers read freely on any thread.
- **INV-USAGE-002. Units are documented on every field.** Bytes for memory, milliseconds for time. No implicit conversions. Only derived property: `CpuUtilizationPercent = CpuTimeTotalMs / WallTimeMs * 100`.
- **INV-USAGE-PORT-001. The probe returns a fresh capture on every `Start`.** Two captures started back-to-back do not share peak samples, CPU deltas, phase lists, or GC counters. A capture's lifetime is scoped to the single analyze run.
- **INV-USAGE-PORT-002. `IUsageCapture.Stop` is idempotent.** A second call returns the same `AnalysisUsage` instance; `Dispose` on an already-stopped capture is a no-op. `AnalyzeWorkspaceUseCase` disposes the capture on the error path so the sampling timer cannot outlive a failed run.
- **INV-USAGE-PROBE-001. `ProcessUsageProbe` takes an initial RSS sample in its constructor.** Sub-sample-interval runs still report a non-zero peak working set. Pinned by `ProcessUsageProbeTests`.
- **INV-USAGE-PROBE-002. The sampling timer is disposed at `Stop` or `Dispose`.** Leaked timers would corrupt the next capture's peak samples. Pinned by `Probe_TwoCaptures_AreIndependent`.

The probe lives in `Lifeblood.Adapters.CSharp` (touches `System.Diagnostics.Process`); the port lives in `Application/Ports/Infrastructure/`. Only composition roots (`CLI.Program`, `Server.Mcp.GraphSession`) construct the concrete probe.

## BCL Ownership (C# Adapter)

Some csprojs ship their own base class library via `<Reference Include="netstandard|mscorlib|System.Runtime">` (Unity ships .NET Standard 2.1; .NET Framework / Mono ship mscorlib). Plain SDK-style csprojs (`<Project Sdk="Microsoft.NET.Sdk">`) don't. They rely on the host runtime BCL.

Lifeblood treats this as a discovered module fact: `Lifeblood.Application.Ports.Left.ModuleInfo.BclOwnership` is `BclOwnershipMode.HostProvided` or `BclOwnershipMode.ModuleProvided`. The decision is computed ONCE during `RoslynModuleDiscovery.ParseProject` and consumed by `ModuleCompilationBuilder.CreateCompilation`. Detection logic lives in exactly one place. `RoslynModuleDiscovery.ReferenceDeclaresBcl` plus its `ParseAssemblyIdentitySimpleName` and `IsBclSimpleName` helpers. The compilation builder reads the field and never re-derives.

- **INV-BCL-001. Single BCL per compilation.** Two BCLs causes CS0433 + CS0518 on every System type; semantic model becomes unusable (`GetSymbolInfo` returns null at every call site → `find_references`, `dependants`, call-graph extraction silently produce zero results).
- **INV-BCL-002. Module owns its BCL when its csproj declares one.** A `<Reference>` whose parsed assembly identity or HintPath basename matches `netstandard`, `mscorlib`, or `System.Runtime` is the authoritative declaration. Such modules MUST NOT also receive the host BCL bundle.
- **INV-BCL-003. Host BCL is the fallback for plain SDK-style projects.** Modules with no BCL-naming `<Reference>` receive `BclReferenceLoader.References.Value`.
- **INV-BCL-004. BCL ownership is decided at discovery time, single source of truth.** Detection logic in `RoslynModuleDiscovery`. `ModuleCompilationBuilder` reads the typed field — no filename sniffing, no re-derivation at the compilation layer.
- **INV-BCL-005. Incremental re-analyze respects csproj edits.** `AnalysisSnapshot.CsprojTimestamps` tracks csproj timestamps. A csproj-timestamp change forces re-discovery and recompile even if no .cs file changed. Without this, a csproj edit that flips BCL ownership silently re-introduces the double-BCL bug under incremental mode.

Full plan + empirical evidence + rollout history at `.claude/plans/bcl-ownership-fix.md`.

### MCP Protocol Invariants (v0.6.1)

The JSON-RPC 2.0 / MCP protocol contract is owned by `McpDispatcher` in `Lifeblood.Server.Mcp`. `Program.cs` is a thin stdio I/O loop that delegates every request to the dispatcher. It contains no routing logic, no method-string matching, no response construction. All of that lives in `McpDispatcher`, which is a public sealed class testable via its normal public API.

- **INV-MCP-001. `initialize` response carries `protocolVersion` and `capabilities`.** Both are required by the MCP spec. `protocolVersion` is sourced from `McpProtocolSpec.SupportedVersion` via `McpDispatcher.SupportedProtocolVersion` (see INV-MCP-003). `capabilities` is a typed `McpCapabilities` POCO, never an inline dictionary, so new MCP capability negotiations are structural additions. Class-level defaults on `McpInitializeResult` plus explicit construction-site assignment make this belt-and-braces. Pinned by `McpProtocolTests.Initialize_ReturnsSpecCompliantResult`, `Initialize_SerializedJson_HasProtocolVersionAndCapabilities`, and `McpProtocolSourceOfTruthTests.McpDispatcher_InitializeResponse_AdvertisesSpecVersion`.

- **INV-MCP-002. Notifications never receive a response body.** Messages with `request.Id == null` are notifications per JSON-RPC 2.0. Recognized notification method names live in `McpProtocolSpec.AllKnownNotifications` (see INV-MCP-003). `McpDispatcher.KnownNotifications` is constructed directly from that set, so the two can never drift. Today's set: `notifications/initialized` (spec canonical), `initialized` (legacy alias during deprecation), `notifications/cancelled`, `$/cancelRequest`. Any entry short-circuits `Dispatch` to return `null` before response construction. Unknown notifications also return `null` and log to stderr for operator visibility. Pinned by `McpProtocolTests.NotificationsInitialized_SpecCompliantForm_ProducesNoResponse`, `NotificationsInitialized_LegacyAlias_ProducesNoResponse`, `UnknownNotification_ProducesNoResponse`, and the spec-level `McpProtocolSourceOfTruthTests.McpDispatcher_KnownNotification_ProducesNoResponse` Theory.

- **INV-MCP-003. Every MCP wire-format constant has exactly one canonical source per side.** Protocol version, JSON-RPC method names, and notification method names live exclusively in `Lifeblood.Connectors.Mcp.McpProtocolSpec`. Clients that cannot take a project reference (Unity bridge today) ship a standalone mirror file compared byte-equal to the spec by ratchet test. Unity mirror: `unity/Editor/LifebloodBridge/McpProtocolConstants.cs`, pinned by `McpProtocolSourceOfTruthTests`. Server consumes the spec directly via project reference: `McpDispatcher.SupportedProtocolVersion` chains to `McpProtocolSpec.SupportedVersion`, `McpDispatcher.KnownNotifications` builds from `McpProtocolSpec.AllKnownNotifications`, the dispatcher's method switch uses `McpProtocolSpec.Methods.*` const cases. Adding a new wire constant: edit `McpProtocolSpec` once; the mirror and consumer ratchets catch every forgotten downstream.

- **INV-TOOLREG-001. Tool availability dispatch is by typed enum, never by name prefix. Internal registry records and wire-format DTOs are separate types.** Every `ToolDefinition` (internal registry record in `Lifeblood.Server.Mcp`) declares `required ToolAvailability Availability { get; init; }`; omitting it is a compile error. `ToolRegistry.GetDefinitions()` returns `ToolDefinition[]` for internal consumers; `ToolRegistry.GetTools(hasCompilationState)` projects those into `McpToolInfo[]`, the wire-format DTO used in `tools/list`, applying `[Unavailable...]` description decoration when no compilation state is loaded. The wire DTO has no `Availability` field — no `[JsonIgnore]` workaround needed; System.Text.Json serializes cleanly. The split exists because conflating wire DTOs with internal records caused both a .NET 8 `[JsonIgnore]`-on-`required init` serialization bug AND a silent tool-classification miss (prefix guard didn't match `lifeblood_resolve_short_name`). Pinned by `McpProtocolTests.ToolRegistry_EveryToolHasExplicitAvailability`, `WriteSideTools_MarkedUnavailable_WhenNoCompilationState`, `ReadSideTools_NeverMarkedUnavailable`, and `ToolRegistry_ToolsList_SerializesWithoutError`.

### Governance Invariants (v0.6.1)

- **INV-SCRIPTHOST-001. `Lifeblood.ScriptHost` has zero `ProjectReference`.** The script host is a process-isolated child that runs untrusted code; its "no access to parent state" guarantee depends on not taking a ProjectReference to any Lifeblood module. NuGet PackageReferences are allowed (Microsoft.CodeAnalysis.CSharp.Scripting is load-bearing). Ratchet-tested by `ArchitectureInvariantTests.ScriptHost_HasZeroProjectReferences`.

- **INV-COMPROOT-001. Composition roots use only the allowlist.** `Lifeblood.CLI` and `Lifeblood.Server.Mcp` reference only `{Domain, Application, Analysis, Adapters.CSharp, Adapters.JsonGraph, Connectors.ContextPack, Connectors.Mcp}`. The allowlist is declared once as `private static readonly HashSet<string> CompositionRootAllowedModules` on `ArchitectureInvariantTests`. Single source of truth. Expanding it is a conscious architectural decision requiring a commit that edits the test. Ratchet-tested by `CompositionRoot_CLI_UsesOnlyAllowedModules` and `CompositionRoot_ServerMcp_UsesOnlyAllowedModules`.

- **INV-DOCS-001. Doc numbers match the repository.** `docs/STATUS.md` declares port and tool counts in HTML comments (`<!-- portCount: N -->`, `<!-- toolCount: N -->`). The HTML comment is the single source of truth; `DocsTests` parses it and asserts the number matches the live count of `public interface I*` declarations under `src/Lifeblood.Application/Ports` (ports) and `Name = "lifeblood_*"` literals in `ToolRegistry.cs` (tools). Editing the count in one place and not the other fails the ratchet.

- **INV-CHANGELOG-001. Every version heading has a link reference.** `CHANGELOG.md` must contain a `[X.Y.Z]: https://github.com/.../compare/...` link reference for every `## [X.Y.Z]` heading. Ratchet-tested by `DocsTests.Changelog_EveryHeadingHasLinkReference`. Closes the drift class where v0.6.0 shipped with stale bottom-of-file link refs.

### Test Discipline (v0.6.1)

- **INV-TESTDISC-001. Tests never silently early-return on precondition failure.** The `TryAnalyze(out ...) ⇒ bool` + `if (!TryAnalyze(...)) return;` pattern is forbidden — it hides both legitimate skips and real failures as silent passes. Missing preconditions (golden repo not restored) turn into `Skip.IfNot(condition, reason)` via `Xunit.SkippableFact`; broken-but-present conditions (graph has zero symbols) turn into loud `Assert.True` / `Assert.Fail`. Canonical example: `WriteSideIntegrationTests.cs`. Grep for `if (!Try*` under `tests/` must return zero hits.

### Dead-Code Analysis (v0.6.3, major fix v0.6.4)

- **INV-DEADCODE-001. `lifeblood_dead_code` walks the graph for symbols with zero incoming non-Contains edges. Every response carries `status` + `warning` fields.** The analyzer checks `graph.GetIncomingEdgeIndexes(sym.Id)` and also checks outgoing `Implements` edges as proof of liveness (a method implementing an interface is reachable through the interface). Self-analysis tail after the post-v0.6.4 extractor pass: ~7 findings (from 150 pre-v0.6.4), all in the known-structural-limitation set below.

  **Closed false-positive classes** (v0.6.4 + post-v0.6.4 extractor pass). Each maps to one or more extractor changes in `RoslynEdgeExtractor`:
  - **Interface dispatch.** `ExtractInheritanceEdges` emits method-level `Implements` edges via `FindImplementationForInterfaceMember` + `AllInterfaces`. Dead-code analyzer checks outgoing `Implements` as proof of liveness.
  - **Member access granularity.** `ExtractMemberAccessEdge` + `ExtractReferenceEdge` emit both type-level and symbol-level `References` edges for properties/fields/method-groups via `EmitSymbolLevelEdge` shared helper. Covers bare field identifiers and method-group references (`new Lazy<>(Load)`, `event += Handler`).
  - **Null-conditional property access.** `MemberBindingExpressionSyntax` handler emits type-level + symbol-level edges for `obj?.Property`.
  - **Lambda / local-function context.** `FindContainingMethodOrLocal` skips lambda and local-function syntax nodes. Calls inside `.Select(x => Foo(x))` attribute to the enclosing named method.
  - **Implicit global usings.** `ModuleInfo.ImplicitUsings` discovered from csproj; `ModuleCompilationBuilder.CreateCompilation` injects a synthetic global-using tree when enabled. Closes the 42% `GetSymbolInfo` null-resolution class (without this, `List<>`, `Dictionary<>` etc. emit CS0246 on every module). Follows INV-COMPFACT pattern.
  - **Constructor `Calls` edge.** `ExtractConstructorCallEdge` emits BOTH a type-level `References` edge (module-coupling signal) AND a method-level `Calls` edge to the `.ctor`. `find_references` on any constructor returns its construction sites.
  - **Field-initializer containing method.** `FindContainingMethodOrLocal` resolves a reference inside `static T _x = Bar()` / `T _x = Bar()` to the type's synthesized `.cctor` / first `.ctor` via `StaticConstructors` / `InstanceConstructors`.
  - **Property accessor context.** `FindContainingMethodOrLocal` returns the accessor `IMethodSymbol`; `GetMethodId` maps `AssociatedSymbol` to the property/event id so the emitted edge source matches the extracted graph node. Covers bodied accessors, expression-bodied properties, and indexer expression bodies.

  **Remaining known false-positive classes (structural, not fixable by static analysis):**
  - **Runtime entry points.** `Program.Main` and `Program` types in composition roots. Self-analysis: 6 findings.
  - **Unity reflection-based dispatch.** Methods called by the Unity engine at runtime via `[RuntimeInitializeOnLoadMethod]`, lifecycle messages (`OnAudioFilterRead`, `OnApplicationFocus`), and `SendMessage`-dispatched handlers. Roslyn cannot see these call sites. Full audit on a real 75-module Unity workspace: 96% true-positive rate (25/26 verified).

  Pinned by `Handle_DeadCode_Response_IncludesExperimentalWarning` plus 26 extractor/analyzer tests in `RoslynExtractorTests` covering each closed FP class.

### Invariant Introspection (v0.6.3)

- **INV-INVARIANT-001. CLAUDE.md is the single source of truth for architectural invariants; the tool parses it at runtime.** `lifeblood_invariant_check` reads `CLAUDE.md` from the loaded project root via `IFileSystem`, parses it with `Internal.ClaudeMdInvariantParser`, caches the result per-root in `InvariantParseCache<T>` (timestamp-based invalidation), and exposes three modes via `IInvariantProvider`: `id` lookup (full body), `audit` (total + per-category breakdown + duplicate ids + parse warnings), `list` (id+title index, no bodies). Two bullet shapes recognised: shape A (`- **INV-X-N**: body`) and shape B (`- **INV-X-N. Title sentence.** Body`). Parser is a pure function; provider is a thin orchestrator over cache + parser — adding a new invariant source (YAML, external governance DB) ships as a sibling provider reusing the cache. Duplicate ids are detected and surfaced in audit. Pinned by `ClaudeMdInvariantParserTests`, `InvariantProviderAndHandlerTests`, `LifebloodClaudeMdSelfTests`, `InvariantParseCacheTests`.

### Write-Side Semantic Comparison (v0.6.1)

- **INV-FINDIMPL-001. `FindImplementations` compares via canonical Lifeblood symbol IDs, never display strings or `SymbolEqualityComparer`.** The canonical ID from `RoslynCompilationHost.BuildSymbolId` (routing through `CanonicalSymbolFormat`) is the only cross-assembly-safe comparison for C# symbols. `ToDisplayString()` is subject to the v0.6.0 nullability/reduced-name/attribute drift class. `SymbolEqualityComparer.Default` is stricter than needed — treats source and PE-downgraded metadata copies as unequal because they live in different assemblies, breaking the cross-module matching `FindImplementations` needs. This invariant generalizes to ALL write-side matching across the source/metadata boundary: always route through `BuildSymbolId`. Pinned by `WriteSideIntegrationTests.FindImplementations_IGreeter_FindsGreeterAndFormalGreeter`.

## Symbol ID Grammar (C# Adapter)

Lifeblood symbol IDs are stable identifiers produced by `Lifeblood.Adapters.CSharp.Internal.SymbolIds`
and consumed by every read/write tool. The C# adapter format:

```
mod:AssemblyName
file:Relative/Forward/Slashed/Path.cs
ns:Fully.Qualified.Namespace
type:Fully.Qualified.TypeName
method:Fully.Qualified.TypeName.MethodName(Param1Type,Param2Type)
field:Fully.Qualified.TypeName.FieldName
property:Fully.Qualified.TypeName.PropertyName
property:Fully.Qualified.TypeName.this[Param1Type,Param2Type] // indexer
```

**Method parameter signature is FULLY-QUALIFIED.** A method that takes one `MyApp.Item` parameter
appears as `method:MyApp.Service.Process(MyApp.Item)`. Never `method:MyApp.Service.Process(Item)`.

The exact format is pinned by `Internal.CanonicalSymbolFormat.ParamType`. Every method-ID builder
in the adapter routes through `CanonicalSymbolFormat.BuildParamSignature` so source and metadata
symbols ALWAYS produce the same ID. Do not call `ITypeSymbol.ToDisplayString()` from a method-ID
builder. Always use the canonical formatter.

**Constructors:** name part is `.ctor`. Example: `method:MyApp.Service..ctor(string,int)`.

**Operators / conversion operators:** name part is the Roslyn operator name (e.g. `op_Addition`,
`op_Implicit`).

**Special types:** C# aliases (`int`, `string`, `bool`, `void`, …) are used instead of their
`System.*` qualified names. The canonical format has `UseSpecialTypes` enabled.

**Nullability:** never reflected in IDs. `string?` and `string` share the same parameter signature
because they refer to the same underlying method symbol.

## Serialization Naming

JSON schemas use **camelCase**. C# models use **PascalCase**. The mapping is mechanical:

| C# Property | JSON Field | Notes |
|-------------|-----------|-------|
| `SourceId` | `sourceId` | Edge reference |
| `TargetId` | `targetId` | Edge reference |
| `QualifiedName` | `qualifiedName` | Symbol FQN |
| `ParentId` | `parentId` | Containment hierarchy |
| `FilePath` | `filePath` | Source location |
| `CanDiscoverSymbols` | `discoverSymbols` | Capability: "Can" prefix dropped |
| `MustNotReference` | `mustNotReference` | Rule constraint |
| `MayOnlyReference` | `mayOnlyReference` | Rule constraint |

Rule: JSON serializers should use `System.Text.Json` with `JsonNamingPolicy.CamelCase`. Capability fields have manual names (documented in `schemas/graph.schema.json`).

## Naming Conventions

| Pattern | Usage |
|---------|-------|
| `*Analyzer` | Stateless analysis pass |
| `*Classifier` | Categorizes nodes |
| `*Detector` | Finds patterns |
| `*Validator` | Checks rules |
| `*Builder` | Constructs complex objects |
| `*Generator` | Produces output artifacts |
| `*Provider` | Supplies data (read-oriented) |
| `*Importer/*Exporter` | Serialization ports |
| `I*` | Port interface |

## Rules for Adding Features

1. **New graph concept** → Domain. Check INV-GRAPH-001 first. Language-specific = Properties.
2. **New analysis** → Lifeblood.Analysis. Stateless. Graph in, result out.
3. **New language adapter** → Lifeblood.Adapters.{Language}/ or external JSON.
4. **New AI connector** → Lifeblood.Connectors.{Name}/
5. **New CLI command** → Lifeblood.CLI/
6. **New use case** → Lifeblood.Application/UseCases/

## Streaming Compilation Architecture (v0.3.0)

- **INV-STREAM-001**: `ModuleCompilationBuilder.ProcessInOrder` compiles one module at a time in topological order. After extraction, `Emit()` → `MetadataReference.CreateFromImage()` downgrades the full compilation (~200MB) to a lightweight PE reference (~10-100KB). Peak memory: O(1 compilation), not O(N).
- **INV-STREAM-002**: `SharedMetadataReferenceCache` deduplicates NuGet MetadataReferences across modules. One instance per `AnalyzeWorkspace` call.
- **INV-STREAM-003**: `AnalysisConfig.RetainCompilations` controls mode. `false` (default, CLI) = streaming/memory-safe. `true` (MCP server) = retained for write-side tools.
- **INV-STREAM-004**: Unity csproj support: if `<Compile Include>` items exist (old-format), use them. If absent (SDK-style), scan filesystem.
- **INV-STREAM-005**: `GraphBuilder.Build()` deduplicates ALL edges by `(sourceId, targetId, kind)`. Partial classes emit duplicate edges. The builder is the authoritative dedup boundary.
- **INV-FILE-EDGE-001**: `GraphBuilder.Build()` derives file-level `References` edges from symbol-level edges. For each non-Contains edge between symbols in different files, a `file:X → file:Y References` edge is emitted with an `edgeCount` property. Evidence: `Inferred`, adapter: `GraphBuilder`. File edges are derived truth. Not primary.
- **INV-INCR-001**: Incremental re-analyze (`lifeblood_analyze` with `incremental: true`) only recompiles modules whose files changed since the last analysis. Per-file extraction results are cached in `AnalysisSnapshot`. Changed files are detected via filesystem timestamps. Module additions/removals fall back to full re-analyze. v1 limitation: does not cascade to dependent modules when API surface changes.

## MCP Tools

Canonical count and per-tool detail live in `docs/STATUS.md` (ratchet-pinned by `DocsTests`) and `docs/TOOLS.md`. `ToolRegistry.cs` is the code-level source of truth.

## What NOT to Do

- Do not put language-specific logic in Domain or Application. Ever.
- Do not make analyzers stateful or mutable.
- Do not let adapters reference other adapters.
- Do not let connectors reference adapters.
- Do not hardcode file extensions or syntax patterns in Domain.
- Do not require adapters to be written in C#. JSON is the universal protocol.
- Do not add "AI features" to the graph model. The graph is pure data. AI consumption happens in connectors.

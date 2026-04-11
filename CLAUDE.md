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

### Left Side (Language Adapters)
```csharp
IWorkspaceAnalyzer.AnalyzeWorkspace(projectRoot, config) → SemanticGraph
IModuleDiscovery.DiscoverModules(projectRoot) → ModuleInfo[]
ICompilationHost.GetDiagnostics / CompileCheck / FindReferences // Roslyn-backed
ICodeExecutor.Execute(code, imports, timeoutMs) → CodeExecutionResult
IWorkspaceRefactoring.Rename(symbolId, newName) → TextEdit[] / Format(code) → string
```

### Right Side (AI Connectors)
```csharp
IAgentContextGenerator.Generate(graph, analysis) → AgentContextPack
IMcpGraphProvider.LookupSymbol / GetDependencies / GetDependants / GetBlastRadius
IInstructionFileGenerator.Generate(graph, analysis) → string
```

### Graph I/O
```csharp
IGraphImporter.Import(stream) → SemanticGraph
IGraphExporter.Export(graph, stream)
```

### Analysis
```csharp
IRuleProvider.LoadRules(path) → ArchitectureRule[]
IBlastRadiusProvider.Analyze(graph, symbolId, maxDepth) → BlastRadiusResult
```

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

- **INV-RESOLVER-005. Wrong-namespace inputs resolve via the trailing
  short-name segment.** Two dogfood reports landed the same failure: the user
  typed a kind-prefixed, namespaced symbol id with a wrong or stale namespace
  (`type:Nebulae.BeatGrid.Audio.DSP.VoicePatchAdapter` when the real symbol
  lives in `Audio.Tuning`), and the resolver returned three completely
  unrelated `MixerScreenAdapter` properties as suggestions. Two compounding
  bugs:

  1. Rules 1-3 in `LifebloodSymbolResolver.Resolve` all had to fail because
     the input had a kind prefix and namespace dots — but rule 3's bare
     short-name lookup only fires when the input has NEITHER prefix NOR
     dots, so the short-name index was never consulted even though the
     trailing segment uniquely identified the real symbol.
  2. The fallback ranker `SuggestNearMatchesInternal` scored the FULL
     canonical-shaped input (`type:Nebulae.BeatGrid.Audio.DSP.VoicePatchAdapter`)
     against every symbol's bare `Name`. Levenshtein closeness in that
     ranker is `closeness = candidateLength - distance`, which biases
     toward extremely long candidate names regardless of semantic
     similarity. The dogfood case ranked
     `MixerScreenAdapter.…IMixerDisplayDataSource.ActivePresetName` first
     because it was *long*, not because it was *related*.

  The architectural fix lives in `LifebloodSymbolResolver` and consists
  of one extracted helper plus one new rule plus one ranker correction:

  - `LifebloodSymbolResolver.ExtractLikelyShortName(string)` is a pure
    function that strips the kind prefix, strips any method parameter
    list, and returns the final dot-separated segment. It's the single
    interpretation of "what short name did the user mean" used by both
    Rule 4 and the suggestion ranker so the two can never diverge.
  - **Rule 4** (extracted short-name fallback). After rules 1-3 fail and
    `ExtractLikelyShortName(input)` produces a non-empty segment that
    differs from the input, the resolver looks the segment up via
    `SemanticGraph.FindByShortName`. Single hit →
    `ResolveOutcome.ShortNameFromQualifiedInput` (a successful resolution
    populating `CanonicalId` + `Symbol` + a Diagnostic that explains the
    namespace correction). Multiple hits →
    `ResolveOutcome.AmbiguousShortNameFromQualifiedInput` with every
    candidate surfaced; the resolver refuses to silently pick one.
  - The suggestion ranker `SuggestNearMatchesInternal` now passes the
    raw query through `ExtractLikelyShortName` BEFORE scoring. Literal
    short-name-index hits land at score `ShortNameHitScore = 1000`,
    deliberately above any reachable `ScoreCandidate` value, so a real
    short-name match always sorts above any fuzzy ranking accident.

  All ten read-side and write-side tools that route through the resolver
  inherit the fix automatically — no per-tool change needed (this is the
  whole point of INV-RESOLVER-001's "every read-side tool routes through
  the resolver"). Pinned by `ResolverShortNameFallbackTests` (24 tests
  including the two exact dogfood cases reproduced in synthetic graphs
  with bias-inducing noise symbols, the ambiguous case, the not-found
  fallthrough with extraction-aware diagnostic text, and the legacy
  rules 1-3 still firing on bare and exact inputs).

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
the same method — one source, one metadata; or one declared in module A,
one implementing the same interface in module B — silently corrupts every
downstream feature: `find_references`, `dependants`, blast radius, the
fuzzy short-name resolver, and every cross-module tool that relies on
string equality of canonical IDs.

- **INV-CANONICAL-001. Roslyn compilations receive the full transitive
  dependency closure, not just direct `ModuleInfo.Dependencies`.** Unlike
  MSBuild's ProjectReference flow, Roslyn's `CSharpCompilation.Create`
  does NOT walk references transitively. If module C directly references
  module B, and B references module A, then compiling C must pass BOTH B
  and A as explicit `MetadataReference`s. If A is missing, every type from
  A that appears through B's public surface becomes a Roslyn error type
  symbol whose `ContainingNamespace` is empty. `CanonicalSymbolFormat.BuildParamSignature`
  then emits the short type name with no namespace qualifier, producing
  non-canonical IDs like `method:C.Impl.M(BarType)` instead of the
  correct `method:C.Impl.M(A.BarType)`. The drift is silent: the
  compilation still succeeds, extraction still runs, diagnostics still
  pass, the graph is populated. Only cross-module lookups, which rely on
  canonical-ID string equality, fail in ways that look like missing
  references.

  The single enforcement site is `Internal.ModuleCompilationBuilder.ProcessInOrder`,
  which routes each module's direct dependencies through
  `Internal.ModuleCompilationBuilder.ComputeTransitiveDependencies` before
  collecting PE references. `ModuleInfo.Dependencies` retains its
  direct-only semantics — it also feeds the module→module graph edges and
  the topological sort, both of which are correct on direct deps only —
  but EVERY compilation-reference consumer MUST use the closure helper.
  Adding a new consumer that iterates `module.Dependencies` directly when
  building compilation refs re-introduces the bug class.

  Dogfood origin: the first version of this invariant was discovered by
  running `lifeblood_resolve_short_name "Resolve"` against Lifeblood itself
  and observing that `LifebloodSymbolResolver.Resolve(SemanticGraph,string)`
  was stored with an unqualified parameter while `ISymbolResolver.Resolve(Lifeblood.Domain.Graph.SemanticGraph,string)`
  was stored fully qualified — same method shape, two different canonical
  IDs. Root cause was that `Lifeblood.Connectors.Mcp.csproj` declares only
  a direct reference to `Lifeblood.Application` and Lifeblood's own
  compilation builder was NOT walking the closure to pick up Domain.

  Pinned by `tests/Lifeblood.Tests/CanonicalSymbolFormatTests.cs`:
  `ComputeTransitiveDependencies_FlatChain_ReturnsFullClosure`,
  `ComputeTransitiveDependencies_Diamond_ReturnsDeduplicatedClosure`,
  `ComputeTransitiveDependencies_Cycle_DoesNotInfinitelyRecurse`, and the
  end-to-end `AnalyzeWorkspace_ThreeModuleTransitiveChain_ProducesCanonicalMethodIds`
  which builds a real three-module workspace on disk where the Outer
  module references Middle only and asserts that a parameter type
  defined in Core still produces the fully-qualified canonical ID.

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

- **INV-USAGE-001. Usage is inert data.** `AnalysisUsage` and
  `PhaseTiming` hold only primitive fields and arrays of primitive fields.
  No `System.Diagnostics` types leak onto the records. Any consumer reads
  the snapshot freely on any thread.
- **INV-USAGE-002. Units are documented on every field.** Bytes for
  memory, milliseconds for time. No implicit conversions. The only derived
  property is `CpuUtilizationPercent`, which is `CpuTimeTotalMs / WallTimeMs * 100`.
- **INV-USAGE-PORT-001. The probe port returns a fresh capture on every
  `Start`.** Two captures started back-to-back do not share peak samples,
  CPU deltas, phase lists, or GC counters. A capture's lifetime is scoped
  to the single analyze run that created it.
- **INV-USAGE-PORT-002. `IUsageCapture.Stop` is idempotent.** A second
  call returns the same `AnalysisUsage` instance. `IDisposable.Dispose`
  on an already-stopped capture is a no-op. The error path in
  `AnalyzeWorkspaceUseCase` disposes the capture when validation throws
  so the sampling timer does not outlive the failed run.
- **INV-USAGE-PROBE-001. The concrete `ProcessUsageProbe` takes an
  initial RSS sample in its constructor.** Sub-sample-interval runs still
  report a non-zero peak working set. This makes the feature honest on
  tiny analyze calls where the background timer never ticks.
- **INV-USAGE-PROBE-002. The sampling timer is disposed at `Stop` or
  `Dispose`.** Leaked timers would keep the probe alive past the run and
  corrupt the next capture's peak samples. Test
  `ProcessUsageProbeTests.Probe_TwoCaptures_AreIndependent` pins this.

The probe lives in `Lifeblood.Adapters.CSharp` alongside
`PhysicalFileSystem` because it touches `System.Diagnostics.Process`. The
port lives in Application alongside `IFileSystem`. Composition roots
(`Lifeblood.CLI.Program` and `Lifeblood.Server.Mcp.GraphSession`) are the
only sites that construct the concrete probe.

## BCL Ownership (C# Adapter)

Some csprojs ship their own base class library via `<Reference Include="netstandard|mscorlib|System.Runtime">` (Unity ships .NET Standard 2.1; .NET Framework / Mono ship mscorlib). Plain SDK-style csprojs (`<Project Sdk="Microsoft.NET.Sdk">`) don't. They rely on the host runtime BCL.

Lifeblood treats this as a discovered module fact: `Lifeblood.Application.Ports.Left.ModuleInfo.BclOwnership` is `BclOwnershipMode.HostProvided` or `BclOwnershipMode.ModuleProvided`. The decision is computed ONCE during `RoslynModuleDiscovery.ParseProject` and consumed by `ModuleCompilationBuilder.CreateCompilation`. Detection logic lives in exactly one place. `RoslynModuleDiscovery.ReferenceDeclaresBcl` plus its `ParseAssemblyIdentitySimpleName` and `IsBclSimpleName` helpers. The compilation builder reads the field and never re-derives.

- **INV-BCL-001. Single BCL per compilation.** Two BCLs causes CS0433 (ambiguous type) and CS0518 (predefined type missing) on every System type. The semantic model becomes unusable: `GetSymbolInfo` returns null at every call site, so `find_references`, `dependants`, and call-graph extraction silently produce zero results. Type-only references survive because their resolution is partially syntactic. Methods do not.
- **INV-BCL-002. Module owns its BCL when its csproj declares one.** A `<Reference>` element whose Include value (parsed as assembly identity, handles both `Include="netstandard"` and `Include="netstandard, Version=2.1.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51"`) or HintPath basename matches `netstandard`, `mscorlib`, or `System.Runtime` is the authoritative declaration. Such modules MUST NOT also receive the host BCL bundle.
- **INV-BCL-003. Host BCL is the fallback for plain SDK-style projects.** Modules with no BCL-naming `<Reference>` element receive `BclReferenceLoader.References.Value` so System types resolve.
- **INV-BCL-004. BCL ownership is decided at discovery time, single source of truth.** Detection logic lives in `RoslynModuleDiscovery`. `ModuleCompilationBuilder` reads the typed field. No filename sniffing, no re-derivation, no detection logic at the compilation layer.
- **INV-BCL-005. Incremental re-analyze respects csproj edits.** `AnalysisSnapshot.CsprojTimestamps` tracks csproj timestamps. When a csproj timestamp changes, the affected module is re-discovered and recompiled even if no .cs file changed. Without this, a csproj edit that adds or removes a BCL reference produces a stale `BclOwnership` value forever and the double-BCL bug returns under incremental mode.

The full plan with empirical evidence, rollout history, and the regression test matrix lives at `.claude/plans/bcl-ownership-fix.md`.

### MCP Protocol Invariants (v0.6.1)

The JSON-RPC 2.0 / MCP protocol contract is owned by `McpDispatcher` in `Lifeblood.Server.Mcp`. `Program.cs` is a thin stdio I/O loop that delegates every request to the dispatcher. It contains no routing logic, no method-string matching, no response construction. All of that lives in `McpDispatcher`, which is a public sealed class testable via its normal public API.

- **INV-MCP-001. `initialize` response carries `protocolVersion` and `capabilities`.** Both are required by the MCP spec. `protocolVersion` is sourced from `McpProtocolSpec.SupportedVersion` via `McpDispatcher.SupportedProtocolVersion` (see INV-MCP-003). `capabilities` is a typed `McpCapabilities` POCO, never an inline dictionary, so new MCP capability negotiations are structural additions. Class-level defaults on `McpInitializeResult` plus explicit construction-site assignment make this belt-and-braces. Pinned by `McpProtocolTests.Initialize_ReturnsSpecCompliantResult`, `Initialize_SerializedJson_HasProtocolVersionAndCapabilities`, and `McpProtocolSourceOfTruthTests.McpDispatcher_InitializeResponse_AdvertisesSpecVersion`.

- **INV-MCP-002. Notifications never receive a response body.** Messages with `request.Id == null` are notifications per JSON-RPC 2.0. Recognized notification method names live in `McpProtocolSpec.AllKnownNotifications` (see INV-MCP-003). `McpDispatcher.KnownNotifications` is constructed directly from that set, so the two can never drift. Today's set: `notifications/initialized` (spec canonical), `initialized` (legacy alias during deprecation), `notifications/cancelled`, `$/cancelRequest`. Any entry short-circuits `Dispatch` to return `null` before response construction. Unknown notifications also return `null` and log to stderr for operator visibility. Pinned by `McpProtocolTests.NotificationsInitialized_SpecCompliantForm_ProducesNoResponse`, `NotificationsInitialized_LegacyAlias_ProducesNoResponse`, `UnknownNotification_ProducesNoResponse`, and the spec-level `McpProtocolSourceOfTruthTests.McpDispatcher_KnownNotification_ProducesNoResponse` Theory.

- **INV-MCP-003. Every MCP wire-format constant has exactly one canonical source per side.** Protocol version, JSON-RPC method names, and notification method names live exclusively in `Lifeblood.Connectors.Mcp.McpProtocolSpec` — the single source of truth for the server and every in-repo client. Clients that cannot take a project reference (Unity bridge today; future first-party clients tomorrow) ship a standalone mirror file pinned by a ratchet test. The Unity mirror lives at `unity/Editor/LifebloodBridge/McpProtocolConstants.cs` and is compared byte-equal to `McpProtocolSpec` by `McpProtocolSourceOfTruthTests`. The server consumes the spec directly via project reference: `McpDispatcher.SupportedProtocolVersion` chains to `McpProtocolSpec.SupportedVersion`, `McpDispatcher.KnownNotifications` is built from `McpProtocolSpec.AllKnownNotifications`, and the dispatcher's method switch uses `McpProtocolSpec.Methods.*` const cases. Drift discovered in the v0.6.2 MCP stabilization pass: the Unity bridge client was sending an empty `initialize.params` (spec violation — required fields `protocolVersion`, `capabilities`, `clientInfo` were missing) AND using the legacy `initialized` notification alias that INV-MCP-002 had already marked "during deprecation". Both bugs were possible only because the wire constants were hardcoded per file. The ratchet tests pin every consumer: `McpDispatcher_SupportedProtocolVersion_ComesFromProtocolSpec`, `UnityBridgeConstants_SupportedVersion_MirrorsProtocolSpec`, `UnityBridgeConstants_MethodNames_MirrorProtocolSpec`, `UnityBridgeConstants_CanonicalNotifications_MirrorProtocolSpec`, `UnityBridgeConstants_DoesNotExposeLegacyAlias`, `UnityBridgeClient_ContainsNoBareProtocolStringLiterals`, `UnityBridgeClient_SendsCanonicalInitializedNotification`, `UnityBridgeClient_InitializeRequest_PopulatesSpecParams`. Adding a new wire constant: edit `McpProtocolSpec` once; the mirror and consumer ratchets catch every forgotten downstream.

- **INV-TOOLREG-001. Tool availability dispatch is by typed enum, never by name prefix. Internal registry records and wire-format DTOs are separate types.** Every `ToolDefinition` (internal registry record in `Lifeblood.Server.Mcp`) declares `required ToolAvailability Availability { get; init; }`. Omitting it is a compile error (C# 11 `required`). `ToolRegistry.GetDefinitions()` returns `ToolDefinition[]` for test seams and internal consumers; `ToolRegistry.GetTools(hasCompilationState)` projects those definitions into `McpToolInfo[]` — the wire-format DTO used in the MCP `tools/list` response — applying the `[Unavailable...]` description decoration when the session has no compilation state. The wire DTO has no `Availability` field at all, and therefore no `[JsonIgnore]` workaround; System.Text.Json serializes it cleanly. **Wire-DTO drift history:** the v0.6.1 credibility pass introduced `required init` on a single conflated type that served as both internal record and wire payload, with `[JsonIgnore]` on `Availability`. System.Text.Json in .NET 8 has a latent bug where `[JsonIgnore]` is NOT honoured on `required init` properties during serialization metadata construction — the runtime threw `JsonException` "marked required but does not specify a setter" on every `tools/list` response, which Claude Code interpreted as a dead server and refused to reconnect. The fix — and the reason the split exists — is that conflating wire DTOs with internal records caused both the serialization bug and the Claude Code connection failure. **The previous 8-prefix-based classification bug is also gone:** it silently misclassified `lifeblood_resolve_short_name` (v0.6.0 prefix guard had no match for that name); the typed enum on `ToolDefinition` makes the classification explicit and test-enforced. Pinned by `McpProtocolTests.ToolRegistry_EveryToolHasExplicitAvailability`, `WriteSideTools_MarkedUnavailable_WhenNoCompilationState`, `ReadSideTools_NeverMarkedUnavailable`, `ResolveShortName_IsClassifiedReadSide`, and the wire-serialization regression test `ToolRegistry_ToolsList_SerializesWithoutError` which confirms `tools/list` responses contain no `availability` field and no `-32603` errors.

### Governance Invariants (v0.6.1)

- **INV-SCRIPTHOST-001. `Lifeblood.ScriptHost` has zero `ProjectReference`.** The script host is a process-isolated child that runs untrusted code; its "no access to parent state" guarantee depends on not taking a ProjectReference to any Lifeblood module. NuGet PackageReferences are allowed (Microsoft.CodeAnalysis.CSharp.Scripting is load-bearing). Ratchet-tested by `ArchitectureInvariantTests.ScriptHost_HasZeroProjectReferences`.

- **INV-COMPROOT-001. Composition roots use only the allowlist.** `Lifeblood.CLI` and `Lifeblood.Server.Mcp` reference only `{Domain, Application, Analysis, Adapters.CSharp, Adapters.JsonGraph, Connectors.ContextPack, Connectors.Mcp}`. The allowlist is declared once as `private static readonly HashSet<string> CompositionRootAllowedModules` on `ArchitectureInvariantTests`. Single source of truth. Expanding it is a conscious architectural decision requiring a commit that edits the test. Ratchet-tested by `CompositionRoot_CLI_UsesOnlyAllowedModules` and `CompositionRoot_ServerMcp_UsesOnlyAllowedModules`.

- **INV-DOCS-001. Doc numbers match the repository.** `docs/STATUS.md` declares port and tool counts in HTML comments (`<!-- portCount: 17 -->`, `<!-- toolCount: 18 -->`). The single source of truth is the HTML comment; ratchet tests in `DocsTests` parse the comment and assert the number matches the live count of `public interface I*` declarations under `src/Lifeblood.Application/Ports` (for ports) and `Name = "lifeblood_*"` literals in `ToolRegistry.cs` (for tools). Editing the count in one place and not the other fails the ratchet.

- **INV-CHANGELOG-001. Every version heading has a link reference.** `CHANGELOG.md` must contain a `[X.Y.Z]: https://github.com/.../compare/...` link reference for every `## [X.Y.Z]` heading. Ratchet-tested by `DocsTests.Changelog_EveryHeadingHasLinkReference`. Closes the drift class where v0.6.0 shipped with stale bottom-of-file link refs.

### Test Discipline (v0.6.1)

- **INV-TESTDISC-001. Tests never silently early-return on precondition failure.** The `TryAnalyze(out ...) ⇒ bool` pattern with `if (!TryAnalyze(...)) return;` guards is forbidden. It hides both legitimate skips AND real failures as silent passes. Missing preconditions (golden repo not restored, etc.) turn into explicit `Skip.IfNot(condition, reason)` calls via `Xunit.SkippableFact`, surfaced in the test runner as Skip with a documented reason. Presence-but-broken conditions (golden repo present, analysis produced zero symbols) turn into loud `Assert.True` / `Assert.Fail`. No hiding. The shape for skippable integration tests is `[SkippableFact]` + a helper that calls `Skip.IfNot` for preconditions and throws on real failures. The deep-audit pass that shipped this invariant caught a latent `FindImplementations` bug that had been hiding behind silent returns for multiple commits, and the user flagged this as "the most load-bearing cleanup in P4" in the audit brief. Enforced by reading `WriteSideIntegrationTests.cs` as the canonical example. Grep for `if (!Try*` in `tests/**/*.cs` should return zero hits.

### Invariant Introspection (v0.6.3)

- **INV-INVARIANT-001. CLAUDE.md is the single source of truth for architectural invariants; the tool parses it at runtime.** `lifeblood_invariant_check` reads `CLAUDE.md` from the loaded project root via `IFileSystem`, parses it with `Internal.ClaudeMdInvariantParser`, caches the result per-root in `Internal.InvariantParseCache<T>` with timestamp-based invalidation, and exposes three modes through `IInvariantProvider`: (1) `id` lookup returns one invariant's full body, (2) `audit` (default) returns the total count + per-category breakdown + duplicate ids + parse warnings, (3) `list` returns every id+title index without bodies. Two bullet shapes are recognised — shape A (`- **INV-X-N**: body`) and shape B (`- **INV-X-N. Title sentence.** Body`) — matching every invariant in this file. The parser is a pure function; the provider is a thin orchestrator over cache + parser, so adding a new invariant source (YAML, JSON, external governance DB) ships as a sibling provider reusing the cache. Duplicate ids are detected and surfaced in the audit so drift like the v0.6.2 `INV-TEST-001` / `INV-TESTDISC-001` rename-collision cannot recur silently. Pinned by `ClaudeMdInvariantParserTests` (17 tests covering both shapes + duplicate detection + category inference), `InvariantProviderAndHandlerTests` (provider direct + MCP dispatch + end-to-end via real `lifeblood_analyze`), and `LifebloodClaudeMdSelfTests` (the tool parsing its own project's CLAUDE.md — the first alarm if the parser drifts from the authoring conventions).

### Write-Side Semantic Comparison (v0.6.1)

- **INV-FINDIMPL-001. `FindImplementations` compares via canonical Lifeblood symbol IDs, never display strings or `SymbolEqualityComparer`.** The canonical symbol ID from `RoslynCompilationHost.BuildSymbolId` (which routes through `Internal.CanonicalSymbolFormat`) is the only cross-assembly-safe comparison for C# symbols in Lifeblood. `ToDisplayString()` happens to work for cross-assembly identity because display strings omit assembly qualification, but the drift class from v0.6.0 Layer 2 (nullability, reduced names, attribute round-trips) applies. `SymbolEqualityComparer.Default` is stricter than needed. It treats source and metadata PE-downgraded copies of the same type as non-equal because they live in different assemblies, which breaks the very cross-module matching `FindImplementations` needs. The canonical-ID approach is correct by construction: the ID is built from the symbol's namespace + name + container, not from identity. Pinned by `WriteSideIntegrationTests.FindImplementations_IGreeter_FindsGreeterAndFormalGreeter` which exercises the cross-module case against the golden repo. This invariant generalizes to ALL future write-side matching operations: when comparing symbols for semantic equality across the source/metadata boundary, always route through `BuildSymbolId`, never through display strings or `SymbolEqualityComparer`.

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

## 17 MCP Tools

Read-side (7): analyze, context, lookup, dependencies, dependants, blast_radius, file_impact
Write-side (10): execute, diagnose, compile_check, find_references, find_definition, find_implementations, symbol_at_position, documentation, rename, format

## What NOT to Do

- Do not put language-specific logic in Domain or Application. Ever.
- Do not make analyzers stateful or mutable.
- Do not let adapters reference other adapters.
- Do not let connectors reference adapters.
- Do not hardcode file extensions or syntax patterns in Domain.
- Do not require adapters to be written in C#. JSON is the universal protocol.
- Do not add "AI features" to the graph model. The graph is pure data. AI consumption happens in connectors.

# Status

2026-05-31 tracking-list hardening adds a server-edge MCP contract layer without changing Domain/Application: `ToolInputContractCatalog` is the typed MCP input-contract SSoT, `ToolDefinition.InputSchema` is generated from that catalog, `ToolArgumentBinder` validates tool arguments under `LIFEBLOOD_JSON_COMPAT=legacy|warn|strict`, and `ToolRequestBinder` binds high-risk analyze/compile-check/contract-audit inputs through typed request records. `LIFEBLOOD_STRICT_JSON` is retained as the strict alias. `GraphSessionGate` serializes retained-session mutation at the MCP host boundary. `McpJsonRequestParser` now deserializes `JsonRpcRequest` through a source-generated context while preserving the dynamic response serializer for tool payloads. Operational telemetry now includes argument diagnostics and real `lifeblood.analyze.phase` events with allocation deltas; invariant-cache telemetry is emitted after releasing the cache lock. Tool packaging/distribution has a local Windows `win-x64` receipt covering pack, local install, CLI help contract, MCP closed-stdin smoke, optional invocation-form skips, and report-only publish experiments.

> **Shared transport rollout:** `--shared` is the recommended configuration for
> same-workspace multi-agent use; private stdio remains the rollback path. Exact
> build `0.7.13-alpha.0.63+962cf9bb8145c779e1f4ff89e5dc9f38a6ece71b`
> passed the final DAWG Editor+Player
> canonical-bridge dogfood, while the earlier 1/2/4-client rollout remains the
> memory/concurrency baseline. Unity and a fresh direct client observed one daemon,
> generation, snapshot, and semantic base; incremental no-op, pinned batch,
> explicit-idle exit, reconnect retention, and clean restart passed. Shared protocol v2 also ratchets
> persistent leases, live status, exact analyze coalescing, per-waiter request
> cancellation, disconnected-waiter survival, build/workspace admission, isolation, bounded graph-only history,
> maintenance drain, malformed-frame recovery, and crash recovery.
> `lifeblood_capabilities.featureFlags.sharedSessionTransportMaturity` reports
> that same `recommended` posture from one server-edge authority
> (`INV-MCP-SHARED-MATURITY-001`).

The current installed local tools are `0.7.13-alpha.0.70` from commit
`07d35398ca81`. The packaging lane packed both dotnet tools, installed private
smoke copies, passed CLI help and closed-stdin MCP smoke, then the global
`lifeblood` and `lifeblood-mcp` shims were updated from the local package
source. Unity refreshed the external `Lifeblood/unity` package at generation 21
(`lifeblood-bridge-deadline-fix-20260716-a1`) with scripts compile requested,
ready confirmed, and external dirty cleared; the previous `CS0103
ReadLineWithTimeout` bridge compile error is gone. Installed CLI dogfood on the
current DAWG tree reported 89,326 symbols, 348,359 edges, 101 modules, 5,619
types, 140 cycles, and zero configured violations in 56.7 seconds wall time
with 994 MB peak working set / 919 MB peak private bytes. These are local
prerelease/dirty-workspace development receipts, not a published-stable release
claim.

The earlier `0.7.13-alpha.0.63+962cf9bb8145c779e1f4ff89e5dc9f38a6ece71b`
DAWG bridge gate used the canonical global `lifeblood-mcp --shared` tool. A fresh
Editor+Player request deliberately asked for incremental analysis with full
fallback allowed. It recovered from generation zero, analyzed an external
Unity `file:` package without leaking a `../Lifeblood/...` path, and published
generation 1 / snapshot `snap_0a7853d321ff44188c5326488a83e9d1` with
88,944 symbols, 346,846 edges, 101 modules, 5,581 types, 138 cycles, and zero
configured violations in 63.71 seconds of client wall time. Its detailed
accepted-change receipt contained 4,386 project-relative workspace identities;
the four bridge sources used the single logical
`Packages/com.dawgtools.lifeblood-bridge/...` namespace and no traversal path.
A subsequent warm incremental request completed in 18.52 seconds and published
generation 2 / snapshot `snap_952a425a50944d03b2508ef1d82f8419`
after one real concurrent DAWG source change, with 88,944 symbols and 346,849
edges. Pinned member/dependant/blast/callsite queries then reported zero files
changed since analyze.

The direct Codex `Transport closed` symptom had a separate deployment cause:
user-level Codex configuration still launched the obsolete
`dist-shared/Lifeblood.Server.Mcp.dll` build `0.7.13-alpha.0.47`, while DAWG and
Unity launched the installed tool. The configuration now uses the same
`lifeblood-mcp --shared` command as every other client. Already-running tasks
retain their dead MCP handle until task/app reload; raw direct MCP and Unity
both prove the installed daemon itself is healthy. An older Unity pending call
was likewise not lost: action-only status returned its terminal
`analysis-input-changed` failure, and the uncontended retry produced the warm
success above.

During the local 0.50-to-0.51 tool update, the installer deliberately stopped
the old DAWG proxy and daemon. Concurrent clients therefore observed
generation 0 / `fallbackReason:"noPriorAnalysis"` and one pending Unity call
observed `Lifeblood server closed or timed out`. Those events are maintenance
restart receipts, not spontaneous state loss on 0.51; the post-install
full/execute/incremental sequence above is the acceptance authority.

Wave 1 of the shared-base plan replaces independently authored read/write and
lock-name lists with one typed behavior registry. All 41 tools now declare
their default session prerequisite, effect, and session access once; mixed
action tools may add a registry-owned per-call behavior resolver. Prerequisite
errors, gate routing, `tools/list` availability, and capability reporting
derive from that contract instead of tool-name branches. Snapshot-read support
derives from `Observe + SharedRead`; it is not another authored axis. The old
23/18 split remains additive compatibility metadata while clients adopt
`behaviorContracts`.

Dogfood-verified. 1647 discovered test cases (53 `[SkippableFact]` methods gate on runtime preconditions such as `LIFEBLOOD_REQUIRE_NATIVE_CLANG`). **41 MCP tools** (23 read + 18 write as a legacy retained-compilation compatibility projection). **33 port interfaces**. Source-control evidence uses one Application port and one bounded Git adapter with distinct neutral admission-provenance and live-change receipts; analyze captures provenance once at request admission and optional diagnostic ownership captures current changed-line evidence without treating it as semantic equality authority; release metadata and additive `releaseGate` receipts consume the admission authority, label capture timing, and distinguish stable-shaped builds from local prerelease/metadata builds or dirty workspaces (`INV-SOURCE-CONTROL-001`, `INV-DIAGNOSTIC-OWNERSHIP-001`, `INV-CHANGELOG-LATEST-TAG-001`). Truth envelope on every read-side response (`INV-ENVELOPE-001`). Caller-owned scope policy on `lifeblood_analyze` (`INV-ANALYZE-FALLBACK-001`): `incremental:true` rejects on detected drift unless `allowFullFallback:true`; rejection wire shape carries `mode` / `requestedMode` / `fallbackReason` (`noPriorAnalysis` / `moduleSetChanged` / `moduleDescriptorChanged` / `analysisScopeChanged` / `compilationStateUnavailable`) / `canRetryFull` / `suggestedRetry` so the agent's next move is self-documenting. `lifeblood_analyze excludePaths` removes vendored/sample source before Roslyn compilation and treats exclude-set changes as analysis-scope drift (`INV-ANALYZE-EXCLUDEPATHS-001`). Incremental source detection is content-hash authoritative (`INV-INCREMENTAL-CONTENT-HASH-001`): mtime-only touches return `mode:"incremental-noop"` and responses expose `mtimeTouchedSourceFiles` plus `contentChangedSourceFiles`; editor-supplied `authoritativeChangedFiles` narrows source scanning without suppressing descriptor/scope drift (`INV-ANALYZE-AUTHORITATIVE-CHANGESET-001`). One canonical `AcceptedChangeSet` now drives the default bounded summary or opt-in detail receipt, distinguishing content, descriptor-forced, deletion, mtime-only, and truthful full-fallback causes without parallel count/path authorities (`INV-ANALYZE-ACCEPTED-CHANGE-001`). External Unity package sources use one adapter-owned logical `Packages/{name}/...` identity across compilation, graph, fingerprint, visibility, deletion, and fallback evidence (`INV-WORKSPACE-SOURCE-PATH-001`). `readOnly:true` sessions recover through structured `compilationStateUnavailable` rejection or full retained fallback, never by reusing missing compilation state (`INV-READONLY-RECOVERY-001`). Shared multi-agent transport is available via `--shared` / `LIFEBLOOD_SHARED_SESSION=1`: stdio clients become thin proxies to one canonical-workspace-keyed named-pipe daemon, so one retained `GraphSession` and freshest analyze result are shared across attached agents instead of multiplying Roslyn heaps per agent. A typed protocol/version/module-MVID/workspace handshake rejects incompatible pipe reuse, bound analyze admission prevents a shared daemon from replacing its session with another workspace, and disconnecting one coalesced waiter does not poison surviving waiters (`INV-SHARED-HOST-IDENTITY-001`, `INV-WORKSPACE-BINDING-001`, `INV-MCP-REQUEST-CANCEL-001`). Same-workspace incremental admission keys from the current publication plus the complete execution policy and then shares the adapter's one timestamp/content scan; it no longer performs a second all-source content preflight (`INV-ANALYZE-COALESCE-001`). Behavior-derived snapshot preconditions, exact historical selection, and `lifeblood_batch` pin evidence to one leased publication; `lifeblood_snapshots action:"list"` observes the hard-bounded graph-only catalog through the shared-read lane while `pin`, `unpin`, and `evict` remain exclusive catalog mutations; every successful envelope projects the exact snapshot, generation, and canonical analysis identity (`INV-SNAPSHOT-PRECONDITION-001`, `INV-MCP-READ-BATCH-001`, `INV-MCP-HISTORICAL-READ-001`, `INV-SNAPSHOT-CATALOG-BOUND-001`, `INV-ANALYSIS-IDENTITY-PROJECTION-001`). Native usage and timing reporting on every `lifeblood_analyze` response. Opt-in operational telemetry (`LIFEBLOOD_TELEMETRY`) records tool success/error, response JSON cost, analyze result/fallback shape, truncation events, invariant-parse cache outcomes, and process/GC observable gauges. Architectural-invariant introspection via `lifeblood_invariant_check` walks `<root>/CLAUDE.md` + `<root>/AGENTS.md` + any `<root>/docs/invariants/**.md` tree (`LB-FR-023`); five authoring shapes recognised (A/B/C/D/E). Lifeblood self: **216 typed invariants across 162 categories** in `docs/invariants/` tree. Enum members are first-class graph symbols (`INV-EXTRACT-ENUMMEMBER-001`); resolver Rule 4 short-name fallback is type-aware and refuses cross-type / cross-kind silent substitution for member-kind inputs (`INV-RESOLVER-007`). Search results are structurally typed by source bucket (`SearchResult.MatchKind`, `INV-SEARCH-MATCHKIND-001`). Smart-dynamic shaping on `cycles` (`LB-FR-021`) and `context` (`LB-FR-022`) keeps responses inside conservative tool-result budgets even on multi-module Unity workspaces. File-mode `compile_check` (`LB-BUG-019`) resolves the owning compilation and swaps the existing tree, so module-owned files compile-check against their real reference set. Diagnose and compile-check share one adapter-owned file-ownership resolver; exact paths outrank suffix matches and ambiguous ownership fails closed with stable candidates instead of selecting the first module (`INV-COMPILATION-FILE-OWNERSHIP-001`). `lifeblood_execute` preserves raw compile diagnostics and adds CS1061 scripting-surface hints with public receiver members plus the `Help` global when available (`INV-EXECUTE-CS1061-HINT-001`). Preprocessor scope is surfaced on every `diagnose` / `compile_check` response (`INV-DIAGNOSTIC-ENVELOPE-DEFINES-001`) so a caller distinguishes Editor-only findings from release-build risk off one call. `dead_code` findings carry triage fields including `bucket:"Scaffolding"` for intentional reference-free anchors, plus `directDependants` / `declarationOnly`, alongside the existing kind histogram (`INV-DEADCODE-TRIAGE-001`, `INV-DEADCODE-SCAFFOLDING-001`); resolved UnityEvent persistent-call targets use the committed workspace context instead of process CWD (`INV-UNITYEVENT-REACHABILITY-001`, `INV-WORKSPACE-CONTEXT-001`); `cycles` SCCs are classified into `GeneratedOrStaticAnalysisArtifact` / `PartialClassCluster` / `LikelyRealLoop` triage buckets (`INV-CYCLE-TAXONOMY-001`). Two post-v0.7.3 tools land: `lifeblood_test_impact` (which test classes transitively depend on a target, `INV-TEST-IMPACT-001`) and `lifeblood_enum_coverage` (per-member produced / consumed-comparison / consumed-switch coverage, `INV-ENUM-COVERAGE-001`). Path-bucket classification (Production / Test / Editor / Generated / Vendored) lives in a single Domain-layer SSoT (`Lifeblood.Domain.PathClassification.PathBucketClassifier`, `INV-PATHBUCKET-SHARED-001`); three drifted classifiers collapsed into one. Csproj-driven compilation facts now thread `LangVersion` / `Nullable` / `NoWarn` / `DefineConstants` into Roslyn `CSharpParseOptions`, preserve framework source-generator analyzer paths, and run target-framework source generators before extraction/diagnostics (FOLLOWUP-001..003 + BUG-2; pre-fix the four options were parsed and stored on `ModuleInfo` but the compilation step ignored them). `SourceGeneratorRunner` serializes analyzer loading and generator-driver execution at the adapter seam (`INV-SOURCEGEN-SERIAL-001`); `SyntaxTreePathIdentity` maps relative generator hints into a workspace-neutral, module-qualified `generated/` namespace for both full and incremental extraction (`INV-SOURCEGEN-PATH-PROVENANCE-001`). Full and incremental candidates publish through one immutable committed-state reference, while exact duplicate full-analysis identities reuse the current publication and report `publication.action:"reusedCurrent"`; non-blocking read leases pin an exact generation while the single writer builds and publishes the next, and retired Roslyn-backed ports dispose after the final lease (`INV-SNAPSHOT-ATOMIC-PUBLISH-001`, `INV-SNAPSHOT-LEASE-001`). Opaque globally unique snapshot identity distinguishes exact committed publications across refreshes and daemon restarts (`INV-SNAPSHOT-IDENTITY-001`); canonical analysis/spec/source/rule identity now flows through every committed snapshot and analyze receipt (`INV-ANALYZE-FINGERPRINT-001`, `INV-ANALYZE-RULE-REFRESH-001`), same-base/same-policy in-flight incremental requests share one candidate through `INV-ANALYZE-COALESCE-001`, and persistent cancellation prevents an abandoned last waiter from publishing (`INV-MCP-REQUEST-CANCEL-001`). CI runs the C# build + test suite on both `ubuntu-latest` and `windows-latest`, alongside the TypeScript adapter, Python adapter, and dogfood jobs. Latest released packages remain on [NuGet](https://www.nuget.org/packages/Lifeblood); this local v0.7.13-alpha worktree is not published.

<!-- portCount: 33 --><!-- testCount: 1647 --><!-- toolCount: 41 --><!-- invariantCount: 216 --><!-- invariantCategoryCount: 162 --><!-- skippableFactCount: 53 --><!-- selfAnalyzeSymbols: 7318 --><!-- selfAnalyzeEdges: 38463 --><!-- selfAnalyzeModules: 12 --><!-- selfAnalyzeTypes: 780 --><!-- staticTablesDefaultMaxRows: 32 --><!-- staticTablesDefaultMaxTables: 64 -->

The current in-tree changed-set, profile-query, and diagnostic-ownership atoms
are verified by 1,647 test cases (1,636 pass, 11 native-clang environment
skips) and 216 typed invariants
across 162 categories. Isolated DAWG dogfood published Editor+Player generation
1 / snapshot `snap_57788656fb494cd2b7ef23e5a705198c` with 89,373 symbols,
348,862 edges, 101 modules, 4,395 files, and zero configured violations. One
pinned `filePaths` request then checked 10 current C# changes across five owning
modules with 10 successes, zero diagnostics, and no stale refresh. This receipt
used the newly built private stdio server so the installed 0.70 shared daemon
and other live DAWG clients were not restarted.

Shared protocol v2 keeps one persistent connection and lease per proxy, retains
the daemon-owned latest base after the last disconnect unless an operator opts
into idle eviction, drains on that explicit policy or an admitted maintenance
request, and reports the live host through `lifeblood_capabilities.sharedService`
(`INV-MCP-CLIENT-LEASE-001`, `INV-MCP-IDLE-DRAIN-001`,
`INV-MCP-SHARED-STATUS-001`).

Unity package source visibility is now machine-readable on the existing analyze
and compile-check surfaces. `lifeblood_analyze` / `GraphSession.Load` project
`packageSourceVisibility` from the Roslyn adapter's descriptor/membership
receipt; default summary mode returns aggregate counts plus only excluded/unbound
package rows, with no per-file inventories even on direct/shared incremental
session calls, while explicit detail projection returns every package row with
bounded file previews. `lifeblood_compile_check(filePath)` projects
`packageSourceResolution` for recognized package files from the same receipt.
Define-profile applicability uses the same server-edge projection rule:
default summary mode returns profile/module counts without the per-module
ledger, while `profileApplicabilityMode:"detail"` returns the exact module
membership/exclusion ledger.
The package visibility descriptors/source inventory also participate in the C#
adapter source fingerprint, so incremental refreshes cannot change this receipt
under an unchanged analysis identity. No separate MCP tool or connector-owned
package scanner exists (`INV-UNITY-PACKAGE-SOURCE-001`).

Occurrence-level semantic evidence now crosses one neutral
`IOperationFactProvider` boundary. The primary define profile scans the one
retained immutable compilation set; another committed profile verifies source,
external/source-generator, and resolved NuGet DLL path sets/content against the
snapshot identity, compiles only the requested scope sequentially from
snapshot-owned metadata references, and releases it immediately. Receipts name
execution mode, admission-time identity verification, compiled modules,
truncation/cancellation, and `AdditionalSemanticBaseCount = 0`; operation facts
do not add graph edges or change architecture counts
(`INV-OPERATION-FACTS-001`).

The multi-profile MCP seam is now explicitly ratcheted and DAWG-verified.
Against shared generation 7 / snapshot
`snap_39365c47b1114268a8c23e4ebbc2345f`, a pinned Player contract audit
verified snapshot inputs, compiled one 106-file module ephemerally, observed
53,924 operations, and completed without changing the generation or snapshot.
The shared session remained `semanticBaseCount:1` and
`additionalSemanticBaseCount:0` (`LB-INTAKE-20260629-003`).

Schema-versioned consumer manifests now project operation guards, external API
cost policy, exact value-domain/conversion policy, non-finite handling, raw
numeric-literal provenance, consumer-toleranced near-equal groups, exact
cadence-boundary shapes, and reasoned suppressions through one stateless
`ContractAuditEngine`. The engine derives a single target-filtered fact query,
fans that stream across selected rule families, counts the complete result, and
retains only bounded deterministic findings/evidence. It neither adds graph
edge kinds nor retains another compilation (`INV-CONTRACT-AUDIT-001`).
Value-domain names and symbol bindings remain consumer data; the shared C# fact
record contributes only exact value origins, nested operators, and typed
constant occurrences (literal/named/default/folded plus finite/NaN/infinity).
Comparison predicates carry exact left/right value provenance and source spans;
the manifest, not Lifeblood, owns accepted boundary sides/operators/offsets.
`lifeblood_contract_audit` exposes this engine as one typed, summary-first
observation tool with bounded inline/workspace manifest input. The Unity bridge
publishes every wrapped tool's discoverable parameter surface from the same
server input-contract authority instead of empty or hand-picked `args:{}`
schemas; snapshot-read preconditions are inherited once, analyze keeps only the
current-project path injection special case, and contract audit keeps only the
`manifestJson` to `manifest` translation (`INV-MCP-UNITY-BRIDGE-001`).

## Components

| Component | State |
|-----------|-------|
| Lifeblood.Domain | Immutable graph model, GraphBuilder (with file-level edge derivation + multi-parent partial-type Contains synthesis), GraphValidator, Evidence, ConfidenceLevel, short-name index, ResponseEnvelope + EnvelopeClassification (truth-envelope contract). **PathClassification/PathBucketClassifier** (Domain-layer SSoT for Production / Test / Editor / Generated / Vendored path bucketing, `INV-PATHBUCKET-SHARED-001`) plus **PathGlobMatcher** (shared POSIX full-path glob grammar for `dead_code pathExclude` and `analyze excludePaths`). **Graph/SymbolPropertyKeys** (pin string-key contract on `Properties["attributes"]` / `Properties["baseType"]` / `Properties["baseTypeChain"]` / `Properties["classification"]` / `Properties["fieldType"]` / `Properties["constantValue"]` / `Properties["referenceClosure"]` between writer + every consumer). **Results/AnalysisResult** + **Results/CompilationResults** + **Results/AuthorityCoverageResults** + **Results/AsmdefBoundaryResults** (typed return shapes for compilation-host and graph-only tool surfaces — `EnumCoverageReport`, `DiagnosticsReport`, `TestImpactReport`, `AuthorityCoverageReport`, `AsmdefBoundaryReport`). |
| Lifeblood.Application | **33 port interfaces**. `WorkspaceSnapshot` is the immutable, language-neutral publication for graph, analysis, capability, workspace context, timestamp, generation, opaque `SnapshotId`, source-control evidence, and an all-or-none semantic-port triplet; reference-counted leases defer port disposal after retirement, and `WorkspaceSession` advances or clears the state through one reference exchange. `WorkspaceSnapshotCatalog` owns hard-bounded graph-only history, pin/name/LRU/age policy, and lease-safe eviction without retaining semantic ports. `IWorkspaceInputFingerprintProvider` is the adapter-owned preflight seam for content-authoritative source/descriptor identity; `ISourceControlSnapshotProvider` is the single source-control evidence seam over the neutral Domain receipt. Ports also include `IWorkspaceAnalyzer`, `ICompilationHost`, `IOperationFactProvider`, `IRuntimeAssemblyResolver` (Unity DLL probe), `ISymbolResolver`, `IUserInputCanonicalizer`, `IResponseDecorator` (truth envelope), `ISemanticSearchProvider`, `IDeadCodeAnalyzer`, `IUnityReachabilityProvider` (runtime-dispatch reachability), `IAuthorityReporter` (type-level authority report), `IPartialViewBuilder`, `Invariants.IInvariantProvider`, `IUsageProbe` + `IUsageCapture`, `IFileSystem`, `IBlastRadiusProvider`, `IRuleProvider`, `IProgressSink`, plus the left-side workspace ports. Typed left-side records keep incremental modes/fallback reasons transport-neutral. Every read-side handler routes through `ISymbolResolver` before graph/workspace lookup and through `IResponseDecorator` after producing its raw result. |
| Lifeblood.Adapters.CSharp | Roslyn workspace analyzer with streaming compilation and downgrading. Incremental re-analyze (mtime-prefilter + source content hash, per-module, csproj-edit aware, asmdef-edit aware via `INV-UNITY-002`, analysis-scope excludePath aware via `INV-ANALYZE-EXCLUDEPATHS-001`, editor-supplied `authoritativeChangedFiles` narrowing via `INV-ANALYZE-AUTHORITATIVE-CHANGESET-001`). Cross-assembly edge extraction, HintPath DLL loading. Per-module BCL ownership (HostProvided or ModuleProvided) decided at discovery. `CanonicalSymbolFormat` owns the symbol ID grammar. Per-module `AllowUnsafeCode` + `ImplicitUsings` parsed from csproj. Target framework and framework source-generator analyzer paths are discovered once on `ModuleInfo`; `SourceGeneratorRunner` executes source generators before extraction/diagnostics behind a serialized analyzer/generator gate (`INV-SOURCEGEN-SERIAL-001`), and `SyntaxTreePathIdentity` keeps relative generator hints out of ambient process-CWD path resolution while qualifying them by module (`INV-SOURCEGEN-PATH-PROVENANCE-001`). `RoslynSemanticView` publishes typed read-only state plus sandbox helpers (`Help` / `SymbolsOfKind(string)` / `EdgesOfKind(string)`). `SnippetWrapper` auto-wraps bare compile_check snippets for library modules. `CsprojPaths` normalizes path-shaped attributes for cross-platform parsing. `UnityReachabilityAdapter` recognizes Unity entrypoint attributes + MonoBehaviour magic methods through resolved base chains and UnityEvent persistent calls through prefab/scene/asset YAML. `UnityAssemblyResolver` probes Library/ScriptAssemblies + Library/Bee/artifacts + Library/PackageCache for execute-time DLL injection. Extractor records `Properties["attributes"]`, `Properties["baseType"]`, `Properties["baseTypeChain"]`, `Properties["classification"]` (forwarder shape), `Properties["fieldType"]`, and const-field `Properties["constantValue"]` on every relevant symbol. |
| Lifeblood.Adapters.Git | Infrastructure adapter for `ISourceControlSnapshotProvider`. Resolves the caller-selected repository root, commit/short hash, latest reachable stable tag, nullable dirty state, exact-or-capped dirty count, bounded sample, and bounded classified failure detail. Server-side release-gate projection adds capture timing, provenance role, server version channel, and stable/dirty status without moving Git process logic out of the adapter seam. Git process/environment/timeout/output mechanics stay outside Domain, Application, connectors, and MCP handlers (`INV-SOURCE-CONTROL-001`). |
| Lifeblood.Adapters.JsonGraph | Import and export with full metadata round-trip. |
| Lifeblood.Connectors.ContextPack | Context pack with GraphSummary, instruction file, reading order. |
| Lifeblood.Connectors.Mcp | Graph provider with blast radius delegation and file-level impact. `LifebloodSymbolResolver` is the reference `ISymbolResolver` with a deterministic primary file path picker for partial types, kind correction, and wrong-namespace short-name fallback. `LifebloodResponseDecorator` is the reference `IResponseDecorator`; classification table is injected from the host's tool registry at construction so registry and decorator cannot drift. `LifebloodAuthorityReporter` produces the authority report from a single graph walk. `LifebloodSemanticSearchProvider` tokenizes multi-word queries into ranked-OR scoring. `LifebloodDeadCodeAnalyzer` consults `IUnityReachabilityProvider` when injected. `LifebloodPartialViewBuilder`. `LifebloodInvariantProvider` walks well-known repo conventions dynamically (`<root>/CLAUDE.md` + `<root>/AGENTS.md` + any `<root>/docs/invariants/**.md`) via `IFileSystem`, parses each through `ClaudeMdInvariantParser` (five authoring shapes A/B/C/D/E), aggregates results across all sources with per-id duplicate detection, and caches per-file in `InvariantParseCache<T>`. `McpProtocolSpec` is the single source of truth for JSON-RPC wire constants. |
| Lifeblood.Analysis | Coupling, blast radius, CircularDependencyDetector (Tarjan SCC + taxonomy classification per `INV-CYCLE-TAXONOMY-001`), TierClassifier (with semantic test-fixture detection via `Properties["attributes"]`, not filename sniffing), TestImpactAnalyzer (`lifeblood_test_impact` BFS over incoming edges with `INV-TEST-IMPACT-001`-pinned attribute set), AuthorityCoverageAnalyzer (`lifeblood_authority_coverage` outgoing-edge authority reachability matrix), AsmdefBoundaryAnalyzer (`lifeblood_asmdef_check` DirectOnly module boundary audit), rule validation. |
| Lifeblood.Server.Mcp | MCP server with **41 tools** over stdio (23/18 legacy compatibility projection). Every tool declares one default `ToolBehavior` covering session requirement, externally meaningful effect, and session access; mixed-action tools may add a registry-owned per-call resolver so `lifeblood_snapshots action:"list"` is read-gated while catalog mutations stay exclusive. Availability, prerequisite rejection, gate routing, snapshot-read eligibility, batch policy, and capability metadata derive from that registry contract. Bidirectional Roslyn. `GraphSession` publishes one immutable host state containing the Application snapshot plus the matching Roslyn adapter, rules, and exclude scope; request-scoped leases keep every property on one generation while publication continues. `RoslynSemanticView` is constructed once per successful candidate after rule analysis and shared by reference across consumers. Optional shared transport (`--shared` / `LIFEBLOOD_SHARED_SESSION=1`) makes per-client stdio processes proxy to one canonical-workspace-keyed named-pipe daemon, so multiple agents share one retained session and newest analyze generation. A typed protocol/version/module-MVID/workspace handshake authenticates reuse, shared-host analyze requests are admitted only for the bound workspace, and every successful tool envelope identifies the exact committed `SnapshotId`, generation, and canonical analysis descriptor. `lifeblood_batch` prevalidates and serially executes up to 32 observe/shared-read calls under one leased publication. `lifeblood_snapshots` lists default-three, hard-sixteen graph-only history through the shared-read lane, mutates it through exclusive pin/unpin/evict actions, and exact `snapshotId` selection never falls through to latest. Shared protocol v2 keeps one handshaken pipe/lease per proxy; the host owns request activity, last-client idle drain, maintenance blocker policy, and live `lifeblood_capabilities.sharedService` status without moving lifecycle state into Domain/Application. `ToolDefinition.EnvelopeClassification` is the registry-side source of truth for the truth envelope. McpDispatcher owns the wire protocol without independently retaining `GraphSession`; `tools/list` reads live availability through `ToolHandler` under the session gate. Program.cs is a thin transport selector. `McpJsonRequestParser` performs strict duplicate-property rejection when `LIFEBLOOD_JSON_COMPAT=strict`, deserializes `JsonRpcRequest` through source-generated metadata, and leaves dynamic response serialization on the existing reflection path for anonymous/object payloads; `LIFEBLOOD_STRICT_JSON` remains the backward-compatible strict alias (`INV-MCP-STRICT-JSON-001`). `ToolInputContractCatalog` + `ToolArgumentBinder` enforce the server-edge argument contract, while `ToolRequestBinder` provides typed high-risk request binding for analyze, compile-check, and contract audit. `DotNetDiagnosticsTelemetrySink` is opt-in via `LIFEBLOOD_TELEMETRY` and records tool result, `lifeblood.tool.arguments`, response JSON cost, analyze fallback, `lifeblood.analyze.phase`, truncation, and cache lookup events. |
| Lifeblood.Server.Mcp contract/telemetry layer | `ToolInputContractCatalog` owns the typed MCP input-contract source for every registered tool (argument name, JSON type, required flag, enum values, description). `ToolDefinition.InputSchema` is generated from that typed source for `tools/list` and schema snapshots; `ToolRegistry` no longer authors anonymous schema objects. `ToolArgumentBinder` enforces contracts under `LIFEBLOOD_JSON_COMPAT=legacy|warn|strict` without moving MCP field names into Domain/Application. `ToolRequestBinder` binds `lifeblood_analyze`, `lifeblood_compile_check`, and `lifeblood_contract_audit` through typed records, with handler-owned compatibility defaults preserved at the MCP edge. `GraphSessionGate` serializes writers while tool dispatch and state-derived `tools/list` acquire non-blocking leases through the registry-resolved effective access path; readers no longer hold a lock across candidate construction. `GraphSession` emits `lifeblood.analyze.phase` telemetry at real phase boundaries with allocation deltas. |
| Lifeblood.CLI | analyze, context, export with centralized validation. |
| adapters/typescript | Standalone TS compiler API adapter. Self-analyzing. |
| adapters/python | Standalone ast-based adapter. Zero dependencies. Self-analyzing. |
| adapters/native-clang | Standalone C extractor built on libclang (beta, v0.7.7). Reads `compile_commands.json`, emits Lifeblood-shape `graph.json` through the same `JsonGraphImporter` boundary the other external adapters use. Surfaces translation units, functions, globals, fields, type shells, enum members, macros, includes, callback-table rows and cells, and per-module, per-file, per-symbol pressure metrics. Partial-parse tolerant. Pinned by `NativeClangAdapterContractTests` and `NativeClangExecutableRatchetTests` over nine C fixture families (`tiny-c`, `direct-refs-c`, `multi-tu-c`, `cross-tu-c`, `callback-table-c`, `profile-c`, `partial-parse-c`, `warning-c`, `return-type-c`). FFmpeg scout workflow at `adapters/native-clang/tools/ffmpeg-scout/` documents the libclang reconnaissance path. First 5-file slice produced 9264 symbols, 1067 methods, 14494 imported edges, 0 architecture violations, 1 likely real cycle in libswscale. LLVM, Clang, and CMake stay outside `Lifeblood.Domain`, `Lifeblood.Application`, `Lifeblood.Analysis`, and every connector. See `docs/NATIVE_CLANG.md` for the capability page. |
| Unity bridge | Canonical UPM outer adapter under `unity/`. Coplay-discoverable typed nested parameters are ratcheted against the server `ToolInputContractCatalog`, including inherited snapshot-read preconditions for observe/shared-read tools and the full analyze/compile-check/contract-audit option surface. One poll coordinator and the installed `lifeblood-mcp --shared --shared-key <UnityRoot>` proxy converge Unity and direct agents on one daemon-owned base; Lifeblood does not author per-tool max-poll metadata or kill a healthy proxy after a fixed tool-call wall clock. Wire constants mirror `McpProtocolSpec` with a byte-equal ratchet. |
| Lifeblood.Tests | 1,647 discovered test cases: 1,636 pass and 11 native-clang environment gates skip when their executable precondition is absent. Coverage includes bounded changed-set compile checks, diff-scoped diagnostic ownership and stale-diagnostic fail-closed behavior, stale-refresh target-diagnostic truth, retained and ephemeral secondary-profile contract audits, the neutral operation-fact boundary and bounded contract-audit surface (including near-equal literal grouping and exact cadence-boundary shapes), recoverable Roslyn emit fallback, source-control root/failure/bounds receipts, latest-tag release metadata, atomic publication/read leases, snapshot preconditions, exact historical selection, bounded graph-only retention, forced-refresh read batches, canonical analysis identity/envelope projection, rule-only semantic sharing, exact cold/full and same-base incremental process coalescing, duplicate-preflight read prevention, persistent request cancellation, canonical accepted-change receipts, client leases, idle/maintenance drain, live shared status, waiter-aware cancellation/failure isolation, input-drift rejection, DevMemory authority, multi-profile applicability/scope safety, unsupported relationship receipts, Unity bridge schema/no-fixed-deadline/action-only-poll/conflict/shared-launch contracts, deterministic parallel extraction, registry routing, all-tools dispatch, process transport, architecture, documentation, packaging, and adapter contract ratchets. |

## Rule Packs

The 2026-05-31 .NET adoption slice is pinned by `ToolArgumentContractTests`, `GraphSessionGateTests`, expanded `ToolHandlerTelemetryTests`, `McpJsonRequestParserTests` strict-parser and source-generated request-context coverage, `CompileCheckParseOptionsParityTests` Runtime Async fixtures, `CsprojCompilationFactsTests` synthetic-tree Runtime Async parity plus framework source-generator parity, `MultiProfileAnalyzeTests` compilation-fact preservation coverage, `BenchmarkSmokeTests` (including the JSON parser benchmark harness ratchet), `DotNetLaneScriptTests` (including .NET 10 and Runtime Async benchmark-lane report ratchets), and the existing invariant-cache telemetry tests.

Built-in architecture rule packs:
- [hexagonal](../packs/hexagonal/rules.json)
- [clean-architecture](../packs/clean-architecture/rules.json)
- [lifeblood](../packs/lifeblood/rules.json) (self-validating)

## Known Limitations

**`lifeblood_dead_code` accuracy.** Eight extractor false-positive classes have been closed (five in v0.6.4: interface dispatch, member access granularity, null-conditional property, lambda context, method-group references; three in v0.6.5: ctor `Calls` edge, field-initializer containing method, property accessor body). Plus the root-cause compilation gap (missing implicit global usings). The Unity reachability port (`INV-UNITY-001`) closes the MonoBehaviour magic-method false-positive class on Unity workspaces, including metadata-only intermediate framework bases through the resolved `baseTypeChain` fact. **`LB-FP-003`** extends the Unity reflection roster with `[SettingsProvider]`, `[SettingsProviderGroup]`, `[Shortcut]`, `[OnOpenAsset]`, `[BurstCompile]`, `[MonoPInvokeCallback]`, the full NUnit fixture lifecycle, plus type-via-child propagation (a type is reachable if any directly-contained member carries an entrypoint attribute — closes the `[SettingsProvider]`-on-static-method-with-dead-host-type FP class). Lifeblood self-analysis: 3 type findings (all `Program` composition roots — runtime entry points). the dogfood pass on a 87-module Unity workspace: 1,095 findings ? 729 post-P3 (-33%), then 6 ? 4 type-level findings post-`LB-FP-003` (XRaySettingsProvider + MpServiceResets cleared). Remaining advisory candidates are structural (UI Toolkit `VisualElement` subclasses, audio callbacks on non-MonoBehaviour bases, reflection-based dispatch via `Type.GetType` + `MethodInfo.Invoke`). See `INV-DEADCODE-001` + `INV-UNITY-001`.

**Call-graph completeness.** Eight extractor gaps closed across v0.6.4 + v0.6.5 raised edge count substantially. `find_references`, `dependants`, `blast_radius`, and `file_impact` all benefit.

**Truth envelope.** Every read-side response ships an `envelope` field with truth tier / confidence / staleness / limitations. Errors deliberately do NOT carry envelopes. Per-tool classification lives on `ToolDefinition.EnvelopeClassification` in the registry (`INV-ENVELOPE-001`).

## Self-Analysis

```
$ lifeblood analyze --project .
Symbols: 7,318
Edges:   38,463
Modules: 12
Types:   780

-- usage (representative; exact numbers on every lifeblood_analyze response) --
  Wall time : ~7-15 s
  CPU total : ~10-25 s
  CPU utilization : ~120-180% of one core
  Peak working set : ~430 MB (MCP retained, full analyze)
  GC collections : low single digits
------------------------------------------------------------------------------
(0 violations; 1 existing cycle in the analysis summary.)
```

Lifeblood also audits its own invariants tree via `lifeblood_invariant_check`. The provider walks `<root>/CLAUDE.md`, `<root>/AGENTS.md` (none today), and every `*.md` under `<root>/docs/invariants/`:

```
> lifeblood_invariant_check { mode: "audit" }

totalCount    : 214
categories    : 160  (live category roster authoritative in `docs/invariants/INDEX.md`; sample no longer enumerated here so the body does not drift on every new invariant)
duplicates    : 0
parseWarnings : 0
sourcePaths   : [
  "D:/Projekti/Lifeblood/CLAUDE.md",
  "D:/Projekti/Lifeblood/docs/invariants/architecture.md",
  "D:/Projekti/Lifeblood/docs/invariants/csharp-adapter.md",
  "D:/Projekti/Lifeblood/docs/invariants/governance.md",
  "D:/Projekti/Lifeblood/docs/invariants/INDEX.md",
  "D:/Projekti/Lifeblood/docs/invariants/mcp-protocol.md",
  "D:/Projekti/Lifeblood/docs/invariants/pipeline.md",
  "D:/Projekti/Lifeblood/docs/invariants/resolver.md",
  "D:/Projekti/Lifeblood/docs/invariants/tools.md",
  "D:/Projekti/Lifeblood/docs/invariants/usage.md"
]
```

## Production Verification (Real-World Unity Workspace)

Tested on a real 90-module Unity workspace (~400k+ LOC). Same workspace, two different call sites, two different memory profiles. Both are correct. Both are by design.

### MCP path (compilations retained for write-side tools)

```
> lifeblood_analyze projectPath="/path/to/your/project"

mode : full
requestedMode : full
summary.symbols : 62,134
summary.edges   : 219,548
summary.modules : 90
cycles  : 123 SCCs
```

Edge count grew +18% over the prior 180,814 baseline because enum-member references the dangling-edge filter was silently dropping (`R2-3`) now resolve as first-class graph edges (`INV-EXTRACT-ENUMMEMBER-001`).

Authority + classification + dead-code numbers from the real-world dogfood pass:
- Methods classified by body shape (representative pre-wave snapshot): 18,985.
- `PureForwarder` count: 3,367 (direct host-type / dispatcher / partial-class extraction triage signal — the original dogfood case was an ABG partial-class wave; the same metric drives any host-with-many-subordinates split decision).
- Dead-code findings (with Unity reachability injected): 729 (down from 1,095 pre-P3, -33%); 4 type-level findings post-`LB-FP-003` (down from 6 — XRaySettingsProvider + MpServiceResets cleared).
- MonoBehaviour magic-method FPs: 13 (down from 378 pre-P3, -97%).
- Invariant tree discovery: 121 invariants across 81 categories aggregated from CLAUDE.md + AGENTS.md + `docs/invariants/**.md`, 0 parse warnings.

### CLI path (streaming, compilations released)

CLI mode uses streaming compilation and releases each `CSharpCompilation` after extraction (`Emit` to PE metadata reference). Peak working set stays well below the MCP profile because only one full Roslyn `Compilation` is held at a time. Use the CLI when you need a one-shot analyze or a graph export; use the MCP path when you need interactive write-side tools (`execute`, `find_references`, `rename`, ...).

### Why the memory profiles differ

The CLI takes one shot at the workspace, extracts the graph, and streams each compilation out via `Emit` to a lightweight PE metadata reference. Compilations are released after extraction. Peak working set stays moderate because only one full Roslyn `Compilation` is held at a time, and the downgraded references are ~10-100 KB each.

The MCP server retains compilations in memory because the write-side tools (`lifeblood_execute`, `lifeblood_find_references`, `lifeblood_rename`, `lifeblood_diagnose`, `lifeblood_compile_check`, ...) need to query the loaded workspace interactively. No retention, no follow-up queries.

The GC counts confirm this architectural difference. The CLI churns because objects are constantly allocated and released across the streaming pipeline. The MCP server barely collects because its object graph is stable once the workspace is loaded.

**Decision guide for downstream users:**

- Need one-shot analysis, a rules check, or a graph export? Use the CLI path. Sub-1 GB memory budget is enough on most workspaces.
- Need interactive MCP queries (`execute`, `find_references`, ...) after the analyze? Use the MCP server. Budget for the larger profile.
- Memory-constrained MCP session? Pass `readOnly: true` to `lifeblood_analyze` and the server falls back to the CLI streaming profile. Write-side tools are unavailable under `readOnly`; to recover write-side state, run a full retained analyze or retry a `compilationStateUnavailable` incremental rejection with `allowFullFallback:true`.

Measured on AMD Ryzen 9 5950X (16 cores / 32 threads). Both blocks come from the native `usage` field on every `lifeblood_analyze` response, the CLI block to stderr and the MCP block inside the `tools/call` result JSON. No external measurement wrapper.

Multiple dogfood sessions found 50+ real bugs. Examples: security bypasses, silent data loss, off-by-one boundaries, resource leaks, missing AST node types, memory architecture, BCL double-load, display-string match across the source/metadata boundary, partial-type last-write-wins, `INV-CANONICAL-001` (transitive dependency closure), `INV-RESOLVER-005` (wrong-namespace short-name fallback), `INV-RESOLVER-006` (kind correction), `INV-TOOLREG-001` (wire/internal split that unblocked MCP reconnect), `INV-DEADCODE-001` (extractor gap classes fixed across v0.6.4 + v0.6.5), `INV-UNITY-001` (Unity reachability port), `INV-ENVELOPE-001` (truth envelope), `INV-AUTHORITY-001` + `INV-FORWARDER-001` (authority report + forwarder classifier), `INV-EXECUTE-001` (Unity DLL probe + host-profile execute + sandbox introspection), `LB-FR-021` (cycles summarize/pagination), `LB-BUG-017` + `LB-BUG-018` + `LB-FR-023` (invariant parser shapes C/D/E + dynamic source discovery), `LB-BUG-019` (compile_check file-mode tree replacement), `LB-FR-022` (context smart-dynamic shaping), `LB-FP-003` (dead_code Unity Editor reflection roster + type-via-child propagation). Every fix carries a regression test.

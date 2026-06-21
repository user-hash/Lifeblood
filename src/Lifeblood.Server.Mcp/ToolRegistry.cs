using Lifeblood.Domain.Results;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// Declares all MCP tools with their schemas.
/// Separate from dispatch logic for clarity.
/// </summary>
public static class ToolRegistry
{
  // Envelope-classification presets. Single source of truth for the
  // truth-tier / confidence each tool ships under (INV-ENVELOPE-001).
  // Every read-side tool registration sets one of these on its
  // EnvelopeClassification property; the response decorator reads
  // them straight off ToolRegistry so registry and decorator cannot
  // drift. Adding a new tier is a one-line entry here.
  private static readonly EnvelopeClassification SemanticProven = new()
  {
    TruthTier = TruthTier.Semantic,
    Confidence = ConfidenceBand.Proven,
    EvidenceSource = "Semantic",
  };

  private static readonly EnvelopeClassification DerivedProven = new()
  {
    TruthTier = TruthTier.Derived,
    Confidence = ConfidenceBand.Proven,
    EvidenceSource = "Semantic",
  };

  private static readonly EnvelopeClassification DerivedInferred = new()
  {
    TruthTier = TruthTier.Derived,
    Confidence = ConfidenceBand.Proven,
    EvidenceSource = "Inferred",
  };

  private static readonly EnvelopeClassification HeuristicAdvisorySearch = new()
  {
    TruthTier = TruthTier.Heuristic,
    Confidence = ConfidenceBand.Advisory,
    EvidenceSource = "Heuristic",
  };

  private static readonly EnvelopeClassification HeuristicAdvisoryDeadCode = new()
  {
    TruthTier = TruthTier.Heuristic,
    Confidence = ConfidenceBand.Advisory,
    EvidenceSource = "Heuristic",
    Limitations = new[]
    {
      "Dead-code is reachability-only — runtime entry points (Program.Main), Unity reflection-based dispatch ([RuntimeInitializeOnLoadMethod], MonoBehaviour magic methods, UnityEvent YAML bindings) are not visible to static analysis and may surface as false positives.",
    },
  };

  // S6 / INV-ADVISORY-LIMITATIONS-001. Tools whose graph-walk is exact
  // against the loaded source set but whose verdict can be undermined by
  // Unity-runtime dispatch invisible to static analysis (UnityEvent YAML
  // bindings, serialized-reference fields on prefabs/scenes/SOs,
  // AnimationEvent callbacks, SendMessage / Invoke string-named
  // dispatch, IL2CPP-reflection-stripping link.xml exemptions).
  // Distinct from HeuristicAdvisoryDeadCode: those tools are honestly
  // heuristic; these tools are graph-proven but the graph cannot model
  // every runtime path. Naming the gap on the wire is the honest move.
  private static readonly EnvelopeClassification DerivedProvenWithUnityRuntimeRisk = new()
  {
    TruthTier = TruthTier.Derived,
    Confidence = ConfidenceBand.Proven,
    EvidenceSource = "Semantic",
    Limitations = new[]
    {
      "Unity runtime dispatch is invisible to static analysis: UnityEvent YAML bindings, prefab/scene serialized-field references, ScriptableObject-bound delegates, AnimationEvent callbacks, SendMessage / Invoke string-named dispatch, and reflection-driven entry points can keep symbols live at runtime even when the graph shows zero incoming edges. On Unity workspaces treat 'dead-looking' results as candidates to verify, not proof.",
    },
  };

  private static readonly EnvelopeClassification SemanticProvenWithUnityRuntimeRisk = new()
  {
    TruthTier = TruthTier.Semantic,
    Confidence = ConfidenceBand.Proven,
    EvidenceSource = "Semantic",
    Limitations = new[]
    {
      "Unity runtime production is invisible to static analysis: serialized-enum fields on prefabs / scenes / ScriptableObjects, Inspector-bound enum values, UnityEvent YAML payloads, and Resources/Addressables-loaded asset values can produce an enum-member at runtime even when this report shows zero source-text production. 'isUnproduced' on Unity workspaces is a candidate signal, not proof.",
    },
  };

  // wire_audit read/write classification is operation-exact against the loaded
  // source set, but the "unplugged" verdict can be undermined by assignment paths
  // static analysis cannot see. Semantic/Proven tier + the wire-specific gap named
  // on the wire. INV-WIRE-AUDIT-001 / INV-ADVISORY-LIMITATIONS-001.
  private static readonly EnvelopeClassification SemanticProvenWithWireRisk = new()
  {
    TruthTier = TruthTier.Semantic,
    Confidence = ConfidenceBand.Proven,
    EvidenceSource = "Semantic",
    Limitations = new[]
    {
      "Assignment paths invisible to static analysis can wire a member this report calls 'unplugged': reflection (FieldInfo/PropertyInfo.SetValue), Unity serialized injection ([SerializeField] / prefab-scene-ScriptableObject YAML / UnityEvent persistent calls), and runtime-procedural assignment. A 'read without write' / 'never assigned' finding is a candidate to verify against those sources, not proof of a bug.",
    },
  };

  private static readonly EnvelopeClassification DerivedProvenWithTestDiscoveryRisk = new()
  {
    TruthTier = TruthTier.Derived,
    Confidence = ConfidenceBand.Proven,
    EvidenceSource = "Semantic",
    Limitations = new[]
    {
      "Reflection-driven test discovery is invisible to static analysis: NUnit [TestCaseSource] static factories, custom test attributes, [Theory] data providers, dynamic fixture generation via TestFixtureSource, and Unity PlayMode runtime-injected test cases may produce test executions whose dependence on the target this report does not predict.",
    },
  };

  /// <summary>
  /// Returns the wire-format tool list for the MCP <c>tools/list</c>
  /// response. When <paramref name="hasCompilationState"/> is false,
  /// write-side tool descriptions are prefixed with
  /// "[Unavailable. Load a project with lifeblood_analyze first]" so
  /// agents know not to call them before loading a project. The returned
  /// array is a pure <see cref="McpToolInfo"/> wire DTO — no availability
  /// metadata leaks onto the wire, and System.Text.Json serializes it
  /// with no required/init interaction quirks.
  ///
  /// <para>
  /// INV-TOOLREG-001: classification is by the typed
  /// <see cref="ToolDefinition.Availability"/> property on the internal
  /// registry record, never by tool name prefix. Adding a new tool
  /// without setting Availability is a compile error because the
  /// property is <c>required</c>.
  /// </para>
  /// </summary>
  public static McpToolInfo[] GetTools(bool hasCompilationState = true)
  {
  var definitions = GetDefinitions();
  var wire = new McpToolInfo[definitions.Length];
  for (var i = 0; i < definitions.Length; i++)
  {
  var def = definitions[i];
  var description = def.Description;
  if (!hasCompilationState && def.Availability == ToolAvailability.WriteSide)
  {
  description = "[Unavailable. Load a project with lifeblood_analyze first] " + description;
  }
  wire[i] = new McpToolInfo
  {
  Name = def.Name,
  Description = description,
  InputSchema = def.InputSchema,
  };
  }
  return wire;
  }

  /// <summary>
  /// Returns the internal tool definitions with their full classification
  /// metadata. This is the test seam for INV-TOOLREG-001 availability
  /// checks — tests that want to inspect <see cref="ToolDefinition.Availability"/>
  /// must consume this method, never <see cref="GetTools"/>, because
  /// availability is deliberately stripped at the wire boundary.
  /// </summary>
  public static ToolDefinition[] GetDefinitions() => GetAllTools();

  private static ToolDefinition[] GetAllTools() => new ToolDefinition[]
  {
  new()
  {
  Name = "lifeblood_capabilities",
  Availability = ToolAvailability.ReadSide,
  EnvelopeClassification = DerivedProven,
  Description = "Report the live Lifeblood MCP server capabilities: server version and version source, optional git commit/dirty state when running from a repo checkout, MCP tool count with read/write split, feature flags, schema snapshot path, STATUS.md anchor path, and current session state. Use this at session start to detect local-tool/documentation drift before relying on stale prose.",
  },
  new()
  {
  Name = "lifeblood_analyze",
  Availability = ToolAvailability.ReadSide,
  EnvelopeClassification = SemanticProven,
  Description = "Analyze a C# project or JSON graph file. Returns symbol/edge/module counts and violations. Loads the graph into memory for subsequent query tools. Wire shape: `mode` reports what the adapter DID (`full` / `incremental` / `incremental-noop` / `rejected`), `requestedMode` separately reports what the caller ASKED (`full` / `incremental`). When the cheap path could not be honored cleanly the response carries `fallbackReason` (`noPriorAnalysis` / `moduleSetChanged` / `moduleDescriptorChanged`) + a human-readable `fallbackDetail`. By default `incremental:true` REJECTS when drift is detected (caller-owned scope policy, INV-ANALYZE-FALLBACK-001) — the rejection is a normal structured result, not a tool error: it carries `canRetryFull:true` and a `suggestedRetry` block (`{incremental:true, allowFullFallback:true}`) so the agent can retry without out-of-band knowledge. Pass `allowFullFallback:true` up front to opt into silent widening; you'll receive `mode:'full'` + `requestedMode:'incremental'` + the populated `fallbackReason` so the cache miss stays visible. Responses report both `changedSourceFiles` (number of .cs files that re-extracted this round) and `touchedGraphFiles` (how many graph entries were rebuilt) — currently the same value, surface kept stable for future divergence. Unity note: Lifeblood discovers source files through the generated project descriptors (`.csproj` / asmdef membership), not a raw disk sweep. A `.cs` file written to disk that Unity has not yet imported (no `.meta` sibling, not yet in any generated `.csproj`) is in no module, so analyze (full or incremental) will not include it until Unity regenerates the project files — the next analyze then picks it up. `compile_check(filePath)` on such a file returns `fileResolution: NotInAnyCompilation` with a `staleDescriptorHint` naming the regenerate-then-reanalyze step. If Unity later assigns the file a different GUID, the next full analyze refreshes all symbol IDs. INV-UNITY-NEWFILE-DISCOVERY-HONESTY-001.",
  },
  new()
  {
  Name = "lifeblood_context",
  Availability = ToolAvailability.ReadSide,
  EnvelopeClassification = SemanticProven,
  Description = "Generate an AI context pack from the loaded graph. Returns summary, high-value files, boundaries, invariants, hotspots, reading order, and a module dependency matrix. Each section has a smart default cap so the response fits inside conservative tool-result budgets even on multi-module Unity workspaces (typically 80+ modules, 53k+ symbols). Override per-section caps with `maxFiles` / `maxBoundaries` / `maxHotspots` / `maxReadingOrder` / `maxMatrixEntries` (use -1 for unlimited or 0 to drop the section entirely). Pass `summarize:true` to drop every list-section to 0 and return only summary + invariants + violations — the smallest viable shape. Pass `sections:[\"summary\",\"boundaries\"]` to allow-list specific sections; everything not listed is replaced with an empty array. Every clipped section is reported in the response's `truncated` map with its full pre-clip count so callers know what was hidden.",
  },
  new()
  {
  Name = "lifeblood_lookup",
  Availability = ToolAvailability.ReadSide,
  EnvelopeClassification = SemanticProven,
  Description = "Look up a symbol by ID. Returns the symbol's name, kind, file, line, visibility, and properties.",
  },
  new()
  {
  Name = "lifeblood_dependencies",
  Availability = ToolAvailability.ReadSide,
  EnvelopeClassification = SemanticProven,
  Description = "Get all symbols that the given symbol depends on (outgoing non-Contains edges). Each entry in `dependencies[]` carries `otherEndId`, `kind`, an optional `callSite` object (`filePath`, `line`, `column`, `endLine`, `endColumn`, `containingSymbolId`) pointing at the FIRST observed authoring expression for that (source, target, kind) edge — graph-level edge deduplication (`INV-STREAM-005`) collapses multiple authoring expressions from the same source to the same target of the same kind into ONE entry, so callSite is the first-extracted occurrence, not every occurrence — and an optional `profiles[]` array naming the define profiles that observed the edge under multi-profile analyze (null on single-profile back-compat, populated set on multi-profile, `INV-MULTI-DEFINE-EDGE-PROFILES-001`). `callSite` is null for graph-derived edges with no single authoring location (module→module DependsOn, type→type Inherits without a surfaced clause node). One call answers \"where in source does X depend on Y?\" for the first occurrence. Pass `profileFilter:[\"Editor\",\"Player\"]` to narrow results to edges observed under at least one of those profiles (single-profile-null edges pass any filter, back-compat). Note: outgoing edges are recorded at the symbol level where the reference physically appears — `Calls` edges live on the calling method, `References` edges live on the referencing field/property/method body, etc. A query for type-level outbound edges (`type:My.Service`) typically returns 0 because the type itself does not author calls; query its members (or use `lifeblood_blast_radius` to walk the transitive incoming closure) to see real coupling.",
  },
  new()
  {
  Name = "lifeblood_dependants",
  Availability = ToolAvailability.ReadSide,
  EnvelopeClassification = SemanticProven,
  Description = "Get all symbols that depend on the given symbol (incoming non-Contains edges). Each entry in `dependants[]` carries `otherEndId`, `kind`, an optional `callSite` object (`filePath`, `line`, `column`, `endLine`, `endColumn`, `containingSymbolId`) pointing at the FIRST observed authoring expression for that edge — graph-level dedup (`INV-STREAM-005`) collapses repeated expressions from the same source to ONE entry, so callSite is the first-extracted occurrence — and an optional `profiles[]` array naming the define profiles that observed the edge under multi-profile analyze (`INV-MULTI-DEFINE-EDGE-PROFILES-001`). Pass `profileFilter:[\"Editor\",\"Player\"]` to narrow results to edges observed under at least one of those profiles (single-profile-null edges pass any filter, back-compat). `callSite` is null for graph-derived edges with no single authoring location (module→module DependsOn, type→type Inherits without a surfaced clause node).",
  },
  new()
  {
  Name = "lifeblood_blast_radius",
  Availability = ToolAvailability.ReadSide,
  EnvelopeClassification = DerivedProven,
  Description = "Compute what breaks if a symbol is changed. Transitive BFS over incoming dependency edges. Every response carries `directDependants` (the immediate one-hop count, distinct from the transitive total) so callers can distinguish a symbol with 5 direct callers from one with 5 transitive blast-radius members. Use `summarize:true` to get a compact result that does not embed the full affected-id array — useful when transitive blast on a popular type would otherwise return a multi-megabyte response. `maxResults` caps the embedded array regardless of summarize mode; the `truncated` flag tells callers whether the array was clipped. Use `groupBy:\"bucket\"|\"module\"|\"both\"` to switch the response shape from a flat affected list to grouped buckets (Production/Test/Editor/Generated) and/or per-module/asmdef counts with optional preview entries per group via `previewPerGroup` (default 5).",
  },
  new()
  {
  Name = "lifeblood_file_impact",
  Availability = ToolAvailability.ReadSide,
  EnvelopeClassification = DerivedInferred,
  Description = "Get file-level impact: which files depend on this file and which files this file depends on. Derived from symbol-level edges. Answers 'if I change this file, what other files are affected?' Every response carries `dependsOnCount` + `dependedOnByCount` (full graph magnitudes — stay byte-stable even when arrays clip) alongside the per-direction arrays. Pass `maxResults` to cap each direction's array (default 500; clipped arrays fire `dependsOnTruncated` / `dependedOnByTruncated` flags plus a composite `truncated` bool for one-field checks). Pass `summarize:true` (INV-FILE-IMPACT-SUMMARIZE-001) for the smallest viable wire shape — forces `maxResults=25` regardless of caller-passed value; mirrors the summarize shortcut already shipped on `dead_code`, `cycles`, `blast_radius`, `test_impact`. Useful on god-type primary partial files (high fan-out / high fan-in) where the full enumeration overflows downstream tool-result budgets.",
  },

  // ── Write-side tools (require Roslyn compilation state) ──
  //
  // Visual grouping only. The authoritative classification is the
  // Availability property on each record, not this comment.

  new()
  {
  Name = "lifeblood_execute",
  Availability = ToolAvailability.WriteSide,
  EnvelopeClassification = HeuristicAdvisorySearch,
  Description = "Execute C# code against the loaded workspace. Code runs in-process (trusted local sandbox — blocklist + AST security checks, not process-isolated). Returns output, errors, and return value. Requires prior lifeblood_analyze with projectPath. The script's globals carry `Graph`, `Compilations`, `ModuleDependencies` plus the `Help` introspection string — see `RoslynSemanticView`. When the workspace is Unity-shaped (Library/ exists at the project root), Unity build artifacts under Library/ScriptAssemblies, Library/Bee/artifacts and Library/PackageCache are auto-injected as references so scripts can touch UnityEngine types; non-managed PEs, runtime BCL/contract assemblies, duplicate assembly identities, and retained workspace assemblies are filtered before Roslyn sees the probe set. If no build artifacts are found a `runtimeAssemblyWarnings` entry tells the caller to run a Unity build first. `targetProfile` is a compatibility hint only: `host` is the execution profile, and non-host values run against the host scripting BCL with a `targetRuntimeWarnings` limitation. Boundary: scripts COMPILE against workspace/engine types (injected as Roslyn metadata references) but those assemblies are not loaded into the analysis host runtime, so instantiation, `Unsafe.SizeOf<T>`, and reflection over workspace types are unsupported — they surface a structured compile-against-not-run `targetRuntimeWarnings` boundary, not a value. Use the `Graph`/`Compilations` symbol globals for workspace-type facts; runtime values needing the workspace assembly loaded must come from the engine's own runtime.",
  },
  new()
  {
  Name = "lifeblood_diagnose",
  Availability = ToolAvailability.WriteSide,
  EnvelopeClassification = SemanticProven,
  Description = "Get compilation diagnostics (errors, warnings) for the loaded project. Without filters, returns the full project's diagnostics. Pass `filePath` (relative or absolute) to scope diagnostics to one source file — useful when you want to verify a single file you just edited without drowning in a 300k-line project dump. Pass `moduleName` to scope to a single module. `filePath` and `moduleName` may be combined (file scope wins; module is used to disambiguate which compilation contains the file when the same path appears in multiple modules). Every response carries `definesActive` (the preprocessor symbols Lifeblood bound this scope under) plus `resolvedModule` (the module the scope resolved to; null for project-wide) — distinguishes Editor-only findings from release-build risk without re-running under a different define set. Also carries `possiblyStale: bool` — diagnose has no auto-refresh path (compile_check has it via MaybeRefreshIfStale), so when the requested scope has any source file whose on-disk mtime is newer than the graph's analyze timestamp the flag fires true. Scope-aware: file scope checks just that file, module scope walks files parented to that module, project scope walks every tracked File symbol. Pass `verbosity:\"compact\"` to drop the full `definesActive[]` list (kept as `definesActiveCount`) for repeated focused checks. INV-DIAGNOSTIC-ENVELOPE-DEFINES-001 / INV-DIAGNOSE-FRESHNESS-002 / INV-DIAGNOSTIC-ENVELOPE-VERBOSITY-001 / LB-INBOX-008.",
  },
  new()
  {
  Name = "lifeblood_compile_check",
  Availability = ToolAvailability.WriteSide,
  EnvelopeClassification = SemanticProven,
  Description = "Check if a C# code snippet compiles in the project context. Returns success/failure with diagnostics. Does not execute the code. Pass either `code` (inline source) or `filePath` (relative or absolute path; the file is read off disk) — exactly one is required. Auto-refreshes the workspace if any tracked file has been edited since the last analyze (opt out via `staleRefresh:false`). Every response carries `definesActive` (the preprocessor symbols Lifeblood bound the snippet/file under) plus `resolvedModule` (the module the check resolved to) so a caller can tell Editor-only findings apart from release-build risk without re-running. File-mode also carries `fileResolution` (Resolved / NotInModule / NotInAnyCompilation / NoTreeToCompile) and, when a path that exists on disk resolves to no loaded compilation, a `staleDescriptorHint` naming the stale-project-descriptor case. Pass `verbosity:\"compact\"` to drop the full `definesActive[]` list (kept as `definesActiveCount`). INV-DIAGNOSTIC-ENVELOPE-DEFINES-001 / INV-COMPILE-CHECK-FILE-RESOLUTION-001 / INV-DIAGNOSTIC-ENVELOPE-VERBOSITY-001 / LB-INBOX-008.",
  },
  new()
  {
  Name = "lifeblood_resolve_member",
  // Type-scoped counterpart to lifeblood_resolve_short_name: scope to
  // a single containing type instead of flattening every member matching
  // a global short name. INV-RESOLVE-MEMBER-001.
  Availability = ToolAvailability.ReadSide,
  EnvelopeClassification = SemanticProven,
  Description = "Resolve a member by short name on a specific containing type, with optional overload disambiguation by parameter signature. typeName accepts canonical 'type:NS.T', fully-qualified 'NS.T', or bare short name 'T'; bare names dispatch through the short-name index and return AmbiguousContainingType when more than one type carries that name. memberName matches Method / Property / Field / Event members on the resolved type. paramTypes (optional array) filters method overloads by parameter signature — pass the comma-joined param list as separate array elements. Returns outcome (Unique / MultipleMatches / NotFound / TypeNotFound / AmbiguousContainingType), the resolved type id, every matching member with kind+file+line+paramDisplay, and ambiguous-type candidates when the short name was not unique. Use this instead of resolve_short_name when you know the containing type and want one specific member.",
  },
  new()
  {
  Name = "lifeblood_resolve_short_name",
  // ReadSide: consults SemanticGraph.GraphIndexes.FindByShortName (the
  // graph-level short-name bucket), not the Roslyn compilation host.
  // Resolves the long-standing classification ambiguity where this
  // tool sat under the write-side comment divider but none of the
  // prefix guards matched its name.
  Availability = ToolAvailability.ReadSide,
  EnvelopeClassification = SemanticProven,
  Description = "Resolve a bare short name (e.g., 'MidiLearnManager') to its canonical symbol ID(s). Returns every matching symbol with its canonical id, file path, and kind. Use `mode` to control matching: 'exact' (default) is literal, 'contains' is substring, 'fuzzy' is a ranked near-match score. Zero-result responses automatically include ranked suggestions so you never hit a dead end.",
  },
  new()
  {
  Name = "lifeblood_dead_code",
  Availability = ToolAvailability.ReadSide,
  EnvelopeClassification = HeuristicAdvisoryDeadCode,
  Description = "[EXPERIMENTAL — ADVISORY ONLY] Scan the loaded graph for symbols with no incoming semantic references — dead code CANDIDATES. Defaults: excludes public-visibility symbols (assumed reachable from outside) and test files. Returns canonical ids, kinds, file:line locations, and a short reason per hit. Every finding additionally carries `directDependants` (incoming non-Contains edge count; 0 for classic findings but forward-compatible signal for future relaxed criteria), `bucket` (one of `Production` / `Test` / `Editor` / `Generated` — path-prefix classification mirroring `blast_radius groupBy=bucket`), and `declarationOnly` (true for abstract/interface members — deleting one is a public-contract change). Every response carries `count`, `kindBreakdown` (per-symbol-kind histogram), `bucketBreakdown` (per-bucket histogram so a caller can fold the Editor/Generated tail in one pass), and `truncated`. Use `includeKinds` to narrow (e.g. ['Method']). Use `summarize:true` for a compact response with `preview[]` instead of the full `findings[]` — useful on large workspaces (53k+ symbols → 286KB+ payload) which would otherwise overflow downstream tool-result limits. `maxResults` caps the embedded array regardless of mode (default 500, or 25 in summarize). **Closed false-positive classes (INV-DEADCODE-001):** v0.6.4 closed five extractor classes (interface dispatch, member-access granularity, null-conditional property, lambda context, method-group references) plus the implicit-global-usings compilation gap. v0.6.5 closed three more (constructor `Calls` edge, field-initializer containing method, property-accessor body context). v0.6.7 closed the MonoBehaviour magic-method class on Unity workspaces via the `IUnityReachabilityProvider` port (`INV-UNITY-001`). v0.7.0 (`LB-FP-003`) closed the Editor reflection-attribute class (`[SettingsProvider]`, `[Shortcut]`, `[OnOpenAsset]`, `[BurstCompile]`, `[MonoPInvokeCallback]`, full NUnit fixture lifecycle) plus type-via-child propagation. **Remaining advisory limitations:** runtime entry points (Program.Main); reflection-based dispatch invisible to static analysis (Type.GetType + MethodInfo.Invoke, Unity SendMessage); UI Toolkit VisualElement subclasses with magic-named methods on non-MonoBehaviour bases; private fields read via same-class access when the enclosing type has no external references. Treat every finding as a candidate to verify with `lifeblood_find_references` before acting. Triage fields per INV-DEADCODE-TRIAGE-001.",
  },
  new()
  {
  Name = "lifeblood_partial_view",
  Availability = ToolAvailability.ReadSide,
  EnvelopeClassification = SemanticProven,
  Description = "Return the combined source of every partial declaration of a type. Takes a type symbol id, walks the incoming Contains edges from File symbols to discover every partial file, reads each file via IFileSystem, and emits both per-segment source and a concatenated combined view with file headers.",
  },
  new()
  {
  Name = "lifeblood_invariant_check",
  Availability = ToolAvailability.ReadSide,
  EnvelopeClassification = SemanticProven,
  Description = "Query the architectural invariants declared in the loaded project's invariant tree. The provider walks <root>/CLAUDE.md, <root>/AGENTS.md, and any <root>/docs/invariants/**.md, aggregating across every source. Three modes: (1) pass 'id' to fetch one invariant's full body, title, category, and source line; (2) pass mode='audit' (default) for a summary — total count, per-category breakdown, duplicate-id collisions, parse warnings, and contributing source paths; (3) pass mode='list' for an id/title index across every declared invariant. Requires a prior lifeblood_analyze to establish the project root.",
  },
  new()
  {
  Name = "lifeblood_authority_report",
  Availability = ToolAvailability.ReadSide,
  EnvelopeClassification = DerivedProvenWithUnityRuntimeRisk,
  Description = "Quantify how much architectural authority a type holds. Use for any type that aggregates surface across multiple subordinates: partial-class hosts, dispatchers that route across many row paths (filter family selectors, kernel routers, capability dispatchers), facade types fronting an internal subsystem, ports with many implementations. Returns implementedInterfaceCount (distinct interfaces directly satisfied — Implements edges for class/struct hosts, Inherits edges for interface hosts that extend other interfaces), ownedPublicSurface (public method/property/field/event count, nested types excluded), per-implemented-interface breakdown carrying direct + inherited member surface (directMemberCount, inheritedMemberCount, aggregateMemberCount, memberCount alias, inheritedInterfaces[], isCompositeInterface) plus distinct consumers reaching the interface OR any of its members across the inheritance closure via Calls/References, a forwarderRatio (PureForwarder methods / total methods, in [0.0,1.0]; -1.0 sentinel when classification data is missing), AND planning-verdict evidence fields: `crossAssemblyConsumerCount` (distinct other modules with incoming edges into the type or its members — boundary-contract evidence), `sameAssemblyConsumerCount` (distinct same-module consumer symbols — adapter-shim evidence), `hasSingleImplementer` (true / false for interface targets with exactly one source-defined implementer; null for non-interface targets — adapter-shim candidate when paired with high cross-assembly use). Composite-facade interfaces (ABG-style) report their inherited contract's load-bearing surface in the same row instead of looking empty. Triage heuristics: many interfaces + low ownedPublicSurface + high forwarderRatio = split candidate; concentrated public surface + low forwarderRatio = doing real work; high crossAssembly + balanced surface = boundary contract; zero crossAssembly + high sameAssembly + hasSingleImplementer = AdapterShimOnly candidate. Caller composes verdict client-side per INV-AUTHORITY-PLANNING-COMPOSITION-001 — Lifeblood ships evidence, not verdicts. Pairs naturally with lifeblood_port_health when one of the implemented interfaces looks vestigial.",
  },
  new()
  {
  Name = "lifeblood_port_health",
  Availability = ToolAvailability.ReadSide,
  EnvelopeClassification = DerivedProvenWithUnityRuntimeRisk,
  Description = "Score how 'alive' the members of an interface or class are. Walks direct Contains edges PLUS the transitive interface-inheritance closure (composite facades — an interface that extends sub-interfaces — aggregate the inherited contract's surface, so an ABG-style empty-looking facade no longer mislabels as 'vestigial'). Runs an incoming-edge check on every member across the aggregate set and reports memberCount (aggregate, back-compat alias) + directMemberCount + inheritedMemberCount + aggregateMemberCount + inheritedInterfaces[] + isCompositeInterface, alongside liveMembers (>=1 incoming non-Contains edge OR outgoing Implements), deadMembers, livenessPct, and a verdict — 'healthy' (>=75% live), 'mixed', or 'vestigial' (<25% live). Use to spot ports/types that are mostly dead surface; composite facades are now triage-able from one call.",
  },
  new()
  {
  Name = "lifeblood_cycles",
  Availability = ToolAvailability.ReadSide,
  EnvelopeClassification = DerivedProven,
  Description = "List all dependency cycles in the loaded graph. Each entry is a strongly-connected component of size >= 2 — symbol IDs in cycle order. Computed from the same Tarjan SCC pass that already runs in AnalysisPipeline; this tool just exposes the result without re-running analysis. Every response carries `count`, `totalSymbolCount`, `largestCycleSize`, `bucketBreakdown` (per-bucket counts), and a `truncated` flag, plus `descriptors[]` — `{ symbols, bucket }` per cycle classified as `GeneratedOrStaticAnalysisArtifact` (build artifact / source-generator output, never a refactor target), `PartialClassCluster` (every member resolves to the same enclosing Type — intra-type mutual-recursion / partial-class cluster), or `LikelyRealLoop` (cross-type / cross-module architectural loop, the actual backlog). Legacy `cycles[][]` shape stays alongside `descriptors[]` for back-compat. Use `summarize:true` to get a compact result with `preview[]` + `previewClassified[]` instead of the full arrays — useful on large workspaces (100+ SCCs ≈ 70KB) which would otherwise overflow downstream tool-result limits. `maxResults` caps the embedded array regardless of mode. INV-CYCLE-TAXONOMY-001 / LB-TRACK-20260514-008.",
  },
  new()
  {
  Name = "lifeblood_test_impact",
  Availability = ToolAvailability.ReadSide,
  EnvelopeClassification = DerivedProvenWithTestDiscoveryRisk,
  Description = "Which test classes transitively depend on a target symbol or file. BFS over incoming non-Contains edges with per-symbol distance tracking; classifies each affected symbol as test-vs-non-test via the extractor-recorded method-level attributes set (`[Test]`, `[TestCase]`, `[TestCaseSource]`, `[Theory]`, `[UnityTest]`, `[Fact]`). Lifecycle attributes (`[SetUp]`, `[OneTimeSetUp]`, `[TearDown]`) are excluded — they participate in test execution but are not the assertion-bearing methods. Test methods are folded by containing type; per-class `minDistance` is the smallest hop count to any of its affected methods, mapped to `confidence` Direct (1) / OneHop (2) / Transitive (3+). Response carries `target`, `targetKind` (Symbol or File), `totalTestMethodCount`, `directTestClassCount`, `affectedTestClassCount`, `affectedTestClasses[]` sorted by ascending distance then by qualified name, plus `recommendedFilters[]` — pre-composed `FullyQualifiedName~<class>` strings the caller pastes into `dotnet test --filter` without composing the filter syntax themselves. Each affected-class row carries `kind`: `Semantic` (BFS hit) or `ReflectionHeuristic` (Wave-3 source-text hit). Top-level `semanticEdgeHits` + `reflectionHeuristicHits` totals make the source-of-truth visible per-call. Optional `includeReflectionHeuristic: true` enables a post-BFS source-text scan for ratchet / reflection tests that reach the target via `typeof(T)` / `nameof(T)` / `Type.GetType(\"FQN\")` / qualified-name string literals — pattern matches the test method's containing file for the target's FQN (always) and bare short name (only when the file also contains the target's namespace OR the short name is globally unique). Source-text approximation surfaces in `limitations[]`. Disambiguation: a `target` value starting with a canonical-id prefix (`type:` / `method:` / `field:` / `property:` / `mod:` / `file:` / `ns:` / `namespace:`) routes through the symbol resolver; otherwise treated as a file path and every symbol declared in that file becomes a multi-source BFS start. The reflection heuristic only applies to symbol targets. INV-TEST-IMPACT-001 + INV-TEST-IMPACT-REFLECTION-001..003.",
  },
  new()
  {
  Name = "lifeblood_search",
  Availability = ToolAvailability.ReadSide,
  EnvelopeClassification = HeuristicAdvisorySearch,
  Description = "Ranked keyword search across symbol names, qualified names, and persisted xml-documentation summaries. Use when you need to find a symbol by WHAT IT DOES, not by what it's NAMED — e.g., search 'canonicalize' and get back every symbol whose xmldoc mentions canonicalization even when none of them are literally called 'Canonicalize'. Returns ranked matches with canonical ids, file paths, lines, scores, short context snippets, and a structurally-typed `matchKind` per hit (`name` / `qualifiedName` / `xmlDoc` / `multiple`) so callers can filter by signal source without parsing the snippet text. Distinct from lifeblood_resolve_short_name (which only searches the short-name index): this tool also mines the xmldoc corpus.",
  },
  new()
  {
  Name = "lifeblood_find_references",
  Availability = ToolAvailability.WriteSide,
  EnvelopeClassification = SemanticProven,
  Description = "Find all references to a symbol across the loaded workspace. Returns file paths, line numbers, and span text. Set includeDeclarations=true to also return the symbol's declaration sites (one entry per partial declaration for partial types). **Profile scope (INV-MULTI-DEFINE-WRITESIDE-001):** searches against the retained (first) profile's compilations only — on a multi-profile snapshot, call sites guarded by preprocessor symbols active under OTHER profiles are NOT in the response. Every response carries `analyzedUnderProfile` + a `limitations[]` entry when the graph is multi-profile. For union-graph reference queries that span profiles use `lifeblood_dependants` / `lifeblood_dependencies` with `profileFilter`. To switch the retained profile, re-analyze with the target profile FIRST in `defineProfiles`. Optional `profileScope` fails loudly when the requested profile is not the retained one.",
  },
  new()
  {
  Name = "lifeblood_find_definition",
  Availability = ToolAvailability.WriteSide,
  EnvelopeClassification = SemanticProven,
  Description = "Find where a symbol is declared. Returns file path, line, column, display name, and documentation. Resolves against the retained profile's compilations (INV-MULTI-DEFINE-WRITESIDE-001); response carries `analyzedUnderProfile` + a `limitations[]` entry when graph is multi-profile.",
  },
  new()
  {
  Name = "lifeblood_find_implementations",
  Availability = ToolAvailability.WriteSide,
  EnvelopeClassification = SemanticProven,
  Description = "Find all types/methods that implement an interface or override a virtual member. Walks the retained profile's compilations only (INV-MULTI-DEFINE-WRITESIDE-001); response carries `analyzedUnderProfile` + a `limitations[]` entry when graph is multi-profile, so implementations gated by other-profile preprocessor symbols (e.g. `#if !UNITY_EDITOR` on a Player-only override) surface as a documented gap, not a silent omission.",
  },
  new()
  {
  Name = "lifeblood_enum_coverage",
  Availability = ToolAvailability.WriteSide,
  EnvelopeClassification = SemanticProvenWithUnityRuntimeRisk,
  Description = "Per-member reference coverage for an enum type. Walks every loaded compilation once, classifies each member reference by parent syntax — `produced` (RHS of assignment, return / yield / arrow body, argument, variable initializer), `consumedComparison` (==, !=, <, <=, >, >=), `consumedSwitch` (case label, is-pattern, constant-pattern, switch-expression arm) — and returns one row per declared member with `totalReferences`, `producedCount`, `consumedComparisonCount`, `consumedSwitchCount`, `dispatchTableReferenceCount` (additive — references inside static-table-shaped initializers, recognised via the same classifier `lifeblood_static_tables` uses so dispatch-table-routed values are triage-able from one row: `producedCount == dispatchTableReferenceCount` means \"only a routing key, never genuinely produced in app code\"), `isUnproduced` (declared + referenced as a consumer but never assigned), `isUnreferenced` (zero references of any kind). Response carries `unproducedCount` + `unreferencedCount` as top-level summaries so the dogfood case (\"how many values in this state-machine enum are never produced?\") reads off one call instead of pairing find_references with manual syntax inspection per hit. `enumTypeId` accepts canonical (`type:NS.T`), fully-qualified (`NS.T`), or bare short name (`T`) — routed through the same resolver as every other type-id-taking tool. INV-ENUM-COVERAGE-001 + INV-ENUM-COVERAGE-DISPATCH-TABLE-001 / LB-TRACK-20260514-003.",
  },
  new()
  {
  Name = "lifeblood_static_tables",
  Availability = ToolAvailability.WriteSide,
  EnvelopeClassification = SemanticProven,
  Description = "Extract static collection-shaped initializers on a type as typed row + cell facts. Walks every `static` field / property whose initializer Roslyn surfaces as `IArrayCreationOperation`, `ICollectionExpressionOperation`, or a single `IObjectCreationOperation`, and reports one `table` entry per matching member with rows in source order. Row constructors carry `constructorId` + a `cells[]` array, where each cell binds a constructor argument to its parameter by name + ordinal and classifies the cell value into one of `Null` / `Bool` / `String` / `Number` / `EnumMember` / `EnumFlags` (e.g. `Bits.A | Bits.B` flattened to `enumFlagMemberIds[]` in authoring order) / `MethodGroup` (delegate-target method id, NOT body content) / `FieldReference` (non-enum static field) / `Array` (nested literal array, recursively classified) / `Computed` (eternal fallback for any shape the classifier doesn't cover yet — caller reads `rawText` for the source span). Literal-array / collection-expression rows carry their classified value on `row.value`; cells stay empty for non-constructor rows. Every value carries `filePath` + `line` + `column` provenance. `argumentKind` mirrors Roslyn's `IArgumentOperation.ArgumentKind` (`Explicit` / `DefaultValue` / `ParamArray`) so a caller can tell author-supplied cells apart from constructor defaults. The extractor is generic — it does not know consumer-domain row shapes; downstream rule-checks join row facts against project-specific contracts. `typeId` accepts canonical (`type:NS.T`), fully-qualified (`NS.T`), or bare short type names. Optional caps: `memberName` narrows to one field/property, `maxRows` (default 32, INV-STATIC-TABLES-DEFAULT-MAXROWS-001) clamps per-table rows, `maxTables` (default 64) clamps per-type tables — both fire the `rowsTruncated` / `tablesTruncated` flags. Pass `summarize:true` (INV-STATIC-TABLES-SUMMARIZE-001) for the smallest viable wire shape — forces maxRows=3 + maxTables=16 regardless of caller-passed caps, useful on dispatch-table god-types where the goal is \"do tables exist + what shape\" not \"give me every row\". INV-EXTRACT-STATIC-TABLES-001.",
  },
  new()
  {
  Name = "lifeblood_assignment_coverage",
  Availability = ToolAvailability.WriteSide,
  EnvelopeClassification = SemanticProven,
  Description = "Per-construction-site slot-coverage for a target type. For each `new TargetType { ... }` or `new TargetType()` + statement-level assignment site, walks the containing method's IOperation tree and reports which of the target's public mutable slot members (default: delegate-typed Func/Action/custom-delegate fields and properties — the Bindings shape) are assigned at that site and which are absent. Per-site `confidence` reflects construction shape: inline object-initializer OR non-aliased single-method statement-level assignment chain before escape is `Proven`; factory-constructed, aliased, or branched MAY-assign sites are `Advisory` with the bumping shape named in `siteLimitations[]` (`FactoryConstruction` / `AliasedLocal` / `BranchedMayAssign` / `PostEscapeAssignment`). Per-slot `status` is `Assigned` / `Absent` / `AssignedNull` (null-literal assignment is distinct from absent so a caller can tell 'forgot to wire' from 'deliberately wired null'). Per-slot `expressionKind` classifies the assignment expression (`Lambda` / `MethodGroup` / `FieldReference` / `PropertyAccess` / `NullLiteral` / `Other`). Operation-tree only — never regex, never syntax-text. `targetTypeId` accepts canonical (`type:NS.T`), fully-qualified, or short name — routed through the same resolver as `lifeblood_static_tables`. Optional flags toggle coverage on public mutable non-delegate fields / properties; `slotName` narrows to one slot; `maxSites` (default 256) clamps result size. INV-ASSIGNMENT-COVERAGE-001..004.",
  },
  new()
  {
  Name = "lifeblood_callsite_arguments",
  Availability = ToolAvailability.WriteSide,
  EnvelopeClassification = SemanticProven,
  Description = "Per-call-site argument facts for a target method or constructor. Walks every loaded compilation's `IInvocationOperation` / `IObjectCreationOperation`, matches the bound callee against the target by canonical id (extension methods matched via their reduced-from definition), and reports for each site the containing symbol, file/line/column, receiver expression, and a per-argument array: bound parameter `name` + `type` + `ordinal`, `supplied` (author-passed) vs omitted (Roslyn-filled `DefaultValue`), `argumentKind` (`Explicit` / `DefaultValue` / `ParamArray`), classified `valueKind` (`Literal` / `NullLiteral` / `Constant` / `FieldReference` / `PropertyReference` / `LocalReference` / `ParameterReference` / `MethodGroup` / `Lambda` / `ObjectCreation` / `Invocation` / `Other`), `isConstant`, and clipped `rawText`. The `parameterSummaries[]` histogram reports `suppliedCount` / `omittedCount` per parameter across ALL discovered sites (computed before `maxSites` truncation), turning 'is this new optional parameter actually adopted?' into a one-call answer — e.g. `lengthSteps omitted by 7/7 call sites`. Default-value arguments are re-sourced to the parameter's own default expression (shared with `lifeblood_static_tables` cell binding) so `rawText` shows the authored default, not the lowered constant. Operation-tree only — never regex. `symbolId` accepts canonical (`method:NS.T.M(P)`), or a short/qualified name routed through the resolver; must resolve to a method or constructor. Optional `moduleScope` restricts to one module, `excludeTests` drops Test-bucket call sites, `maxSites` (default 256) clamps the returned `sites[]` (histogram still counts all). INV-CALLSITE-ARGS-001.",
  },
  new()
  {
  Name = "lifeblood_wire_audit",
  Availability = ToolAvailability.WriteSide,
  EnvelopeClassification = SemanticProvenWithWireRisk,
  Description = "Dead-WIRE audit — members that compile green and are REFERENCED but are structurally unplugged at runtime. The complement of `lifeblood_dead_code` (which finds UN-referenced symbols): this catches the opposite failure, the recurring extraction-severed-wiring bug class. One operation-tree pass over every loaded compilation classifies each field/property reference as read or write (assignment target, ++/--, ref/out arg, or declaration initializer = write; else read) and accumulates per-member counts. Two passes: `FieldReadWithoutWrite` = private/internal mutable field READ at >=1 site with zero write sites (no assignment anywhere, no initializer) — 'forgot to wire it'; `DelegateSlotNeverAssigned` = delegate-typed (Func/Action/custom-delegate) mutable field or property with zero assignment sites — a binding/callback slot that nothing ever fills. Each finding carries memberId, memberKind, memberType, declaringTypeId, file:line, readCount, writeCount, and a reason. Response carries `kindBreakdown` + `findingCount` + `truncated`. ADVISORY (Heuristic envelope): a member wired only through reflection, Unity serialized YAML (UnityEvent / [SerializeField]), or runtime-procedural assignment looks unplugged here — verify before acting. Optional `typeId` / `moduleScope` filter the FINDINGS (read/write counting always scans all compilations); `includeFieldReadWithoutWrite` / `includeDelegateSlots` toggle passes; `maxFindings` (default 200) clamps. INV-WIRE-AUDIT-001.",
  },
  new()
  {
  Name = "lifeblood_symbol_at_position",
  Availability = ToolAvailability.WriteSide,
  EnvelopeClassification = SemanticProven,
  Description = "Resolve what symbol is at a specific source position. Returns symbol ID, name, kind, qualified name, and documentation.",
  },
  new()
  {
  Name = "lifeblood_documentation",
  Availability = ToolAvailability.WriteSide,
  EnvelopeClassification = SemanticProven,
  Description = "Get XML documentation for a symbol. Returns the summary content.",
  },
  new()
  {
  Name = "lifeblood_rename",
  Availability = ToolAvailability.WriteSide,
  EnvelopeClassification = SemanticProven,
  Description = "Rename a symbol across the workspace. Returns text edits (does NOT apply them). The caller decides whether to apply. Computes edits against the retained profile's compilations (INV-MULTI-DEFINE-WRITESIDE-001) — on a multi-profile snapshot, edit sites guarded by other-profile preprocessor symbols are NOT in the returned edit set. Response carries `analyzedUnderProfile` + a `limitations[]` entry when graph is multi-profile so callers see the gap before applying. To rename across both profiles today, re-analyze under each profile separately and union the edit sets.",
  },
  new()
  {
  Name = "lifeblood_format",
  Availability = ToolAvailability.WriteSide,
  EnvelopeClassification = SemanticProven,
  Description = "Format C# code using Roslyn's formatter. Returns the formatted code string.",
  },
  };
}

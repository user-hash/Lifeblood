namespace Lifeblood.Server.Mcp;

/// <summary>
/// Single source of truth for MCP tool input contracts. The registry owns tool
/// identity/availability/descriptions; this catalog owns argument names, JSON
/// types, required flags, enum values, and per-argument descriptions. Schemas
/// exposed through tools/list are generated from these typed contracts.
/// </summary>
public static class ToolInputContractCatalog
{
    private static readonly Lazy<IReadOnlyDictionary<string, ToolInputContract>> Contracts = new(() =>
        Create().ToDictionary(c => c.ToolName, c => c, StringComparer.Ordinal));

    public static IReadOnlyCollection<ToolInputContract> All => Contracts.Value.Values.ToArray();

    public static ToolInputContract Get(string toolName)
    {
        if (Contracts.Value.TryGetValue(toolName, out var contract))
        {
            return contract;
        }

        throw new InvalidOperationException($"No MCP tool input contract is registered for '{toolName}'.");
    }

    private static IEnumerable<ToolInputContract> Create()
    {
        yield return Contract(@"lifeblood_capabilities");

        yield return Contract(@"lifeblood_analyze",
            Arg(@"projectPath", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Path to C# project root (with .sln or .csproj)", enumValues: Array.Empty<string>()),
            Arg(@"graphPath", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Path to a graph.json file (alternative to projectPath)", enumValues: Array.Empty<string>()),
            Arg(@"rulesPath", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional: built-in pack name (hexagonal, clean-architecture, lifeblood) or path to a rules.json file", enumValues: Array.Empty<string>()),
            Arg(@"incremental", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"When true, only recompiles modules with changed files since the last analysis. Much faster for iterative work. If the adapter detects drift it cannot honor cheaply (no prior cache, module set changed, project descriptor edited), the response is REJECTED unless `allowFullFallback` is also true. Caller-owned scope policy. Default: false.", enumValues: Array.Empty<string>()),
            Arg(@"readOnly", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"When true, uses streaming compilation (much lower memory. ~4GB vs ~7GB for large projects). Write-side tools (execute, find-references, rename, etc.) will be unavailable. Use for large projects when you only need read-side tools. Default: false.", enumValues: Array.Empty<string>()),
            Arg(@"allowFullFallback", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Pairs with `incremental:true`. When true, the adapter silently widens to a full re-analyze on detected drift and reports `mode:'full'` + `requestedMode:'incremental'` + the populated `fallbackReason` so the cache miss stays visible. When false (default), the adapter REJECTS with `mode:'rejected'` + `requestedMode:'incremental'` + `fallbackReason` + `canRetryFull:true` + `suggestedRetry` and does no work — caller decides next step. Eternal-repo posture: scope policy is the caller's choice, not the adapter's. INV-ANALYZE-FALLBACK-001.", enumValues: Array.Empty<string>()),
            Arg(@"defineProfiles", ToolArgumentType.Array, required: false, arrayItemType: ToolArgumentType.String, description: @"Optional. Define-profile names to analyze under. Null / empty = single-profile back-compat (default Editor identity, wire shape byte-stable with pre-Wave-6). Non-empty = multi-profile union analyze: the adapter compiles every module once per active profile and unions edges. On Unity workspaces (`Library/` exists) the canonical 2-profile MVP is `[""Editor"", ""Player""]` — Player flips `#if !UNITY_EDITOR` callsites from inactive to active. Unknown profile names throw eagerly. Response summary carries `profileCount` + `activeProfiles` + `perProfileEdgeCounts`; edges in dependants/dependencies responses carry `profiles[]`. INV-MULTI-DEFINE-ANALYZE-001.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_context",
            Arg(@"summarize", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Smallest viable response: drop every list-section to 0, keep summary + invariants + violations. Defaults to false.", enumValues: Array.Empty<string>()),
            Arg(@"sections", ToolArgumentType.Array, required: false, arrayItemType: ToolArgumentType.String, description: @"Optional allowlist of section names to include. Sections not on the list are emitted as empty arrays. Recognised: highValueFiles, boundaries, hotspots, readingOrder, dependencyMatrix. Summary, invariants, and violations are always retained.", enumValues: Array.Empty<string>()),
            Arg(@"maxFiles", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Cap on highValueFiles entries. Default 25. -1 unlimited; 0 drops the section.", enumValues: Array.Empty<string>()),
            Arg(@"maxBoundaries", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Cap on boundaries entries (one per module). Default 50.", enumValues: Array.Empty<string>()),
            Arg(@"maxHotspots", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Cap on hotspots entries. Default 20.", enumValues: Array.Empty<string>()),
            Arg(@"maxReadingOrder", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Cap on readingOrder entries. Default 50.", enumValues: Array.Empty<string>()),
            Arg(@"maxMatrixEntries", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Cap on dependencyMatrix entries (module-to-module edges). Default 100. The full matrix on an 80+-module workspace is ~2600 entries.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_lookup",
            Arg(@"symbolId", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Symbol ID (e.g., type:MyApp.AuthService)", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_dependencies",
            Arg(@"symbolId", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Symbol ID", enumValues: Array.Empty<string>()),
            Arg(@"profileFilter", ToolArgumentType.Array, required: false, arrayItemType: ToolArgumentType.String, description: @"Optional. Narrow results to edges whose `profiles[]` intersect this set. Edges with `profiles=null` (single-profile back-compat) pass every filter. INV-MULTI-DEFINE-WIRE-001.", enumValues: Array.Empty<string>()),
            Arg(@"groupBy", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional grouping mode for the dependency endpoints. 'bucket' = Production/Test/Editor/Generated; 'module' = per-module/asmdef counts; 'both' = both; 'none' (default) = legacy flat shape with no extra keys. INV-EDGE-GROUP-001.", enumValues: new[] { @"none", @"bucket", @"module", @"both" }),
            Arg(@"excludeTests", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Drop edges whose endpoint classifies to the Test bucket (default false). Narrows the flat list AND the grouped view.", enumValues: Array.Empty<string>()),
            Arg(@"excludeGenerated", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Drop edges whose endpoint classifies to the Generated bucket (default false).", enumValues: Array.Empty<string>()),
            Arg(@"includeBuckets", ToolArgumentType.Array, required: false, arrayItemType: ToolArgumentType.String, description: @"Optional allowlist of endpoint buckets to keep (case-insensitive): Production / Test / Editor / Generated. Empty / omitted = all buckets.", enumValues: Array.Empty<string>()),
            Arg(@"previewPerGroup", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Cap on preview endpoint-ids per bucket/module when `groupBy` is set. 0 = counts only. Default: 5.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_dependants",
            Arg(@"symbolId", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Symbol ID", enumValues: Array.Empty<string>()),
            Arg(@"profileFilter", ToolArgumentType.Array, required: false, arrayItemType: ToolArgumentType.String, description: @"Optional. Narrow results to edges whose `profiles[]` intersect this set. Edges with `profiles=null` (single-profile back-compat) pass every filter. INV-MULTI-DEFINE-WIRE-001.", enumValues: Array.Empty<string>()),
            Arg(@"groupBy", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional grouping mode for the dependant call sites. 'bucket' = Production/Test/Editor/Generated (answers 'is this production-live or test-only?'); 'module' = per-module/asmdef counts; 'both' = both; 'none' (default) = legacy flat shape with no extra keys. INV-EDGE-GROUP-001.", enumValues: new[] { @"none", @"bucket", @"module", @"both" }),
            Arg(@"excludeTests", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Drop dependant edges whose source classifies to the Test bucket (default false). Narrows the flat list AND the grouped view — the fast path to 'production-only callers'.", enumValues: Array.Empty<string>()),
            Arg(@"excludeGenerated", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Drop dependant edges whose source classifies to the Generated bucket (default false).", enumValues: Array.Empty<string>()),
            Arg(@"includeBuckets", ToolArgumentType.Array, required: false, arrayItemType: ToolArgumentType.String, description: @"Optional allowlist of caller buckets to keep (case-insensitive): Production / Test / Editor / Generated. Empty / omitted = all buckets.", enumValues: Array.Empty<string>()),
            Arg(@"previewPerGroup", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Cap on preview caller-ids per bucket/module when `groupBy` is set. 0 = counts only. Default: 5.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_blast_radius",
            Arg(@"symbolId", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Symbol ID", enumValues: Array.Empty<string>()),
            Arg(@"maxDepth", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Maximum traversal depth (default: 10)", enumValues: Array.Empty<string>()),
            Arg(@"summarize", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"When true, omit the full affected-id array and return only counts + a small preview (size capped by maxResults). Defaults to false. Mutually exclusive with `groupBy` — when `groupBy` is set, the response shape switches to grouped buckets/modules and `summarize` is ignored.", enumValues: Array.Empty<string>()),
            Arg(@"maxResults", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Maximum number of affected-symbol IDs embedded in the response. When the transitive set is larger, the array is clipped and `truncated:true` is set. Default: 500 in normal mode, 25 in summarize mode.", enumValues: Array.Empty<string>()),
            Arg(@"groupBy", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional grouping mode. 'bucket' = Production/Test/Editor/Generated; 'module' = per-module/asmdef counts; 'both' = both groupings populated; 'none' (default) = legacy flat shape. INV-BLAST-RADIUS-GROUP-001.", enumValues: new[] { @"none", @"bucket", @"module", @"both" }),
            Arg(@"previewPerGroup", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Cap on preview entries per bucket/module when `groupBy` is set. 0 = no preview, counts only. Default: 5.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_file_impact",
            Arg(@"filePath", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Relative file path (e.g., src/MyApp/AuthService.cs)", enumValues: Array.Empty<string>()),
            Arg(@"maxResults", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Optional. Cap on entries returned per direction (`dependsOn` and `dependedOnBy` are clipped independently). Default 500 normal mode / 25 summarize mode. Zero / negative values fall back to the default. Ignored when `summarize:true`.", enumValues: Array.Empty<string>()),
            Arg(@"summarize", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Optional. When true, forces `maxResults=25` regardless of caller-passed value — smallest viable wire shape for triage workflows on god-type files. Defaults to false. INV-FILE-IMPACT-SUMMARIZE-001.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_execute",
            Arg(@"code", ToolArgumentType.String, required: true, arrayItemType: null, description: @"C# code to compile and execute", enumValues: Array.Empty<string>()),
            Arg(@"imports", ToolArgumentType.Array, required: false, arrayItemType: ToolArgumentType.String, description: @"Additional using namespaces", enumValues: Array.Empty<string>()),
            Arg(@"timeoutMs", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Execution timeout in milliseconds (default: 5000)", enumValues: Array.Empty<string>()),
            Arg(@"targetProfile", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Compatibility hint for runtime profile. 'host' (default) is the only execution profile; non-host values are accepted but run against the host scripting BCL and surface a targetRuntimeWarnings limitation.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_diagnose",
            Arg(@"filePath", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Source file path (relative to the project root, or absolute). When set, diagnostics are scoped to this file's syntax tree.", enumValues: Array.Empty<string>()),
            Arg(@"moduleName", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Specific module to diagnose, or omit for all. When combined with filePath, picks the module that contains the file.", enumValues: Array.Empty<string>()),
            Arg(@"verbosity", ToolArgumentType.String, required: false, arrayItemType: null, description: @"'compact' drops the full definesActive[] list (definesActiveCount is retained) for repeated focused checks. Default (verbose) returns the full list.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_compile_check",
            Arg(@"code", ToolArgumentType.String, required: false, arrayItemType: null, description: @"C# code to compile-check. Mutually exclusive with filePath.", enumValues: Array.Empty<string>()),
            Arg(@"filePath", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Path to a .cs file (relative to project root, or absolute) to read and compile-check. Mutually exclusive with code.", enumValues: Array.Empty<string>()),
            Arg(@"moduleName", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Module context for type resolution", enumValues: Array.Empty<string>()),
            Arg(@"staleRefresh", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"If true (default), incrementally re-analyze the workspace before compile_check when any tracked file has changed on disk since the last analyze. Set false to check against the pinned workspace state.", enumValues: Array.Empty<string>()),
            Arg(@"verbosity", ToolArgumentType.String, required: false, arrayItemType: null, description: @"'compact' drops the full definesActive[] list (definesActiveCount is retained) for repeated focused checks. Default (verbose) returns the full list.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_resolve_member",
            Arg(@"typeName", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Containing type: canonical 'type:NS.T', fully-qualified 'NS.T', or bare short name 'T'.", enumValues: Array.Empty<string>()),
            Arg(@"memberName", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Simple member name (no namespace, no parens).", enumValues: Array.Empty<string>()),
            Arg(@"paramTypes", ToolArgumentType.Array, required: false, arrayItemType: ToolArgumentType.String, description: @"Optional: fully-qualified parameter type names for method overload disambiguation. Each array entry is one parameter. Ignored for non-method members.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_resolve_short_name",
            Arg(@"name", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Short symbol name (no namespace)", enumValues: Array.Empty<string>()),
            Arg(@"mode", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Matching mode: 'exact' (default, literal), 'contains' (substring), or 'fuzzy' (ranked near-match).", enumValues: new[] { @"exact", @"contains", @"fuzzy" })
        );

        yield return Contract(@"lifeblood_dead_code",
            Arg(@"includeKinds", ToolArgumentType.Array, required: false, arrayItemType: ToolArgumentType.String, description: @"Optional symbol-kind filter (e.g. ['Method','Type']). Case-insensitive. Unknown kinds are silently ignored. Default: Method, Type, Property, Field.", enumValues: Array.Empty<string>()),
            Arg(@"excludePublic", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Skip public symbols (default true).", enumValues: Array.Empty<string>()),
            Arg(@"excludeTests", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Skip files matching test conventions — any 'tests/' path segment or *Tests.cs / *Test.cs filename (default true).", enumValues: Array.Empty<string>()),
            Arg(@"summarize", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"When true, omit the full `findings[]` array and return only counts + `kindBreakdown` + a small `preview[]` (size capped by maxResults). Defaults to false.", enumValues: Array.Empty<string>()),
            Arg(@"maxResults", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Maximum number of findings embedded in the response. When the finding set is larger, the array is clipped and `truncated:true` is set. Default: 500 in normal mode, 25 in summarize mode.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_partial_view",
            Arg(@"symbolId", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Canonical symbol id of the type (e.g. 'type:MyApp.MyClass').", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_invariant_check",
            Arg(@"id", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Exact invariant id (e.g. 'INV-CANONICAL-001'). Mutually exclusive with 'mode'.", enumValues: Array.Empty<string>()),
            Arg(@"mode", ToolArgumentType.String, required: false, arrayItemType: null, description: @"'audit' (default) or 'list'. Mutually exclusive with 'id'.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_authority_report",
            Arg(@"symbolId", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Canonical id of a type (e.g. 'type:My.Service').", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_port_health",
            Arg(@"symbolId", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Canonical id of an interface or class type.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_cycles",
            Arg(@"summarize", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"When true, omit the full `cycles[]` array and return only counts + a small `preview[]` (size capped by maxResults). Defaults to false.", enumValues: Array.Empty<string>()),
            Arg(@"maxResults", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Maximum number of cycles embedded in the response. When the SCC set is larger, the array is clipped and `truncated:true` is set. Default: 500 in normal mode, 25 in summarize mode.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_test_impact",
            Arg(@"target", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Symbol id (canonical / qualified / bare short name) OR a file path. Symbol-id routing happens when the value starts with a known id prefix; otherwise it's treated as a file.", enumValues: Array.Empty<string>()),
            Arg(@"includeReflectionHeuristic", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Default false. When true, after the BFS completes, scan each test method's containing file for the target's FQN as a source-text substring (with namespace-context or uniqueness-gated short-name fallback). Hits surface as `kind: ReflectionHeuristic` rows. Symbol targets only — ignored when `target` is a file path.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_search",
            Arg(@"query", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Query text. Matched against symbol names, qualified names, and xmldoc summaries.", enumValues: Array.Empty<string>()),
            Arg(@"kinds", ToolArgumentType.Array, required: false, arrayItemType: ToolArgumentType.String, description: @"Optional symbol-kind filter (e.g. ['Method','Type']). Case-insensitive. Unknown kinds are silently ignored.", enumValues: Array.Empty<string>()),
            Arg(@"limit", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Maximum number of results (default 20).", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_find_references",
            Arg(@"symbolId", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Symbol ID (e.g., type:MyApp.AuthService)", enumValues: Array.Empty<string>()),
            Arg(@"includeDeclarations", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"When true, include the symbol's declaration sites in the result. Default false.", enumValues: Array.Empty<string>()),
            Arg(@"profileScope", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional INV-MULTI-DEFINE-WRITESIDE-001 honesty gate. When set, must equal the retained profile name; mismatched values fail loudly with switch-instructions.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_find_definition",
            Arg(@"symbolId", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Symbol ID (e.g., type:MyApp.AuthService)", enumValues: Array.Empty<string>()),
            Arg(@"profileScope", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional INV-MULTI-DEFINE-WRITESIDE-001 honesty gate. When set, must equal the retained profile name; mismatched values fail loudly.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_find_implementations",
            Arg(@"symbolId", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Interface, abstract class, or virtual method ID", enumValues: Array.Empty<string>()),
            Arg(@"profileScope", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional INV-MULTI-DEFINE-WRITESIDE-001 honesty gate. When set, must equal the retained profile name; mismatched values fail loudly.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_enum_coverage",
            Arg(@"enumTypeId", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Canonical, qualified, or short name of an enum type.", enumValues: Array.Empty<string>()),
            Arg(@"profileScope", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional. Define profile to scope IOperation extraction to. Currently must match the retained profile. INV-MULTI-DEFINE-IOP-001.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_static_tables",
            Arg(@"typeId", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Canonical, qualified, or short name of a type that may carry static table initializers.", enumValues: Array.Empty<string>()),
            Arg(@"memberName", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional. When set, only the matching static field / property is reported.", enumValues: Array.Empty<string>()),
            Arg(@"maxRows", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Optional. Cap on rows extracted per table; defaults to 32. Zero / negative values clamp to the default. Ignored when `summarize:true`.", enumValues: Array.Empty<string>()),
            Arg(@"maxTables", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Optional. Cap on tables extracted per type; defaults to 64. Zero / negative values clamp to the default. Ignored when `summarize:true`.", enumValues: Array.Empty<string>()),
            Arg(@"summarize", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Optional. When true, forces compact caps (maxRows=3, maxTables=16) regardless of caller-passed values — smallest viable wire shape for triage workflows on dispatch-table god-types. Defaults to false. INV-STATIC-TABLES-SUMMARIZE-001.", enumValues: Array.Empty<string>()),
            Arg(@"profileScope", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional. Define profile to scope IOperation extraction to. Currently must match the retained profile (the first one in `defineProfiles` from the most recent analyze) — IOperation-walking tools operate against the retained compilations only. Mismatched values fail with a guidance error. Single-profile analyze: retained profile is the resolver's default (Editor). INV-MULTI-DEFINE-IOP-001.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_assignment_coverage",
            Arg(@"targetTypeId", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Canonical, qualified, or short name of the type whose construction sites are reported.", enumValues: Array.Empty<string>()),
            Arg(@"includeDelegateFields", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Include public mutable Func/Action/custom-delegate-typed fields as slots. Default true (Bindings shape).", enumValues: Array.Empty<string>()),
            Arg(@"includeDelegateProperties", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Include public mutable Func/Action/custom-delegate-typed properties as slots. Default true.", enumValues: Array.Empty<string>()),
            Arg(@"includePublicMutableFields", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Include public mutable non-delegate fields as slots. Default false.", enumValues: Array.Empty<string>()),
            Arg(@"includePublicMutableProperties", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Include public mutable non-delegate properties (settable from outside) as slots. Default false.", enumValues: Array.Empty<string>()),
            Arg(@"slotName", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional. When set, only the matching slot is reported in the per-site slots array.", enumValues: Array.Empty<string>()),
            Arg(@"maxSites", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Optional. Cap on construction sites returned; defaults to 256. Zero / negative values clamp to the default.", enumValues: Array.Empty<string>()),
            Arg(@"profileScope", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional. Define profile to scope IOperation extraction to. Currently must match the retained profile. INV-MULTI-DEFINE-IOP-001.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_symbol_at_position",
            Arg(@"filePath", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Source file path (absolute or relative)", enumValues: Array.Empty<string>()),
            Arg(@"line", ToolArgumentType.Integer, required: true, arrayItemType: null, description: @"Line number (1-based)", enumValues: Array.Empty<string>()),
            Arg(@"column", ToolArgumentType.Integer, required: true, arrayItemType: null, description: @"Column number (1-based)", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_documentation",
            Arg(@"symbolId", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Symbol ID", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_rename",
            Arg(@"symbolId", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Symbol ID to rename", enumValues: Array.Empty<string>()),
            Arg(@"newName", ToolArgumentType.String, required: true, arrayItemType: null, description: @"The new name", enumValues: Array.Empty<string>()),
            Arg(@"profileScope", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional INV-MULTI-DEFINE-WRITESIDE-001 honesty gate. When set, must equal the retained profile name; mismatched values fail loudly.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_format",
            Arg(@"code", ToolArgumentType.String, required: true, arrayItemType: null, description: @"C# code to format", enumValues: Array.Empty<string>())
        );
    }

    private static ToolInputContract Contract(string toolName, params ToolArgumentContract[] arguments)
        => new(toolName, arguments);

    private static ToolArgumentContract Arg(
        string name,
        ToolArgumentType type,
        bool required,
        ToolArgumentType? arrayItemType,
        string? description,
        IReadOnlyList<string> enumValues)
        => new(name, type, required, arrayItemType, description, enumValues);
}

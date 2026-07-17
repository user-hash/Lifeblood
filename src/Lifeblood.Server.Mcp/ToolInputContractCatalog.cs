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
    private static readonly ToolArgumentContract[] SnapshotReadArguments =
    {
        Arg(
            @"snapshotId",
            ToolArgumentType.String,
            required: false,
            arrayItemType: null,
            description: @"Optional exact-publication selection. When present, the tool leases this retained current or graph-only historical snapshot instead of silently reading latest. Historical selections do not retain Roslyn services, so compilation-required tools return their normal unavailable-state result.",
            enumValues: Array.Empty<string>()),
        Arg(
            @"expectedSnapshotId",
            ToolArgumentType.String,
            required: false,
            arrayItemType: null,
            description: @"Optional optimistic-read precondition. The tool runs only when the leased publication has this exact `snap_<32 lowercase hex>` identity; mismatch returns a structured retryable result without silently reading latest.",
            enumValues: Array.Empty<string>()),
        Arg(
            @"expectedAnalysisGeneration",
            ToolArgumentType.Integer,
            required: false,
            arrayItemType: null,
            description: @"Optional optimistic-read precondition. The tool runs only when the leased publication has this process-local generation; mismatch returns expected/actual identity and retry guidance.",
            enumValues: Array.Empty<string>()),
    };
    private static readonly Lazy<IReadOnlyDictionary<string, ToolInputContract>> SnapshotReadContracts = new(() =>
        Contracts.Value.Values.ToDictionary(
            contract => contract.ToolName,
            contract => new ToolInputContract(
                contract.ToolName,
                contract.ArgumentList.Concat(SnapshotReadArguments).ToArray()),
            StringComparer.Ordinal));

    public static IReadOnlyCollection<ToolInputContract> All => Contracts.Value.Values.ToArray();

    public static ToolInputContract Get(string toolName)
    {
        if (Contracts.Value.TryGetValue(toolName, out var contract))
        {
            return contract;
        }

        throw new InvalidOperationException($"No MCP tool input contract is registered for '{toolName}'.");
    }

    public static ToolInputContract Get(string toolName, bool includeSnapshotReadArguments)
    {
        if (!includeSnapshotReadArguments)
            return Get(toolName);
        if (SnapshotReadContracts.Value.TryGetValue(toolName, out var contract))
            return contract;

        throw new InvalidOperationException($"No MCP tool input contract is registered for '{toolName}'.");
    }

    private static IEnumerable<ToolInputContract> Create()
    {
        yield return Contract(@"lifeblood_capabilities");

        yield return Contract(@"lifeblood_batch",
            Arg(@"calls", ToolArgumentType.Array, required: true, arrayItemType: ToolArgumentType.Object, description: @"Ordered read-only query plan. Each item is `{ tool: string, arguments?: object }`. The entire plan holds one snapshot lease, executes serially, rejects non-observation/exclusive/nested-batch tools before any call runs, and is hard-capped at 32 calls.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_snapshots",
            Arg(@"action", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Catalog action. list (default) returns current + retained graph-only history; pin captures/names a graph-only publication; unpin releases retention protection; evict removes an unpinned historical copy.", enumValues: new[] { @"list", @"pin", @"unpin", @"evict" }),
            Arg(@"targetSnapshotId", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Canonical snapshot id targeted by pin, unpin, or evict. Pin may target the current publication or an existing historical entry.", enumValues: Array.Empty<string>()),
            Arg(@"name", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional unique investigation-lane name assigned while pinning. Trimmed, case-insensitively unique, maximum 128 characters.", enumValues: Array.Empty<string>()),
            Arg(@"checkDrift", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"When true on list, recapture live source/descriptor/rule identity once per base key and report current/drifted/unavailable. Default false avoids filesystem hashing.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_evidence_drift",
            Arg(@"baselinePath", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Repository-contained generated evidence Markdown. Relative paths resolve from the analyzed workspace root. Default: docs/code-maps/EVIDENCE.generated.md.", enumValues: Array.Empty<string>()),
            Arg(@"relativeTolerancePercent", ToolArgumentType.Number, required: false, arrayItemType: null, description: @"Relative drift tolerance for volume counts such as symbols, edges, modules, types, files, profile edges, and invariant totals. Default 0.5; finite range 0..100. Safety metrics keep fixed no-increase/zero-required policies.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_analyze",
            Arg(@"projectPath", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Path to C# project root (with .sln or .csproj)", enumValues: Array.Empty<string>()),
            Arg(@"graphPath", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Path to a graph.json file (alternative to projectPath)", enumValues: Array.Empty<string>()),
            Arg(@"rulesPath", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional: built-in pack name (hexagonal, clean-architecture, lifeblood) or path to a rules.json file", enumValues: Array.Empty<string>()),
            Arg(@"incremental", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"When true, only recompiles modules with changed files since the last analysis. Much faster for iterative work. If the adapter detects drift it cannot honor cheaply (no prior cache, module set changed, project descriptor edited, analysis-scope excludePath set changed), the response is REJECTED unless `allowFullFallback` is also true. Caller-owned scope policy. Default: false.", enumValues: Array.Empty<string>()),
            Arg(@"readOnly", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"When true, uses streaming compilation (much lower memory. ~4GB vs ~7GB for large projects). Write-side tools (execute, find-references, rename, etc.) will be unavailable. Use for large projects when you only need read-side tools. Default: false.", enumValues: Array.Empty<string>()),
            Arg(@"allowFullFallback", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Pairs with `incremental:true`. When true, the adapter silently widens to a full re-analyze on detected drift and reports `mode:'full'` + `requestedMode:'incremental'` + the populated `fallbackReason` so the cache miss stays visible. When false (default), the adapter REJECTS with `mode:'rejected'` + `requestedMode:'incremental'` + `fallbackReason` + `canRetryFull:true` + `suggestedRetry` and does no work — caller decides next step. Eternal-repo posture: scope policy is the caller's choice, not the adapter's. INV-ANALYZE-FALLBACK-001.", enumValues: Array.Empty<string>()),
            Arg(@"defineProfiles", ToolArgumentType.Array, required: false, arrayItemType: ToolArgumentType.String, description: @"Optional. Define-profile names to analyze under. Null / empty = single-profile back-compat (default Editor identity, wire shape byte-stable with pre-Wave-6). Non-empty = multi-profile union analyze: the adapter compiles every module once per active profile and unions edges. On Unity workspaces (`Library/` exists) the canonical profile vocabulary is `[""Editor"", ""Player"", ""Standalone""]`: Player flips `#if !UNITY_EDITOR` callsites; Standalone also adds `UNITY_STANDALONE` for platform-neutral desktop guards such as `#if UNITY_STANDALONE && !UNITY_EDITOR`. Unknown profile names throw eagerly. Response summary carries `profileCount` + `activeProfiles` + `perProfileEdgeCounts`; edges in dependants/dependencies responses carry `profiles[]`. INV-MULTI-DEFINE-ANALYZE-001.", enumValues: Array.Empty<string>()),
            Arg(@"excludePaths", ToolArgumentType.Array, required: false, arrayItemType: ToolArgumentType.String, description: @"Optional. Project-relative POSIX path globs to exclude before source files enter Roslyn compilation (for vendored/sample trees such as `*/Examples*/*`, `*/Samples*/*`, or `Packages/*`). Globs are anchored to the full project-relative path; `*` matches any run including `/`, `?` matches one character. Changing this set on an incremental analyze returns `fallbackReason:'analysisScopeChanged'` unless `allowFullFallback:true` is supplied. INV-ANALYZE-EXCLUDEPATHS-001.", enumValues: Array.Empty<string>()),
            Arg(@"authoritativeChangedFiles", ToolArgumentType.Array, required: false, arrayItemType: ToolArgumentType.String, description: @"Optional. For incremental analyze, an editor/build-system-provided set of project-relative or absolute changed source paths. When supplied, Lifeblood bounds the source-file scan to this set while still using content hashes to decide whether listed files actually need graph replacement. Descriptor drift checks still run independently.", enumValues: Array.Empty<string>()),
            Arg(@"changeReceiptMode", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Accepted-change receipt projection. `summary` (default) returns truthful cause/count evidence without paths; `detail` returns one bounded union of normalized project-relative paths with per-path flags for reanalysis, mtime touch, content change, descriptor-forced recompile, and deletion.", enumValues: new[] { @"summary", @"detail" }),
            Arg(@"changeReceiptLimit", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Maximum path records returned when changeReceiptMode is `detail`. Default 50; clamped to 1..200. Counts and evidenceFileCount always describe the complete accepted set.", enumValues: Array.Empty<string>()),
            Arg(@"packageSourceVisibilityMode", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Unity package visibility projection. `summary` (default) returns aggregate counts plus only actionable excluded/unbound package rows, without per-file inventories; `detail` returns all package rows with bounded per-package file previews for investigations that need exact source rows.", enumValues: new[] { @"summary", @"detail" }),
            Arg(@"profileApplicabilityMode", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Define-profile module-applicability projection. `summary` (default) returns profile/module counts without the per-module ledger; `detail` returns the module ledger with project file, UnityProjectType, included/excluded profiles, and exclusion reasons.", enumValues: new[] { @"summary", @"detail" })
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
            Arg(@"groupBy", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional grouping mode for the dependency endpoints. 'bucket' = Production/Test/Editor/Generated/Vendored; 'module' = per-module/asmdef counts; 'both' = both; 'none' (default) = legacy flat shape with no extra keys. Grouped mode is a SUMMARY — it omits the full flat dependencies[] array (the per-group previews are the payload; use filters/includeBuckets for a bounded flat list), staying overflow-safe on hub types. INV-EDGE-GROUP-001.", enumValues: new[] { @"none", @"bucket", @"module", @"both" }),
            Arg(@"excludeTests", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Drop edges whose endpoint classifies to the Test bucket (default false). Narrows the flat list AND the grouped view.", enumValues: Array.Empty<string>()),
            Arg(@"excludeGenerated", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Drop edges whose endpoint classifies to the Generated bucket (default false).", enumValues: Array.Empty<string>()),
            Arg(@"includeBuckets", ToolArgumentType.Array, required: false, arrayItemType: ToolArgumentType.String, description: @"Optional allowlist of endpoint buckets to keep (case-insensitive): Production / Test / Editor / Generated / Vendored. Empty / omitted = all buckets.", enumValues: Array.Empty<string>()),
            Arg(@"previewPerGroup", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Cap on preview endpoint-ids per bucket/module when `groupBy` is set. 0 = counts only. Default: 5.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_dependants",
            Arg(@"symbolId", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Symbol ID", enumValues: Array.Empty<string>()),
            Arg(@"profileFilter", ToolArgumentType.Array, required: false, arrayItemType: ToolArgumentType.String, description: @"Optional. Narrow results to edges whose `profiles[]` intersect this set. Edges with `profiles=null` (single-profile back-compat) pass every filter. INV-MULTI-DEFINE-WIRE-001.", enumValues: Array.Empty<string>()),
            Arg(@"groupBy", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional grouping mode for the dependant call sites. 'bucket' = Production/Test/Editor/Generated/Vendored (answers 'is this production-live or test-only?'); 'module' = per-module/asmdef counts; 'both' = both; 'none' (default) = legacy flat shape with no extra keys. Grouped mode is a SUMMARY — it omits the full flat dependants[] array (the per-group previews are the payload; use filters/includeBuckets for a bounded flat list), staying overflow-safe on hub types. INV-EDGE-GROUP-001.", enumValues: new[] { @"none", @"bucket", @"module", @"both" }),
            Arg(@"excludeTests", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Drop dependant edges whose source classifies to the Test bucket (default false). Narrows the flat list AND the grouped view — the fast path to 'production-only callers'.", enumValues: Array.Empty<string>()),
            Arg(@"excludeGenerated", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Drop dependant edges whose source classifies to the Generated bucket (default false).", enumValues: Array.Empty<string>()),
            Arg(@"includeBuckets", ToolArgumentType.Array, required: false, arrayItemType: ToolArgumentType.String, description: @"Optional allowlist of caller buckets to keep (case-insensitive): Production / Test / Editor / Generated / Vendored. Empty / omitted = all buckets.", enumValues: Array.Empty<string>()),
            Arg(@"previewPerGroup", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Cap on preview caller-ids per bucket/module when `groupBy` is set. 0 = counts only. Default: 5.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_blast_radius",
            Arg(@"symbolId", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Symbol ID", enumValues: Array.Empty<string>()),
            Arg(@"maxDepth", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Maximum traversal depth (default: 10)", enumValues: Array.Empty<string>()),
            Arg(@"summarize", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"When true, omit the full affected-id array and return only counts + a small preview (size capped by maxResults). Defaults to false. Mutually exclusive with `groupBy` — when `groupBy` is set, the response shape switches to grouped buckets/modules and `summarize` is ignored.", enumValues: Array.Empty<string>()),
            Arg(@"maxResults", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Maximum number of affected-symbol IDs embedded in the response. When the transitive set is larger, the array is clipped and `truncated:true` is set. Default: 500 in normal mode, 25 in summarize mode.", enumValues: Array.Empty<string>()),
            Arg(@"groupBy", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional grouping mode. 'bucket' = Production/Test/Editor/Generated/Vendored; 'module' = per-module/asmdef counts; 'both' = both groupings populated; 'none' (default) = legacy flat shape. INV-BLAST-RADIUS-GROUP-001.", enumValues: new[] { @"none", @"bucket", @"module", @"both" }),
            Arg(@"previewPerGroup", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Cap on preview entries per bucket/module when `groupBy` is set. 0 = no preview, counts only. Default: 5.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_file_impact",
            Arg(@"filePath", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Relative file path (e.g., src/MyApp/AuthService.cs)", enumValues: Array.Empty<string>()),
            Arg(@"maxResults", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Optional. Cap on entries returned per direction (`dependsOn` and `dependedOnBy` are clipped independently). Default 500 normal mode / 25 summarize mode. Zero / negative values fall back to the default. Ignored when `summarize:true`.", enumValues: Array.Empty<string>()),
            Arg(@"summarize", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Optional. When true, forces `maxResults=25` regardless of caller-passed value — smallest viable wire shape for triage workflows on god-type files. Defaults to false. INV-FILE-IMPACT-SUMMARIZE-001.", enumValues: Array.Empty<string>()),
            Arg(@"includeUnsupportedRelationships", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Optional. When true, adds an advisory `unsupportedRelationships` receipt separate from semantic impact edges. Currently scans source-file IO literals such as `File.ReadAllText(""Target.cs"")` plus graph-resolved reflection type strings such as `Type.GetType(""Namespace.Target"")`, and documents unsupported families such as Resources.Load paths and Unity serialized asset references. Defaults to false.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_asmdef_check",
            Arg(@"summarize", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Optional. When true, forces the compact violation cap (25) regardless of caller-passed maxResults. Defaults to false.", enumValues: Array.Empty<string>()),
            Arg(@"maxResults", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Maximum number of source-target module violation rows embedded in the response. Default 200 normal mode / 25 summarize mode.", enumValues: Array.Empty<string>()),
            Arg(@"excludeTests", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Drop source edges whose source symbol path classifies to the Test bucket. Default false.", enumValues: Array.Empty<string>()),
            Arg(@"excludeGenerated", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Drop source edges whose source symbol path classifies to the Generated bucket. Default false.", enumValues: Array.Empty<string>())
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
            Arg(@"verbosity", ToolArgumentType.String, required: false, arrayItemType: null, description: @"'compact' drops the full definesActive[] list (definesActiveCount is retained) for repeated focused checks. Default (verbose) returns the full list.", enumValues: Array.Empty<string>()),
            Arg(@"diagnosticOwnershipMode", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional change-ownership grouping: workingTree (HEAD to current index/worktree plus untracked), staged (HEAD to index), sinceCommit (selected commit to current index/worktree), or explicitFiles (caller-declared touched paths without line history).", enumValues: new[] { @"workingTree", @"staged", @"sinceCommit", @"explicitFiles" }),
            Arg(@"sinceCommit", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Commit-ish used only with diagnosticOwnershipMode:'sinceCommit'. The Git adapter resolves it to an exact commit before diffing.", enumValues: Array.Empty<string>()),
            Arg(@"touchedFiles", ToolArgumentType.Array, required: false, arrayItemType: ToolArgumentType.String, description: @"Caller-supplied project-relative or absolute paths used only with diagnosticOwnershipMode:'explicitFiles'. Bounded to 1..128; these paths have no changed-line history, so in-file diagnostics remain unknownOwnership.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_compile_check",
            Arg(@"code", ToolArgumentType.String, required: false, arrayItemType: null, description: @"C# code to compile-check. Mutually exclusive with filePath and filePaths.", enumValues: Array.Empty<string>()),
            Arg(@"filePath", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Path to one .cs file (relative to project root, or absolute) to read and compile-check. Mutually exclusive with code and filePaths.", enumValues: Array.Empty<string>()),
            Arg(@"filePaths", ToolArgumentType.Array, required: false, arrayItemType: ToolArgumentType.String, description: @"Ordered set of 1..32 .cs files to compile-check under one prevalidated request and at most one shared stale refresh. Mutually exclusive with code and filePath. Duplicate resolved paths are rejected; moduleName, when supplied, constrains every file.", enumValues: Array.Empty<string>()),
            Arg(@"moduleName", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Module context for type resolution. In filePaths mode the same explicit module constraint applies to every file; omit it to resolve each owner independently.", enumValues: Array.Empty<string>()),
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
            Arg(@"pathExclude", ToolArgumentType.Array, required: false, arrayItemType: ToolArgumentType.String, description: @"Optional glob patterns matched against each symbol's normalized POSIX file path; any match drops the finding. Folds vendored / sample / third-party roots out of triage (e.g. '*/Examples*/*', '*/Samples*/*', 'Packages/*'). '*' = any run of chars incl. '/'; '?' = one char; case-insensitive; must match the FULL path (use '*' liberally for substring intent). INV-DEADCODE-TRIAGE-003.", enumValues: Array.Empty<string>()),
            Arg(@"summarize", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"When true, omit the full `findings[]` array and return only counts + `kindBreakdown` + a small `preview[]` (size capped by maxResults). Defaults to false.", enumValues: Array.Empty<string>()),
            Arg(@"maxResults", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Maximum number of findings embedded in the response. When the finding set is larger, the array is clipped and `truncated:true` is set. Default: 500 in normal mode, 25 in summarize mode.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_partial_view",
            Arg(@"symbolId", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Canonical symbol id of the type (e.g. 'type:MyApp.MyClass').", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_invariant_check",
            Arg(@"id", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Exact invariant id (e.g. 'INV-CANONICAL-001'). Mutually exclusive with 'mode'.", enumValues: Array.Empty<string>()),
            Arg(@"mode", ToolArgumentType.String, required: false, arrayItemType: null, description: @"'audit' (default) or 'list'. Mutually exclusive with 'id'.", enumValues: Array.Empty<string>()),
            Arg(@"summarize", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Audit mode only. When true, keep totals/categories/duplicates/warnings but emit nonzero source counts exactly once in evidenceReceipt.sourceProjection; the top level carries a reference instead of duplicating the source ledger. Default false preserves the full v1 response.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_authority_report",
            Arg(@"symbolId", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Canonical id of a type (e.g. 'type:My.Service').", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_authority_coverage",
            Arg(@"subjects", ToolArgumentType.Array, required: true, arrayItemType: ToolArgumentType.String, description: @"Subject methods/types/files to audit. Symbols resolve through the standard resolver; path-like unresolved values are treated as file paths. Types/files expand to contained methods before traversal.", enumValues: Array.Empty<string>()),
            Arg(@"requiredAuthority", ToolArgumentType.Array, required: true, arrayItemType: ToolArgumentType.String, description: @"Authority symbols/types/namespaces/files that each subject should reach by outgoing non-Contains graph paths. Authority types/files expand to their contained symbols.", enumValues: Array.Empty<string>()),
            Arg(@"allowedAlternatives", ToolArgumentType.Array, required: false, arrayItemType: ToolArgumentType.String, description: @"Optional authority symbols/types/namespaces/files that are acceptable competing sources of truth. Reaching one is reported as evidence, but does not remove missing required-authority rows.", enumValues: Array.Empty<string>()),
            Arg(@"maxDepth", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Maximum outgoing dependency traversal depth from each expanded subject seed. Default: 6.", enumValues: Array.Empty<string>()),
            Arg(@"excludeTests", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Drop expanded subject seeds whose declaration path classifies to the Test bucket. Default false.", enumValues: Array.Empty<string>()),
            Arg(@"excludeGenerated", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Drop expanded subject seeds whose declaration path classifies to the Generated bucket. Default false.", enumValues: Array.Empty<string>()),
            Arg(@"includeBuckets", ToolArgumentType.Array, required: false, arrayItemType: ToolArgumentType.String, description: @"Optional allowlist of subject seed buckets to keep (case-insensitive): Production / Test / Editor / Generated / Vendored. Empty / omitted = all buckets.", enumValues: Array.Empty<string>())
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

        yield return Contract(@"lifeblood_callsite_arguments",
            Arg(@"symbolId", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Canonical (`method:NS.T.M(P)`), qualified, or short name of the target method or constructor whose call sites are reported.", enumValues: Array.Empty<string>()),
            Arg(@"moduleScope", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional. Restrict discovered call sites to this module/asmdef.", enumValues: Array.Empty<string>()),
            Arg(@"excludeTests", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Drop call sites whose containing symbol is in a Test path bucket. Default false.", enumValues: Array.Empty<string>()),
            Arg(@"maxSites", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Optional. Cap on call sites returned in `sites[]`; defaults to 256. Zero / negative values clamp to the default. The `parameterSummaries[]` histogram still counts every discovered site.", enumValues: Array.Empty<string>()),
            Arg(@"profileScope", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional. Define profile to scope IOperation extraction to. Currently must match the retained profile. INV-MULTI-DEFINE-IOP-001.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_contract_audit",
            Arg(@"manifest", ToolArgumentType.Object, required: false, arrayItemType: null, description: @"Inline schema-versioned contract manifest. Families include bounded callRoutes, operationGuards, externalApiCosts, stateAccesses, valueDomains, and exact operationShapes for multi-input/result/control/branch-arm/constant/uniqueness policy. External-cost and shared-state contracts may select direct/transitive occurrences through callRouteIds; explicit matchAnyTarget and matchAnyMember modes are bounded by operation kind or route-referenced members. Supply exactly one of manifest or manifestPath.", enumValues: Array.Empty<string>()),
            Arg(@"manifestPath", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Path to a contract-manifest JSON file inside the analyzed workspace. Relative paths resolve from the workspace root. Supply exactly one of manifest or manifestPath.", enumValues: Array.Empty<string>()),
            Arg(@"profileScope", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional committed define profile. The primary profile reuses its retained compilation; another profile executes ephemerally after exact input verification.", enumValues: Array.Empty<string>()),
            Arg(@"moduleScope", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional module/asmdef scope. Especially useful for bounded non-primary-profile execution.", enumValues: Array.Empty<string>()),
            Arg(@"filePaths", ToolArgumentType.Array, required: false, arrayItemType: ToolArgumentType.String, description: @"Optional source-file allowlist for the fact stream.", enumValues: Array.Empty<string>()),
            Arg(@"containingSymbolIds", ToolArgumentType.Array, required: false, arrayItemType: ToolArgumentType.String, description: @"Optional containing-symbol allowlist for occurrence evaluation.", enumValues: Array.Empty<string>()),
            Arg(@"includeRuleIds", ToolArgumentType.Array, required: false, arrayItemType: ToolArgumentType.String, description: @"Optional rule-family or exact contract-id allowlist. Unknown selectors fail loudly.", enumValues: Array.Empty<string>()),
            Arg(@"maxFacts", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Maximum emitted operation facts. Default 50000; hard cap 250000.", enumValues: Array.Empty<string>()),
            Arg(@"maxFindings", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Maximum returned findings. Default 200; hard cap 1000. summarize:true further clamps to 25.", enumValues: Array.Empty<string>()),
            Arg(@"maxEvidencePerFinding", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Maximum evidence records per returned finding. Default 8; hard cap 32. summarize:true omits evidence.", enumValues: Array.Empty<string>()),
            Arg(@"summarize", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Summary-first default true: retain at most 25 findings and omit their evidence arrays while preserving complete counts and breakdowns. Pass false for bounded evidence detail.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_wire_audit",
            Arg(@"typeId", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional. Restrict findings to members declared on this type (canonical / qualified / short). Read+write counting still scans every loaded compilation.", enumValues: Array.Empty<string>()),
            Arg(@"moduleScope", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional. Restrict findings to members declared in this module/asmdef.", enumValues: Array.Empty<string>()),
            Arg(@"includeFieldReadWithoutWrite", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Run the field-read-without-write pass (private/internal mutable fields read with zero writes). Default true.", enumValues: Array.Empty<string>()),
            Arg(@"includeDelegateSlots", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Run the delegate-slot-never-assigned pass (Func/Action/custom-delegate fields & properties with zero assignment sites). Default true.", enumValues: Array.Empty<string>()),
            Arg(@"includeEvents", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Run the event pass: events subscribed (+=) but never raised, and events raised but never subscribed. Default true.", enumValues: Array.Empty<string>()),
            Arg(@"includeDegenerateConstantCallSites", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Run the degenerate-call pass: private/internal methods whose every call site passes only compile-time-degenerate args (constant / default / null). Default true.", enumValues: Array.Empty<string>()),
            Arg(@"maxFindings", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Optional. Cap on findings returned; defaults to 200. Zero / negative clamps to the default.", enumValues: Array.Empty<string>()),
            Arg(@"summarize", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"When true, force the compact triage cap (25 findings) regardless of maxFindings; `kindBreakdown` still counts every finding. Default false.", enumValues: Array.Empty<string>()),
            Arg(@"profileScope", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional. Define profile to scope IOperation extraction to. Currently must match the retained profile. INV-MULTI-DEFINE-IOP-001.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_feature_switch_audit",
            Arg(@"typeId", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional. Restrict findings to switches declared on this type (canonical / qualified / short). Read+write counting still scans every loaded compilation.", enumValues: Array.Empty<string>()),
            Arg(@"moduleScope", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional. Restrict findings to switches declared in this module/asmdef.", enumValues: Array.Empty<string>()),
            Arg(@"requireBranchCondition", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Only audit booleans read in >=1 branch condition (the feature-switch shape). Default true; false widens to every boolean field & settable property.", enumValues: Array.Empty<string>()),
            Arg(@"includeProperties", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"Audit settable boolean properties as well as fields. Default true.", enumValues: Array.Empty<string>()),
            Arg(@"maxFindings", ToolArgumentType.Integer, required: false, arrayItemType: null, description: @"Optional. Cap on switches returned; defaults to 200. Zero / negative clamps to the default.", enumValues: Array.Empty<string>()),
            Arg(@"summarize", ToolArgumentType.Boolean, required: false, arrayItemType: null, description: @"When true, return a compact verdict census: cap at 25 switches regardless of maxFindings AND drop each switch's evidence arrays (assignments / branchGatedMembers / mutators) — a widely-read flag carries many, so a count cap alone does not bound size. `verdictBreakdown` still counts every switch; query a specific typeId unsummarized for the evidence. Default false.", enumValues: Array.Empty<string>()),
            Arg(@"profileScope", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional. Define profile to scope IOperation extraction to. Currently must match the retained profile. INV-MULTI-DEFINE-IOP-001.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_member_count",
            Arg(@"typeId", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Canonical (`type:NS.T`), qualified, or short name of the type to count members of.", enumValues: Array.Empty<string>()),
            Arg(@"semantics", ToolArgumentType.String, required: false, arrayItemType: null, description: @"`reflectionDeclared` (default) = bit-exact System.Reflection DeclaredOnly count (compiler-generated/backing filtered, nested excluded, implicit ctor counted); `sourceSymbols` = graph child-symbol count (nested included, synthesized accessors/backing excluded).", enumValues: new[] { @"reflectionDeclared", @"sourceSymbols" }),
            Arg(@"profileScope", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional. Define profile to scope extraction to. Currently must match the retained profile. INV-MULTI-DEFINE-IOP-001.", enumValues: Array.Empty<string>())
        );

        yield return Contract(@"lifeblood_struct_layout",
            Arg(@"typeId", ToolArgumentType.String, required: true, arrayItemType: null, description: @"Canonical (`type:NS.T`), qualified, or short name of the struct to compute layout for.", enumValues: Array.Empty<string>()),
            Arg(@"profileScope", ToolArgumentType.String, required: false, arrayItemType: null, description: @"Optional. Define profile to scope extraction to. Currently must match the retained profile. INV-MULTI-DEFINE-IOP-001.", enumValues: Array.Empty<string>())
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

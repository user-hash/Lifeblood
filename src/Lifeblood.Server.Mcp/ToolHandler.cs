using System.Text.Json;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Application.Ports.Right.Invariants;
using Lifeblood.Application.UseCases;
using Lifeblood.Connectors.ContextPack;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// Dispatches MCP tool calls. Read-side handled inline, write-side delegated to WriteToolHandler.
///
/// Read-side handlers that take a <c>symbolId</c> parameter route the input
/// through <see cref="ISymbolResolver"/> first (Plan v4 Seam #1, INV-RESOLVER-001).
/// The resolver canonicalizes truncated method ids, resolves bare short names,
/// and returns the merged read model for partial types — once, in one place,
/// for every read-side tool.
/// </summary>
public sealed class ToolHandler
{
    private readonly GraphSession _session;
    private readonly IMcpGraphProvider _provider;
    private readonly ISymbolResolver _resolver;
    private readonly ISemanticSearchProvider _search;
    private readonly IDeadCodeAnalyzer _deadCode;
    private readonly IPartialViewBuilder _partialView;
    private readonly IInvariantProvider _invariants;
    private readonly IResponseDecorator _decorator;
    private readonly IAuthorityReporter _authority;
    private readonly IPortHealthAnalyzer _portHealth;
    private readonly WriteToolHandler _write;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        // INV-ENVELOPE-001: human-readable enum names in every read-side
        // response so envelope.truthTier / envelope.confidence ship as
        // "Semantic" / "Proven" instead of integer ordinals. Applies to
        // every tool's payload, not just the envelope.
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public ToolHandler(
        GraphSession session,
        IMcpGraphProvider provider,
        ISymbolResolver resolver,
        ISemanticSearchProvider search,
        IDeadCodeAnalyzer deadCode,
        IPartialViewBuilder partialView,
        IInvariantProvider invariants,
        IResponseDecorator decorator,
        IAuthorityReporter? authority = null,
        IPortHealthAnalyzer? portHealth = null)
    {
        _session = session;
        _provider = provider;
        _resolver = resolver;
        _search = search;
        _deadCode = deadCode;
        _partialView = partialView;
        _invariants = invariants;
        _decorator = decorator;
        _authority = authority ?? new Lifeblood.Connectors.Mcp.LifebloodAuthorityReporter();
        _portHealth = portHealth ?? new Lifeblood.Connectors.Mcp.LifebloodPortHealthAnalyzer();
        _write = new WriteToolHandler(session, JsonOpts, _resolver);
    }

    public McpToolResult Handle(string toolName, JsonElement? arguments)
    {
        try
        {
            return toolName switch
            {
                // Read-side
                "lifeblood_analyze" => HandleAnalyze(arguments),
                "lifeblood_context" => HandleContext(arguments),
                "lifeblood_lookup" => HandleLookup(arguments),
                "lifeblood_dependencies" => HandleDependencies(arguments),
                "lifeblood_dependants" => HandleDependants(arguments),
                "lifeblood_blast_radius" => HandleBlastRadius(arguments),
                "lifeblood_file_impact" => HandleFileImpact(arguments),
                "lifeblood_resolve_short_name" => HandleResolveShortName(arguments),
                "lifeblood_resolve_member" => HandleResolveMember(arguments),
                "lifeblood_search" => HandleSearch(arguments),
                "lifeblood_dead_code" => HandleDeadCode(arguments),
                "lifeblood_partial_view" => HandlePartialView(arguments),
                "lifeblood_invariant_check" => HandleInvariantCheck(arguments),
                "lifeblood_authority_report" => HandleAuthorityReport(arguments),
                "lifeblood_port_health" => HandlePortHealth(arguments),
                "lifeblood_cycles" => HandleCycles(arguments),
                "lifeblood_test_impact" => HandleTestImpact(arguments),
                // Write-side. Wrapped uniformly through WrapWriteSide so
                // every write-side response carries the same envelope shape
                // as the read-side tools. INV-ENVELOPE-001 +
                // INV-ADVISORY-LIMITATIONS-001.
                "lifeblood_execute" => WrapWriteSide("lifeblood_execute", _write.HandleExecute(arguments)),
                "lifeblood_diagnose" => WrapWriteSide("lifeblood_diagnose", _write.HandleDiagnose(arguments)),
                "lifeblood_compile_check" => WrapWriteSide("lifeblood_compile_check", _write.HandleCompileCheck(arguments)),
                "lifeblood_find_references" => WrapWriteSide("lifeblood_find_references", _write.HandleFindReferences(arguments)),
                "lifeblood_find_definition" => WrapWriteSide("lifeblood_find_definition", _write.HandleFindDefinition(arguments)),
                "lifeblood_find_implementations" => WrapWriteSide("lifeblood_find_implementations", _write.HandleFindImplementations(arguments)),
                "lifeblood_enum_coverage" => WrapWriteSide("lifeblood_enum_coverage", _write.HandleEnumCoverage(arguments)),
                "lifeblood_static_tables" => WrapWriteSide("lifeblood_static_tables", _write.HandleStaticTables(arguments)),
                "lifeblood_assignment_coverage" => WrapWriteSide("lifeblood_assignment_coverage", _write.HandleAssignmentCoverage(arguments)),
                "lifeblood_symbol_at_position" => WrapWriteSide("lifeblood_symbol_at_position", _write.HandleGetSymbolAtPosition(arguments)),
                "lifeblood_documentation" => WrapWriteSide("lifeblood_documentation", _write.HandleGetDocumentation(arguments)),
                "lifeblood_rename" => WrapWriteSide("lifeblood_rename", _write.HandleRename(arguments)),
                "lifeblood_format" => WrapWriteSide("lifeblood_format", _write.HandleFormat(arguments)),
                _ => ErrorResult($"Unknown tool: {toolName}"),
            };
        }
        catch (Exception ex)
        {
            return ErrorResult($"Error: {ex.Message}");
        }
    }

    // ── Read-side handlers ──

    private McpToolResult HandleAnalyze(JsonElement? args)
    {
        var projectPath = WriteToolHandler.GetString(args, "projectPath");
        var graphPath = WriteToolHandler.GetString(args, "graphPath");
        var rulesPath = WriteToolHandler.GetString(args, "rulesPath");
        var incremental = WriteToolHandler.GetBool(args, "incremental") ?? false;
        var readOnly = WriteToolHandler.GetBool(args, "readOnly") ?? false;
        // INV-ANALYZE-FALLBACK-001: caller-owned scope policy. Default false
        // = fail-loud rejection when adapter cannot honor incremental cleanly.
        // Caller opts in to silent widening by passing allowFullFallback:true.
        // This handler does NOT auto-retry on rejection — surfacing the
        // signal is the whole point of the typed fallback shape.
        var allowFullFallback = WriteToolHandler.GetBool(args, "allowFullFallback") ?? false;

        var result = _session.Load(projectPath, graphPath, rulesPath, incremental, readOnly, allowFullFallback);
        return TextResult(MergeEnvelopeIntoJson("lifeblood_analyze", result));
    }

    private McpToolResult HandleContext(JsonElement? args)
    {
        if (!_session.IsLoaded)
            return ErrorResult("No graph loaded. Call lifeblood_analyze first.");

        var useCase = new GenerateContextUseCase(new AgentContextGenerator());
        var pack = useCase.Execute(_session.Graph!, _session.Analysis!);

        // Default pack on an 80+-module workspace is ~375KB and overflows
        // downstream tool-result limits, so every list-section carries a
        // smart default cap. Callers can override per-section, pass
        // `summarize:true` for the smallest viable shape (summary +
        // invariants + violations only), or supply an explicit `sections`
        // allowlist to drop anything not requested. The response always
        // carries a `truncated` map naming every clipped section + its
        // full pre-clip count so callers know what was hidden.
        var summarize = WriteToolHandler.GetBool(args, "summarize") ?? false;
        var sections = ReadSectionsArray(args);

        // Per-section caps. -1 means unlimited; 0 means drop. Defaults are
        // sized so the full default response fits inside conservative
        // tool-result budgets even on multi-module Unity workspaces.
        var maxFiles = WriteToolHandler.GetInt(args, "maxFiles") ?? (summarize ? 0 : 25);
        var maxBoundaries = WriteToolHandler.GetInt(args, "maxBoundaries") ?? (summarize ? 0 : 50);
        var maxHotspots = WriteToolHandler.GetInt(args, "maxHotspots") ?? (summarize ? 0 : 20);
        var maxReadingOrder = WriteToolHandler.GetInt(args, "maxReadingOrder") ?? (summarize ? 0 : 50);
        var maxMatrixEntries = WriteToolHandler.GetInt(args, "maxMatrixEntries") ?? (summarize ? 0 : 100);

        var truncated = new Dictionary<string, object>(System.StringComparer.Ordinal);

        var (highValueFiles, filesTrunc) = ApplyCap(pack.HighValueFiles, maxFiles);
        var (boundaries, bndTrunc) = ApplyCap(pack.Boundaries, maxBoundaries);
        var (hotspots, hsTrunc) = ApplyCap(pack.Hotspots, maxHotspots);
        var (readingOrder, roTrunc) = ApplyCap(pack.ReadingOrder, maxReadingOrder);
        var (matrix, mxTrunc) = ApplyCap(pack.DependencyMatrix, maxMatrixEntries);
        if (filesTrunc.HasValue) truncated["highValueFiles"] = new { fullCount = filesTrunc.Value, included = highValueFiles.Length };
        if (bndTrunc.HasValue) truncated["boundaries"] = new { fullCount = bndTrunc.Value, included = boundaries.Length };
        if (hsTrunc.HasValue) truncated["hotspots"] = new { fullCount = hsTrunc.Value, included = hotspots.Length };
        if (roTrunc.HasValue) truncated["readingOrder"] = new { fullCount = roTrunc.Value, included = readingOrder.Length };
        if (mxTrunc.HasValue) truncated["dependencyMatrix"] = new { fullCount = mxTrunc.Value, included = matrix.Length };

        // `sections` allowlist: when set, every section not on the list is
        // dropped to an empty array. Summary, invariants, and violations
        // are always retained because they're the cheapest signal.
        bool Include(string name) => sections == null || sections.Contains(name, System.StringComparer.OrdinalIgnoreCase);

        var shaped = new
        {
            pack.Summary,
            HighValueFiles = Include("highValueFiles") ? highValueFiles : System.Array.Empty<HighValueFile>(),
            Boundaries = Include("boundaries") ? boundaries : System.Array.Empty<BoundaryInfo>(),
            pack.Invariants,
            Hotspots = Include("hotspots") ? hotspots : System.Array.Empty<string>(),
            ReadingOrder = Include("readingOrder") ? readingOrder : System.Array.Empty<string>(),
            DependencyMatrix = Include("dependencyMatrix") ? matrix : System.Array.Empty<ModuleDependency>(),
            pack.ActiveViolations,
            truncated,
            summarize,
            sections,
        };

        return TextResult(WithEnvelope("lifeblood_context", shaped));
    }

    private static (T[] Items, int? FullCount) ApplyCap<T>(T[] source, int max)
    {
        if (max < 0) return (source, null);
        if (max == 0) return (System.Array.Empty<T>(), source.Length > 0 ? source.Length : (int?)null);
        if (source.Length <= max) return (source, null);
        var clipped = new T[max];
        System.Array.Copy(source, clipped, max);
        return (clipped, source.Length);
    }

    private static string[]? ReadSectionsArray(JsonElement? args)
    {
        if (args == null || args.Value.ValueKind != JsonValueKind.Object) return null;
        if (!args.Value.TryGetProperty("sections", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.String && el.GetString() is { } s && s.Length > 0)
                list.Add(s);
        }
        return list.ToArray();
    }

    private McpToolResult HandleLookup(JsonElement? args)
    {
        if (!_session.IsLoaded)
            return ErrorResult("No graph loaded. Call lifeblood_analyze first.");

        var raw = WriteToolHandler.GetString(args, "symbolId");
        if (string.IsNullOrEmpty(raw))
            return ErrorResult("symbolId is required");

        // Plan v4 Seam #1 (INV-RESOLVER-001): every read-side tool routes
        // through the resolver. The resolver returns the canonical id PLUS
        // the merged read model for partial types — both fields below
        // (FilePath as deterministic primary, FilePaths as the full set)
        // come from the resolution result, not from raw graph storage.
        var resolved = _resolver.Resolve(_session.Graph!, raw);
        if (resolved.CanonicalId == null)
            return ErrorResult(resolved.Diagnostic ?? $"Symbol not found: {raw}");

        var sym = resolved.Symbol!;
        var result = new
        {
            sym.Id,
            sym.Name,
            sym.QualifiedName,
            Kind = sym.Kind.ToString(),
            FilePath = resolved.PrimaryFilePath,           // deterministic primary
            FilePaths = resolved.DeclarationFilePaths,     // all partials, sorted
            sym.Line,
            sym.ParentId,
            Visibility = sym.Visibility.ToString(),
            sym.IsAbstract,
            sym.IsStatic,
            sym.Properties,
        };
        return TextResult(WithEnvelope("lifeblood_lookup", result));
    }

    private McpToolResult HandleDependencies(JsonElement? args)
    {
        if (!_session.IsLoaded)
            return ErrorResult("No graph loaded. Call lifeblood_analyze first.");

        var raw = WriteToolHandler.GetString(args, "symbolId");
        if (string.IsNullOrEmpty(raw))
            return ErrorResult("symbolId is required");

        var resolved = _resolver.Resolve(_session.Graph!, raw);
        if (resolved.CanonicalId == null)
            return ErrorResult(resolved.Diagnostic ?? $"Symbol not found: {raw}");

        var edges = _provider.GetDependencyEdges(_session.Graph!, resolved.CanonicalId);
        return TextResult(WithEnvelope("lifeblood_dependencies", new
        {
            symbolId = resolved.CanonicalId,
            count = edges.Length,
            dependencies = edges.Select(BuildEdgeWire).ToArray(),
        }));
    }

    private McpToolResult HandleDependants(JsonElement? args)
    {
        if (!_session.IsLoaded)
            return ErrorResult("No graph loaded. Call lifeblood_analyze first.");

        var raw = WriteToolHandler.GetString(args, "symbolId");
        if (string.IsNullOrEmpty(raw))
            return ErrorResult("symbolId is required");

        var resolved = _resolver.Resolve(_session.Graph!, raw);
        if (resolved.CanonicalId == null)
            return ErrorResult(resolved.Diagnostic ?? $"Symbol not found: {raw}");

        var edges = _provider.GetDependantEdges(_session.Graph!, resolved.CanonicalId);
        return TextResult(WithEnvelope("lifeblood_dependants", new
        {
            symbolId = resolved.CanonicalId,
            count = edges.Length,
            dependants = edges.Select(BuildEdgeWire).ToArray(),
        }));
    }

    /// <summary>
    /// Build the wire shape for a single dependency / dependant edge: the
    /// canonical id of the other endpoint, the edge kind, and the optional
    /// call-site provenance (file/line/column + containing symbol id).
    /// <see cref="CallSite"/> is null for edges with no single authoring
    /// location (module→module DependsOn, type-level Inherits without a
    /// surfaced clause node). INV-EDGE-CALLSITE-001.
    /// </summary>
    private static object BuildEdgeWire(EdgeDetail e) => new
    {
        otherEndId = e.OtherEndId,
        kind = e.Kind.ToString(),
        callSite = e.CallSite == null ? null : new
        {
            filePath = e.CallSite.FilePath,
            line = e.CallSite.Line,
            column = e.CallSite.Column,
            endLine = e.CallSite.EndLine,
            endColumn = e.CallSite.EndColumn,
            containingSymbolId = e.CallSite.ContainingSymbolId,
        } as object,
    };


    private McpToolResult HandleBlastRadius(JsonElement? args)
    {
        if (!_session.IsLoaded)
            return ErrorResult("No graph loaded. Call lifeblood_analyze first.");

        var raw = WriteToolHandler.GetString(args, "symbolId");
        if (string.IsNullOrEmpty(raw))
            return ErrorResult("symbolId is required");

        var resolved = _resolver.Resolve(_session.Graph!, raw);
        if (resolved.CanonicalId == null)
            return ErrorResult(resolved.Diagnostic ?? $"Symbol not found: {raw}");

        var maxDepth = WriteToolHandler.GetInt(args, "maxDepth") ?? 10;
        var summarize = WriteToolHandler.GetBool(args, "summarize") ?? false;
        var maxResults = WriteToolHandler.GetInt(args, "maxResults") ?? (summarize ? 25 : 500);
        if (maxResults < 0) maxResults = 0;
        // Optional grouping by path-bucket and/or module. Default "none"
        // preserves the flat affected[] shape; INV-BLAST-RADIUS-GROUP-001.
        var groupBy = (WriteToolHandler.GetString(args, "groupBy") ?? "none")
            .ToLowerInvariant();

        if (groupBy == "bucket" || groupBy == "module" || groupBy == "both")
        {
            var groupPreview = WriteToolHandler.GetInt(args, "previewPerGroup") ?? 5;
            if (groupPreview < 0) groupPreview = 0;
            var groups = _provider.ClassifyBlastRadius(
                _session.Graph!, resolved.CanonicalId, maxDepth, groupPreview);

            return TextResult(WithEnvelope("lifeblood_blast_radius", new
            {
                symbolId = resolved.CanonicalId,
                maxDepth,
                directDependants = groups.DirectDependants,
                affectedCount = groups.TotalAffected,
                groupBy,
                byBucket = (groupBy == "bucket" || groupBy == "both")
                    ? groups.ByBucket.ToDictionary(kv => kv.Key, kv => new
                    {
                        count = kv.Value.Count,
                        preview = kv.Value.Preview,
                    })
                    : null,
                byModule = (groupBy == "module" || groupBy == "both")
                    ? groups.ByModule.ToDictionary(kv => kv.Key, kv => new
                    {
                        count = kv.Value.Count,
                        preview = kv.Value.Preview,
                    })
                    : null,
            }));
        }

        // Direct (one-hop) dependants computed independently of the transitive
        // walk. The transitive blast can be 100x bigger than the direct count
        // for popular types — callers need both to make the right decision.
        var directDependants = _provider.GetDependants(_session.Graph!, resolved.CanonicalId);

        var affected = _provider.GetBlastRadius(_session.Graph!, resolved.CanonicalId, maxDepth);
        var truncated = affected.Length > maxResults;
        var preview = truncated ? affected.Take(maxResults).ToArray() : affected;

        if (summarize)
        {
            return TextResult(WithEnvelope("lifeblood_blast_radius", new
            {
                symbolId = resolved.CanonicalId,
                maxDepth,
                directDependants = directDependants.Length,
                affectedCount = affected.Length,
                truncated,
                preview,
                summarize = true,
            }));
        }

        return TextResult(WithEnvelope("lifeblood_blast_radius", new
        {
            symbolId = resolved.CanonicalId,
            maxDepth,
            directDependants = directDependants.Length,
            affectedCount = affected.Length,
            truncated,
            affected = preview,
        }));
    }

    private McpToolResult HandleResolveShortName(JsonElement? args)
    {
        if (!_session.IsLoaded)
            return ErrorResult("No graph loaded. Call lifeblood_analyze first.");

        var name = WriteToolHandler.GetString(args, "name");
        if (string.IsNullOrEmpty(name))
            return ErrorResult("name is required");

        var modeString = WriteToolHandler.GetString(args, "mode");
        var mode = ParseResolutionMode(modeString);

        var matches = _resolver.ResolveShortName(_session.Graph!, name, mode);
        return TextResult(WithEnvelope("lifeblood_resolve_short_name", new
        {
            name,
            mode = mode.ToString().ToLowerInvariant(),
            count = matches.Length,
            matches,
        }));
    }

    private McpToolResult HandleResolveMember(JsonElement? args)
    {
        if (!_session.IsLoaded)
            return ErrorResult("No graph loaded. Call lifeblood_analyze first.");

        var typeName = WriteToolHandler.GetString(args, "typeName");
        if (string.IsNullOrEmpty(typeName))
            return ErrorResult("typeName is required");

        var memberName = WriteToolHandler.GetString(args, "memberName");
        if (string.IsNullOrEmpty(memberName))
            return ErrorResult("memberName is required");

        // paramTypes is an optional array of fully-qualified type names used
        // to disambiguate method overloads. Non-array, null, and missing all
        // collapse to "no filter" — the resolver returns every overload.
        string[]? paramTypes = null;
        if (args.HasValue && args.Value.TryGetProperty("paramTypes", out var p) &&
            p.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>(p.GetArrayLength());
            foreach (var el in p.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
                }
            }
            paramTypes = list.Count > 0 ? list.ToArray() : null;
        }

        var result = _resolver.ResolveMember(_session.Graph!, typeName, memberName, paramTypes);

        return TextResult(WithEnvelope("lifeblood_resolve_member", new
        {
            typeName,
            memberName,
            paramTypes,
            outcome = result.Outcome.ToString(),
            resolvedTypeId = result.ResolvedTypeId,
            count = result.Members.Length,
            members = result.Members.Select(m => new
            {
                canonicalId = m.CanonicalId,
                kind = m.Kind.ToString(),
                name = m.Name,
                filePath = m.FilePath,
                line = m.Line,
                paramDisplay = m.ParamDisplay,
            }).ToArray(),
            ambiguousTypeCandidates = result.AmbiguousTypeCandidates,
            diagnostic = result.Diagnostic,
        }));
    }

    /// <summary>
    /// Parse the user-facing mode string into the typed enum. Unknown values
    /// and empty/null fall through to <see cref="ResolutionMode.Exact"/>,
    /// matching the default documented in the tool schema. Unknown values
    /// are accepted silently rather than erroring because the enum is open
    /// to future extension and the default is always safe.
    /// </summary>
    private static ResolutionMode ParseResolutionMode(string? mode) =>
        (mode ?? "").ToLowerInvariant() switch
        {
            "contains" => ResolutionMode.Contains,
            "fuzzy" => ResolutionMode.Fuzzy,
            _ => ResolutionMode.Exact,
        };

    private McpToolResult HandleSearch(JsonElement? args)
    {
        if (!_session.IsLoaded)
            return ErrorResult("No graph loaded. Call lifeblood_analyze first.");

        var query = WriteToolHandler.GetString(args, "query");
        if (string.IsNullOrEmpty(query))
            return ErrorResult("query is required");

        var limit = WriteToolHandler.GetInt(args, "limit") ?? 20;
        var kinds = ParseKindsFilter(args);

        var results = _search.Search(_session.Graph!, new SearchQuery(query, kinds, limit));
        return TextResult(WithEnvelope("lifeblood_search", new
        {
            query,
            count = results.Length,
            results,
        }));
    }

    private McpToolResult HandleDeadCode(JsonElement? args)
    {
        if (!_session.IsLoaded)
            return ErrorResult("No graph loaded. Call lifeblood_analyze first.");

        var includeKinds = ParseKindsArray(args, "includeKinds");
        var excludePublic = WriteToolHandler.GetBool(args, "excludePublic") ?? true;
        var excludeTests = WriteToolHandler.GetBool(args, "excludeTests") ?? true;

        var options = new DeadCodeOptions(includeKinds, excludePublic, excludeTests);
        var findings = _deadCode.FindDeadCode(_session.Graph!, options);

        // Same response-shape pattern as cycles / context.
        // Large workspaces (53k+ symbols, default kinds) produce 286KB+ payloads that
        // overflow downstream tool-result limits — the tool succeeded but
        // the wire payload was unconsumable. summarize:true returns a
        // small preview-only response. maxResults caps the embedded array
        // regardless of mode. Per-kind breakdown always returned so the
        // caller can decide whether to drill in via includeKinds.
        var summarize = WriteToolHandler.GetBool(args, "summarize") ?? false;
        var maxResults = WriteToolHandler.GetInt(args, "maxResults") ?? (summarize ? 25 : 500);
        if (maxResults < 0) maxResults = 0;

        var truncated = findings.Length > maxResults;
        var preview = truncated ? findings.Take(maxResults).ToArray() : findings;

        // Per-kind breakdown — small map, always cheap, always emitted.
        // SymbolKind is a Domain enum; serialize as its string name so the
        // wire shape is callable as a stable kind filter via includeKinds.
        var kindBreakdown = new Dictionary<string, int>(System.StringComparer.Ordinal);
        foreach (var f in findings)
        {
            var key = f.Kind.ToString();
            kindBreakdown.TryGetValue(key, out var c);
            kindBreakdown[key] = c + 1;
        }

        // Per-bucket breakdown — Production / Test / Editor / Generated
        // counts in one map so a caller can fold the giant Editor or
        // Generated tail in one pass instead of post-processing the
        // findings array. INV-DEADCODE-TRIAGE-001.
        var bucketBreakdown = new Dictionary<string, int>(System.StringComparer.Ordinal);
        foreach (var f in findings)
        {
            var key = f.Bucket.ToString();
            bucketBreakdown.TryGetValue(key, out var c);
            bucketBreakdown[key] = c + 1;
        }

        const string sharedWarning =
            "Findings are ADVISORY. Known false-positive classes: " +
            "(1) runtime/reflection-dispatched methods and framework entry points " +
            "not modeled by the active reachability provider; " +
            "(2) methods with call-site canonical-id drift in multi-module " +
            "workspaces (pre-existing extraction gap under investigation); " +
            "(3) private fields read via same-class access when the enclosing " +
            "type has no external references. Method-group delegate arguments " +
            "are expected to be covered by graph edges; if one appears here, " +
            "treat it as an extractor regression. Verify each finding with " +
            "lifeblood_find_references and direct code inspection before acting.";

        if (summarize)
        {
            return TextResult(WithEnvelope("lifeblood_dead_code", new
            {
                // Surfaced in every response so agents cannot use the tool
                // without seeing the caveat. INV-DEADCODE-001. Same caveat
                // is also carried in the typed envelope.limitations field
                // per INV-ENVELOPE-001.
                status = "experimental",
                warning = sharedWarning,
                count = findings.Length,
                kindBreakdown,
                bucketBreakdown,
                truncated,
                preview,
                summarize = true,
            }));
        }

        return TextResult(WithEnvelope("lifeblood_dead_code", new
        {
            status = "experimental",
            warning = sharedWarning,
            count = findings.Length,
            kindBreakdown,
            bucketBreakdown,
            truncated,
            findings = preview,
        }));
    }

    private McpToolResult HandlePartialView(JsonElement? args)
    {
        if (!_session.IsLoaded)
            return ErrorResult("No graph loaded. Call lifeblood_analyze first.");

        var raw = WriteToolHandler.GetString(args, "symbolId");
        if (string.IsNullOrEmpty(raw))
            return ErrorResult("symbolId is required");

        var resolved = _resolver.Resolve(_session.Graph!, raw);
        if (resolved.CanonicalId == null)
            return ErrorResult(resolved.Diagnostic ?? $"Symbol not found: {raw}");

        // Partial view takes projectRoot as a method parameter so the
        // port stays free of session-specific state. The session owns
        // the root value and feeds it in here.
        var view = _partialView.Build(_session.Graph!, resolved.CanonicalId, _session.ProjectRoot);
        return TextResult(WithEnvelope("lifeblood_partial_view", view));
    }

    /// <summary>
    /// Handle <c>lifeblood_invariant_check</c>. Three modes selected by
    /// parameter shape, exactly one of which must be present:
    ///
    /// <list type="bullet">
    ///   <item><c>id</c>: return the single invariant with the matching
    ///     id, or an error if it doesn't exist.</item>
    ///   <item><c>mode: "audit"</c>: return the summary (total count,
    ///     category breakdown, duplicates, parse warnings).</item>
    ///   <item><c>mode: "list"</c>: return every invariant (id + title +
    ///     category + source line, body omitted to keep the response
    ///     lean — callers who need the body should query by id).</item>
    /// </list>
    ///
    /// No graph required. The provider parses <c>CLAUDE.md</c> at the
    /// loaded project root; callers who haven't run lifeblood_analyze
    /// yet get a clear error rather than an empty response.
    /// </summary>
    private McpToolResult HandleInvariantCheck(JsonElement? args)
    {
        if (!_session.IsLoaded || string.IsNullOrEmpty(_session.ProjectRoot))
        {
            return ErrorResult(
                "No workspace loaded. Call lifeblood_analyze with a projectPath first so " +
                "the invariant provider can locate CLAUDE.md.");
        }

        var projectRoot = _session.ProjectRoot;
        var id = WriteToolHandler.GetString(args, "id");
        var mode = WriteToolHandler.GetString(args, "mode");

        // Exactly one of {id, mode} must be populated. Both → error,
        // neither → default to audit.
        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(mode))
        {
            return ErrorResult("Specify exactly one of 'id' or 'mode', not both.");
        }

        if (!string.IsNullOrEmpty(id))
        {
            var inv = _invariants.GetById(projectRoot, id);
            if (inv == null)
            {
                var audit = _invariants.Audit(projectRoot);
                return ErrorResult(
                    $"Invariant '{id}' not found in {audit.SourcePath}. " +
                    $"{audit.TotalCount} invariants are declared. " +
                    "Use mode='list' to see every declared id.");
            }
            return TextResult(WithEnvelope("lifeblood_invariant_check", new
            {
                inv.Id,
                inv.Title,
                inv.Category,
                inv.SourceLine,
                inv.Body,
            }));
        }

        if (string.IsNullOrEmpty(mode) || string.Equals(mode, "audit", System.StringComparison.OrdinalIgnoreCase))
        {
            var audit = _invariants.Audit(projectRoot);
            return TextResult(WithEnvelope("lifeblood_invariant_check", new
            {
                mode = "audit",
                audit.SourcePath,
                audit.TotalCount,
                audit.CategoryCounts,
                audit.Duplicates,
                audit.ParseWarnings,
            }));
        }

        if (string.Equals(mode, "list", System.StringComparison.OrdinalIgnoreCase))
        {
            var all = _invariants.GetAll(projectRoot);
            // Body is omitted in list mode — callers who need it query
            // by id, keeping list responses compact even for projects
            // with dozens of invariants.
            var summaries = all.Select(inv => new
            {
                inv.Id,
                inv.Title,
                inv.Category,
                inv.SourceLine,
            }).ToArray();
            return TextResult(WithEnvelope("lifeblood_invariant_check", new
            {
                mode = "list",
                count = summaries.Length,
                invariants = summaries,
            }));
        }

        return ErrorResult(
            $"Unknown mode '{mode}'. Valid modes: 'audit' (default), 'list'. " +
            "Or omit mode and pass 'id' to fetch a single invariant.");
    }

    /// <summary>
    /// Parse the optional <c>kinds</c> array from the search tool's
    /// arguments into a typed <see cref="Lifeblood.Domain.Graph.SymbolKind"/>
    /// array. Entries that don't parse (typo, unknown kind) are silently
    /// dropped rather than erroring, so the filter is best-effort and
    /// degrades to "no filter" on bad input.
    /// </summary>
    private static Lifeblood.Domain.Graph.SymbolKind[]? ParseKindsFilter(JsonElement? args)
        => ParseKindsArray(args, "kinds");

    /// <summary>
    /// Generic kinds-array parser used by <c>lifeblood_search</c>
    /// (<c>kinds</c> property) and <c>lifeblood_dead_code</c>
    /// (<c>includeKinds</c> property). Different property names, same
    /// shape.
    /// </summary>
    private static Lifeblood.Domain.Graph.SymbolKind[]? ParseKindsArray(JsonElement? args, string propertyName)
    {
        if (args is not JsonElement obj || obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(propertyName, out var kindsElement) || kindsElement.ValueKind != JsonValueKind.Array) return null;
        var list = new List<Lifeblood.Domain.Graph.SymbolKind>(kindsElement.GetArrayLength());
        foreach (var item in kindsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            if (System.Enum.TryParse<Lifeblood.Domain.Graph.SymbolKind>(item.GetString(), ignoreCase: true, out var parsed))
                list.Add(parsed);
        }
        return list.Count > 0 ? list.ToArray() : null;
    }

    private McpToolResult HandleFileImpact(JsonElement? args)
    {
        if (!_session.IsLoaded)
            return ErrorResult("No graph loaded. Call lifeblood_analyze first.");

        var filePath = WriteToolHandler.GetString(args, "filePath");
        if (string.IsNullOrEmpty(filePath))
            return ErrorResult("filePath is required");

        var fileId = "file:" + filePath.Replace('\\', '/');
        var symbol = _session.Graph!.GetSymbol(fileId);
        if (symbol == null)
            return ErrorResult($"File not found in graph: {filePath} (tried ID: {fileId})");

        var result = _provider.GetFileImpact(_session.Graph!, fileId);
        return TextResult(WithEnvelope("lifeblood_file_impact", new
        {
            result.FileId,
            result.FilePath,
            dependsOnCount = result.DependsOn.Length,
            dependedOnByCount = result.DependedOnBy.Length,
            result.DependsOn,
            result.DependedOnBy,
        }));
    }

    // ── P5: authority / port_health / cycles ──

    private McpToolResult HandleAuthorityReport(JsonElement? args)
    {
        if (!_session.IsLoaded)
            return ErrorResult("No graph loaded. Call lifeblood_analyze first.");

        var raw = WriteToolHandler.GetString(args, "symbolId");
        if (string.IsNullOrEmpty(raw))
            return ErrorResult("symbolId is required");

        var resolved = _resolver.Resolve(_session.Graph!, raw);
        if (resolved.CanonicalId == null)
            return ErrorResult(resolved.Diagnostic ?? $"Symbol not found: {raw}");

        var report = _authority.Analyze(_session.Graph!, resolved.CanonicalId);
        return TextResult(WithEnvelope("lifeblood_authority_report", report));
    }

    private McpToolResult HandlePortHealth(JsonElement? args)
    {
        if (!_session.IsLoaded)
            return ErrorResult("No graph loaded. Call lifeblood_analyze first.");

        var raw = WriteToolHandler.GetString(args, "symbolId");
        if (string.IsNullOrEmpty(raw))
            return ErrorResult("symbolId is required");

        var resolved = _resolver.Resolve(_session.Graph!, raw);
        if (resolved.CanonicalId == null)
            return ErrorResult(resolved.Diagnostic ?? $"Symbol not found: {raw}");

        var report = _portHealth.Analyze(_session.Graph!, resolved.CanonicalId);
        if (report == null)
        {
            var sym = _session.Graph!.GetSymbol(resolved.CanonicalId);
            return ErrorResult($"port_health requires a Type symbol; got Kind={sym?.Kind} for {resolved.CanonicalId}");
        }

        return TextResult(WithEnvelope("lifeblood_port_health", new
        {
            symbolId = report.TypeId,
            memberCount = report.MemberCount,
            liveMembers = report.LiveMembers,
            deadMembers = report.DeadMembers,
            livenessPct = report.LivenessPct,
            verdict = report.Verdict,
            live = report.Live,
            dead = report.Dead,
            // F3b composite-surface fields. INV-PORT-HEALTH-COMPOSITE-001.
            directMemberCount = report.DirectMemberCount,
            inheritedMemberCount = report.InheritedMemberCount,
            aggregateMemberCount = report.AggregateMemberCount,
            inheritedInterfaces = report.InheritedInterfaces,
            isCompositeInterface = report.IsCompositeInterface,
        }));
    }

    private McpToolResult HandleCycles(JsonElement? args)
    {
        if (!_session.IsLoaded)
            return ErrorResult("No graph loaded. Call lifeblood_analyze first.");

        // Classified detection. Each descriptor is { symbols, bucket }.
        // INV-CYCLE-TAXONOMY-001 — caller can fold the Generated + Partial
        // noise tail without re-walking the cycle members.
        var descriptors = Lifeblood.Analysis.CircularDependencyDetector.DetectClassified(_session.Graph!);

        // Same response-shape pattern as blast_radius summarize / maxResults.
        // Large workspaces commonly carry 100+ SCCs serializing to ~70KB —
        // exceeds downstream tool-result limits. summarize:true returns
        // counts + a small preview without the full cycles array;
        // maxResults caps the embedded array regardless of mode.
        var summarize = WriteToolHandler.GetBool(args, "summarize") ?? false;
        var maxResults = WriteToolHandler.GetInt(args, "maxResults") ?? (summarize ? 25 : 500);
        if (maxResults < 0) maxResults = 0;

        var truncated = descriptors.Length > maxResults;
        var totalSymbolCount = 0;
        var largestCycleSize = 0;
        var bucketCounts = new Dictionary<string, int>(3, StringComparer.Ordinal);
        foreach (var d in descriptors)
        {
            totalSymbolCount += d.Symbols.Length;
            if (d.Symbols.Length > largestCycleSize) largestCycleSize = d.Symbols.Length;
            var bucketName = d.Bucket.ToString();
            bucketCounts[bucketName] = bucketCounts.TryGetValue(bucketName, out var prior) ? prior + 1 : 1;
        }

        var previewDescriptors = truncated ? descriptors.Take(maxResults).ToArray() : descriptors;
        // Project into wire shape AFTER truncation so the legacy
        // `cycles[][]` array view stays available alongside the new
        // `descriptors[]` shape — purely additive, no field removal.
        var previewSymbolArrays = previewDescriptors.Select(d => d.Symbols).ToArray();
        var previewClassified = previewDescriptors
            .Select(d => new { symbols = d.Symbols, bucket = d.Bucket.ToString() })
            .ToArray();

        if (summarize)
        {
            return TextResult(WithEnvelope("lifeblood_cycles", new
            {
                count = descriptors.Length,
                totalSymbolCount,
                largestCycleSize,
                truncated,
                bucketBreakdown = bucketCounts,
                preview = previewSymbolArrays,
                previewClassified,
                summarize = true,
            }));
        }

        return TextResult(WithEnvelope("lifeblood_cycles", new
        {
            count = descriptors.Length,
            totalSymbolCount,
            largestCycleSize,
            truncated,
            bucketBreakdown = bucketCounts,
            cycles = previewSymbolArrays,
            descriptors = previewClassified,
        }));
    }

    private McpToolResult HandleTestImpact(JsonElement? args)
    {
        if (!_session.IsLoaded)
            return ErrorResult("No graph loaded. Call lifeblood_analyze first.");

        var raw = WriteToolHandler.GetString(args, "target");
        if (string.IsNullOrEmpty(raw))
            return ErrorResult("target is required (a symbol id or file path)");

        // Disambiguation: a value with a `:` in the canonical-id prefix
        // position (`type:`, `method:`, `field:`, `mod:`, `file:`,
        // `property:`) is a symbol id. Otherwise treat as a file path
        // and route to the multi-source file analyzer. The `file:` id
        // prefix routes through the symbol resolver too (the graph
        // builds a File-kind symbol with that id); both shapes work.
        var isSymbolId = LooksLikeSymbolId(raw);

        TestImpactReport report;
        if (isSymbolId)
        {
            var resolved = _resolver.Resolve(_session.Graph!, raw);
            if (resolved.CanonicalId == null)
                return ErrorResult(resolved.Diagnostic ?? $"Symbol not found: {raw}");
            report = Lifeblood.Analysis.TestImpactAnalyzer.AnalyzeSymbol(_session.Graph!, resolved.CanonicalId);
        }
        else
        {
            report = Lifeblood.Analysis.TestImpactAnalyzer.AnalyzeFile(_session.Graph!, raw);
        }

        return TextResult(WithEnvelope("lifeblood_test_impact", new
        {
            target = report.Target,
            targetKind = report.TargetKind.ToString(),
            totalTestMethodCount = report.TotalTestMethodCount,
            directTestClassCount = report.DirectTestClassCount,
            affectedTestClassCount = report.AffectedTestClasses.Length,
            affectedTestClasses = report.AffectedTestClasses.Select(c => new
            {
                typeId = c.TypeId,
                name = c.Name,
                qualifiedName = c.QualifiedName,
                filePath = c.FilePath,
                minDistance = c.MinDistance,
                confidence = c.Confidence.ToString(),
                testMethodCount = c.TestMethodNames.Length,
                testMethodNames = c.TestMethodNames,
            }).ToArray(),
            recommendedFilters = report.RecommendedFilters,
        }));
    }

    /// <summary>
    /// True when <paramref name="raw"/> starts with one of the canonical
    /// Lifeblood symbol-id prefixes. Used by <c>HandleTestImpact</c> to
    /// route between symbol-mode and file-mode without forcing the
    /// caller to pick the route explicitly.
    /// </summary>
    private static bool LooksLikeSymbolId(string raw)
    {
        var colon = raw.IndexOf(':');
        if (colon <= 0) return false;
        var prefix = raw.AsSpan(0, colon);
        return prefix.SequenceEqual("type") || prefix.SequenceEqual("method")
            || prefix.SequenceEqual("field") || prefix.SequenceEqual("property")
            || prefix.SequenceEqual("mod") || prefix.SequenceEqual("file")
            || prefix.SequenceEqual("ns") || prefix.SequenceEqual("namespace");
    }

    private static McpToolResult TextResult(string text) => new()
    {
        Content = new[] { new McpContent { Type = "text", Text = text } },
    };

    private static McpToolResult ErrorResult(string message) => new()
    {
        Content = new[] { new McpContent { Type = "text", Text = message } },
        IsError = true,
    };

    /// <summary>
    /// Build the read-side <see cref="EnvelopeContext"/> for the currently
    /// loaded session. Capped staleness scan: 256 files at full mtime
    /// resolution, more than enough to detect drift on any realistically-
    /// sized workspace without making every tool call an O(N) disk scan.
    /// Empty context when no graph is loaded — the decorator degrades
    /// gracefully (zero staleness, zero files-changed).
    /// </summary>
    private const int EnvelopeFileScanLimit = 256;

    private EnvelopeContext BuildEnvelopeContext()
    {
        if (!_session.IsLoaded || _session.Graph == null)
        {
            return new EnvelopeContext
            {
                FileSystem = _session.FileSystem,
                AnalysisGeneration = _session.AnalysisGeneration,
                AdapterCapability = _session.AdapterCapability,
            };
        }

        // Walk the graph for File symbols. Resolve relative paths against
        // the project root so the file-system port can stat them; absolute
        // paths pass through unchanged.
        var root = _session.ProjectRoot;
        var paths = _session.Graph.Symbols
            .Where(s => s.Kind == SymbolKind.File && !string.IsNullOrEmpty(s.FilePath))
            .Select(s => System.IO.Path.IsPathRooted(s.FilePath) || string.IsNullOrEmpty(root)
                ? s.FilePath
                : System.IO.Path.GetFullPath(System.IO.Path.Combine(root, s.FilePath)))
            .ToArray();

        return new EnvelopeContext
        {
            AnalyzedAtUtc = _session.AnalyzedAtUtc,
            TrackedFilePaths = paths,
            FileSystem = _session.FileSystem,
            FileScanLimit = EnvelopeFileScanLimit,
            AnalysisGeneration = _session.AnalysisGeneration,
            AdapterCapability = _session.AdapterCapability,
        };
    }

    /// <summary>
    /// Wrap a payload object with the truth envelope and emit JSON.
    /// Non-breaking: every existing top-level field is preserved verbatim;
    /// the envelope is injected as a sibling <c>envelope</c> property. Every
    /// read-side tool routes its successful response through this helper
    /// so INV-ENVELOPE-001 holds. Errors go through <see cref="ErrorResult"/>
    /// directly — envelopes are for successful results, not for
    /// "no graph loaded" or input-validation errors.
    /// </summary>
    private string WithEnvelope(string toolName, object payload)
    {
        var envelope = _decorator.Decorate(toolName, BuildEnvelopeContext());
        var node = System.Text.Json.JsonSerializer.SerializeToNode(payload, JsonOpts)
            as System.Text.Json.Nodes.JsonObject;
        if (node == null)
        {
            // Payload wasn't an object (string / array / scalar). Fall back to
            // a thin wrapper so the envelope still ships.
            return JsonSerializer.Serialize(new
            {
                envelope,
                result = payload,
            }, JsonOpts);
        }
        node["envelope"] = System.Text.Json.JsonSerializer.SerializeToNode(envelope, JsonOpts);
        return node.ToJsonString(new System.Text.Json.JsonSerializerOptions(JsonOpts) { WriteIndented = true });
    }

    /// <summary>
    /// Inject the envelope into a JSON string the session already
    /// produced (analyze response, JSON-graph import). Idempotent: if the
    /// payload already has an <c>envelope</c> field, the existing value
    /// is preserved.
    /// </summary>
    private string MergeEnvelopeIntoJson(string toolName, string payloadJson)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(payloadJson)
                as System.Text.Json.Nodes.JsonObject;
            if (node == null) return payloadJson;
            if (node.ContainsKey("envelope")) return payloadJson;
            var envelope = _decorator.Decorate(toolName, BuildEnvelopeContext());
            node["envelope"] = System.Text.Json.JsonSerializer.SerializeToNode(envelope, JsonOpts);
            return node.ToJsonString(new System.Text.Json.JsonSerializerOptions(JsonOpts) { WriteIndented = true });
        }
        catch
        {
            // Best-effort merge; never fail a tool call because envelope
            // injection ran into an unparseable payload.
            return payloadJson;
        }
    }

    /// <summary>
    /// Wrap a write-side tool's <see cref="McpToolResult"/> with the
    /// truth envelope. Mirrors the read-side <c>WithEnvelope</c> seam:
    /// every successful response that carries a JSON-object payload
    /// gets the same <c>envelope</c> field structure as a read-side
    /// response. Error results (<c>IsError == true</c>) pass through
    /// unchanged — envelopes are for successful results, not for
    /// "no graph loaded" or input-validation errors. Idempotent via
    /// <see cref="MergeEnvelopeIntoJson"/>. INV-ENVELOPE-001 +
    /// INV-ADVISORY-LIMITATIONS-001.
    /// </summary>
    private McpToolResult WrapWriteSide(string toolName, McpToolResult result)
    {
        if (result.IsError == true) return result;
        if (result.Content.Length == 0) return result;
        var wrappedText = MergeEnvelopeIntoJson(toolName, result.Content[0].Text);
        if (ReferenceEquals(wrappedText, result.Content[0].Text)) return result;
        return new McpToolResult
        {
            Content = new[] { new McpContent { Type = result.Content[0].Type, Text = wrappedText } },
            IsError = result.IsError,
        };
    }
}

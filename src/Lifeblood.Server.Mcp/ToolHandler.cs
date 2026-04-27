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
        IAuthorityReporter? authority = null)
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
                "lifeblood_search" => HandleSearch(arguments),
                "lifeblood_dead_code" => HandleDeadCode(arguments),
                "lifeblood_partial_view" => HandlePartialView(arguments),
                "lifeblood_invariant_check" => HandleInvariantCheck(arguments),
                "lifeblood_authority_report" => HandleAuthorityReport(arguments),
                "lifeblood_port_health" => HandlePortHealth(arguments),
                "lifeblood_cycles" => HandleCycles(arguments),
                // Write-side
                "lifeblood_execute" => _write.HandleExecute(arguments),
                "lifeblood_diagnose" => _write.HandleDiagnose(arguments),
                "lifeblood_compile_check" => _write.HandleCompileCheck(arguments),
                "lifeblood_find_references" => _write.HandleFindReferences(arguments),
                "lifeblood_find_definition" => _write.HandleFindDefinition(arguments),
                "lifeblood_find_implementations" => _write.HandleFindImplementations(arguments),
                "lifeblood_symbol_at_position" => _write.HandleGetSymbolAtPosition(arguments),
                "lifeblood_documentation" => _write.HandleGetDocumentation(arguments),
                "lifeblood_rename" => _write.HandleRename(arguments),
                "lifeblood_format" => _write.HandleFormat(arguments),
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

        var result = _session.Load(projectPath, graphPath, rulesPath, incremental, readOnly);
        return TextResult(MergeEnvelopeIntoJson("lifeblood_analyze", result));
    }

    private McpToolResult HandleContext(JsonElement? args)
    {
        if (!_session.IsLoaded)
            return ErrorResult("No graph loaded. Call lifeblood_analyze first.");

        var useCase = new GenerateContextUseCase(new AgentContextGenerator());
        var pack = useCase.Execute(_session.Graph!, _session.Analysis!);

        // LB-FR-022 (DAWG dogfood): default behaviour previously emitted the
        // full pack (~375KB on a 87-module workspace), overflowing downstream
        // tool-result limits. Same fix shape as cycles/blast_radius — every
        // section gets a smart default cap, callers can override per-section
        // or pass `summarize:true` for the smallest viable shape, and an
        // explicit `sections` allowlist drops anything not requested. The
        // response always carries a `truncated` map that names every clipped
        // section + its full-pre-clip count, so callers know what was hidden.
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

        var deps = _provider.GetDependencies(_session.Graph!, resolved.CanonicalId);
        return TextResult(WithEnvelope("lifeblood_dependencies", new
        {
            symbolId = resolved.CanonicalId,
            count = deps.Length,
            dependencies = deps,
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

        var deps = _provider.GetDependants(_session.Graph!, resolved.CanonicalId);
        return TextResult(WithEnvelope("lifeblood_dependants", new
        {
            symbolId = resolved.CanonicalId,
            count = deps.Length,
            dependants = deps,
        }));
    }

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

        // Direct (one-hop) dependants computed independently of the transitive
        // walk. The transitive blast can be 100x bigger than the direct count
        // for popular types — callers need both to make the right decision
        // (LB-FR-010 from the DAWG dogfood backlog).
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

        // LB-FR-024 (DAWG dogfood): same shape as cycles / context.
        // DAWG (53k symbols, default kinds) produces 286KB+ payloads that
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

        const string sharedWarning =
            "Findings are ADVISORY. Known false-positive classes: " +
            "(1) methods referenced via method-group conversion " +
            "(Lazy<T>, event handlers, delegate arguments); " +
            "(2) methods with call-site canonical-id drift in multi-module " +
            "workspaces (pre-existing extraction gap under investigation); " +
            "(3) private fields read via same-class access when the enclosing " +
            "type has no external references. Verify each finding with " +
            "lifeblood_find_references (which has the same gap class) and " +
            "direct code inspection before acting.";

        if (summarize)
        {
            return TextResult(WithEnvelope("lifeblood_dead_code", new
            {
                // Surfaced in every response so agents cannot use the tool
                // without seeing the caveat. INV-DEADCODE-001. Note: with
                // INV-ENVELOPE-001 (v0.6.7) the same caveat is also carried
                // in the typed envelope.limitations field.
                status = "experimental",
                warning = sharedWarning,
                count = findings.Length,
                kindBreakdown,
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

        var graph = _session.Graph!;
        var typeId = resolved.CanonicalId;
        var sym = graph.GetSymbol(typeId);
        if (sym == null || sym.Kind != SymbolKind.Type)
            return ErrorResult($"port_health requires a Type symbol; got Kind={sym?.Kind} for {typeId}");

        var memberIds = new List<string>();
        foreach (int idx in graph.GetOutgoingEdgeIndexes(typeId))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind != EdgeKind.Contains) continue;
            var member = graph.GetSymbol(edge.TargetId);
            if (member == null) continue;
            if (member.Kind == SymbolKind.Type) continue; // exclude nested types
            memberIds.Add(member.Id);
        }

        int liveCount = 0;
        var live = new List<string>();
        var dead = new List<string>();
        foreach (var id in memberIds)
        {
            bool hasIncoming = false;
            foreach (int idx in graph.GetIncomingEdgeIndexes(id))
            {
                var e = graph.Edges[idx];
                if (e.Kind == EdgeKind.Contains) continue;
                hasIncoming = true; break;
            }
            // Methods that implement an interface member are reachable
            // through the interface — same liveness rule the dead-code
            // analyzer uses (Implements outgoing = alive by definition).
            if (!hasIncoming)
            {
                foreach (int idx in graph.GetOutgoingEdgeIndexes(id))
                {
                    if (graph.Edges[idx].Kind == EdgeKind.Implements) { hasIncoming = true; break; }
                }
            }
            if (hasIncoming) { liveCount++; live.Add(id); }
            else dead.Add(id);
        }

        double pct = memberIds.Count == 0 ? 0.0 : (double)liveCount / memberIds.Count;
        string verdict = memberIds.Count == 0
            ? "empty"
            : pct >= 0.75 ? "healthy"
            : pct >= 0.25 ? "mixed"
            : "vestigial";

        return TextResult(WithEnvelope("lifeblood_port_health", new
        {
            symbolId = typeId,
            memberCount = memberIds.Count,
            liveMembers = liveCount,
            deadMembers = dead.Count,
            livenessPct = System.Math.Round(pct, 3),
            verdict,
            live = live.ToArray(),
            dead = dead.ToArray(),
        }));
    }

    private McpToolResult HandleCycles(JsonElement? args)
    {
        if (!_session.IsLoaded)
            return ErrorResult("No graph loaded. Call lifeblood_analyze first.");

        var cycles = Lifeblood.Analysis.CircularDependencyDetector.Detect(_session.Graph!);

        // LB-FR-021 (DAWG dogfood): same shape as blast_radius summarize/maxResults.
        // DAWG has 117 SCCs that serialize to ~70KB — exceeds downstream tool-result
        // limits. summarize:true returns counts + a small preview without the full
        // cycles array; maxResults caps the embedded array regardless of mode.
        var summarize = WriteToolHandler.GetBool(args, "summarize") ?? false;
        var maxResults = WriteToolHandler.GetInt(args, "maxResults") ?? (summarize ? 25 : 500);
        if (maxResults < 0) maxResults = 0;

        var truncated = cycles.Length > maxResults;
        var preview = truncated ? cycles.Take(maxResults).ToArray() : cycles;
        var totalSymbolCount = 0;
        var largestCycleSize = 0;
        foreach (var c in cycles)
        {
            totalSymbolCount += c.Length;
            if (c.Length > largestCycleSize) largestCycleSize = c.Length;
        }

        if (summarize)
        {
            return TextResult(WithEnvelope("lifeblood_cycles", new
            {
                count = cycles.Length,
                totalSymbolCount,
                largestCycleSize,
                truncated,
                preview,
                summarize = true,
            }));
        }

        return TextResult(WithEnvelope("lifeblood_cycles", new
        {
            count = cycles.Length,
            totalSymbolCount,
            largestCycleSize,
            truncated,
            cycles = preview,
        }));
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
            return new EnvelopeContext { FileSystem = _session.FileSystem };
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
}

using System.Text.Json;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Application.Ports.Right.Invariants;
using Lifeblood.Application.UseCases;
using Lifeblood.Connectors.ContextPack;

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
    private readonly WriteToolHandler _write;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public ToolHandler(
        GraphSession session,
        IMcpGraphProvider provider,
        ISymbolResolver resolver,
        ISemanticSearchProvider search,
        IDeadCodeAnalyzer deadCode,
        IPartialViewBuilder partialView,
        IInvariantProvider invariants)
    {
        _session = session;
        _provider = provider;
        _resolver = resolver;
        _search = search;
        _deadCode = deadCode;
        _partialView = partialView;
        _invariants = invariants;
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
                "lifeblood_context" => HandleContext(),
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
        return TextResult(result);
    }

    private McpToolResult HandleContext()
    {
        if (!_session.IsLoaded)
            return ErrorResult("No graph loaded. Call lifeblood_analyze first.");

        var useCase = new GenerateContextUseCase(new AgentContextGenerator());
        var pack = useCase.Execute(_session.Graph!, _session.Analysis!);
        var json = JsonSerializer.Serialize(pack, JsonOpts);
        return TextResult(json);
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
        return TextResult(JsonSerializer.Serialize(result, JsonOpts));
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
        return TextResult(JsonSerializer.Serialize(deps, JsonOpts));
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
        return TextResult(JsonSerializer.Serialize(deps, JsonOpts));
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
        var affected = _provider.GetBlastRadius(_session.Graph!, resolved.CanonicalId, maxDepth);
        return TextResult(JsonSerializer.Serialize(
            new { symbolId = resolved.CanonicalId, maxDepth, affectedCount = affected.Length, affected },
            JsonOpts));
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
        return TextResult(JsonSerializer.Serialize(
            new { name, mode = mode.ToString().ToLowerInvariant(), count = matches.Length, matches },
            JsonOpts));
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
        return TextResult(JsonSerializer.Serialize(
            new { query, count = results.Length, results },
            JsonOpts));
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
        return TextResult(JsonSerializer.Serialize(
            new { count = findings.Length, findings },
            JsonOpts));
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
        return TextResult(JsonSerializer.Serialize(view, JsonOpts));
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
            return TextResult(JsonSerializer.Serialize(new
            {
                inv.Id,
                inv.Title,
                inv.Category,
                inv.SourceLine,
                inv.Body,
            }, JsonOpts));
        }

        if (string.IsNullOrEmpty(mode) || string.Equals(mode, "audit", System.StringComparison.OrdinalIgnoreCase))
        {
            var audit = _invariants.Audit(projectRoot);
            return TextResult(JsonSerializer.Serialize(new
            {
                mode = "audit",
                audit.SourcePath,
                audit.TotalCount,
                audit.CategoryCounts,
                audit.Duplicates,
                audit.ParseWarnings,
            }, JsonOpts));
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
            return TextResult(JsonSerializer.Serialize(new
            {
                mode = "list",
                count = summaries.Length,
                invariants = summaries,
            }, JsonOpts));
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
        return TextResult(JsonSerializer.Serialize(new
        {
            result.FileId,
            result.FilePath,
            dependsOnCount = result.DependsOn.Length,
            dependedOnByCount = result.DependedOnBy.Length,
            result.DependsOn,
            result.DependedOnBy,
        }, JsonOpts));
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
}

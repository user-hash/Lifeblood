using System.Text.Json;
using Lifeblood.Application.Ports.Right;
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
    private readonly WriteToolHandler _write;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public ToolHandler(GraphSession session, IMcpGraphProvider provider, ISymbolResolver resolver)
    {
        _session = session;
        _provider = provider;
        _resolver = resolver;
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

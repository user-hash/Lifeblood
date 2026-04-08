using System.Text.Json;
using Lifeblood.Application.Ports.Analysis;
using Lifeblood.Application.UseCases;
using Lifeblood.Connectors.ContextPack;
using Lifeblood.Connectors.Mcp;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// Dispatches MCP tool calls. Read-side handled inline, write-side delegated to WriteToolHandler.
/// </summary>
public sealed class ToolHandler
{
    private readonly GraphSession _session;
    private readonly LifebloodMcpProvider _provider;
    private readonly WriteToolHandler _write;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public ToolHandler(GraphSession session, IBlastRadiusProvider blastRadius)
    {
        _session = session;
        _provider = new LifebloodMcpProvider(blastRadius);
        _write = new WriteToolHandler(session, JsonOpts);
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

        var result = _session.Load(projectPath, graphPath, rulesPath, incremental);
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

        var symbolId = WriteToolHandler.GetString(args, "symbolId");
        if (string.IsNullOrEmpty(symbolId))
            return ErrorResult("symbolId is required");

        var symbol = _provider.LookupSymbol(_session.Graph!, symbolId);
        if (symbol == null)
            return ErrorResult($"Symbol not found: {symbolId}");

        var result = new
        {
            symbol.Id,
            symbol.Name,
            symbol.QualifiedName,
            Kind = symbol.Kind.ToString(),
            symbol.FilePath,
            symbol.Line,
            symbol.ParentId,
            Visibility = symbol.Visibility.ToString(),
            symbol.IsAbstract,
            symbol.IsStatic,
            symbol.Properties,
        };
        return TextResult(JsonSerializer.Serialize(result, JsonOpts));
    }

    private McpToolResult HandleDependencies(JsonElement? args)
    {
        if (!_session.IsLoaded)
            return ErrorResult("No graph loaded. Call lifeblood_analyze first.");

        var symbolId = WriteToolHandler.GetString(args, "symbolId");
        if (string.IsNullOrEmpty(symbolId))
            return ErrorResult("symbolId is required");

        var deps = _provider.GetDependencies(_session.Graph!, symbolId);
        return TextResult(JsonSerializer.Serialize(deps, JsonOpts));
    }

    private McpToolResult HandleDependants(JsonElement? args)
    {
        if (!_session.IsLoaded)
            return ErrorResult("No graph loaded. Call lifeblood_analyze first.");

        var symbolId = WriteToolHandler.GetString(args, "symbolId");
        if (string.IsNullOrEmpty(symbolId))
            return ErrorResult("symbolId is required");

        var deps = _provider.GetDependants(_session.Graph!, symbolId);
        return TextResult(JsonSerializer.Serialize(deps, JsonOpts));
    }

    private McpToolResult HandleBlastRadius(JsonElement? args)
    {
        if (!_session.IsLoaded)
            return ErrorResult("No graph loaded. Call lifeblood_analyze first.");

        var symbolId = WriteToolHandler.GetString(args, "symbolId");
        if (string.IsNullOrEmpty(symbolId))
            return ErrorResult("symbolId is required");

        var maxDepth = WriteToolHandler.GetInt(args, "maxDepth") ?? 10;
        var affected = _provider.GetBlastRadius(_session.Graph!, symbolId, maxDepth);
        return TextResult(JsonSerializer.Serialize(new { symbolId, maxDepth, affectedCount = affected.Length, affected }, JsonOpts));
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

using System.Text.Json;
using Lifeblood.Application.UseCases;
using Lifeblood.Connectors.ContextPack;
using Lifeblood.Connectors.Mcp;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// Dispatches MCP tool calls to the appropriate Lifeblood operations.
/// Depends on GraphSession for state, LifebloodMcpProvider for queries.
/// </summary>
public sealed class ToolHandler
{
    private readonly GraphSession _session;
    private readonly LifebloodMcpProvider _provider = new();

    public ToolHandler(GraphSession session) => _session = session;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public McpToolResult Handle(string toolName, JsonElement? arguments)
    {
        try
        {
            return toolName switch
            {
                "lifeblood_analyze" => HandleAnalyze(arguments),
                "lifeblood_context" => HandleContext(),
                "lifeblood_lookup" => HandleLookup(arguments),
                "lifeblood_dependencies" => HandleDependencies(arguments),
                "lifeblood_dependants" => HandleDependants(arguments),
                "lifeblood_blast_radius" => HandleBlastRadius(arguments),
                "lifeblood_execute" => HandleExecute(arguments),
                "lifeblood_diagnose" => HandleDiagnose(arguments),
                "lifeblood_compile_check" => HandleCompileCheck(arguments),
                "lifeblood_find_references" => HandleFindReferences(arguments),
                "lifeblood_rename" => HandleRename(arguments),
                "lifeblood_format" => HandleFormat(arguments),
                _ => ErrorResult($"Unknown tool: {toolName}"),
            };
        }
        catch (Exception ex)
        {
            return ErrorResult($"Error: {ex.Message}");
        }
    }

    private McpToolResult HandleAnalyze(JsonElement? args)
    {
        var projectPath = GetString(args, "projectPath");
        var graphPath = GetString(args, "graphPath");
        var rulesPath = GetString(args, "rulesPath");

        var result = _session.Load(projectPath, graphPath, rulesPath);
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

        var symbolId = GetString(args, "symbolId");
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

        var symbolId = GetString(args, "symbolId");
        if (string.IsNullOrEmpty(symbolId))
            return ErrorResult("symbolId is required");

        var deps = _provider.GetDependencies(_session.Graph!, symbolId);
        return TextResult(JsonSerializer.Serialize(deps, JsonOpts));
    }

    private McpToolResult HandleDependants(JsonElement? args)
    {
        if (!_session.IsLoaded)
            return ErrorResult("No graph loaded. Call lifeblood_analyze first.");

        var symbolId = GetString(args, "symbolId");
        if (string.IsNullOrEmpty(symbolId))
            return ErrorResult("symbolId is required");

        var deps = _provider.GetDependants(_session.Graph!, symbolId);
        return TextResult(JsonSerializer.Serialize(deps, JsonOpts));
    }

    private McpToolResult HandleBlastRadius(JsonElement? args)
    {
        if (!_session.IsLoaded)
            return ErrorResult("No graph loaded. Call lifeblood_analyze first.");

        var symbolId = GetString(args, "symbolId");
        if (string.IsNullOrEmpty(symbolId))
            return ErrorResult("symbolId is required");

        var maxDepth = GetInt(args, "maxDepth") ?? 10;
        var affected = _provider.GetBlastRadius(_session.Graph!, symbolId, maxDepth);
        return TextResult(JsonSerializer.Serialize(new { symbolId, maxDepth, affectedCount = affected.Length, affected }, JsonOpts));
    }

    // ── Write-side tool handlers (require compilation state) ──

    private McpToolResult HandleExecute(JsonElement? args)
    {
        if (!_session.HasCompilationState)
            return ErrorResult("Write-side tools require loading via projectPath (Roslyn adapter). Call lifeblood_analyze with projectPath first.");

        var code = GetString(args, "code");
        if (string.IsNullOrEmpty(code))
            return ErrorResult("code is required");

        var timeoutMs = GetInt(args, "timeoutMs") ?? 5000;
        string[]? imports = null;
        if (args?.TryGetProperty("imports", out var importsEl) == true && importsEl.ValueKind == JsonValueKind.Array)
            imports = importsEl.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s != "").ToArray();

        var result = _session.CodeExecutor!.Execute(code, imports, timeoutMs);
        return TextResult(JsonSerializer.Serialize(result, JsonOpts));
    }

    private McpToolResult HandleDiagnose(JsonElement? args)
    {
        if (!_session.HasCompilationState)
            return ErrorResult("Write-side tools require loading via projectPath (Roslyn adapter). Call lifeblood_analyze with projectPath first.");

        var moduleName = GetString(args, "moduleName");
        var diagnostics = _session.CompilationHost!.GetDiagnostics(moduleName);
        return TextResult(JsonSerializer.Serialize(new { count = diagnostics.Length, diagnostics }, JsonOpts));
    }

    private McpToolResult HandleCompileCheck(JsonElement? args)
    {
        if (!_session.HasCompilationState)
            return ErrorResult("Write-side tools require loading via projectPath (Roslyn adapter). Call lifeblood_analyze with projectPath first.");

        var code = GetString(args, "code");
        if (string.IsNullOrEmpty(code))
            return ErrorResult("code is required");

        var moduleName = GetString(args, "moduleName");
        var result = _session.CompilationHost!.CompileCheck(code, moduleName);
        return TextResult(JsonSerializer.Serialize(result, JsonOpts));
    }

    private McpToolResult HandleFindReferences(JsonElement? args)
    {
        if (!_session.HasCompilationState)
            return ErrorResult("Write-side tools require loading via projectPath (Roslyn adapter). Call lifeblood_analyze with projectPath first.");

        var symbolId = GetString(args, "symbolId");
        if (string.IsNullOrEmpty(symbolId))
            return ErrorResult("symbolId is required");

        var locations = _session.CompilationHost!.FindReferences(symbolId);
        return TextResult(JsonSerializer.Serialize(new { symbolId, count = locations.Length, locations }, JsonOpts));
    }

    private McpToolResult HandleRename(JsonElement? args)
    {
        if (!_session.HasCompilationState)
            return ErrorResult("Write-side tools require loading via projectPath (Roslyn adapter). Call lifeblood_analyze with projectPath first.");

        var symbolId = GetString(args, "symbolId");
        var newName = GetString(args, "newName");
        if (string.IsNullOrEmpty(symbolId)) return ErrorResult("symbolId is required");
        if (string.IsNullOrEmpty(newName)) return ErrorResult("newName is required");

        var edits = _session.Refactoring!.Rename(symbolId, newName);
        return TextResult(JsonSerializer.Serialize(new { symbolId, newName, editCount = edits.Length, edits }, JsonOpts));
    }

    private McpToolResult HandleFormat(JsonElement? args)
    {
        if (!_session.HasCompilationState)
            return ErrorResult("Write-side tools require loading via projectPath (Roslyn adapter). Call lifeblood_analyze with projectPath first.");

        var code = GetString(args, "code");
        if (string.IsNullOrEmpty(code))
            return ErrorResult("code is required");

        var formatted = _session.Refactoring!.Format(code);
        return TextResult(formatted);
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

    private static string? GetString(JsonElement? args, string key)
    {
        if (args == null) return null;
        if (args.Value.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
            return val.GetString();
        return null;
    }

    private static int? GetInt(JsonElement? args, string key)
    {
        if (args == null) return null;
        if (args.Value.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.Number)
            return val.GetInt32();
        return null;
    }
}

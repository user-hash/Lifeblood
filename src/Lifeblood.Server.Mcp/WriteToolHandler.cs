using System.Text.Json;
using Lifeblood.Application.Ports.Right;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// Handles write-side MCP tool calls (require compilation state).
/// Execute, Diagnose, CompileCheck, FindReferences, Rename, Format.
///
/// Symbol-id-bearing handlers (FindReferences, FindDefinition, FindImplementations,
/// Documentation, Rename) route the user's input through <see cref="ISymbolResolver"/>
/// before passing the canonical id to the live workspace tools. See
/// INV-RESOLVER-001 in CLAUDE.md.
/// </summary>
internal sealed class WriteToolHandler
{
    private readonly GraphSession _session;
    private readonly JsonSerializerOptions _jsonOpts;
    private readonly ISymbolResolver _resolver;

    public WriteToolHandler(GraphSession session, JsonSerializerOptions jsonOpts, ISymbolResolver resolver)
    {
        _session = session;
        _jsonOpts = jsonOpts;
        _resolver = resolver;
    }

    public McpToolResult HandleExecute(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;

        var code = GetString(args, "code");
        if (string.IsNullOrEmpty(code))
            return ErrorResult("code is required");

        var timeoutMs = GetInt(args, "timeoutMs") ?? 5000;
        string[]? imports = null;
        if (args?.TryGetProperty("imports", out var importsEl) == true && importsEl.ValueKind == JsonValueKind.Array)
            imports = importsEl.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s != "").ToArray();

        var result = _session.CodeExecutor!.Execute(code, imports, timeoutMs);
        return TextResult(JsonSerializer.Serialize(result, _jsonOpts));
    }

    public McpToolResult HandleDiagnose(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;

        var moduleName = GetString(args, "moduleName");
        var diagnostics = _session.CompilationHost!.GetDiagnostics(moduleName);
        return TextResult(JsonSerializer.Serialize(new { count = diagnostics.Length, diagnostics }, _jsonOpts));
    }

    public McpToolResult HandleCompileCheck(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;

        var code = GetString(args, "code");
        if (string.IsNullOrEmpty(code))
            return ErrorResult("code is required");

        var moduleName = GetString(args, "moduleName");
        var result = _session.CompilationHost!.CompileCheck(code, moduleName);
        return TextResult(JsonSerializer.Serialize(result, _jsonOpts));
    }

    public McpToolResult HandleFindReferences(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;

        var raw = GetString(args, "symbolId");
        if (string.IsNullOrEmpty(raw))
            return ErrorResult("symbolId is required");

        // Plan v4 Seam #1: resolver canonicalizes the input first.
        var resolved = _resolver.Resolve(_session.Graph!, raw);
        if (resolved.CanonicalId == null)
            return ErrorResult(resolved.Diagnostic ?? $"Symbol not found: {raw}");

        // Plan v4 §2.6 / Correction 2: declaration inclusion is operation
        // policy, modeled as an explicit FindReferencesOptions on the host.
        var includeDecls = GetBool(args, "includeDeclarations") ?? false;
        var options = new Lifeblood.Application.Ports.Left.FindReferencesOptions
        {
            IncludeDeclarations = includeDecls,
        };

        var locations = _session.CompilationHost!.FindReferences(resolved.CanonicalId, options);
        return TextResult(JsonSerializer.Serialize(
            new { symbolId = resolved.CanonicalId, count = locations.Length, locations },
            _jsonOpts));
    }

    public McpToolResult HandleRename(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;

        var raw = GetString(args, "symbolId");
        var newName = GetString(args, "newName");
        if (string.IsNullOrEmpty(raw)) return ErrorResult("symbolId is required");
        if (string.IsNullOrEmpty(newName)) return ErrorResult("newName is required");

        var resolved = _resolver.Resolve(_session.Graph!, raw);
        if (resolved.CanonicalId == null)
            return ErrorResult(resolved.Diagnostic ?? $"Symbol not found: {raw}");

        var edits = _session.Refactoring!.Rename(resolved.CanonicalId, newName);
        return TextResult(JsonSerializer.Serialize(
            new { symbolId = resolved.CanonicalId, newName, editCount = edits.Length, edits },
            _jsonOpts));
    }

    public McpToolResult HandleFormat(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;

        var code = GetString(args, "code");
        if (string.IsNullOrEmpty(code))
            return ErrorResult("code is required");

        var formatted = _session.Refactoring!.Format(code);
        return TextResult(formatted);
    }

    public McpToolResult HandleFindDefinition(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;

        var raw = GetString(args, "symbolId");
        if (string.IsNullOrEmpty(raw))
            return ErrorResult("symbolId is required");

        var resolved = _resolver.Resolve(_session.Graph!, raw);
        if (resolved.CanonicalId == null)
            return ErrorResult(resolved.Diagnostic ?? $"Symbol not found: {raw}");

        var def = _session.CompilationHost!.FindDefinition(resolved.CanonicalId);
        if (def == null)
            return ErrorResult($"Definition not found: {resolved.CanonicalId}");

        return TextResult(JsonSerializer.Serialize(def, _jsonOpts));
    }

    public McpToolResult HandleFindImplementations(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;

        var raw = GetString(args, "symbolId");
        if (string.IsNullOrEmpty(raw))
            return ErrorResult("symbolId is required");

        var resolved = _resolver.Resolve(_session.Graph!, raw);
        if (resolved.CanonicalId == null)
            return ErrorResult(resolved.Diagnostic ?? $"Symbol not found: {raw}");

        var impls = _session.CompilationHost!.FindImplementations(resolved.CanonicalId);
        return TextResult(JsonSerializer.Serialize(
            new { symbolId = resolved.CanonicalId, count = impls.Length, implementations = impls },
            _jsonOpts));
    }

    public McpToolResult HandleGetSymbolAtPosition(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;

        var filePath = GetString(args, "filePath");
        var line = GetInt(args, "line");
        var column = GetInt(args, "column");
        if (string.IsNullOrEmpty(filePath) || line == null || column == null)
            return ErrorResult("filePath, line, and column are required");

        var symbol = _session.CompilationHost!.GetSymbolAtPosition(filePath, line.Value, column.Value);
        if (symbol == null)
            return ErrorResult($"No symbol at {filePath}:{line}:{column}");

        return TextResult(JsonSerializer.Serialize(symbol, _jsonOpts));
    }

    public McpToolResult HandleGetDocumentation(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;

        var raw = GetString(args, "symbolId");
        if (string.IsNullOrEmpty(raw))
            return ErrorResult("symbolId is required");

        var resolved = _resolver.Resolve(_session.Graph!, raw);
        if (resolved.CanonicalId == null)
            return ErrorResult(resolved.Diagnostic ?? $"Symbol not found: {raw}");

        var doc = _session.CompilationHost!.GetDocumentation(resolved.CanonicalId);
        return TextResult(string.IsNullOrEmpty(doc)
            ? $"No documentation found for {resolved.CanonicalId}"
            : doc);
    }

    private McpToolResult? CompilationStateError()
    {
        if (_session.HasCompilationState) return null;
        return ErrorResult("Write-side tools require loading via projectPath (Roslyn adapter). Call lifeblood_analyze with projectPath first.");
    }

    internal static string? GetString(JsonElement? args, string key)
    {
        if (args == null) return null;
        if (args.Value.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
            return val.GetString();
        return null;
    }

    internal static int? GetInt(JsonElement? args, string key)
    {
        if (args == null) return null;
        if (args.Value.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.Number)
            return val.GetInt32();
        return null;
    }

    internal static bool? GetBool(JsonElement? args, string key)
    {
        if (args == null) return null;
        if (args.Value.TryGetProperty(key, out var val) &&
            (val.ValueKind == JsonValueKind.True || val.ValueKind == JsonValueKind.False))
            return val.GetBoolean();
        return null;
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

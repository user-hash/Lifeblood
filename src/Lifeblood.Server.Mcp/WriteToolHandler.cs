using System.Text.Json;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Domain.Results;

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

        var targetProfile = GetString(args, "targetProfile");
        var request = new Lifeblood.Application.Ports.Left.CodeExecutionRequest
        {
            Code = code,
            Imports = imports,
            TimeoutMs = timeoutMs,
            TargetProfile = string.IsNullOrEmpty(targetProfile) ? "host" : targetProfile,
        };
        var result = _session.CodeExecutor!.Execute(request);
        return TextResult(JsonSerializer.Serialize(result, _jsonOpts));
    }

    public McpToolResult HandleDiagnose(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;

        var moduleName = GetString(args, "moduleName");
        var filePath = GetString(args, "filePath");

        var request = new Lifeblood.Application.Ports.Left.DiagnosticsRequest
        {
            FilePath = string.IsNullOrEmpty(filePath) ? null : filePath,
            ModuleName = string.IsNullOrEmpty(moduleName) ? null : moduleName,
        };
        // Use the report shape so the wire carries definesActive +
        // resolvedModule alongside the diagnostics. Callers no longer
        // have to re-run with a different define set to tell Editor-
        // only noise apart from release-build risk.
        // INV-DIAGNOSTIC-ENVELOPE-DEFINES-001 / LB-INBOX-008.
        var report = _session.CompilationHost!.GetDiagnosticsReport(request);
        return TextResult(JsonSerializer.Serialize(new
        {
            scope = !string.IsNullOrEmpty(filePath) ? "file" : (!string.IsNullOrEmpty(moduleName) ? "module" : "project"),
            filePath,
            moduleName,
            resolvedModule = string.IsNullOrEmpty(report.ResolvedModule) ? null : report.ResolvedModule,
            count = report.Diagnostics.Length,
            definesActive = report.DefinesActive,
            diagnostics = report.Diagnostics,
        }, _jsonOpts));
    }

    public McpToolResult HandleCompileCheck(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;

        var code = GetString(args, "code");
        var filePath = GetString(args, "filePath");

        // BUG-015: accept either inline `code` or a `filePath`. Exactly one
        // is required; both being set is a caller error because the result
        // would silently depend on which one wins in the handler.
        if (string.IsNullOrEmpty(code) && string.IsNullOrEmpty(filePath))
            return ErrorResult("Either 'code' or 'filePath' is required.");
        if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(filePath))
            return ErrorResult("'code' and 'filePath' are mutually exclusive — supply exactly one.");

        // File-mode hand-off: the host now owns owning-compilation
        // detection AND tree-swapping (LB-BUG-019). Reading the file off
        // disk and stuffing it through the snippet path is what produced
        // the "every type unresolved" failure on Unity files — adding the
        // same file as a fresh tree alongside its own existing tree means
        // the compilation has zero references to the file's siblings,
        // so every cross-file type lookup emits CS0246. The host instead
        // ReplaceSyntaxTree's the file's own tree inside the file's own
        // module compilation, preserving every reference.
        string? overrideCode = null;
        if (!string.IsNullOrEmpty(filePath))
        {
            var resolvedPath = ResolveWorkspacePath(filePath);
            if (!_session.FileSystem.FileExists(resolvedPath))
                return ErrorResult($"File not found: {filePath} (resolved to '{resolvedPath}'). Pass an absolute path or one relative to the loaded project root.");
            try
            {
                // Read the on-disk content so the host can swap it in for
                // the existing tree. Stale-refresh below covers other
                // edited files; this read covers THIS file's edits.
                overrideCode = _session.FileSystem.ReadAllText(resolvedPath);
            }
            catch (System.IO.IOException ex)
            {
                return ErrorResult($"Could not read '{filePath}': {ex.Message}");
            }
        }

        var moduleName = GetString(args, "moduleName");

        // Auto-refresh the workspace if source has been edited since the
        // last analyze. Prevents stale-source errors when a user edits a
        // file then runs compile_check. Opt-out via `staleRefresh:false`
        // for callers that explicitly want the pinned-workspace check.
        var staleRefresh = GetBool(args, "staleRefresh") ?? true;
        var refreshed = staleRefresh ? _session.MaybeRefreshIfStale() : null;

        var request = new CompileCheckRequest
        {
            Code = !string.IsNullOrEmpty(filePath) ? overrideCode : code,
            FilePath = !string.IsNullOrEmpty(filePath) ? filePath : null,
            ModuleName = moduleName,
        };
        var result = _session.CompilationHost!.CompileCheck(request);

        var commonShape = new
        {
            result.Success,
            result.Diagnostics,
            source = !string.IsNullOrEmpty(filePath) ? "filePath" : "code",
            filePath,
            resolvedModule = string.IsNullOrEmpty(result.ResolvedModule) ? null : result.ResolvedModule,
            existingTreeReplaced = result.ExistingTreeReplaced,
            // INV-DIAGNOSTIC-ENVELOPE-DEFINES-001 / LB-INBOX-008.
            definesActive = result.DefinesActive,
        };

        if (refreshed is int changedFileCount)
        {
            return TextResult(JsonSerializer.Serialize(new
            {
                commonShape.Success,
                commonShape.Diagnostics,
                commonShape.source,
                commonShape.filePath,
                commonShape.resolvedModule,
                commonShape.existingTreeReplaced,
                commonShape.definesActive,
                autoRefreshed = true,
                changedFileCount,
            }, _jsonOpts));
        }
        return TextResult(JsonSerializer.Serialize(commonShape, _jsonOpts));
    }

    /// <summary>
    /// Resolve a user-supplied file path against the loaded workspace.
    /// Absolute paths pass through unchanged. Relative paths are joined to
    /// the session's project root. Empty project root (e.g. JSON-graph
    /// mode) leaves the path unchanged so the caller's relative form is
    /// preserved for the FileSystem call.
    /// </summary>
    private string ResolveWorkspacePath(string path)
    {
        if (System.IO.Path.IsPathRooted(path)) return path;
        var root = _session.ProjectRoot;
        if (string.IsNullOrEmpty(root)) return path;
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path));
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

    public McpToolResult HandleEnumCoverage(JsonElement? args)
    {
        if (CompilationStateError() is { } error) return error;

        var raw = GetString(args, "enumTypeId");
        if (string.IsNullOrEmpty(raw))
            return ErrorResult("enumTypeId is required");

        // Route through the same resolver every other type-id-taking
        // tool uses so callers can pass canonical / qualified / bare
        // short type names interchangeably. Mirrors HandleFindReferences.
        var resolved = _resolver.Resolve(_session.Graph!, raw);
        if (resolved.CanonicalId == null)
            return ErrorResult(resolved.Diagnostic ?? $"Symbol not found: {raw}");

        var report = _session.CompilationHost!.GetEnumCoverage(resolved.CanonicalId);
        if (report == null)
            return ErrorResult($"Not an enum type: {resolved.CanonicalId}");

        return TextResult(JsonSerializer.Serialize(new
        {
            enumTypeId = report.EnumTypeId,
            enumTypeName = report.EnumTypeName,
            memberCount = report.Members.Length,
            unproducedCount = report.UnproducedCount,
            unreferencedCount = report.UnreferencedCount,
            members = report.Members,
        }, _jsonOpts));
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

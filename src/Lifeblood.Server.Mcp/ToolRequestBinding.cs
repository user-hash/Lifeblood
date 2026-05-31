using System.Text.Json;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// Typed request-record binding for MCP tool arguments. This is deliberately a
/// Server.Mcp boundary concern: MCP field names live here, while Domain and
/// Application continue to receive protocol-neutral request objects.
/// </summary>
public static class ToolRequestBinder
{
    private const string AnalyzeToolName = "lifeblood_analyze";
    private const string CompileCheckToolName = "lifeblood_compile_check";

    private static readonly string AnalyzeProjectPath = ArgumentName(AnalyzeToolName, "projectPath");
    private static readonly string AnalyzeGraphPath = ArgumentName(AnalyzeToolName, "graphPath");
    private static readonly string AnalyzeRulesPath = ArgumentName(AnalyzeToolName, "rulesPath");
    private static readonly string AnalyzeIncremental = ArgumentName(AnalyzeToolName, "incremental");
    private static readonly string AnalyzeReadOnly = ArgumentName(AnalyzeToolName, "readOnly");
    private static readonly string AnalyzeAllowFullFallback = ArgumentName(AnalyzeToolName, "allowFullFallback");
    private static readonly string AnalyzeDefineProfiles = ArgumentName(AnalyzeToolName, "defineProfiles");

    private static readonly string CompileCheckCode = ArgumentName(CompileCheckToolName, "code");
    private static readonly string CompileCheckFilePath = ArgumentName(CompileCheckToolName, "filePath");
    private static readonly string CompileCheckModuleName = ArgumentName(CompileCheckToolName, "moduleName");
    private static readonly string CompileCheckStaleRefresh = ArgumentName(CompileCheckToolName, "staleRefresh");
    private static readonly string CompileCheckVerbosity = ArgumentName(CompileCheckToolName, "verbosity");

    public static AnalyzeToolRequest BindAnalyze(JsonElement? args)
    {
        if (!TryGetObject(args, out var root))
        {
            return AnalyzeToolRequest.Empty;
        }

        return new AnalyzeToolRequest
        {
            ProjectPath = ReadString(root, AnalyzeProjectPath),
            GraphPath = ReadString(root, AnalyzeGraphPath),
            RulesPath = ReadString(root, AnalyzeRulesPath),
            Incremental = ReadBool(root, AnalyzeIncremental) ?? false,
            ReadOnly = ReadBool(root, AnalyzeReadOnly) ?? false,
            AllowFullFallback = ReadBool(root, AnalyzeAllowFullFallback) ?? false,
            DefineProfiles = ReadStringArray(root, AnalyzeDefineProfiles),
        };
    }

    public static CompileCheckToolRequest BindCompileCheck(JsonElement? args)
    {
        if (!TryGetObject(args, out var root))
        {
            return CompileCheckToolRequest.Empty;
        }

        return new CompileCheckToolRequest
        {
            Code = ReadString(root, CompileCheckCode),
            FilePath = ReadString(root, CompileCheckFilePath),
            ModuleName = ReadString(root, CompileCheckModuleName),
            StaleRefresh = ReadBool(root, CompileCheckStaleRefresh),
            Verbosity = ReadString(root, CompileCheckVerbosity),
        };
    }

    private static string ArgumentName(string toolName, string argumentName)
    {
        var contract = ToolInputContractCatalog.Get(toolName);
        if (!contract.Arguments.ContainsKey(argumentName))
        {
            throw new InvalidOperationException(
                $"Tool request binder references '{argumentName}', but '{toolName}' does not declare that argument in ToolInputContractCatalog.");
        }

        return argumentName;
    }

    private static bool TryGetObject(JsonElement? args, out JsonElement root)
    {
        if (args.HasValue && args.Value.ValueKind == JsonValueKind.Object)
        {
            root = args.Value;
            return true;
        }

        root = default;
        return false;
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? ReadBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static string[]? ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!.Trim())
            .ToArray();
        return values.Length == 0 ? null : values;
    }
}

public sealed record AnalyzeToolRequest
{
    public static AnalyzeToolRequest Empty { get; } = new();

    public string? ProjectPath { get; init; }
    public string? GraphPath { get; init; }
    public string? RulesPath { get; init; }
    public bool Incremental { get; init; }
    public bool ReadOnly { get; init; }
    public bool AllowFullFallback { get; init; }
    public string[]? DefineProfiles { get; init; }
}

public sealed record CompileCheckToolRequest
{
    public static CompileCheckToolRequest Empty { get; } = new();

    public string? Code { get; init; }
    public string? FilePath { get; init; }
    public string? ModuleName { get; init; }
    public bool? StaleRefresh { get; init; }
    public bool EffectiveStaleRefresh => StaleRefresh ?? true;
    public string? Verbosity { get; init; }
}

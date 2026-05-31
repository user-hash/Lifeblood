using System.Text.Json;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// Compatibility posture for MCP tool argument validation. The default
/// remains legacy/back-compatible; warn and strict are operator opt-ins.
/// </summary>
public enum ToolJsonCompatibilityMode
{
    Legacy,
    Warn,
    Strict,
}

public enum ToolArgumentType
{
    Unknown,
    String,
    Integer,
    Number,
    Boolean,
    Array,
    Object,
}

public sealed record ToolArgumentContract(
    string Name,
    ToolArgumentType Type,
    bool Required,
    ToolArgumentType? ArrayItemType,
    string? Description);

public sealed record ToolInputContract(
    string ToolName,
    IReadOnlyDictionary<string, ToolArgumentContract> Arguments)
{
    private static readonly JsonSerializerOptions SchemaJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static ToolInputContract FromSchema(string toolName, object inputSchema)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(inputSchema, SchemaJsonOptions));
        var root = document.RootElement;
        var required = ReadRequiredNames(root);
        var arguments = new Dictionary<string, ToolArgumentContract>(StringComparer.Ordinal);

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                var value = property.Value;
                var type = ReadType(value);
                var itemType = type == ToolArgumentType.Array
                    && value.TryGetProperty("items", out var items)
                        ? ReadType(items)
                        : (ToolArgumentType?)null;
                var description = value.TryGetProperty("description", out var desc)
                    && desc.ValueKind == JsonValueKind.String
                        ? desc.GetString()
                        : null;

                arguments[property.Name] = new ToolArgumentContract(
                    property.Name,
                    type,
                    required.Contains(property.Name),
                    itemType,
                    description);
            }
        }

        foreach (var requiredName in required)
        {
            if (!arguments.ContainsKey(requiredName))
            {
                arguments[requiredName] = new ToolArgumentContract(
                    requiredName,
                    ToolArgumentType.Unknown,
                    Required: true,
                    ArrayItemType: null,
                    Description: null);
            }
        }

        return new ToolInputContract(toolName, arguments);
    }

    private static HashSet<string> ReadRequiredNames(JsonElement root)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("required", out var requiredElement)
            || requiredElement.ValueKind != JsonValueKind.Array)
        {
            return required;
        }

        foreach (var item in requiredElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name)
            {
                required.Add(name);
            }
        }

        return required;
    }

    private static ToolArgumentType ReadType(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String)
        {
            return ToolArgumentType.Unknown;
        }

        return type.GetString() switch
        {
            "string" => ToolArgumentType.String,
            "integer" => ToolArgumentType.Integer,
            "number" => ToolArgumentType.Number,
            "boolean" => ToolArgumentType.Boolean,
            "array" => ToolArgumentType.Array,
            "object" => ToolArgumentType.Object,
            _ => ToolArgumentType.Unknown,
        };
    }
}

public sealed record ToolArgumentDiagnostic(
    string Kind,
    string? Argument,
    string Message,
    string? Expected,
    string? Actual);

public sealed record ToolArgumentBindingResult(
    ToolJsonCompatibilityMode Mode,
    bool Accepted,
    ToolArgumentDiagnostic[] Diagnostics);

public sealed class ToolArgumentBinder
{
    private readonly IReadOnlyDictionary<string, ToolInputContract> _contracts;

    public ToolArgumentBinder(IEnumerable<ToolInputContract> contracts)
    {
        _contracts = contracts.ToDictionary(
            c => c.ToolName,
            c => c,
            StringComparer.Ordinal);
    }

    public ToolArgumentBindingResult Validate(
        string toolName,
        JsonElement? arguments,
        ToolJsonCompatibilityMode mode)
    {
        if (mode == ToolJsonCompatibilityMode.Legacy
            || !_contracts.TryGetValue(toolName, out var contract))
        {
            return new ToolArgumentBindingResult(mode, Accepted: true, Array.Empty<ToolArgumentDiagnostic>());
        }

        var diagnostics = new List<ToolArgumentDiagnostic>();
        if (arguments == null)
        {
            foreach (var required in contract.Arguments.Values.Where(a => a.Required))
            {
                diagnostics.Add(Missing(required.Name));
            }

            return Build(mode, diagnostics);
        }

        if (arguments.Value.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new ToolArgumentDiagnostic(
                "typeMismatch",
                Argument: null,
                Message: "Tool arguments must be a JSON object.",
                Expected: "object",
                Actual: Describe(arguments.Value)));
            return Build(mode, diagnostics);
        }

        var present = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.Value.EnumerateObject())
        {
            present.Add(property.Name);
            if (!seen.Add(property.Name))
            {
                diagnostics.Add(new ToolArgumentDiagnostic(
                    "duplicate",
                    property.Name,
                    $"Duplicate tool argument '{property.Name}' is not allowed in {mode.ToString().ToLowerInvariant()} JSON compatibility mode.",
                    Expected: "single property",
                    Actual: "duplicate property"));
                continue;
            }

            if (!contract.Arguments.TryGetValue(property.Name, out var argument))
            {
                diagnostics.Add(new ToolArgumentDiagnostic(
                    "unknown",
                    property.Name,
                    $"Unknown argument '{property.Name}' for tool '{toolName}'.",
                    Expected: "known argument",
                    Actual: Describe(property.Value)));
                continue;
            }

            if (!Matches(argument, property.Value))
            {
                diagnostics.Add(new ToolArgumentDiagnostic(
                    "typeMismatch",
                    property.Name,
                    $"Argument '{property.Name}' for tool '{toolName}' has the wrong JSON type.",
                    Expected: Describe(argument),
                    Actual: Describe(property.Value)));
            }
        }

        foreach (var required in contract.Arguments.Values.Where(a => a.Required))
        {
            if (!present.Contains(required.Name))
            {
                diagnostics.Add(Missing(required.Name));
            }
        }

        return Build(mode, diagnostics);
    }

    private static ToolArgumentBindingResult Build(
        ToolJsonCompatibilityMode mode,
        List<ToolArgumentDiagnostic> diagnostics)
        => new(
            mode,
            Accepted: mode != ToolJsonCompatibilityMode.Strict || diagnostics.Count == 0,
            diagnostics.ToArray());

    private static ToolArgumentDiagnostic Missing(string name)
        => new(
            "missingRequired",
            name,
            $"Required argument '{name}' is missing.",
            Expected: "present",
            Actual: "missing");

    private static bool Matches(ToolArgumentContract argument, JsonElement value)
    {
        if (argument.Type == ToolArgumentType.Unknown)
        {
            return true;
        }

        return argument.Type switch
        {
            ToolArgumentType.String => value.ValueKind == JsonValueKind.String,
            ToolArgumentType.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            ToolArgumentType.Number => value.ValueKind == JsonValueKind.Number,
            ToolArgumentType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            ToolArgumentType.Array => value.ValueKind == JsonValueKind.Array,
            ToolArgumentType.Object => value.ValueKind == JsonValueKind.Object,
            _ => true,
        };
    }

    private static string Describe(ToolArgumentContract argument)
    {
        var type = argument.Type.ToString().ToLowerInvariant();
        if (argument.Type == ToolArgumentType.Array && argument.ArrayItemType is { } itemType)
        {
            type += $"<{itemType.ToString().ToLowerInvariant()}>";
        }

        return type;
    }

    private static string Describe(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Array => "array",
            JsonValueKind.Object => "object",
            JsonValueKind.Null => "null",
            JsonValueKind.Undefined => "undefined",
            _ => value.ValueKind.ToString().ToLowerInvariant(),
        };
}

public static class ToolJsonCompatibilityModeReader
{
    public static ToolJsonCompatibilityMode ReadFromEnvironment(
        string compatibilityVariableName,
        string strictAliasVariableName)
    {
        var raw = Environment.GetEnvironmentVariable(compatibilityVariableName);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return Parse(raw);
        }

        return ReadFlag(strictAliasVariableName)
            ? ToolJsonCompatibilityMode.Strict
            : ToolJsonCompatibilityMode.Legacy;
    }

    public static ToolJsonCompatibilityMode Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ToolJsonCompatibilityMode.Legacy;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "legacy" or "0" or "false" or "off" => ToolJsonCompatibilityMode.Legacy,
            "warn" or "warning" => ToolJsonCompatibilityMode.Warn,
            "strict" or "1" or "true" or "yes" or "on" => ToolJsonCompatibilityMode.Strict,
            _ => ToolJsonCompatibilityMode.Legacy,
        };
    }

    private static bool ReadFlag(string environmentVariableName)
    {
        var raw = Environment.GetEnvironmentVariable(environmentVariableName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            _ => false,
        };
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;

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
    string? Description,
    IReadOnlyList<string> EnumValues);

public sealed record ToolInputContract(
    string ToolName,
    IReadOnlyList<ToolArgumentContract> ArgumentList)
{
    private static readonly JsonSerializerOptions SchemaJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public IReadOnlyDictionary<string, ToolArgumentContract> Arguments { get; } =
        ArgumentList.ToDictionary(a => a.Name, a => a, StringComparer.Ordinal);

    public static ToolInputContract FromSchema(string toolName, object inputSchema)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(inputSchema, SchemaJsonOptions));
        var root = document.RootElement;
        var required = ReadRequiredNames(root);
        var requiredSet = required.ToHashSet(StringComparer.Ordinal);
        var arguments = new List<ToolArgumentContract>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

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
                var enumValues = ReadEnumValues(value);

                seen.Add(property.Name);
                arguments.Add(new ToolArgumentContract(
                    property.Name,
                    type,
                    requiredSet.Contains(property.Name),
                    itemType,
                    description,
                    enumValues));
            }
        }

        foreach (var requiredName in required)
        {
            if (!seen.Contains(requiredName))
            {
                arguments.Add(new ToolArgumentContract(
                    requiredName,
                    ToolArgumentType.Unknown,
                    Required: true,
                    ArrayItemType: null,
                    Description: null,
                    EnumValues: Array.Empty<string>()));
            }
        }

        return new ToolInputContract(toolName, arguments);
    }

    public JsonElement ToInputSchema()
    {
        var root = new JsonObject
        {
            ["type"] = "object",
        };

        var required = ArgumentList.Where(a => a.Required).Select(a => a.Name).ToArray();
        if (required.Length > 0)
        {
            var requiredArray = new JsonArray();
            foreach (var name in required)
            {
                requiredArray.Add(name);
            }

            root["required"] = requiredArray;
        }

        var properties = new JsonObject();
        foreach (var argument in ArgumentList)
        {
            var schema = new JsonObject
            {
                ["type"] = ToSchemaType(argument.Type),
            };

            if (argument.Type == ToolArgumentType.Array && argument.ArrayItemType is { } itemType)
            {
                schema["items"] = new JsonObject
                {
                    ["type"] = ToSchemaType(itemType),
                };
            }

            if (argument.Description is { Length: > 0 } description)
            {
                schema["description"] = description;
            }

            if (argument.EnumValues.Count > 0)
            {
                var values = new JsonArray();
                foreach (var value in argument.EnumValues)
                {
                    values.Add(value);
                }

                schema["enum"] = values;
            }

            properties[argument.Name] = schema;
        }

        root["properties"] = properties;
        return JsonSerializer.SerializeToElement(root, SchemaJsonOptions);
    }

    private static string ToSchemaType(ToolArgumentType type)
        => type switch
        {
            ToolArgumentType.String => "string",
            ToolArgumentType.Integer => "integer",
            ToolArgumentType.Number => "number",
            ToolArgumentType.Boolean => "boolean",
            ToolArgumentType.Array => "array",
            ToolArgumentType.Object => "object",
            _ => "object",
        };

    private static List<string> ReadRequiredNames(JsonElement root)
    {
        var required = new List<string>();
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

    private static string[] ReadEnumValues(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("enum", out var enumElement)
            || enumElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var values = new List<string>();
        foreach (var item in enumElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } value)
            {
                values.Add(value);
            }
        }

        return values.ToArray();
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
    string? Actual)
{
    // Diagnostic-kind SSoT shared by the producer (ToolArgumentBinder.Validate)
    // and every consumer (ToolHandler argument-binding telemetry buckets). A new
    // kind added here without a telemetry bucket is the drift this prevents.
    public const string KindUnknown = "unknown";
    public const string KindMissingRequired = "missingRequired";
    public const string KindTypeMismatch = "typeMismatch";
    public const string KindDuplicate = "duplicate";
    public const string KindEnumMismatch = "enumMismatch";
}

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
                ToolArgumentDiagnostic.KindTypeMismatch,
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
                    ToolArgumentDiagnostic.KindDuplicate,
                    property.Name,
                    $"Duplicate tool argument '{property.Name}' is not allowed in {mode.ToString().ToLowerInvariant()} JSON compatibility mode.",
                    Expected: "single property",
                    Actual: "duplicate property"));
                continue;
            }

            if (!contract.Arguments.TryGetValue(property.Name, out var argument))
            {
                diagnostics.Add(new ToolArgumentDiagnostic(
                    ToolArgumentDiagnostic.KindUnknown,
                    property.Name,
                    $"Unknown argument '{property.Name}' for tool '{toolName}'.",
                    Expected: "known argument",
                    Actual: Describe(property.Value)));
                continue;
            }

            if (!MatchesType(argument, property.Value))
            {
                diagnostics.Add(new ToolArgumentDiagnostic(
                    ToolArgumentDiagnostic.KindTypeMismatch,
                    property.Name,
                    $"Argument '{property.Name}' for tool '{toolName}' has the wrong JSON type.",
                    Expected: Describe(argument),
                    Actual: Describe(property.Value)));
            }
            else if (!MatchesEnum(argument, property.Value))
            {
                diagnostics.Add(new ToolArgumentDiagnostic(
                    ToolArgumentDiagnostic.KindEnumMismatch,
                    property.Name,
                    $"Argument '{property.Name}' for tool '{toolName}' is outside the declared enum values.",
                    Expected: Describe(argument),
                    Actual: property.Value.GetString()));
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
            ToolArgumentDiagnostic.KindMissingRequired,
            name,
            $"Required argument '{name}' is missing.",
            Expected: "present",
            Actual: "missing");

    private static bool MatchesType(ToolArgumentContract argument, JsonElement value)
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

    private static bool MatchesEnum(ToolArgumentContract argument, JsonElement value)
    {
        if (argument.EnumValues.Count == 0)
        {
            return true;
        }

        return value.ValueKind == JsonValueKind.String
            && value.GetString() is { } raw
            && argument.EnumValues.Contains(raw, StringComparer.Ordinal);
    }

    private static string Describe(ToolArgumentContract argument)
    {
        var type = argument.Type.ToString().ToLowerInvariant();
        if (argument.Type == ToolArgumentType.Array && argument.ArrayItemType is { } itemType)
        {
            type += $"<{itemType.ToString().ToLowerInvariant()}>";
        }

        if (argument.EnumValues.Count > 0)
        {
            type += " enum[" + string.Join(", ", argument.EnumValues) + "]";
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

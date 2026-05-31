using System.Text;
using System.Text.Json;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// JSON request parsing helpers for the MCP stdio host.
/// </summary>
public static class McpJsonRequestParser
{
    private static readonly HashSet<string> JsonRpcEnvelopeProperties = new(StringComparer.Ordinal)
    {
        "jsonrpc",
        "id",
        "method",
        "params",
    };

    /// <summary>
    /// Parse a JSON-RPC request. When strict mode is enabled, duplicate
    /// property names AND unknown envelope properties are rejected before
    /// normal System.Text.Json binding. Unknown properties inside
    /// <c>params</c> stay opaque (bound to <see cref="JsonElement"/>) and are
    /// validated per-tool by the handler — see ToolHandler missing-required
    /// arms.
    /// </summary>
    public static JsonRpcRequest? DeserializeRequest(
        string json,
        JsonSerializerOptions options,
        bool strictJson)
    {
        if (!strictJson)
        {
            return JsonSerializer.Deserialize(json, McpJsonSerializerContext.Default.JsonRpcRequest);
        }

        var utf8 = Encoding.UTF8.GetBytes(json);
        ThrowIfInvalidStrictEnvelope(utf8);

        return JsonSerializer.Deserialize(utf8, McpJsonSerializerContext.Default.JsonRpcRequest);
    }

    public static bool ReadStrictJsonFlag(string environmentVariableName)
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

    private static void ThrowIfInvalidStrictEnvelope(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(
            utf8,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });

        var scopes = new Stack<HashSet<string>>();

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    if (scopes.Count > 0)
                    {
                        scopes.Pop();
                    }
                    break;
                case JsonTokenType.PropertyName:
                    if (scopes.Count == 0)
                    {
                        break;
                    }

                    var propertyName = reader.GetString() ?? "";
                    if (!scopes.Peek().Add(propertyName))
                    {
                        throw new JsonException(
                            $"Duplicate JSON property '{propertyName}' is not allowed when strict JSON mode is enabled.");
                    }

                    if (scopes.Count == 1 && !JsonRpcEnvelopeProperties.Contains(propertyName))
                    {
                        throw new JsonException(
                            $"Unknown JSON-RPC envelope property '{propertyName}' is not allowed when strict JSON mode is enabled.");
                    }

                    break;
            }
        }
    }
}

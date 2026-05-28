using System.Text;
using System.Text.Json;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// JSON request parsing helpers for the MCP stdio host.
/// </summary>
public static class McpJsonRequestParser
{
    /// <summary>
    /// Parse a JSON-RPC request. When strict mode is enabled, duplicate
    /// property names are rejected before normal System.Text.Json binding.
    /// </summary>
    public static JsonRpcRequest? DeserializeRequest(
        string json,
        JsonSerializerOptions options,
        bool strictJson)
    {
        if (strictJson)
        {
            ThrowIfDuplicateProperties(json);
        }

        return JsonSerializer.Deserialize<JsonRpcRequest>(json, options);
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

    private static void ThrowIfDuplicateProperties(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(
            bytes,
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

                    break;
            }
        }
    }
}

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// JSON request parsing helpers for the MCP stdio host.
/// </summary>
public static class McpJsonRequestParser
{
    // Strict-mode derived options are cached per source-options instance so
    // System.Text.Json metadata caching is preserved across requests (the
    // stdio loop passes one stable options instance for the process lifetime).
    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> StrictOptionsCache = new();

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
            return JsonSerializer.Deserialize<JsonRpcRequest>(json, options);
        }

        var utf8 = Encoding.UTF8.GetBytes(json);
        ThrowIfDuplicateProperties(utf8);

        var strictOptions = StrictOptionsCache.GetValue(
            options,
            static src => new JsonSerializerOptions(src)
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            });

        return JsonSerializer.Deserialize<JsonRpcRequest>(utf8, strictOptions);
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

    private static void ThrowIfDuplicateProperties(ReadOnlySpan<byte> utf8)
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

                    break;
            }
        }
    }
}

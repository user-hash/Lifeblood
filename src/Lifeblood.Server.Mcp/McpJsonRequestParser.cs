using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// JSON request parsing helpers for the MCP stdio host.
/// </summary>
public static class McpJsonRequestParser
{
    private static readonly McpJsonSerializerContext StrictContext = new(
        new JsonSerializerOptions(McpJsonSerializerContext.Default.Options)
        {
            AllowDuplicateProperties = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        });

    /// <summary>
    /// Parse a JSON-RPC request. When strict mode is enabled, duplicate
    /// property names and unknown envelope properties are rejected during
    /// System.Text.Json binding. Unknown properties inside
    /// <c>params</c> stay opaque (bound to <see cref="JsonElement"/>) and are
    /// validated per-tool by the handler.
    /// </summary>
    /// <param name="json">One complete request frame.</param>
    /// <param name="options">Compatibility parameter; generated request metadata owns parsing options.</param>
    /// <param name="strictJson">Reject duplicate properties and unknown envelope members.</param>
    public static JsonRpcRequest? DeserializeRequest(
        string json,
        JsonSerializerOptions options,
        bool strictJson)
    {
        return JsonSerializer.Deserialize(json,
            (strictJson ? StrictContext : McpJsonSerializerContext.Default).JsonRpcRequest);
    }

    /// <summary>Parse one complete UTF-8 request frame with the same envelope policy.</summary>
    public static JsonRpcRequest? DeserializeRequest(ReadOnlySpan<byte> utf8, bool strictJson)
        => JsonSerializer.Deserialize(utf8,
            (strictJson ? StrictContext : McpJsonSerializerContext.Default).JsonRpcRequest);

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
}

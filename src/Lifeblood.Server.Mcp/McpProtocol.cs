using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// JSON-RPC 2.0 message types for MCP stdio transport.
/// Minimal, correct, no external dependencies.
/// </summary>

public sealed class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }
}

public sealed class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("result")]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; set; }
}

public sealed class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

/// <summary>
/// MCP-specific response shapes for tools/list and tools/call.
/// </summary>
public sealed class McpToolInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("inputSchema")]
    public object InputSchema { get; set; } = new { type = "object" };
}

public sealed class McpToolResult
{
    [JsonPropertyName("content")]
    public McpContent[] Content { get; set; } = Array.Empty<McpContent>();

    [JsonPropertyName("isError")]
    public bool? IsError { get; set; }
}

public sealed class McpContent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

public sealed class McpServerInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";
}

public sealed class McpCapabilities
{
    [JsonPropertyName("tools")]
    public object Tools { get; set; } = new { };
}

public sealed class McpInitializeResult
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = "2024-11-05";

    [JsonPropertyName("capabilities")]
    public McpCapabilities Capabilities { get; set; } = new();

    [JsonPropertyName("serverInfo")]
    public McpServerInfo ServerInfo { get; set; } = new();
}

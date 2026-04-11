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
/// Classification of an MCP tool by whether it requires a loaded compilation
/// state. ReadSide tools work off the in-memory semantic graph and are always
/// available once a project (or a graph.json file) has been analyzed.
/// WriteSide tools require live Roslyn compilations retained from the last
/// analyze and are marked unavailable when the session is empty.
///
/// INV-TOOLREG-001: every McpToolInfo sets Availability explicitly at
/// registration. The GetTools(hasCompilationState) guard filters on this
/// field. Never on tool name prefixes.
/// </summary>
public enum ToolAvailability
{
  ReadSide,
  WriteSide,
}

/// <summary>
/// Wire-format DTO for the MCP <c>tools/list</c> response. Pure JSON
/// serialization shape — name, description, input schema. No internal
/// concerns. See <see cref="ToolDefinition"/> for the internal registry
/// record that carries compile-time availability metadata.
///
/// <para>
/// INV-TOOLREG-001 rationale for the split: the original design used a
/// single <c>McpToolInfo</c> type for BOTH the internal registry record
/// (where <c>required ToolAvailability</c> gives compile-time enforcement)
/// AND the wire payload for <c>tools/list</c> (where System.Text.Json
/// serialization happens). System.Text.Json in .NET 8 has a latent bug
/// where <c>[JsonIgnore]</c> on a <c>required init</c> property is NOT
/// honoured during serialization metadata construction, so <c>tools/list</c>
/// threw <c>JsonException</c> "property is marked required but does not
/// specify a setter" at runtime. Claude Code interpreted the error as
/// a broken server and aborted connection. The fix — and the reason the
/// types are split — is that wire DTOs and internal records are
/// different concerns, and conflating them caused the serialization bug
/// plus the Claude Code connection failure.
/// </para>
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

/// <summary>
/// Internal registry record for a Lifeblood MCP tool. Pairs the wire
/// shape (name, description, input schema) with compile-time classification
/// metadata (<see cref="Availability"/>). Lives only inside the server;
/// projected to <see cref="McpToolInfo"/> at <c>tools/list</c> time.
///
/// <para>
/// <b>INV-TOOLREG-001:</b> every <c>ToolDefinition</c> sets
/// <see cref="Availability"/> explicitly at registration. The property is
/// <c>required</c>, so omitting it is a compile error — not a runtime
/// default-0 bug. <c>ToolRegistry.GetTools(bool)</c> filters on this
/// field to decide which tools receive the "[Unavailable. Load a project
/// with lifeblood_analyze first]" decoration in their wire descriptions.
/// </para>
/// </summary>
public sealed class ToolDefinition
{
  public required string Name { get; init; }
  public required string Description { get; init; }
  public required object InputSchema { get; init; }
  public required ToolAvailability Availability { get; init; }
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

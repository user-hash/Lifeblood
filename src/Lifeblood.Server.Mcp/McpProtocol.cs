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

  /// <summary>
  /// Optional JSON-RPC 2.0 <c>data</c> member. Carries the structured fatal
  /// envelope on internal-error responses (phase / recoverable / recovery hint)
  /// so an agent can decide whether to retry, re-analyze, or reconnect instead
  /// of treating an opaque transport drop as terminal. INV-MCP-TRANSPORT-RESILIENCE-001.
  /// </summary>
  [JsonPropertyName("data")]
  public object? Data { get; set; }
}

/// <summary>
/// Legacy compatibility projection for the original MCP tool split. New
/// policy must consume <see cref="ToolBehavior"/> instead: "write side" means
/// only that retained Roslyn compilations are required; it says nothing about
/// whether a tool mutates the session or returns proposed edits.
/// </summary>
public enum ToolAvailability
{
  ReadSide,
  WriteSide,
}

/// <summary>
/// Minimum retained state a tool needs before its handler can run.
/// </summary>
public enum ToolSessionRequirement
{
  None,
  AnalyzedWorkspace,
  WorkspaceRoot,
  RetainedCompilation,
}

/// <summary>
/// Externally meaningful effect of a tool call. This is independent from the
/// lock used to protect the retained session.
/// </summary>
public enum ToolEffect
{
  Observe,
  RefreshWorkspace,
  ManageSnapshotCatalog,
  ExecuteCode,
  PreviewChanges,
}

/// <summary>
/// Host-edge access required while a tool consumes the retained session.
/// </summary>
public enum ToolSessionAccess
{
  SharedRead,
  Exclusive,
}

/// <summary>
/// Immutable per-tool policy contract. Keeping the three axes together makes
/// the registry the single source for availability, dispatch locking, and
/// agent-facing capability metadata without conflating those concerns.
/// </summary>
public sealed record ToolBehavior(
  ToolSessionRequirement SessionRequirement,
  ToolEffect Effect,
  ToolSessionAccess SessionAccess);

/// <summary>
/// Process-local session facts used to evaluate a <see cref="ToolBehavior"/>.
/// </summary>
public readonly record struct ToolSessionState(
  bool HasAnalyzedWorkspace,
  bool HasWorkspaceRoot,
  bool HasRetainedCompilation)
{
  public bool Satisfies(ToolSessionRequirement requirement) => requirement switch
  {
    ToolSessionRequirement.None => true,
    ToolSessionRequirement.AnalyzedWorkspace => HasAnalyzedWorkspace,
    ToolSessionRequirement.WorkspaceRoot => HasAnalyzedWorkspace && HasWorkspaceRoot,
    ToolSessionRequirement.RetainedCompilation => HasAnalyzedWorkspace && HasRetainedCompilation,
    _ => false,
  };
}

/// <summary>
/// Wire-format DTO for the MCP <c>tools/list</c> response. Pure JSON
/// serialization shape — name, description, input schema. No internal
/// concerns. See <see cref="ToolDefinition"/> for the internal registry
/// record that carries compile-time behavior metadata.
///
/// <para>
/// INV-TOOLREG-001 rationale for the split: the original design used a
/// single <c>McpToolInfo</c> type for BOTH the internal registry record
/// (where <c>required ToolBehavior</c> gives compile-time enforcement)
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
/// shape (name, description, input schema) with compile-time behavior
/// metadata (<see cref="Behavior"/>). Lives only inside the server;
/// projected to <see cref="McpToolInfo"/> at <c>tools/list</c> time.
///
/// <para>
/// <b>INV-TOOLREG-001:</b> every <c>ToolDefinition</c> sets one immutable
/// <see cref="ToolBehavior"/> explicitly at registration. The property is
/// required, so omitting the state/effect/access contract is a compile error.
/// Availability decoration, prerequisite checks, session locking, and
/// capability reporting all derive from that same contract.
/// </para>
/// </summary>
public sealed class ToolDefinition
{
  public required string Name { get; init; }
  public required string Description { get; init; }
  public required ToolBehavior Behavior { get; init; }

  /// <summary>
  /// Optional per-call behavior resolver for action-based tools. The registry
  /// still owns the policy; dispatch asks this definition for the call's
  /// effective behavior instead of branching on tool names.
  /// </summary>
  public Func<JsonElement?, ToolBehavior>? CallBehavior { get; init; }

  public ToolBehavior ResolveCallBehavior(JsonElement? arguments)
    => CallBehavior?.Invoke(arguments) ?? Behavior;

  /// <summary>
  /// Backward-compatible read/write projection. This is derived, never
  /// registered independently, so it cannot drift from the retained-state
  /// requirement that the old labels represented.
  /// </summary>
  public ToolAvailability Availability =>
    Behavior.SessionRequirement == ToolSessionRequirement.RetainedCompilation
      ? ToolAvailability.WriteSide
      : ToolAvailability.ReadSide;
  public bool SupportsSnapshotRead =>
    Behavior.Effect == ToolEffect.Observe
    && Behavior.SessionAccess == ToolSessionAccess.SharedRead;

  public ToolInputContract InputContract =>
    ToolInputContractCatalog.Get(Name, SupportsSnapshotRead);
  public object InputSchema => InputContract.ToInputSchema();

  /// <summary>
  /// Truth-envelope classification carried by every successful response
  /// from this tool. Required for every tool (INV-ENVELOPE-001). Single source of truth: the
  /// envelope decorator reads this field directly off <c>ToolRegistry</c>
  /// at decoration time, so the per-tool tier / confidence / evidence /
  /// limitations cannot drift between the registry and the decorator.
  /// Pinned by <c>ResponseEnvelopeTests.Decorator_AllReadSideToolsInRegistry_HaveClassification</c>.
  /// </summary>
  public Lifeblood.Domain.Results.EnvelopeClassification? EnvelopeClassification { get; init; }
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

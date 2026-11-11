using System.Text.Json;
using Lifeblood.Connectors.Mcp;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// JSON-RPC 2.0 / MCP protocol dispatcher. Routes incoming requests to the
/// right handler, distinguishes notifications from requests, and enforces
/// the MCP spec-compliant response shape on <c>initialize</c>.
///
/// Separated from <see cref="Program"/> so the stdio I/O loop stays a thin
/// composition root and the dispatch logic is testable via its public API
/// without visibility tricks (no <c>InternalsVisibleTo</c>, no reflection).
///
/// Invariants enforced here:
/// <list type="bullet">
/// <item><c>INV-MCP-001</c>: the <c>initialize</c> response always carries
/// <c>protocolVersion</c> and <c>capabilities</c>, populated from the single
/// constants and typed <see cref="McpCapabilities"/> owned by
/// <see cref="McpProtocol"/> shapes.</item>
/// <item><c>INV-MCP-002</c>: notifications (messages with no <c>id</c>)
/// never receive a response body. Both the spec-compliant
/// <c>notifications/initialized</c> form and the legacy <c>initialized</c>
/// alias are accepted.</item>
/// <item><c>INV-MCP-003</c>: every MCP wire constant (protocol version,
/// method name, notification name) is sourced from
/// <see cref="McpProtocolSpec"/> — the single source of truth shared
/// with every first-party client. No hardcoded protocol strings live
/// in this file.</item>
/// </list>
/// </summary>
public sealed class McpDispatcher
{
  /// <summary>
  /// Canonical MCP protocol version this server speaks. Sourced from
  /// <see cref="McpProtocolSpec.SupportedVersion"/> so the server and
  /// every first-party client agree on a single literal.
  /// </summary>
  public const string SupportedProtocolVersion = McpProtocolSpec.SupportedVersion;

  /// <summary>
  /// JSON-RPC 2.0 / MCP notification method names the dispatcher
  /// short-circuits to return <c>null</c> before response construction.
  /// Derived directly from <see cref="McpProtocolSpec.AllKnownNotifications"/>
  /// so adding a notification is a single edit in the protocol spec.
  /// </summary>
  private static readonly HashSet<string> KnownNotifications =
    new(McpProtocolSpec.AllKnownNotifications, StringComparer.Ordinal);

  private readonly ToolHandler _toolHandler;
  private readonly string _serverVersion;
  private string _schemaVersion = McpProtocolSpec.SchemaVersions.Legacy;
  private string _resultFormat = McpProtocolSpec.ResultFormats.Text;
  private readonly string? _clientLeaseId;

  public McpDispatcher(ToolHandler toolHandler, string? clientLeaseId = null)
  {
  _toolHandler = toolHandler;
  _clientLeaseId = clientLeaseId;
  _serverVersion = ServerIdentity.ResolveServerVersion();
  }

  /// <summary>
  /// Dispatch a JSON-RPC request. Returns <c>null</c> for notifications
  /// (messages with no <c>id</c>), a populated <see cref="JsonRpcResponse"/>
  /// for requests. Never throws; internal errors surface via
  /// <see cref="JsonRpcResponse.Error"/>.
  /// </summary>
  public JsonRpcResponse? Dispatch(
    JsonRpcRequest request,
    CancellationToken cancellationToken = default)
  {
  // INV-MCP-002: notifications never receive a response body, known or
  // unknown. Detect notification shape once, short-circuit before any
  // response construction.
  var isNotification = request.Id == null;

  if (KnownNotifications.Contains(request.Method))
  {
  return null;
  }

  if (isNotification)
  {
  // Unknown notification. Still no response body per JSON-RPC 2.0.
  // Log to stderr so operators can spot unexpected traffic.
  Console.Error.WriteLine($"Unknown notification method: {request.Method}");
  return null;
  }

  return request.Method switch
  {
  McpProtocolSpec.Methods.Initialize => HandleInitialize(request),
  McpProtocolSpec.Methods.ToolsList => HandleToolsList(request),
  McpProtocolSpec.Methods.ToolsCall => HandleToolsCall(request, cancellationToken),
  "ping" => new JsonRpcResponse { Id = request.Id, Result = new { } },
  _ => new JsonRpcResponse
  {
  Id = request.Id,
  Error = new JsonRpcError
  {
  Code = -32601,
  Message = $"Method not found: {request.Method}",
  },
  },
  };
  }

  // ─── Per-method handlers ─────────────────────────────────────────────

  private JsonRpcResponse HandleInitialize(JsonRpcRequest request)
  {
  var selected = McpProtocolSpec.SchemaVersions.Legacy;
  if (request.Params is { ValueKind: JsonValueKind.Object } parameters
      && parameters.TryGetProperty(McpProtocolSpec.SchemaVersions.InitializeParameter, out var version))
  {
  if (version.ValueKind != JsonValueKind.String
      || version.GetString() is not (McpProtocolSpec.SchemaVersions.Legacy or McpProtocolSpec.SchemaVersions.Canonical))
  return new JsonRpcResponse { Id = request.Id, Error = new JsonRpcError {
      Code = -32602, Message = "Unsupported Lifeblood schema version. Select v1 or v2 during initialize." } };
  selected = version.GetString()!;
  }
  var protocol = McpProtocolSpec.SupportedVersion;
  var format = McpProtocolSpec.ResultFormats.Text;
  if (request.Params is { ValueKind: JsonValueKind.Object } initialize)
  {
      if (initialize.TryGetProperty("protocolVersion", out var requestedProtocol))
      {
          if (requestedProtocol.ValueKind != JsonValueKind.String)
              return InitializeError(request, "protocolVersion must be a string.");
          protocol = requestedProtocol.GetString() == McpProtocolSpec.SupportedVersion
              ? McpProtocolSpec.SupportedVersion : McpProtocolSpec.StructuredContentVersion;
      }
      format = protocol == McpProtocolSpec.StructuredContentVersion
          ? McpProtocolSpec.ResultFormats.Dual : McpProtocolSpec.ResultFormats.Text;
      if (initialize.TryGetProperty("capabilities", out var capabilities)
          && capabilities.ValueKind == JsonValueKind.Object
          && capabilities.TryGetProperty("experimental", out var experimental)
          && experimental.ValueKind == JsonValueKind.Object
          && experimental.TryGetProperty(McpProtocolSpec.ResultFormats.Capability, out var requestedFormat))
      {
          if (requestedFormat.ValueKind != JsonValueKind.String
              || requestedFormat.GetString() is not (McpProtocolSpec.ResultFormats.Text or McpProtocolSpec.ResultFormats.Dual or McpProtocolSpec.ResultFormats.StructuredOnly))
              return InitializeError(request, "Unsupported Lifeblood result format. Select text, dual, or structuredOnly.");
          format = requestedFormat.GetString()!;
          if (protocol == McpProtocolSpec.SupportedVersion && format != McpProtocolSpec.ResultFormats.Text)
              return InitializeError(request, "Structured results require the structured-content MCP revision.");
      }
  }
  _schemaVersion = selected;
  _resultFormat = format;

  // INV-MCP-001: protocolVersion and capabilities are required by the
  // MCP spec. They also have class-level defaults on McpInitializeResult
  // as a belt-and-braces guard, but setting them explicitly here makes
  // the contract visible at the call site.
  return new JsonRpcResponse
  {
  Id = request.Id,
  Result = new McpInitializeResult
  {
  ProtocolVersion = protocol,
  SchemaVersion = _schemaVersion,
  Capabilities = new McpCapabilities
  {
      Experimental = protocol == McpProtocolSpec.StructuredContentVersion
          ? new Dictionary<string, object>
          {
              [McpProtocolSpec.ResultFormats.Capability] = new
              {
                  selected = format,
                  supported = new[] { McpProtocolSpec.ResultFormats.Text, McpProtocolSpec.ResultFormats.Dual, McpProtocolSpec.ResultFormats.StructuredOnly },
                  maximumProjectionCharacters = McpStructuredContentProjection.MaximumProjectionCharacters,
              },
          } : null,
  },
  ServerInfo = new McpServerInfo
  {
  Name = "lifeblood",
  Version = _serverVersion,
  },
  },
  };
  }

  private static JsonRpcResponse InitializeError(JsonRpcRequest request, string message)
      => new() { Id = request.Id, Error = new JsonRpcError { Code = -32602, Message = message } };

  private JsonRpcResponse HandleToolsList(JsonRpcRequest request)
  {
  return new JsonRpcResponse
  {
  Id = request.Id,
  Result = new { tools = _toolHandler.GetTools() },
  };
  }

  private JsonRpcResponse HandleToolsCall(
    JsonRpcRequest request,
    CancellationToken cancellationToken)
  {
  var response = new JsonRpcResponse { Id = request.Id };

  if (!request.Params.HasValue)
  {
  response.Error = new JsonRpcError
  {
  Code = -32602,
  Message = "params is required for tools/call",
  };
  return response;
  }

  var callParams = request.Params.Value;

  if (!callParams.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
  {
  response.Error = new JsonRpcError
  {
  Code = -32602,
  Message = "name (string) is required in tools/call params",
  };
  return response;
  }

  var toolName = nameEl.GetString() ?? "";
  JsonElement? arguments = null;
  if (callParams.TryGetProperty("arguments", out var argsEl))
  {
  arguments = argsEl;
  }

  var schemaVersion = _schemaVersion;
  var format = _resultFormat;
  var result = _toolHandler.Handle(toolName, arguments, cancellationToken, _clientLeaseId);
  var selected = schemaVersion == McpProtocolSpec.SchemaVersions.Legacy
      ? result.LegacyProjection ?? result : result;
  response.Result = McpStructuredContentProjection.Project(selected, format);
  return response;
  }

}

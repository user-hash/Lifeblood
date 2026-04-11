using System.Reflection;
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

  private readonly GraphSession _session;
  private readonly ToolHandler _toolHandler;
  private readonly string _serverVersion;

  public McpDispatcher(GraphSession session, ToolHandler toolHandler)
  {
  _session = session;
  _toolHandler = toolHandler;
  _serverVersion = ResolveServerVersion();
  }

  /// <summary>
  /// Dispatch a JSON-RPC request. Returns <c>null</c> for notifications
  /// (messages with no <c>id</c>), a populated <see cref="JsonRpcResponse"/>
  /// for requests. Never throws; internal errors surface via
  /// <see cref="JsonRpcResponse.Error"/>.
  /// </summary>
  public JsonRpcResponse? Dispatch(JsonRpcRequest request)
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
  McpProtocolSpec.Methods.ToolsCall => HandleToolsCall(request),
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
  // INV-MCP-001: protocolVersion and capabilities are required by the
  // MCP spec. They also have class-level defaults on McpInitializeResult
  // as a belt-and-braces guard, but setting them explicitly here makes
  // the contract visible at the call site.
  return new JsonRpcResponse
  {
  Id = request.Id,
  Result = new McpInitializeResult
  {
  ProtocolVersion = SupportedProtocolVersion,
  Capabilities = new McpCapabilities(),
  ServerInfo = new McpServerInfo
  {
  Name = "lifeblood",
  Version = _serverVersion,
  },
  },
  };
  }

  private JsonRpcResponse HandleToolsList(JsonRpcRequest request)
  {
  return new JsonRpcResponse
  {
  Id = request.Id,
  Result = new { tools = ToolRegistry.GetTools(_session.HasCompilationState) },
  };
  }

  private JsonRpcResponse HandleToolsCall(JsonRpcRequest request)
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

  response.Result = _toolHandler.Handle(toolName, arguments);
  return response;
  }

  // ─── Version resolution ──────────────────────────────────────────────

  /// <summary>
  /// Prefer <see cref="AssemblyInformationalVersionAttribute"/> because
  /// MinVer populates it with the full semver-plus-provenance form
  /// (e.g. <c>0.6.0+abc123</c> or <c>0.6.1-pre.0.3+def456</c>).
  /// Falls back to the plain <c>AssemblyName.Version</c> three-part form,
  /// then to <c>"0.0.0"</c> if neither is set. Resolved once per dispatcher
  /// instance. The assembly metadata does not change at runtime.
  /// </summary>
  private static string ResolveServerVersion()
  {
  var asm = typeof(McpDispatcher).Assembly;

  var informational = asm
  .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
  .InformationalVersion;
  if (!string.IsNullOrWhiteSpace(informational))
  {
  return informational!;
  }

  return asm.GetName().Version?.ToString(3) ?? "0.0.0";
  }
}

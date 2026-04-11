using System.Text.Json;
using System.Text.Json.Serialization;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Analysis;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Tests for the MCP JSON-RPC protocol dispatcher
/// <see cref="McpDispatcher"/>. Exercises the MCP-spec-compliant shapes for
/// <c>initialize</c> and the <c>notifications/initialized</c> notification,
/// plus the legacy <c>initialized</c> alias.
///
/// Invariants pinned:
/// INV-MCP-001. Initialize response always carries protocolVersion and capabilities.
/// INV-MCP-002. Notifications never receive a response body.
/// INV-TOOLREG-001. Tool availability dispatch is by typed enum, not name prefix.
/// </summary>
public class McpProtocolTests
{
  private static readonly JsonSerializerOptions JsonOpts = new()
  {
  PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
  DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
  };

  private static readonly PhysicalFileSystem Fs = new();

  private sealed class TestBlastRadiusProvider : IBlastRadiusProvider
  {
  public BlastRadiusResult Analyze(SemanticGraph graph, string targetSymbolId, int maxDepth = 10)
  => BlastRadiusAnalyzer.Analyze(graph, targetSymbolId, maxDepth);
  }

  private static McpDispatcher CreateDispatcher()
  {
  var session = new GraphSession(Fs);
  IMcpGraphProvider provider = new LifebloodMcpProvider(new TestBlastRadiusProvider());
  ISymbolResolver resolver = new LifebloodSymbolResolver();
  ISemanticSearchProvider search = new LifebloodSemanticSearchProvider();
  IDeadCodeAnalyzer deadCode = new LifebloodDeadCodeAnalyzer();
  IPartialViewBuilder partialView = new LifebloodPartialViewBuilder(Fs);
  var handler = new ToolHandler(session, provider, resolver, search, deadCode, partialView);
  return new McpDispatcher(session, handler);
  }

  private static JsonRpcRequest MakeRequest(string method, int? id = 1, object? @params = null)
  {
  var req = new JsonRpcRequest { Method = method };
  if (id.HasValue)
  {
  var idJson = JsonSerializer.Serialize(id.Value);
  req.Id = JsonSerializer.Deserialize<JsonElement>(idJson);
  }
  if (@params != null)
  {
  var paramsJson = JsonSerializer.Serialize(@params);
  req.Params = JsonSerializer.Deserialize<JsonElement>(paramsJson);
  }
  return req;
  }

  // ──────────────────────────────────────────────────────────────────
  // INV-MCP-001: initialize response carries protocolVersion + capabilities
  // ──────────────────────────────────────────────────────────────────

  [Fact]
  public void Initialize_ReturnsSpecCompliantResult()
  {
  var dispatcher = CreateDispatcher();
  var request = MakeRequest("initialize");

  var response = dispatcher.Dispatch(request);

  Assert.NotNull(response);
  Assert.Null(response!.Error);

  var result = Assert.IsType<McpInitializeResult>(response.Result);
  Assert.Equal("2024-11-05", result.ProtocolVersion);
  Assert.NotNull(result.Capabilities);
  Assert.NotNull(result.Capabilities.Tools);
  Assert.Equal("lifeblood", result.ServerInfo.Name);
  Assert.False(string.IsNullOrWhiteSpace(result.ServerInfo.Version),
  "serverInfo.version must be non-empty. Should come from AssemblyInformationalVersionAttribute.");
  }

  [Fact]
  public void Initialize_SerializedJson_HasProtocolVersionAndCapabilities()
  {
  // Belt-and-braces: even if the class defaults drift, the serialized
  // wire form must carry these fields. Verifies the full round-trip
  // through JSON serialization so a missing JsonPropertyName wouldn't
  // slip past Initialize_ReturnsSpecCompliantResult.
  var dispatcher = CreateDispatcher();
  var request = MakeRequest("initialize");

  var response = dispatcher.Dispatch(request);
  Assert.NotNull(response);

  var json = JsonSerializer.Serialize(response, JsonOpts);
  using var doc = JsonDocument.Parse(json);
  var root = doc.RootElement;

  var result = root.GetProperty("result");
  Assert.Equal("2024-11-05", result.GetProperty("protocolVersion").GetString());
  Assert.Equal(JsonValueKind.Object, result.GetProperty("capabilities").ValueKind);
  Assert.Equal(JsonValueKind.Object, result.GetProperty("capabilities").GetProperty("tools").ValueKind);
  Assert.Equal("lifeblood", result.GetProperty("serverInfo").GetProperty("name").GetString());
  }

  // ──────────────────────────────────────────────────────────────────
  // INV-MCP-002: notifications never receive a response body
  // ──────────────────────────────────────────────────────────────────

  [Fact]
  public void NotificationsInitialized_SpecCompliantForm_ProducesNoResponse()
  {
  // The MCP spec canonical notification method is "notifications/initialized".
  // Previously the dispatcher only matched the bare "initialized" alias and
  // a spec-compliant client would have received a -32601 error (double
  // spec violation: unknown method response AND response body for a notification).
  var dispatcher = CreateDispatcher();
  var request = MakeRequest("notifications/initialized", id: null);

  var response = dispatcher.Dispatch(request);

  Assert.Null(response);
  }

  [Fact]
  public void NotificationsInitialized_LegacyAlias_ProducesNoResponse()
  {
  // Back-compat: the bare "initialized" form must still be accepted during
  // the deprecation window. Same rule. No response body.
  var dispatcher = CreateDispatcher();
  var request = MakeRequest("initialized", id: null);

  var response = dispatcher.Dispatch(request);

  Assert.Null(response);
  }

  [Fact]
  public void UnknownNotification_ProducesNoResponse()
  {
  // Any method sent with no id is a notification per JSON-RPC 2.0.
  // Even if the method name is unknown, the server MUST NOT respond.
  // Pre-fix, the default switch branch would have constructed a -32601
  // response body, violating the spec.
  var dispatcher = CreateDispatcher();
  var request = MakeRequest("notifications/fabricated", id: null);

  var response = dispatcher.Dispatch(request);

  Assert.Null(response);
  }

  [Fact]
  public void UnknownRequest_ReturnsMethodNotFound()
  {
  // Contrast with unknown notification: a request (has an id) with an
  // unknown method MUST return -32601.
  var dispatcher = CreateDispatcher();
  var request = MakeRequest("fabricated/method", id: 42);

  var response = dispatcher.Dispatch(request);

  Assert.NotNull(response);
  Assert.NotNull(response!.Error);
  Assert.Equal(-32601, response.Error!.Code);
  Assert.Contains("fabricated/method", response.Error.Message);
  }

  // ──────────────────────────────────────────────────────────────────
  // INV-TOOLREG-001: availability dispatch is by typed enum
  // ──────────────────────────────────────────────────────────────────

  [Fact]
  public void ToolRegistry_EveryToolHasExplicitAvailability()
  {
  // C#'s required modifier on Availability makes this impossible to omit
  // at declaration. Verifying here belt-and-braces that every tool
  // comes back with a definite enum value. Neither default (0) by
  // accident nor an undocumented extra.
  var tools = ToolRegistry.GetTools(hasCompilationState: true);
  Assert.NotEmpty(tools);
  foreach (var tool in tools)
  {
  Assert.True(
  tool.Availability == ToolAvailability.ReadSide || tool.Availability == ToolAvailability.WriteSide,
  $"Tool {tool.Name} has unexpected Availability: {tool.Availability}");
  }
  }

  [Fact]
  public void ToolRegistry_WriteSideTools_MarkedUnavailable_WhenNoCompilationState()
  {
  var tools = ToolRegistry.GetTools(hasCompilationState: false);
  var writeSide = tools.Where(t => t.Availability == ToolAvailability.WriteSide).ToArray();
  Assert.NotEmpty(writeSide);
  Assert.All(writeSide, tool =>
  Assert.StartsWith("[Unavailable", tool.Description));
  }

  [Fact]
  public void ToolRegistry_ReadSideTools_NeverMarkedUnavailable()
  {
  // Read-side tools (including lifeblood_resolve_short_name) work off
  // the in-memory graph alone and do not need Roslyn compilation state.
  // They must never carry the unavailable decoration.
  var tools = ToolRegistry.GetTools(hasCompilationState: false);
  var readSide = tools.Where(t => t.Availability == ToolAvailability.ReadSide).ToArray();
  Assert.NotEmpty(readSide);
  Assert.All(readSide, tool =>
  Assert.False(tool.Description.StartsWith("[Unavailable"),
  $"Read-side tool {tool.Name} was incorrectly marked unavailable."));
  }

  [Fact]
  public void ToolRegistry_ResolveShortName_IsClassifiedReadSide()
  {
  // Pin the classification decision from FINDING-005. The previous
  // prefix-based guard silently misclassified this tool because its
  // name did not match any of the 8 hard-coded prefixes. The typed
  // Availability field makes the decision explicit and test-enforced.
  var tools = ToolRegistry.GetTools(hasCompilationState: true);
  var resolver = tools.Single(t => t.Name == "lifeblood_resolve_short_name");
  Assert.Equal(ToolAvailability.ReadSide, resolver.Availability);
  }
}

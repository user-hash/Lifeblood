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
/// INV-TOOLREG-001. Tool behavior dispatch is typed, not name-based.
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
  Lifeblood.Application.Ports.Right.Invariants.IInvariantProvider invariants
  = new LifebloodInvariantProvider(Fs);
  var classifications = ToolRegistry.GetDefinitions()
      .Where(d => d.EnvelopeClassification != null)
      .ToDictionary(d => d.Name, d => d.EnvelopeClassification!, System.StringComparer.Ordinal);
  IResponseDecorator decorator = new LifebloodResponseDecorator(classifications);
  var handler = new ToolHandler(session, provider, resolver, search, deadCode, partialView, invariants, decorator);
  return new McpDispatcher(handler);
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
  // INV-TOOLREG-001: one typed behavior contract owns tool policy
  // ──────────────────────────────────────────────────────────────────

  [Fact]
  public void ToolRegistry_EveryToolHasExpectedBehaviorContract()
  {
  var noneObserve = new ToolBehavior(ToolSessionRequirement.None, ToolEffect.Observe, ToolSessionAccess.SharedRead);
  var refresh = new ToolBehavior(ToolSessionRequirement.None, ToolEffect.RefreshWorkspace, ToolSessionAccess.Exclusive);
  var snapshotCatalog = new ToolBehavior(ToolSessionRequirement.None, ToolEffect.ManageSnapshotCatalog, ToolSessionAccess.Exclusive);
  var graphObserve = new ToolBehavior(ToolSessionRequirement.AnalyzedWorkspace, ToolEffect.Observe, ToolSessionAccess.SharedRead);
  var workspaceRootObserve = new ToolBehavior(ToolSessionRequirement.WorkspaceRoot, ToolEffect.Observe, ToolSessionAccess.SharedRead);
  var operationFactObserve = new ToolBehavior(ToolSessionRequirement.OperationFacts, ToolEffect.Observe, ToolSessionAccess.SharedRead);
  var compilationObserve = new ToolBehavior(ToolSessionRequirement.RetainedCompilation, ToolEffect.Observe, ToolSessionAccess.SharedRead);
  var compilationRefresh = new ToolBehavior(ToolSessionRequirement.RetainedCompilation, ToolEffect.RefreshWorkspace, ToolSessionAccess.Exclusive);
  var execute = new ToolBehavior(ToolSessionRequirement.RetainedCompilation, ToolEffect.ExecuteCode, ToolSessionAccess.SharedRead);
  var preview = new ToolBehavior(ToolSessionRequirement.RetainedCompilation, ToolEffect.PreviewChanges, ToolSessionAccess.SharedRead);
  var expected = new Dictionary<string, ToolBehavior>(StringComparer.Ordinal)
  {
  ["lifeblood_capabilities"] = noneObserve,
  ["lifeblood_batch"] = noneObserve,
  ["lifeblood_snapshots"] = snapshotCatalog,
  ["lifeblood_evidence_drift"] = workspaceRootObserve,
  ["lifeblood_performance_evidence"] = workspaceRootObserve,
  ["lifeblood_analyze"] = refresh,
  ["lifeblood_context"] = graphObserve,
  ["lifeblood_lookup"] = graphObserve,
  ["lifeblood_dependencies"] = graphObserve,
  ["lifeblood_dependants"] = graphObserve,
  ["lifeblood_blast_radius"] = graphObserve,
  ["lifeblood_file_impact"] = graphObserve,
  ["lifeblood_asmdef_check"] = graphObserve,
  ["lifeblood_resolve_member"] = graphObserve,
  ["lifeblood_resolve_short_name"] = graphObserve,
  ["lifeblood_dead_code"] = graphObserve,
  ["lifeblood_partial_view"] = workspaceRootObserve,
  ["lifeblood_invariant_check"] = workspaceRootObserve,
  ["lifeblood_authority_report"] = graphObserve,
  ["lifeblood_authority_coverage"] = graphObserve,
  ["lifeblood_port_health"] = graphObserve,
  ["lifeblood_cycles"] = graphObserve,
  ["lifeblood_test_impact"] = graphObserve,
  ["lifeblood_search"] = graphObserve,
  ["lifeblood_execute"] = execute,
  ["lifeblood_diagnose"] = compilationObserve,
  ["lifeblood_compile_check"] = compilationRefresh,
  ["lifeblood_find_references"] = compilationObserve,
  ["lifeblood_find_definition"] = compilationObserve,
  ["lifeblood_find_implementations"] = compilationObserve,
  ["lifeblood_enum_coverage"] = compilationObserve,
  ["lifeblood_static_tables"] = compilationObserve,
  ["lifeblood_assignment_coverage"] = compilationObserve,
  ["lifeblood_callsite_arguments"] = compilationObserve,
  ["lifeblood_contract_audit"] = operationFactObserve,
  ["lifeblood_wire_audit"] = compilationObserve,
  ["lifeblood_feature_switch_audit"] = compilationObserve,
  ["lifeblood_member_count"] = compilationObserve,
  ["lifeblood_struct_layout"] = compilationObserve,
  ["lifeblood_symbol_at_position"] = compilationObserve,
  ["lifeblood_documentation"] = compilationObserve,
  ["lifeblood_rename"] = preview,
  ["lifeblood_format"] = preview,
  };

  var definitions = ToolRegistry.GetDefinitions();
  Assert.Equal(expected.Count, definitions.Length);
  foreach (var def in definitions)
  {
  Assert.True(expected.ContainsKey(def.Name), $"Unexpected registered tool: {def.Name}");
  var behavior = expected[def.Name];
  Assert.Equal(behavior, def.Behavior);
  var expectedLegacy = behavior.SessionRequirement == ToolSessionRequirement.RetainedCompilation
    ? ToolAvailability.WriteSide
    : ToolAvailability.ReadSide;
  Assert.Equal(expectedLegacy, def.Availability);
  }
  }

  [Fact]
  public void ToolRegistry_WriteSideTools_MarkedUnavailable_WhenNoCompilationState()
  {
  // Correlate by name: availability lives on the definitions, the
  // decorated description lives on the wire payload.
  var definitions = ToolRegistry.GetDefinitions();
  var writeSideNames = definitions
  .Where(d => d.Behavior.SessionRequirement == ToolSessionRequirement.RetainedCompilation)
  .Select(d => d.Name)
  .ToHashSet();

  var wire = ToolRegistry.GetTools(hasCompilationState: false);
  var writeSideWire = wire.Where(t => writeSideNames.Contains(t.Name)).ToArray();
  Assert.NotEmpty(writeSideWire);
  Assert.All(writeSideWire, tool =>
  Assert.StartsWith("[Unavailable", tool.Description));
  }

  [Fact]
  public void ToolRegistry_ReadSideTools_NeverMarkedUnavailable()
  {
  // Read-side tools (including lifeblood_resolve_short_name) work off
  // the in-memory graph alone and do not need Roslyn compilation state.
  // They must never carry the unavailable decoration.
  var definitions = ToolRegistry.GetDefinitions();
  var readSideNames = definitions
  .Where(d => d.Behavior.SessionRequirement != ToolSessionRequirement.RetainedCompilation
    && d.Behavior.SessionRequirement != ToolSessionRequirement.OperationFacts)
  .Select(d => d.Name)
  .ToHashSet();

  var wire = ToolRegistry.GetTools(hasCompilationState: false);
  var readSideWire = wire.Where(t => readSideNames.Contains(t.Name)).ToArray();
  Assert.NotEmpty(readSideWire);
  Assert.All(readSideWire, tool =>
  Assert.False(tool.Description.StartsWith("[Unavailable"),
  $"Read-side tool {tool.Name} was incorrectly marked unavailable."));
  Assert.StartsWith(
    "[Unavailable",
    wire.Single(tool => tool.Name == "lifeblood_contract_audit").Description);
  }

  [Fact]
  public void ToolRegistry_ContractAudit_AdvertisesEvidenceFamiliesAndTheirTruthBoundary()
  {
  var description = ToolRegistry.GetDefinitions()
    .Single(tool => tool.Name == "lifeblood_contract_audit")
    .Description;

  Assert.Contains("sourceTextPolicies[]", description, StringComparison.Ordinal);
  Assert.Contains("invariantEvidence[]", description, StringComparison.Ordinal);
  Assert.Contains("explicit selector counts", description, StringComparison.Ordinal);
  Assert.Contains("no global score", description, StringComparison.Ordinal);
  Assert.Contains("retained semantic base", description, StringComparison.Ordinal);
  Assert.Contains("NotRequested", description, StringComparison.Ordinal);
  }

  [Fact]
  public void ToolRegistry_PerformanceEvidence_CallBehaviorRequiresSourceEvidenceOnlyForCorrelation()
  {
  var definition = ToolRegistry.GetDefinitions()
    .Single(tool => tool.Name == "lifeblood_performance_evidence");

  var import = definition.ResolveCallBehavior(JsonSerializer.SerializeToElement(new { action = "import", sourcePath = "capture.json" }));
  var compare = definition.ResolveCallBehavior(JsonSerializer.SerializeToElement(new { action = "compare", sourcePath = "a.json", candidatePath = "b.json" }));
  var correlate = definition.ResolveCallBehavior(JsonSerializer.SerializeToElement(new { action = "correlate", sourcePath = "capture.json" }));

  Assert.Equal(ToolSessionRequirement.WorkspaceRoot, import.SessionRequirement);
  Assert.Equal(ToolSessionRequirement.WorkspaceRoot, compare.SessionRequirement);
  Assert.Equal(ToolSessionRequirement.SourceEvidence, correlate.SessionRequirement);
  Assert.Equal(ToolEffect.Observe, correlate.Effect);
  Assert.Equal(ToolSessionAccess.SharedRead, correlate.SessionAccess);
  }

  [Fact]
  public void ToolRegistry_ResolveShortName_IsGraphObservation()
  {
  // Pin the classification decision from FINDING-005. The previous
  // prefix-based guard silently misclassified this tool because its
  // name did not match any of the 8 hard-coded prefixes. The typed
  // ToolDefinition.Behavior makes the state/effect/access decision explicit
  // and test-enforced.
  var definitions = ToolRegistry.GetDefinitions();
  var resolver = definitions.Single(d => d.Name == "lifeblood_resolve_short_name");
  Assert.Equal(ToolSessionRequirement.AnalyzedWorkspace, resolver.Behavior.SessionRequirement);
  Assert.Equal(ToolEffect.Observe, resolver.Behavior.Effect);
  Assert.Equal(ToolSessionAccess.SharedRead, resolver.Behavior.SessionAccess);
  }

  [Fact]
  public void ToolRegistry_EmptySession_MarksEveryStatefulToolUnavailable()
  {
  var wire = ToolRegistry.GetTools(new ToolSessionState(
    HasAnalyzedWorkspace: false,
    HasWorkspaceRoot: false,
    HasOperationFactProvider: false,
    HasRetainedCompilation: false));
  var definitions = ToolRegistry.GetDefinitions().ToDictionary(d => d.Name, StringComparer.Ordinal);

  foreach (var tool in wire)
  {
  var requiresState = definitions[tool.Name].Behavior.SessionRequirement != ToolSessionRequirement.None;
  Assert.Equal(requiresState, tool.Description.StartsWith("[Unavailable", StringComparison.Ordinal));
  }

  Assert.DoesNotContain("[Unavailable", wire.Single(t => t.Name == "lifeblood_capabilities").Description);
  Assert.DoesNotContain("[Unavailable", wire.Single(t => t.Name == "lifeblood_analyze").Description);
  Assert.StartsWith("[Unavailable", wire.Single(t => t.Name == "lifeblood_lookup").Description);
  Assert.StartsWith("[Unavailable", wire.Single(t => t.Name == "lifeblood_compile_check").Description);

  var inconsistent = ToolRegistry.GetTools(new ToolSessionState(
    HasAnalyzedWorkspace: false,
    HasWorkspaceRoot: true,
    HasOperationFactProvider: true,
    HasRetainedCompilation: true));
  Assert.StartsWith("[Unavailable", inconsistent.Single(t => t.Name == "lifeblood_compile_check").Description);
  }

  [Fact]
  public void ToolRegistry_ToolsList_SerializesWithoutError()
  {
  // Direct regression test for the drift that broke Claude Code
  // reconnection in v0.6.2: the old McpToolInfo carried a
  // [JsonIgnore] required init property, which System.Text.Json .NET 8
  // refused to process during serialization metadata construction. The
  // response went out as a JSON-RPC -32603 error, Claude Code read
  // it as a dead server, connection aborted. Splitting wire DTO
  // (McpToolInfo) from internal record (ToolDefinition) eliminates the
  // drift class. This test walks the tools/list response through the
  // same JsonSerializerOptions the dispatcher uses and asserts no
  // exception escapes.
  var wire = ToolRegistry.GetTools(hasCompilationState: true);
  var json = JsonSerializer.Serialize(new { tools = wire }, JsonOpts);
  Assert.Contains("\"name\":\"lifeblood_analyze\"", json);
  Assert.DoesNotContain("\"availability\"", json);
  }
}

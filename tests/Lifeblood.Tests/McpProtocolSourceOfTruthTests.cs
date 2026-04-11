using System.Text.Json;
using System.Text.RegularExpressions;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Analysis;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Analysis;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-MCP-003 ratchet. Every MCP wire-format constant (protocol
/// version, JSON-RPC method name, notification name) has exactly one
/// canonical home per side of the wire:
///
/// <list type="bullet">
/// <item>Server + in-repo clients: <see cref="McpProtocolSpec"/> in
/// <c>Lifeblood.Connectors.Mcp</c>.</item>
/// <item>Unity bridge (standalone, compiles inside Unity with no
/// project reference to Lifeblood): <c>McpProtocolConstants</c> at
/// <c>unity/Editor/LifebloodBridge/McpProtocolConstants.cs</c>.</item>
/// </list>
///
/// These tests pin every consumer to the source of truth. Adding a
/// hardcoded protocol string anywhere else fails the ratchet at CI.
/// </summary>
public class McpProtocolSourceOfTruthTests
{
    // -------- Server side: McpDispatcher consumes McpProtocolSpec --------

    [Fact]
    public void McpDispatcher_SupportedProtocolVersion_ComesFromProtocolSpec()
    {
        Assert.Equal(McpProtocolSpec.SupportedVersion, McpDispatcher.SupportedProtocolVersion);
    }

    [Fact]
    public void McpDispatcher_InitializeResponse_AdvertisesSpecVersion()
    {
        // Drive the dispatcher through its public Dispatch API with a
        // real initialize request and read the response. No reflection,
        // no internal access — the test sees only what every MCP client
        // sees on the wire.
        var dispatcher = CreateMinimalDispatcher();
        var request = MakeRequest(McpProtocolSpec.Methods.Initialize);
        var response = dispatcher.Dispatch(request);

        Assert.NotNull(response);
        Assert.NotNull(response!.Result);
        var result = Assert.IsType<McpInitializeResult>(response.Result);
        Assert.Equal(McpProtocolSpec.SupportedVersion, result.ProtocolVersion);
    }

    [Theory]
    [InlineData("notifications/initialized")]
    [InlineData("initialized")]
    [InlineData("notifications/cancelled")]
    [InlineData("$/cancelRequest")]
    public void McpDispatcher_KnownNotification_ProducesNoResponse(string method)
    {
        // Notifications in McpProtocolSpec.AllKnownNotifications MUST be
        // short-circuited by the dispatcher without emitting a response.
        // Confirms INV-MCP-002 + INV-MCP-003 on a single path.
        Assert.Contains(method, McpProtocolSpec.AllKnownNotifications);

        var dispatcher = CreateMinimalDispatcher();
        var notification = MakeRequest(method, id: null);

        Assert.Null(dispatcher.Dispatch(notification));
    }

    [Fact]
    public void McpProtocolSpec_CanonicalAndLegacy_CoverAllKnownNotifications()
    {
        // The union of canonical + legacy must equal the full known set.
        // Protects against someone adding a notification to one bucket
        // without adding it to AllKnownNotifications.
        var union = new HashSet<string>(McpProtocolSpec.CanonicalNotifications, StringComparer.Ordinal);
        union.UnionWith(McpProtocolSpec.LegacyNotificationAliases);

        Assert.Equal(
            new HashSet<string>(McpProtocolSpec.AllKnownNotifications, StringComparer.Ordinal),
            union);
    }

    [Fact]
    public void McpProtocolSpec_CanonicalAndLegacy_AreDisjoint()
    {
        // A single notification name must be in exactly one bucket.
        foreach (var canonical in McpProtocolSpec.CanonicalNotifications)
            Assert.DoesNotContain(canonical, McpProtocolSpec.LegacyNotificationAliases);
    }

    // -------- Unity side: McpProtocolConstants mirrors McpProtocolSpec --------

    [Fact]
    public void UnityBridgeConstants_FileExists()
    {
        Assert.True(
            File.Exists(UnityBridgeConstantsPath),
            $"Unity bridge constants file missing at {UnityBridgeConstantsPath}");
    }

    [Fact]
    public void UnityBridgeConstants_SupportedVersion_MirrorsProtocolSpec()
    {
        var constants = ParseUnityBridgeConstants();
        Assert.Equal(McpProtocolSpec.SupportedVersion, constants["SupportedVersion"]);
    }

    [Fact]
    public void UnityBridgeConstants_MethodNames_MirrorProtocolSpec()
    {
        var constants = ParseUnityBridgeConstants();
        Assert.Equal(McpProtocolSpec.Methods.Initialize, constants["MethodInitialize"]);
        Assert.Equal(McpProtocolSpec.Methods.ToolsList, constants["MethodToolsList"]);
        Assert.Equal(McpProtocolSpec.Methods.ToolsCall, constants["MethodToolsCall"]);
        Assert.Equal(McpProtocolSpec.Methods.Shutdown, constants["MethodShutdown"]);
    }

    [Fact]
    public void UnityBridgeConstants_CanonicalNotifications_MirrorProtocolSpec()
    {
        // First-party clients only send canonical forms. The Unity
        // mirror must expose the canonical values, never the legacy alias.
        var constants = ParseUnityBridgeConstants();
        Assert.Equal(McpProtocolSpec.Notifications.Initialized, constants["NotificationInitialized"]);
        Assert.Equal(McpProtocolSpec.Notifications.Cancelled, constants["NotificationCancelled"]);
    }

    [Fact]
    public void UnityBridgeConstants_DoesNotExposeLegacyAlias()
    {
        // The whole point of the Unity mirror is to prevent first-party
        // clients from sending the deprecated bare "initialized" form.
        // If the constant ever appears on the mirror, the ratchet trips.
        var source = File.ReadAllText(UnityBridgeConstantsPath);
        foreach (var legacy in McpProtocolSpec.LegacyNotificationAliases)
        {
            Assert.DoesNotContain($"\"{legacy}\"", source);
        }
    }

    // -------- Unity side: LifebloodBridgeClient uses the constants --------

    [Fact]
    public void UnityBridgeClient_ContainsNoBareProtocolStringLiterals()
    {
        var source = File.ReadAllText(UnityBridgeClientPath);

        // Every value the client sends over the wire must route through
        // McpProtocolConstants. Bare literals are lint violations — they
        // would silently drift from McpProtocolSpec.
        var forbidden = new[]
        {
            $"\"{McpProtocolSpec.Methods.Initialize}\"",
            $"\"{McpProtocolSpec.Methods.ToolsList}\"",
            $"\"{McpProtocolSpec.Methods.ToolsCall}\"",
            $"\"{McpProtocolSpec.Methods.Shutdown}\"",
            $"\"{McpProtocolSpec.Notifications.Initialized}\"",
            $"\"{McpProtocolSpec.Notifications.InitializedLegacyAlias}\"",
            $"\"{McpProtocolSpec.SupportedVersion}\"",
            "\"2.0\"", // JSON-RPC framing version
        };

        foreach (var literal in forbidden)
        {
            Assert.False(
                source.Contains(literal),
                $"LifebloodBridgeClient contains bare protocol string literal {literal}. " +
                "Route it through McpProtocolConstants per INV-MCP-003.");
        }
    }

    [Fact]
    public void UnityBridgeClient_SendsCanonicalInitializedNotification()
    {
        var source = File.ReadAllText(UnityBridgeClientPath);
        // The client must reference the canonical constant by name, not
        // any string form. Having the reference proves the initialized
        // notification goes out as "notifications/initialized".
        Assert.Contains("McpProtocolConstants.NotificationInitialized", source);
    }

    [Fact]
    public void UnityBridgeClient_InitializeRequest_PopulatesSpecParams()
    {
        // MCP initialize.params must carry protocolVersion, capabilities,
        // clientInfo. Empty {} is a spec violation even if the server is
        // lenient. Pin the three keys so the bridge stays compliant.
        var source = File.ReadAllText(UnityBridgeClientPath);
        Assert.Contains("[\"protocolVersion\"]", source);
        Assert.Contains("[\"capabilities\"]", source);
        Assert.Contains("[\"clientInfo\"]", source);
    }

    // -------- helpers --------

    private static string RepoRoot
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null && !File.Exists(Path.Combine(current.FullName, "Lifeblood.sln")))
                current = current.Parent;
            Assert.NotNull(current);
            return current!.FullName;
        }
    }

    private static string UnityBridgeConstantsPath =>
        Path.Combine(RepoRoot, "unity", "Editor", "LifebloodBridge", "McpProtocolConstants.cs");

    private static string UnityBridgeClientPath =>
        Path.Combine(RepoRoot, "unity", "Editor", "LifebloodBridge", "LifebloodBridgeClient.cs");

    /// <summary>
    /// Parses <c>public const string Name = "value";</c> declarations
    /// out of the Unity mirror file. The ratchet asserts every relevant
    /// constant's value against the source of truth in McpProtocolSpec.
    /// </summary>
    private static Dictionary<string, string> ParseUnityBridgeConstants()
    {
        var source = File.ReadAllText(UnityBridgeConstantsPath);
        var regex = new Regex(@"public\s+const\s+string\s+(\w+)\s*=\s*""([^""]*)""\s*;");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in regex.Matches(source))
            result[m.Groups[1].Value] = m.Groups[2].Value;
        return result;
    }

    private static readonly PhysicalFileSystem Fs = new();

    private sealed class TestBlastRadiusProvider : IBlastRadiusProvider
    {
        public BlastRadiusResult Analyze(SemanticGraph graph, string targetSymbolId, int maxDepth = 10)
            => BlastRadiusAnalyzer.Analyze(graph, targetSymbolId, maxDepth);
    }

    private static McpDispatcher CreateMinimalDispatcher()
    {
        // Same wiring shape as McpProtocolTests.CreateDispatcher — the
        // dispatcher needs a full ToolHandler even when the tests only
        // exercise initialize + notification paths, because the public
        // McpDispatcher constructor requires a non-null handler. We
        // build a zero-graph session; the notification and initialize
        // paths never touch session state.
        var session = new GraphSession(Fs);
        IMcpGraphProvider provider = new LifebloodMcpProvider(new TestBlastRadiusProvider());
        ISymbolResolver resolver = new LifebloodSymbolResolver();
        ISemanticSearchProvider search = new LifebloodSemanticSearchProvider();
        IDeadCodeAnalyzer deadCode = new LifebloodDeadCodeAnalyzer();
        IPartialViewBuilder partialView = new LifebloodPartialViewBuilder(Fs);
        Lifeblood.Application.Ports.Right.Invariants.IInvariantProvider invariants
            = new LifebloodInvariantProvider(Fs);
        var handler = new ToolHandler(session, provider, resolver, search, deadCode, partialView, invariants);
        return new McpDispatcher(session, handler);
    }

    private static JsonRpcRequest MakeRequest(string method, int? id = 1)
    {
        var req = new JsonRpcRequest { Method = method };
        if (id.HasValue)
        {
            var idJson = JsonSerializer.Serialize(id.Value);
            req.Id = JsonSerializer.Deserialize<JsonElement>(idJson);
        }
        return req;
    }
}

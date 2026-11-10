using System.IO.Pipes;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Lifeblood.Adapters.JsonGraph;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Xunit;
using Xunit.Abstractions;

namespace Lifeblood.Tests;

/// <summary>
/// Black-box coverage for the real shared stdio-proxy/named-pipe-daemon path.
/// Every test owns a unique explicit pipe and the daemon process handle, so no
/// detached process or retained graph survives the fixture.
/// </summary>
[Collection(McpProcessTestCollection.Name)]
public sealed class SharedMcpTransportProcessTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _firstGraphPath;
    private readonly string _secondGraphPath;
    private readonly ITestOutputHelper _output;

    public SharedMcpTransportProcessTests(ITestOutputHelper output)
    {
        _output = output;
        _tempDirectory = Path.Combine(
            Path.GetTempPath(),
            $"lifeblood-shared-process-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _firstGraphPath = WriteGraph("first.graph.json", "First");
        _secondGraphPath = WriteGraph("second.graph.json", "First", "Second");
    }

    [Fact]
    public async Task DetachedAutostart_PreservesPublicationAcrossProxyExitWithUnicodeWorkspace()
    {
        var dll = McpProcessTestClient.LocateServerDll();
        Assert.True(File.Exists(dll));
        var workspace = Path.Combine(_tempDirectory, "detached workspace č");
        Directory.CreateDirectory(workspace);
        var graph = Path.Combine(workspace, "input.graph.json");
        File.Copy(_firstGraphPath, graph);
        var pipeName = UniquePipeName();
        var environment = new Dictionary<string, string?>
        {
            ["LIFEBLOOD_SHARED_DAEMON_AUTOSTART"] = "1",
            ["LIFEBLOOD_SHARED_IDLE_SECONDS"] = "10",
        };
        Process? daemon = null;
        try
        {
            await using var first = McpProcessTestClient.Start(dll, environment,
                "--shared", "--shared-pipe", pipeName, "--shared-key", workspace);
            using var initialized = await first.InitializeAsync();
            using (var response = await first.CallToolAsync("lifeblood_capabilities"))
            using (var payload = McpProcessTestClient.ParseToolPayload(response))
                daemon = Process.GetProcessById(payload.RootElement.GetProperty("sharedService").GetProperty("processId").GetInt32());
            await AnalyzeGraphAsync(first, graph);
            var committed = await ReadSessionAsync(first);
            var shutdownClock = Stopwatch.StartNew();
            await first.CloseInputAndDrainStdoutAsync(TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(daemon.HasExited,
                $"Detached daemon exited; proxy EOF took {shutdownClock.ElapsedMilliseconds} ms.");
            await using var second = McpProcessTestClient.Start(dll, environment,
                "--shared", "--shared-pipe", pipeName, "--shared-key", workspace);
            using var reinitialized = await second.InitializeAsync();
            Assert.Equal(committed, await ReadSessionAsync(second));
        }
        finally
        {
            if (daemon != null)
            {
                if (!daemon.HasExited) daemon.Kill(entireProcessTree: true);
                Assert.True(daemon.WaitForExit(5000));
                daemon.Dispose();
            }
        }
    }

    [SkippableFact]
    public async Task SamePipe_TwoProxiesShareAndPublishLatestGeneration()
    {
        var dll = McpProcessTestClient.LocateServerDll();
        Skip.IfNot(File.Exists(dll),
            $"Server dll not found at {dll}. Run `dotnet build tests/Lifeblood.Tests` first.");

        Directory.CreateDirectory(Path.Combine(_tempDirectory, ".git"));
        var firstAgentDirectory = Directory.CreateDirectory(
            Path.Combine(_tempDirectory, "agent-one")).FullName;
        var secondAgentDirectory = Directory.CreateDirectory(
            Path.Combine(_tempDirectory, "agent-two")).FullName;
        var pipeName = UniquePipeName();
        await using var daemon = await StartDaemonAsync(dll, pipeName, _tempDirectory);
        await using var firstProxy = StartProxy(dll, pipeName, firstAgentDirectory);
        await using var secondProxy = StartProxy(dll, pipeName, secondAgentDirectory);

        using var firstInitialize = await firstProxy.InitializeAsync();
        using var secondInitialize = await secondProxy.InitializeAsync();
        using var initialCatalog = await firstProxy.SendRequestAsync(
            """{"jsonrpc":"2.0","id":701,"method":"tools/list"}""", TimeSpan.FromSeconds(30));
        var cachedTools = initialCatalog.RootElement.GetProperty("result").GetProperty("tools").GetRawText();
        Assert.NotEqual(daemon.ProcessId, firstProxy.ProcessId);
        Assert.NotEqual(firstProxy.ProcessId, secondProxy.ProcessId);
        var connectedStatus = await ReadSharedStatusAsync(firstProxy);
        Assert.True(connectedStatus.Active);
        Assert.Equal("shared-daemon", connectedStatus.Mode);
        Assert.Equal(2, connectedStatus.ProtocolVersion);
        Assert.Equal(2, connectedStatus.ClientCount);
        Assert.StartsWith("daemon_", connectedStatus.DaemonInstanceId, StringComparison.Ordinal);

        await AnalyzeGraphAsync(firstProxy, Path.GetFileName(_firstGraphPath));
        var observedBySecond = await ReadSessionAsync(secondProxy);
        Assert.True(observedBySecond.HasGraphLoaded);
        Assert.Equal(1, observedBySecond.AnalysisGeneration);
        Assert.StartsWith("snap_", observedBySecond.SnapshotId, StringComparison.Ordinal);

        await AnalyzeGraphAsync(secondProxy, _secondGraphPath);
        var observedByFirst = await ReadSessionAsync(firstProxy);
        Assert.True(observedByFirst.HasGraphLoaded);
        Assert.Equal(2, observedByFirst.AnalysisGeneration);
        Assert.NotEqual(observedBySecond.SnapshotId, observedByFirst.SnapshotId);
        using var refreshedCatalog = await secondProxy.SendRequestAsync(
            """{"jsonrpc":"2.0","id":702,"method":"tools/list"}""", TimeSpan.FromSeconds(30));
        Assert.Equal(cachedTools,
            refreshedCatalog.RootElement.GetProperty("result").GetProperty("tools").GetRawText());

        using var lookupRpc = await firstProxy.CallToolAsync(
            "lifeblood_lookup",
            new { symbolId = "type:Shared.Second" });
        using var lookup = McpProcessTestClient.ParseToolPayload(lookupRpc);
        Assert.Contains("Second", lookup.RootElement.ToString(), StringComparison.Ordinal);

        await firstProxy.DisposeAsync();
        var afterDisconnect = await ReadSessionAsync(secondProxy);
        Assert.Equal(2, afterDisconnect.AnalysisGeneration);
        Assert.Equal(observedByFirst.SnapshotId, afterDisconnect.SnapshotId);
        var afterDisconnectStatus = await ReadSharedStatusAsync(secondProxy);
        Assert.Equal(1, afterDisconnectStatus.ClientCount);
        Assert.Equal(connectedStatus.DaemonInstanceId, afterDisconnectStatus.DaemonInstanceId);
        Assert.False(daemon.HasExited);
    }

    [SkippableFact]
    public async Task BoundWorkspace_RejectsOutsideGraphWithoutReplacingCommittedGeneration()
    {
        var dll = McpProcessTestClient.LocateServerDll();
        Skip.IfNot(File.Exists(dll),
            $"Server dll not found at {dll}. Run `dotnet build tests/Lifeblood.Tests` first.");

        var boundWorkspace = Path.Combine(_tempDirectory, "bound-workspace");
        Directory.CreateDirectory(boundWorkspace);
        var insideGraph = Path.Combine(boundWorkspace, "inside.graph.json");
        File.Copy(_firstGraphPath, insideGraph);

        var pipeName = UniquePipeName();
        await using var daemon = await StartDaemonAsync(dll, pipeName, boundWorkspace);
        await using var proxy = StartProxy(dll, pipeName, boundWorkspace);
        using var initialize = await proxy.InitializeAsync();

        await AnalyzeGraphAsync(proxy, insideGraph);
        var beforeRejection = await ReadSessionAsync(proxy);
        Assert.Equal(1, beforeRejection.AnalysisGeneration);

        using var rejectedRpc = await proxy.CallToolAsync(
            "lifeblood_analyze",
            new { graphPath = _secondGraphPath });
        var rejectedResult = rejectedRpc.RootElement.GetProperty("result");
        Assert.True(rejectedResult.GetProperty("isError").GetBoolean());
        Assert.Contains(
            "workspace-binding",
            rejectedResult.ToString(),
            StringComparison.Ordinal);

        var afterRejection = await ReadSessionAsync(proxy);
        Assert.Equal(1, afterRejection.AnalysisGeneration);
        using var lookupRpc = await proxy.CallToolAsync(
            "lifeblood_lookup",
            new { symbolId = "type:Shared.First" });
        using var lookup = McpProcessTestClient.ParseToolPayload(lookupRpc);
        Assert.Contains("First", lookup.RootElement.ToString(), StringComparison.Ordinal);
        Assert.False(daemon.HasExited);
    }

    [SkippableFact]
    public async Task DifferentPipes_KeepDaemonSessionsIsolated()
    {
        var dll = McpProcessTestClient.LocateServerDll();
        Skip.IfNot(File.Exists(dll),
            $"Server dll not found at {dll}. Run `dotnet build tests/Lifeblood.Tests` first.");

        var firstPipe = UniquePipeName();
        var secondPipe = UniquePipeName();
        await using var firstDaemon = await StartDaemonAsync(dll, firstPipe, _tempDirectory);
        await using var secondDaemon = await StartDaemonAsync(dll, secondPipe, _tempDirectory);
        await using var firstProxy = StartProxy(dll, firstPipe, _tempDirectory);
        await using var secondProxy = StartProxy(dll, secondPipe, _tempDirectory);

        using var firstInitialize = await firstProxy.InitializeAsync();
        using var secondInitialize = await secondProxy.InitializeAsync();

        await AnalyzeGraphAsync(firstProxy, _firstGraphPath);

        var firstState = await ReadSessionAsync(firstProxy);
        var secondState = await ReadSessionAsync(secondProxy);
        Assert.True(firstState.HasGraphLoaded);
        Assert.Equal(1, firstState.AnalysisGeneration);
        Assert.StartsWith("snap_", firstState.SnapshotId, StringComparison.Ordinal);
        Assert.False(secondState.HasGraphLoaded);
        Assert.Equal(0, secondState.AnalysisGeneration);
        Assert.Equal("", secondState.SnapshotId);

        using var missingRpc = await secondProxy.CallToolAsync(
            "lifeblood_lookup",
            new { symbolId = "type:Shared.First" });
        var missingResult = missingRpc.RootElement.GetProperty("result");
        Assert.True(missingResult.GetProperty("isError").GetBoolean());
        Assert.Contains("No graph loaded", missingResult.ToString(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task SamePipe_DifferentWorkspaceIdentityIsRejectedBeforeMcpForwarding()
    {
        var dll = McpProcessTestClient.LocateServerDll();
        Skip.IfNot(File.Exists(dll),
            $"Server dll not found at {dll}. Run `dotnet build tests/Lifeblood.Tests` first.");

        var daemonWorkspace = Path.Combine(_tempDirectory, "daemon-workspace");
        var proxyWorkspace = Path.Combine(_tempDirectory, "proxy-workspace");
        Directory.CreateDirectory(daemonWorkspace);
        Directory.CreateDirectory(proxyWorkspace);

        var pipeName = UniquePipeName();
        await using var daemon = await StartDaemonAsync(dll, pipeName, daemonWorkspace);
        await using var proxy = StartProxy(dll, pipeName, proxyWorkspace);

        using var response = await proxy.InitializeAsync();
        var error = response.RootElement.GetProperty("error");
        Assert.Equal(-32603, error.GetProperty("code").GetInt32());
        Assert.Contains(
            "workspace mismatch",
            error.GetProperty("message").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.False(proxy.HasExited);
        Assert.False(daemon.HasExited);
    }

    [SkippableFact]
    public async Task UnsupportedSharedProtocol_IsRejectedBeforeMcpForwarding()
    {
        var dll = McpProcessTestClient.LocateServerDll();
        Skip.IfNot(File.Exists(dll),
            $"Server dll not found at {dll}. Run `dotnet build tests/Lifeblood.Tests` first.");

        var pipeName = UniquePipeName();
        await using var daemon = await StartDaemonAsync(dll, pipeName, _tempDirectory);
        using var response = await SendRawHandshakeAsync(pipeName, new
        {
            kind = "lifeblood.shared.handshake",
            protocolVersion = 0,
            serverVersion = "test-client",
            buildIdentity = "test-build",
            workspaceRoot = _tempDirectory,
        });
        Assert.False(response.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal(2, response.RootElement.GetProperty("protocolVersion").GetInt32());
        Assert.Contains(
            "protocol mismatch",
            response.RootElement.GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.False(daemon.HasExited);
    }

    [SkippableFact]
    public async Task DifferentSharedBuild_IsRejectedBeforeMcpForwarding()
    {
        var dll = McpProcessTestClient.LocateServerDll();
        Skip.IfNot(File.Exists(dll),
            $"Server dll not found at {dll}. Run `dotnet build tests/Lifeblood.Tests` first.");

        var pipeName = UniquePipeName();
        await using var daemon = await StartDaemonAsync(dll, pipeName, _tempDirectory);
        using var response = await SendRawHandshakeAsync(pipeName, new
        {
            kind = "lifeblood.shared.handshake",
            protocolVersion = 2,
            serverVersion = "different-version",
            buildIdentity = "different-build",
            workspaceRoot = _tempDirectory,
        });
        Assert.False(response.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Contains(
            "build mismatch",
            response.RootElement.GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.False(daemon.HasExited);
    }

    [SkippableFact]
    public async Task LastPersistentProxyDisconnect_ZeroIdlePolicyDrainsDaemon()
    {
        var dll = McpProcessTestClient.LocateServerDll();
        Skip.IfNot(File.Exists(dll),
            $"Server dll not found at {dll}. Run `dotnet build tests/Lifeblood.Tests` first.");

        var pipeName = UniquePipeName();
        var environment = new Dictionary<string, string?>
        {
            ["LIFEBLOOD_SHARED_IDLE_SECONDS"] = "0",
        };
        await using var daemon = await StartDaemonAsync(
            dll,
            pipeName,
            _tempDirectory,
            environment);
        var proxy = StartProxy(dll, pipeName, _tempDirectory);
        using (var initialize = await proxy.InitializeAsync())
        {
            var status = await ReadSharedStatusAsync(proxy);
            Assert.Equal(1, status.ClientCount);
        }

        await proxy.DisposeAsync();
        await daemon.WaitForExitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, daemon.ExitCode);
    }

    [SkippableFact]
    public async Task DefaultNoIdleEviction_ReconnectRetainsExactPublication()
    {
        var dll = McpProcessTestClient.LocateServerDll();
        Skip.IfNot(File.Exists(dll),
            $"Server dll not found at {dll}. Run `dotnet build tests/Lifeblood.Tests` first.");

        var pipeName = UniquePipeName();
        await using var daemon = await StartDaemonAsync(
            dll,
            pipeName,
            _tempDirectory);
        var firstProxy = StartProxy(dll, pipeName, _tempDirectory);
        SessionState published;
        SharedStatus originalStatus;
        try
        {
            using var initialize = await firstProxy.InitializeAsync();
            await AnalyzeGraphAsync(firstProxy, _firstGraphPath);
            published = await ReadSessionAsync(firstProxy);
            originalStatus = await ReadSharedStatusAsync(firstProxy);
        }
        finally
        {
            await firstProxy.DisposeAsync();
        }

        Assert.False(daemon.HasExited);

        await using var secondProxy = StartProxy(dll, pipeName, _tempDirectory);
        using var secondInitialize = await secondProxy.InitializeAsync();
        var reattached = await ReadSessionAsync(secondProxy);
        var reattachedStatus = await ReadSharedStatusAsync(secondProxy);

        Assert.True(reattached.HasGraphLoaded);
        Assert.Equal(published.AnalysisGeneration, reattached.AnalysisGeneration);
        Assert.Equal(published.SnapshotId, reattached.SnapshotId);
        Assert.Equal(originalStatus.DaemonInstanceId, reattachedStatus.DaemonInstanceId);
        Assert.False(reattachedStatus.IdleEvictionEnabled);
        Assert.Null(reattachedStatus.IdleTimeoutSeconds);
    }

    [SkippableFact]
    public async Task SameAnalysis_TwoPersistentClientsCoalesceAndPublishOnce()
    {
        var dll = McpProcessTestClient.LocateServerDll();
        Skip.IfNot(File.Exists(dll),
            $"Server dll not found at {dll}. Run `dotnet build tests/Lifeblood.Tests` first.");

        WriteCSharpWorkspace(fileCount: 250);
        var pipeName = UniquePipeName();
        await using var daemon = await StartDaemonAsync(dll, pipeName, _tempDirectory);
        await using var firstProxy = StartProxy(dll, pipeName, _tempDirectory);
        await using var secondProxy = StartProxy(dll, pipeName, _tempDirectory);
        using var firstInitialize = await firstProxy.InitializeAsync();
        using var secondInitialize = await secondProxy.InitializeAsync();

        var firstCall = firstProxy.CallToolAsync(
            "lifeblood_analyze",
            new { projectPath = _tempDirectory, readOnly = false },
            TimeSpan.FromSeconds(90));
        var secondCall = secondProxy.CallToolAsync(
            "lifeblood_analyze",
            new { projectPath = _tempDirectory, readOnly = false },
            TimeSpan.FromSeconds(90));
        var responses = await Task.WhenAll(firstCall, secondCall);
        using var firstResponse = responses[0];
        using var secondResponse = responses[1];
        using var firstPayload = McpProcessTestClient.ParseToolPayload(firstResponse);
        using var secondPayload = McpProcessTestClient.ParseToolPayload(secondResponse);

        Assert.Equal(
            firstPayload.RootElement.GetProperty("analysisRequestId").GetString(),
            secondPayload.RootElement.GetProperty("analysisRequestId").GetString());
        Assert.NotEqual(
            firstPayload.RootElement.GetProperty("coalesced").GetBoolean(),
            secondPayload.RootElement.GetProperty("coalesced").GetBoolean());
        Assert.Equal(2, firstPayload.RootElement.GetProperty("waiterCount").GetInt32());
        Assert.Equal(2, secondPayload.RootElement.GetProperty("waiterCount").GetInt32());

        var session = await ReadSessionAsync(firstProxy);
        Assert.Equal(1, session.AnalysisGeneration);
        Assert.False(daemon.HasExited);
    }

    [SkippableFact]
    public async Task GraphOnlyClientPreservesHotPublicationAndVerifiedRecoveryReusesIt()
    {
        var dll = McpProcessTestClient.LocateServerDll();
        Skip.IfNot(File.Exists(dll), $"Server dll not found at {dll}.");
        WriteCSharpWorkspace(fileCount: 250);
        var pipeName = UniquePipeName();
        await using var daemon = await StartDaemonAsync(dll, pipeName, _tempDirectory);
        await using var first = StartProxy(dll, pipeName, _tempDirectory);
        await using var second = StartProxy(dll, pipeName, _tempDirectory);
        using var firstInit = await first.InitializeAsync();
        using var secondInit = await second.InitializeAsync();
        using var analyzedRpc = await first.CallToolAsync("lifeblood_analyze",
            new { projectPath = _tempDirectory }, TimeSpan.FromSeconds(60));
        using var analyzed = McpProcessTestClient.ParseToolPayload(analyzedRpc);
        var original = await ReadSessionAsync(first);
        var originalPublisher = analyzed.RootElement.GetProperty("semanticRetention").GetProperty("publishingClientLeaseId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(originalPublisher));
        using var readOnlyRpc = await second.CallToolAsync("lifeblood_analyze",
            new { projectPath = _tempDirectory, readOnly = true }, TimeSpan.FromSeconds(60));
        using var readOnly = McpProcessTestClient.ParseToolPayload(readOnlyRpc);
        Assert.Equal("reusedCurrent", readOnly.RootElement.GetProperty("publication").GetProperty("action").GetString());
        Assert.Equal("GraphOnly", readOnly.RootElement.GetProperty("requestedRetentionMode").GetString());
        Assert.Equal(original.SnapshotId, (await ReadSessionAsync(first)).SnapshotId);

        await AnalyzeGraphAsync(second, _secondGraphPath);
        using var capabilitiesRpc = await first.CallToolAsync("lifeblood_capabilities");
        using var capabilities = McpProcessTestClient.ParseToolPayload(capabilitiesRpc);
        var graphOnly = capabilities.RootElement.GetProperty("session");
        Assert.False(graphOnly.GetProperty("hasCompilationState").GetBoolean());
        Assert.Equal(1, graphOnly.GetProperty("snapshotHistory").GetProperty("semanticBaseCount").GetInt32());
        Assert.Equal("preserved", graphOnly.GetProperty("semanticRetention").GetProperty("action").GetString());
        Assert.Equal(original.SnapshotId, graphOnly.GetProperty("semanticRetention").GetProperty("hotSnapshotId").GetString());
        Assert.NotEqual(originalPublisher, graphOnly.GetProperty("semanticRetention").GetProperty("publishingClientLeaseId").GetString());
        var graphOnlyGeneration = graphOnly.GetProperty("analysisGeneration").GetInt64();

        using var recoveredRpc = await first.CallToolAsync("lifeblood_analyze",
            new { projectPath = _tempDirectory, incremental = true }, TimeSpan.FromSeconds(60));
        using var recovered = McpProcessTestClient.ParseToolPayload(recoveredRpc);
        Assert.Equal("restoredHot", recovered.RootElement.GetProperty("publication").GetProperty("action").GetString());
        Assert.Equal(JsonValueKind.Null, recovered.RootElement.GetProperty("usage").ValueKind);
        Assert.Equal("reused", recovered.RootElement.GetProperty("semanticRetention").GetProperty("action").GetString());
        Assert.Equal(originalPublisher, recovered.RootElement.GetProperty("semanticRetention").GetProperty("publishingClientLeaseId").GetString());
        Assert.Equal(graphOnlyGeneration + 1, (await ReadSessionAsync(second)).AnalysisGeneration);
        using var compileRpc = await second.CallToolAsync("lifeblood_compile_check",
            new { filePaths = new[] { "Type000.cs" }, staleRefresh = false });
        using var compile = McpProcessTestClient.ParseToolPayload(compileRpc);
        Assert.True(compile.RootElement.GetProperty("success").GetBoolean());
        using var historicalCompileRpc = await first.CallToolAsync("lifeblood_diagnose",
            new { snapshotId = original.SnapshotId });
        var historicalResult = historicalCompileRpc.RootElement.GetProperty("result");
        Assert.True(historicalResult.GetProperty("isError").GetBoolean());
        Assert.False(daemon.HasExited);
    }

    [SkippableFact]
    public async Task IncompatibleHotRecoveryCoalescesOneExplicitRebuildForTwoClients()
    {
        var dll = McpProcessTestClient.LocateServerDll();
        Skip.IfNot(File.Exists(dll), $"Server dll not found at {dll}.");
        WriteCSharpWorkspace(fileCount: 250);
        var pipeName = UniquePipeName();
        await using var daemon = await StartDaemonAsync(dll, pipeName, _tempDirectory);
        await using var first = StartProxy(dll, pipeName, _tempDirectory);
        await using var second = StartProxy(dll, pipeName, _tempDirectory);
        using var firstInit = await first.InitializeAsync();
        using var secondInit = await second.InitializeAsync();
        using var analyzedRpc = await first.CallToolAsync("lifeblood_analyze",
            new { projectPath = _tempDirectory }, TimeSpan.FromSeconds(60));
        using var analyzed = McpProcessTestClient.ParseToolPayload(analyzedRpc);
        var originalPublisher = analyzed.RootElement.GetProperty("semanticRetention").GetProperty("publishingClientLeaseId").GetString();
        var original = await ReadSessionAsync(first);
        File.AppendAllText(Path.Combine(_tempDirectory, "Type000.cs"), " public class Changed {} ");
        using var graphOnlyRpc = await second.CallToolAsync("lifeblood_analyze",
            new { projectPath = _tempDirectory, readOnly = true }, TimeSpan.FromSeconds(60));
        using var graphOnly = McpProcessTestClient.ParseToolPayload(graphOnlyRpc);
        Assert.Equal("preserved", graphOnly.RootElement.GetProperty("semanticRetention").GetProperty("action").GetString());
        var before = await ReadSessionAsync(first);
        var responses = await Task.WhenAll(
            first.CallToolAsync("lifeblood_analyze", new { projectPath = _tempDirectory, incremental = true, allowFullFallback = true }, TimeSpan.FromSeconds(60)),
            second.CallToolAsync("lifeblood_analyze", new { projectPath = _tempDirectory, incremental = true, allowFullFallback = true }, TimeSpan.FromSeconds(60)));
        using var firstRpc = responses[0]; using var secondRpc = responses[1];
        using var firstPayload = McpProcessTestClient.ParseToolPayload(firstRpc);
        using var secondPayload = McpProcessTestClient.ParseToolPayload(secondRpc);
        Assert.Equal(firstPayload.RootElement.GetProperty("analysisRequestId").GetString(), secondPayload.RootElement.GetProperty("analysisRequestId").GetString());
        foreach (var payload in new[] { firstPayload, secondPayload })
        {
            Assert.Equal(2, payload.RootElement.GetProperty("waiterCount").GetInt32());
            Assert.Equal("compilationStateUnavailable", payload.RootElement.GetProperty("fallbackReason").GetString());
            Assert.Equal(JsonValueKind.Object, payload.RootElement.GetProperty("usage").ValueKind);
            var receipt = payload.RootElement.GetProperty("semanticRetention");
            Assert.Equal("replaced", receipt.GetProperty("action").GetString());
            Assert.Equal(original.SnapshotId, receipt.GetProperty("displacedHotSnapshotId").GetString());
            Assert.Equal(originalPublisher, receipt.GetProperty("displacedClientLeaseId").GetString());
            Assert.False(string.IsNullOrWhiteSpace(receipt.GetProperty("publishingClientLeaseId").GetString()));
            Assert.Equal(1, receipt.GetProperty("hotSemanticOwnerCount").GetInt32());
            Assert.Equal("usage", receipt.GetProperty("recoveryCostReceipt").GetString());
        }
        Assert.Equal(before.AnalysisGeneration + 1, (await ReadSessionAsync(second)).AnalysisGeneration);
        using var compileRpc = await first.CallToolAsync("lifeblood_compile_check",
            new { filePaths = new[] { "Type000.cs" }, staleRefresh = false });
        using var compile = McpProcessTestClient.ParseToolPayload(compileRpc);
        Assert.True(compile.RootElement.GetProperty("success").GetBoolean());
        Assert.False(daemon.HasExited);
    }

    [SkippableFact]
    public async Task MixedSchemaProxiesShareOneCanonicalPublicationAcrossBatchAndRefresh()
    {
        var dll = McpProcessTestClient.LocateServerDll();
        Skip.IfNot(File.Exists(dll), $"Server dll not found at {dll}.");
        WriteCSharpWorkspace(fileCount: 250);
        var sourcePath = Path.Combine(_tempDirectory, "Collision.cs");
        File.WriteAllText(sourcePath, "namespace Coalesce; public class Collision { public void M(int x) {} public void M(ref int x) {} }");
        var pipeName = UniquePipeName();
        await using var daemon = await StartDaemonAsync(dll, pipeName, _tempDirectory);
        await using var legacy = StartProxy(dll, pipeName, _tempDirectory);
        await using var canonical = StartProxy(dll, pipeName, _tempDirectory);
        using var legacyInit = await legacy.InitializeAsync();
        using var canonicalInit = await canonical.SendRequestAsync(JsonSerializer.Serialize(new {
            jsonrpc = "2.0", id = 9001, method = "initialize",
            @params = new Dictionary<string, object> {
                [Lifeblood.Connectors.Mcp.McpProtocolSpec.SchemaVersions.InitializeParameter] = Lifeblood.Connectors.Mcp.McpProtocolSpec.SchemaVersions.Canonical }
        }), TimeSpan.FromSeconds(30));
        Assert.Equal("v1", legacyInit.RootElement.GetProperty("result").GetProperty("lifebloodSchemaVersion").GetString());
        Assert.Equal("v2", canonicalInit.RootElement.GetProperty("result").GetProperty("lifebloodSchemaVersion").GetString());
        var calls = await Task.WhenAll(
            legacy.CallToolAsync("lifeblood_analyze", new { projectPath = _tempDirectory }, TimeSpan.FromSeconds(60)),
            canonical.CallToolAsync("lifeblood_analyze", new { projectPath = _tempDirectory }, TimeSpan.FromSeconds(60)));
        using var firstRpc = calls[0]; using var secondRpc = calls[1];
        using var first = McpProcessTestClient.ParseToolPayload(firstRpc);
        using var second = McpProcessTestClient.ParseToolPayload(secondRpc);
        Assert.Equal(first.RootElement.GetProperty("analysisRequestId").GetString(), second.RootElement.GetProperty("analysisRequestId").GetString());
        Assert.Equal(2, first.RootElement.GetProperty("waiterCount").GetInt32());
        Assert.Contains("schema v1", first.RootElement.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("schema v1", second.RootElement.ToString(), StringComparison.Ordinal);
        var before = await ReadSessionAsync(legacy);
        Assert.Equal(1, before.AnalysisGeneration);
        using var oldRpc = await legacy.CallToolAsync("lifeblood_lookup", new { symbolId = "type:Coalesce.Collision" });
        using var old = McpProcessTestClient.ParseToolPayload(oldRpc);
        using var currentRpc = await canonical.CallToolAsync("lifeblood_lookup", new { symbolId = "type:Coalesce.Collision" });
        using var current = McpProcessTestClient.ParseToolPayload(currentRpc);
        Assert.Equal("type:Coalesce.Collision", old.RootElement.GetProperty("id").GetString());
        Assert.StartsWith("type:v2|", current.RootElement.GetProperty("id").GetString(), StringComparison.Ordinal);
        Assert.Equal(before.SnapshotId, old.RootElement.GetProperty("envelope").GetProperty("snapshotId").GetString());
        Assert.Equal(before.SnapshotId, current.RootElement.GetProperty("envelope").GetProperty("snapshotId").GetString());
        using var batchRpc = await legacy.CallToolAsync("lifeblood_batch", new { calls = new object[] {
            new { tool = "lifeblood_lookup", arguments = new { symbolId = "type:Coalesce.Collision" } },
            new { tool = "lifeblood_lookup", arguments = new { symbolId = "method:Coalesce.Collision.M(int)" } }
        } });
        using var batch = McpProcessTestClient.ParseToolPayload(batchRpc);
        var rows = batch.RootElement.GetProperty("results");
        Assert.False(rows[0].GetProperty("isError").GetBoolean());
        Assert.True(rows[1].GetProperty("isError").GetBoolean());
        using var ambiguity = JsonDocument.Parse(rows[1].GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.Equal("legacy-canonical-id-ambiguity", ambiguity.RootElement.GetProperty("failure").GetString());
        Assert.Equal(2, ambiguity.RootElement.GetProperty("canonicalCandidates").GetArrayLength());
        foreach (var proxy in new[] { legacy, canonical })
        {
            using var compactRpc = await proxy.CallToolAsync("lifeblood_batch", new { responseFormat = "compact", calls = new object[] {
                new { tool = "lifeblood_lookup", arguments = new { symbolId = "type:Coalesce.Collision" } },
                new { tool = "lifeblood_lookup", arguments = new { symbolId = "method:Coalesce.Collision.M(int)" } },
            } });
            using var compact = McpProcessTestClient.ParseToolPayload(compactRpc);
            Assert.Equal(before.SnapshotId, compact.RootElement.GetProperty("envelope").GetProperty("snapshotId").GetString());
            var compactRows = compact.RootElement.GetProperty("results");
            Assert.False(compactRows[0].GetProperty("isError").GetBoolean());
            Assert.True(compactRows[1].GetProperty("isError").GetBoolean());
            var compactAddress = compactRows[0].GetProperty("payload").GetProperty("id").GetString();
            if (proxy == legacy)
            {
                Assert.Equal("type:Coalesce.Collision", compactAddress);
                Assert.Equal("legacy-canonical-id-ambiguity", compactRows[1].GetProperty("payload").GetProperty("failure").GetString());
                Assert.Equal(2, compactRows[1].GetProperty("payload").GetProperty("canonicalCandidates").GetArrayLength());
                Assert.Contains("schema v1", compactRows[0].GetProperty("evidence").ToString(), StringComparison.Ordinal);
                var contentBytes = Encoding.UTF8.GetByteCount(batchRpc.RootElement.GetRawText());
                var compactBytes = Encoding.UTF8.GetByteCount(compactRpc.RootElement.GetRawText());
                Assert.True(compactBytes < contentBytes);
                _output.WriteLine("COMPACT_BATCH_WIRE_MEASUREMENT=" + JsonSerializer.Serialize(new
                {
                    scope = "Two lookup calls through the actual shared daemon and v1 proxy, same publication",
                    fixture = "250 generated type files plus legal overload source",
                    calls = 2, snapshotId = before.SnapshotId, contentBytes, compactBytes,
                }));
            }
            else Assert.StartsWith("type:v2|", compactAddress, StringComparison.Ordinal);
            Assert.False(compactRows[0].GetProperty("evidence").TryGetProperty("snapshotId", out _));
            Assert.True(compactRows[0].GetProperty("evidence").TryGetProperty("truthTier", out _));
        }
        using var noopRpc = await canonical.CallToolAsync("lifeblood_analyze", new { projectPath = _tempDirectory, incremental = true });
        Assert.Equal(before.SnapshotId, (await ReadSessionAsync(legacy)).SnapshotId);
        File.AppendAllText(sourcePath, " public class Added {} ");
        using var refreshRpc = await legacy.CallToolAsync("lifeblood_analyze", new { projectPath = _tempDirectory, incremental = true });
        var after = await ReadSessionAsync(canonical);
        Assert.Equal(2, after.AnalysisGeneration);
        Assert.NotEqual(before.SnapshotId, after.SnapshotId);
        using var pinnedRpc = await legacy.CallToolAsync("lifeblood_lookup", new { symbolId = "type:Coalesce.Collision", snapshotId = before.SnapshotId });
        using var pinned = McpProcessTestClient.ParseToolPayload(pinnedRpc);
        Assert.Equal(before.SnapshotId, pinned.RootElement.GetProperty("envelope").GetProperty("snapshotId").GetString());
        Assert.Equal("type:Coalesce.Collision", pinned.RootElement.GetProperty("id").GetString());
        using var rejected = await canonical.CallToolAsync("lifeblood_lookup", new { symbolId = "type:Coalesce.Collision", expectedSnapshotId = before.SnapshotId });
        Assert.True(rejected.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.False(daemon.HasExited);
    }

    [SkippableFact]
    public async Task DisconnectedAnalyzeWaiter_DoesNotPoisonSurvivingSharedWaiter()
    {
        var dll = McpProcessTestClient.LocateServerDll();
        Skip.IfNot(File.Exists(dll),
            $"Server dll not found at {dll}. Run `dotnet build tests/Lifeblood.Tests` first.");

        WriteCSharpWorkspace(fileCount: 500);
        var pipeName = UniquePipeName();
        await using var daemon = await StartDaemonAsync(dll, pipeName, _tempDirectory);
        await using var disappearingProxy = StartProxy(dll, pipeName, _tempDirectory);
        await using var survivingProxy = StartProxy(dll, pipeName, _tempDirectory);
        await using var observerProxy = StartProxy(dll, pipeName, _tempDirectory);
        using var disappearingInitialize = await disappearingProxy.InitializeAsync();
        using var survivingInitialize = await survivingProxy.InitializeAsync();
        using var observerInitialize = await observerProxy.InitializeAsync();

        var disappearingCall = disappearingProxy.CallToolAsync(
            "lifeblood_analyze",
            new { projectPath = _tempDirectory, readOnly = false },
            TimeSpan.FromSeconds(90));
        await WaitUntilSharedAnalyzeInFlightAsync(observerProxy);

        var survivingCall = survivingProxy.CallToolAsync(
            "lifeblood_analyze",
            new { projectPath = _tempDirectory, readOnly = false },
            TimeSpan.FromSeconds(90));
        await Task.Delay(100);
        await disappearingProxy.TerminateAsync();

        using var survivingResponse = await survivingCall;
        using var payload = McpProcessTestClient.ParseToolPayload(survivingResponse);

        Assert.Equal("full", payload.RootElement.GetProperty("mode").GetString());
        Assert.StartsWith(
            "analysis_request_",
            payload.RootElement.GetProperty("analysisRequestId").GetString(),
            StringComparison.Ordinal);

        try
        {
            using var ignored = await disappearingCall;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            // The first proxy is the intentionally broken pipe in this test.
        }

        var session = await ReadSessionAsync(observerProxy);
        Assert.True(session.HasGraphLoaded);
        Assert.Equal(1, session.AnalysisGeneration);
        Assert.False(daemon.HasExited);
    }

    [SkippableFact]
    public async Task PersistentProxy_CancelledAnalyzeDoesNotPublishAndRetrySucceeds()
    {
        var dll = McpProcessTestClient.LocateServerDll();
        Skip.IfNot(File.Exists(dll),
            $"Server dll not found at {dll}. Run `dotnet build tests/Lifeblood.Tests` first.");

        WriteCSharpWorkspace(fileCount: 500);
        var pipeName = UniquePipeName();
        await using var daemon = await StartDaemonAsync(dll, pipeName, _tempDirectory);
        await using var firstProxy = StartProxy(dll, pipeName, _tempDirectory);
        await using var secondProxy = StartProxy(dll, pipeName, _tempDirectory);
        using var firstInitialize = await firstProxy.InitializeAsync();
        using var secondInitialize = await secondProxy.InitializeAsync();

        const int analyzeId = 8101;
        await firstProxy.WriteLineAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = analyzeId,
            method = "tools/call",
            @params = new
            {
                name = "lifeblood_analyze",
                arguments = new { projectPath = _tempDirectory, readOnly = false },
            },
        }));
        await Task.Delay(10);
        await firstProxy.WriteLineAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "notifications/cancelled",
            @params = new { requestId = analyzeId },
        }));

        using var cancelled = McpProcessTestClient.AssertValidJsonRpcLine(
            await firstProxy.ReadJsonLineAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(analyzeId, cancelled.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(
            -32800,
            cancelled.RootElement.GetProperty("error").GetProperty("code").GetInt32());

        var afterCancellation = await ReadSessionAsync(secondProxy);
        Assert.False(afterCancellation.HasGraphLoaded);
        Assert.Equal(0, afterCancellation.AnalysisGeneration);

        using var retryResponse = await secondProxy.CallToolAsync(
            "lifeblood_analyze",
            new { projectPath = _tempDirectory, readOnly = false },
            TimeSpan.FromSeconds(90));
        using var retryPayload = McpProcessTestClient.ParseToolPayload(retryResponse);
        Assert.Equal(1, retryPayload.RootElement.GetProperty("envelope")
            .GetProperty("analysisGeneration").GetInt32());
        Assert.False(daemon.HasExited);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("compact")]
    public async Task ActiveBatchCancellation_StopsWorkAndKeepsPublicationUsable(string responseFormat)
    {
        var dll = McpProcessTestClient.LocateServerDll();
        Assert.True(File.Exists(dll), $"Server dll not found at {dll}.");
        WriteCSharpWorkspace(fileCount: 500);
        var pipeName = UniquePipeName();
        await using var daemon = await StartDaemonAsync(dll, pipeName, _tempDirectory);
        await using var caller = StartProxy(dll, pipeName, _tempDirectory);
        await using var observer = StartProxy(dll, pipeName, _tempDirectory);
        using var callerInitialize = await caller.InitializeAsync();
        using var observerInitialize = await observer.InitializeAsync();
        using var analyzed = await caller.CallToolAsync("lifeblood_analyze",
            new { projectPath = _tempDirectory, readOnly = false }, TimeSpan.FromSeconds(60));
        Assert.False(analyzed.RootElement.GetProperty("result").TryGetProperty("isError", out var analyzeError) && analyzeError.GetBoolean());
        var before = await ReadSessionAsync(observer);
        using var warmRpc = await caller.CallToolAsync("lifeblood_find_references", new { symbolId = "type:Coalesce.Type000" });
        using var warm = McpProcessTestClient.ParseToolPayload(warmRpc);
        var referenceCount = warm.RootElement.GetProperty("count").GetInt32();
        Assert.True(referenceCount > 0);

        const int batchId = 8201;
        var calls = Enumerable.Range(0, 32).Select(_ => new
        {
            tool = "lifeblood_find_references", arguments = new { symbolId = "type:Coalesce.Type000" },
        }).ToArray();
        await caller.WriteLineAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = batchId, method = "tools/call",
            @params = new { name = "lifeblood_batch", arguments = new { responseFormat, calls } },
        }));

        var observeDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var observedActiveRequests = 0;
        while (DateTime.UtcNow < observeDeadline)
        {
            using var statusRpc = await observer.CallToolAsync("lifeblood_capabilities", timeout: TimeSpan.FromSeconds(5));
            using var status = McpProcessTestClient.ParseToolPayload(statusRpc);
            var shared = status.RootElement.GetProperty("sharedService");
            Assert.Equal(2, shared.GetProperty("clientCount").GetInt32());
            observedActiveRequests = shared.GetProperty("activeRequestCount").GetInt32();
            // The observer owns one request; a second proves the caller's batch is active.
            if (observedActiveRequests >= 2) break;
            await Task.Delay(10);
        }
        Assert.True(observedActiveRequests >= 2, "The batch completed before active-work observation; cancellation was not exercised.");

        var stopWatch = Stopwatch.StartNew();
        await caller.WriteLineAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", method = "notifications/cancelled", @params = new { requestId = batchId },
        }));
        using var cancelled = McpProcessTestClient.AssertValidJsonRpcLine(
            await caller.ReadJsonLineAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(batchId, cancelled.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(-32800, cancelled.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        using var stoppedRpc = await observer.CallToolAsync("lifeblood_capabilities", timeout: TimeSpan.FromSeconds(5));
        using var stopped = McpProcessTestClient.ParseToolPayload(stoppedRpc);
        var stoppedActiveRequests = stopped.RootElement.GetProperty("sharedService").GetProperty("activeRequestCount").GetInt32();
        Assert.Equal(1, stoppedActiveRequests);
        stopWatch.Stop();
        Assert.InRange(stopWatch.Elapsed.TotalSeconds, 0, 5);

        using var lookupRpc = await caller.CallToolAsync("lifeblood_lookup", new { symbolId = "type:Coalesce.Type000" }, TimeSpan.FromSeconds(5));
        using var lookup = McpProcessTestClient.ParseToolPayload(lookupRpc);
        Assert.Equal("type:Coalesce.Type000", lookup.RootElement.GetProperty("id").GetString());
        Assert.Equal(before.SnapshotId, lookup.RootElement.GetProperty("envelope").GetProperty("snapshotId").GetString());
        using var retryRpc = await observer.CallToolAsync("lifeblood_find_references", new { symbolId = "type:Coalesce.Type000" }, TimeSpan.FromSeconds(5));
        using var retry = McpProcessTestClient.ParseToolPayload(retryRpc);
        Assert.Equal(referenceCount, retry.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(before, await ReadSessionAsync(observer));
        Assert.False(daemon.HasExited);
        _output.WriteLine("BATCH_TRANSPORT_CANCELLATION=" + JsonSerializer.Serialize(new
        {
            responseFormat, sourceFiles = 500, plannedCalls = calls.Length,
            activeRequestsBeforeCancellation = observedActiveRequests,
            activeRequestsAfterCancellation = stoppedActiveRequests,
            observerRequestIncluded = true, cancellationCode = -32800,
            cancellationAndStoppedObservationMs = stopWatch.Elapsed.TotalMilliseconds,
            boundMs = 5000, snapshotId = before.SnapshotId, analysisGeneration = before.AnalysisGeneration,
            subsequentCallerLookup = true, subsequentObserverReferences = referenceCount,
        }));
    }

    [SkippableFact]
    public async Task MaintenanceDrain_RefusesUnrelatedLeaseThenStopsExclusiveOwner()
    {
        var dll = McpProcessTestClient.LocateServerDll();
        Skip.IfNot(File.Exists(dll),
            $"Server dll not found at {dll}. Run `dotnet build tests/Lifeblood.Tests` first.");

        var pipeName = UniquePipeName();
        await using var daemon = await StartDaemonAsync(dll, pipeName, _tempDirectory);
        await using var firstProxy = StartProxy(dll, pipeName, _tempDirectory);
        await using var secondProxy = StartProxy(dll, pipeName, _tempDirectory);
        using var firstInitialize = await firstProxy.InitializeAsync();
        using var secondInitialize = await secondProxy.InitializeAsync();

        using var rejected = await firstProxy.SendRequestAsync(
            JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 7001,
                method = "lifeblood/shared/drain",
                @params = new { coordinatedDrain = false },
            }),
            TimeSpan.FromSeconds(10));
        var rejectedResult = rejected.RootElement.GetProperty("result");
        Assert.False(rejectedResult.GetProperty("accepted").GetBoolean());
        Assert.Equal(1, rejectedResult.GetProperty("unrelatedClientCount").GetInt32());
        Assert.False(daemon.HasExited);

        await secondProxy.DisposeAsync();
        using var accepted = await firstProxy.SendRequestAsync(
            JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 7002,
                method = "lifeblood/shared/drain",
                @params = new { coordinatedDrain = false },
            }),
            TimeSpan.FromSeconds(10));
        Assert.True(accepted.RootElement.GetProperty("result").GetProperty("accepted").GetBoolean());

        await daemon.WaitForExitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, daemon.ExitCode);
    }

    [SkippableFact]
    public async Task MalformedProxyFrame_ReturnsParseErrorAndKeepsServing()
    {
        var dll = McpProcessTestClient.LocateServerDll();
        Skip.IfNot(File.Exists(dll),
            $"Server dll not found at {dll}. Run `dotnet build tests/Lifeblood.Tests` first.");

        var pipeName = UniquePipeName();
        await using var daemon = await StartDaemonAsync(dll, pipeName);
        await using var proxy = StartProxy(dll, pipeName);
        using var initialize = await proxy.InitializeAsync();

        await proxy.WriteLineAsync("{ this is not json");
        using var parseError = McpProcessTestClient.AssertValidJsonRpcLine(
            await proxy.ReadJsonLineAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(
            -32700,
            parseError.RootElement.GetProperty("error").GetProperty("code").GetInt32());

        var session = await ReadSessionAsync(proxy);
        Assert.False(session.HasGraphLoaded);
        Assert.Equal(0, session.AnalysisGeneration);
        Assert.False(proxy.HasExited);
        Assert.False(daemon.HasExited);
    }

    [SkippableFact]
    public async Task DuplicateDaemonForSamePipe_ExitsWithoutDisturbingOwner()
    {
        var dll = McpProcessTestClient.LocateServerDll();
        Skip.IfNot(File.Exists(dll),
            $"Server dll not found at {dll}. Run `dotnet build tests/Lifeblood.Tests` first.");

        var pipeName = UniquePipeName();
        await using var owner = await StartDaemonAsync(dll, pipeName, _tempDirectory);
        await using var duplicate = McpProcessTestClient.Start(
            dll,
            "--shared-daemon",
            pipeName,
            "--shared-key",
            _tempDirectory);

        await duplicate.WaitForExitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, duplicate.ExitCode);
        Assert.False(owner.HasExited);

        await using var proxy = StartProxy(dll, pipeName, _tempDirectory);
        using var initialize = await proxy.InitializeAsync();
        var session = await ReadSessionAsync(proxy);
        Assert.False(session.HasGraphLoaded);
    }

    [SkippableFact]
    public async Task CrashedDaemon_ProxySurvivesAndReplacementStartsWithDisposedSession()
    {
        var dll = McpProcessTestClient.LocateServerDll();
        Skip.IfNot(File.Exists(dll),
            $"Server dll not found at {dll}. Run `dotnet build tests/Lifeblood.Tests` first.");

        var pipeName = UniquePipeName();
        await using var owner = await StartDaemonAsync(dll, pipeName, _tempDirectory);
        var environment = new Dictionary<string, string?>
        {
            ["LIFEBLOOD_SHARED_DAEMON_AUTOSTART"] = "0",
        };
        await using var proxy = McpProcessTestClient.Start(
            dll,
            environment,
            "--shared",
            "--shared-pipe",
            pipeName,
            "--shared-key",
            _tempDirectory);

        using var initialize = await proxy.InitializeAsync();
        await AnalyzeGraphAsync(proxy, _firstGraphPath);
        var beforeCrash = await ReadSessionAsync(proxy);
        Assert.True(beforeCrash.HasGraphLoaded);
        Assert.Equal(1, beforeCrash.AnalysisGeneration);
        Assert.StartsWith("snap_", beforeCrash.SnapshotId, StringComparison.Ordinal);

        await owner.TerminateAsync();

        using var unavailable = await proxy.CallToolAsync("lifeblood_capabilities");
        var error = unavailable.RootElement.GetProperty("error");
        Assert.Equal(-32603, error.GetProperty("code").GetInt32());
        Assert.Equal("shared-proxy", error.GetProperty("data").GetProperty("phase").GetString());
        Assert.Contains("automatic startup is disabled", error.GetProperty("message").GetString());
        Assert.False(proxy.HasExited);

        await using var replacement = await StartDaemonAsync(dll, pipeName, _tempDirectory);
        var afterRestart = await ReadSessionAsync(proxy);
        Assert.False(afterRestart.HasGraphLoaded);
        Assert.Equal(0, afterRestart.AnalysisGeneration);
        Assert.Equal("", afterRestart.SnapshotId);
        Assert.NotEqual(owner.ProcessId, replacement.ProcessId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private string WriteGraph(string fileName, params string[] typeNames)
    {
        var builder = new GraphBuilder()
            .AddSymbol(new Symbol
            {
                Id = "mod:Shared",
                Name = "Shared",
                Kind = SymbolKind.Module,
            });

        foreach (var typeName in typeNames)
        {
            builder.AddSymbol(new Symbol
            {
                Id = $"type:Shared.{typeName}",
                Name = typeName,
                Kind = SymbolKind.Type,
                ParentId = "mod:Shared",
                FilePath = $"{typeName}.cs",
                Line = 1,
            });
        }

        var document = new GraphDocument
        {
            Language = "test",
            Adapter = new AdapterCapability
            {
                CanDiscoverSymbols = true,
                TypeResolution = ConfidenceLevel.Proven,
            },
            Graph = builder.Build(),
        };

        var path = Path.Combine(_tempDirectory, fileName);
        using var stream = File.Create(path);
        new JsonGraphExporter().Export(document, stream);
        return path;
    }

    private void WriteCSharpWorkspace(int fileCount)
    {
        File.WriteAllText(
            Path.Combine(_tempDirectory, "Coalesce.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        for (var index = 0; index < fileCount; index++)
        {
            var next = (index + 1) % fileCount;
            File.WriteAllText(
                Path.Combine(_tempDirectory, $"Type{index:D3}.cs"),
                $"namespace Coalesce; public sealed class Type{index:D3} {{ private Type{next:D3}? _next; public Type{next:D3}? Next() => _next; }}");
        }
    }

    private static string UniquePipeName()
        => $"lifeblood-process-test-{Guid.NewGuid():N}";

    private static async Task<McpProcessTestClient> StartDaemonAsync(
        string dll,
        string pipeName,
        string? workspaceKey = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var daemon = workspaceKey == null
            ? McpProcessTestClient.Start(
                dll,
                environment,
                "--shared-daemon",
                pipeName)
            : McpProcessTestClient.Start(
                dll,
                environment,
                "--shared-daemon",
                pipeName,
                "--shared-key",
                workspaceKey);
        try
        {
            await McpProcessTestClient.WaitForPipeAsync(
                pipeName,
                TimeSpan.FromSeconds(10));
            return daemon;
        }
        catch
        {
            await daemon.DisposeAsync();
            throw;
        }
    }

    private static McpProcessTestClient StartProxy(
        string dll,
        string pipeName,
        string? workspaceKey = null)
        => workspaceKey == null
            ? McpProcessTestClient.Start(dll, "--shared", "--shared-pipe", pipeName)
            : McpProcessTestClient.Start(
                dll,
                "--shared",
                "--shared-pipe",
                pipeName,
                "--shared-key",
                workspaceKey);

    private static async Task<JsonDocument> SendRawHandshakeAsync(
        string pipeName,
        object request)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var reader = new StreamReader(
            pipe,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        await using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            leaveOpen: true)
        {
            AutoFlush = true,
        };

        await writer.WriteLineAsync(JsonSerializer.Serialize(request));
        var responseLine = await reader.ReadLineAsync()
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(responseLine);
        return JsonDocument.Parse(responseLine);
    }

    private static async Task AnalyzeGraphAsync(
        McpProcessTestClient proxy,
        string graphPath)
    {
        using var response = await proxy.CallToolAsync(
            "lifeblood_analyze",
            new { graphPath },
            TimeSpan.FromSeconds(30));
        using var payload = McpProcessTestClient.ParseToolPayload(response);
        Assert.Equal("full", payload.RootElement.GetProperty("mode").GetString());
    }

    private static async Task<SessionState> ReadSessionAsync(
        McpProcessTestClient proxy)
    {
        using var response = await proxy.CallToolAsync("lifeblood_capabilities");
        using var payload = McpProcessTestClient.ParseToolPayload(response);
        var session = payload.RootElement.GetProperty("session");
        return new SessionState(
            session.GetProperty("hasGraphLoaded").GetBoolean(),
            session.GetProperty("analysisGeneration").GetInt64(),
            session.GetProperty("snapshotId").GetString() ?? "");
    }

    private static async Task<SharedStatus> ReadSharedStatusAsync(
        McpProcessTestClient proxy)
    {
        using var response = await proxy.CallToolAsync("lifeblood_capabilities");
        using var payload = McpProcessTestClient.ParseToolPayload(response);
        var status = payload.RootElement.GetProperty("sharedService");
        return new SharedStatus(
            status.GetProperty("active").GetBoolean(),
            status.GetProperty("mode").GetString() ?? "",
            status.GetProperty("protocolVersion").GetInt32(),
            status.GetProperty("daemonInstanceId").GetString() ?? "",
            status.GetProperty("clientCount").GetInt32(),
            status.GetProperty("inFlightAnalysisCount").GetInt32(),
            status.GetProperty("idleEvictionEnabled").GetBoolean(),
            status.GetProperty("idleTimeoutSeconds").ValueKind == JsonValueKind.Null
                ? null
                : status.GetProperty("idleTimeoutSeconds").GetDouble());
    }

    private static async Task WaitUntilSharedAnalyzeInFlightAsync(
        McpProcessTestClient observerProxy)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var status = await ReadSharedStatusAsync(observerProxy);
            if (status.InFlightAnalysisCount > 0)
                return;

            await Task.Delay(50);
        }

        throw new TimeoutException("Shared daemon did not report an in-flight analyze.");
    }

    private readonly record struct SessionState(
        bool HasGraphLoaded,
        long AnalysisGeneration,
        string SnapshotId);

    private readonly record struct SharedStatus(
        bool Active,
        string Mode,
        int ProtocolVersion,
        string DaemonInstanceId,
        int ClientCount,
        int InFlightAnalysisCount,
        bool IdleEvictionEnabled,
        double? IdleTimeoutSeconds);
}

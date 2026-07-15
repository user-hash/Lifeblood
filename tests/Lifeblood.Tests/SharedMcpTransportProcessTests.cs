using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Lifeblood.Adapters.JsonGraph;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Xunit;

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

    public SharedMcpTransportProcessTests()
    {
        _tempDirectory = Path.Combine(
            Path.GetTempPath(),
            $"lifeblood-shared-process-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _firstGraphPath = WriteGraph("first.graph.json", "First");
        _secondGraphPath = WriteGraph("second.graph.json", "First", "Second");
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
            status.GetProperty("clientCount").GetInt32());
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
        int ClientCount);
}

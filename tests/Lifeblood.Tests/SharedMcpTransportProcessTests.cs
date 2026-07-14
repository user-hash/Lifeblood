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

        var pipeName = UniquePipeName();
        await using var daemon = await StartDaemonAsync(dll, pipeName);
        await using var firstProxy = StartProxy(dll, pipeName);
        await using var secondProxy = StartProxy(dll, pipeName);

        using var firstInitialize = await firstProxy.InitializeAsync();
        using var secondInitialize = await secondProxy.InitializeAsync();
        Assert.NotEqual(daemon.ProcessId, firstProxy.ProcessId);
        Assert.NotEqual(firstProxy.ProcessId, secondProxy.ProcessId);

        await AnalyzeGraphAsync(firstProxy, _firstGraphPath);
        var observedBySecond = await ReadSessionAsync(secondProxy);
        Assert.True(observedBySecond.HasGraphLoaded);
        Assert.Equal(1, observedBySecond.AnalysisGeneration);

        await AnalyzeGraphAsync(secondProxy, _secondGraphPath);
        var observedByFirst = await ReadSessionAsync(firstProxy);
        Assert.True(observedByFirst.HasGraphLoaded);
        Assert.Equal(2, observedByFirst.AnalysisGeneration);

        using var lookupRpc = await firstProxy.CallToolAsync(
            "lifeblood_lookup",
            new { symbolId = "type:Shared.Second" });
        using var lookup = McpProcessTestClient.ParseToolPayload(lookupRpc);
        Assert.Contains("Second", lookup.RootElement.ToString(), StringComparison.Ordinal);

        await firstProxy.DisposeAsync();
        var afterDisconnect = await ReadSessionAsync(secondProxy);
        Assert.Equal(2, afterDisconnect.AnalysisGeneration);
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
        await using var firstDaemon = await StartDaemonAsync(dll, firstPipe);
        await using var secondDaemon = await StartDaemonAsync(dll, secondPipe);
        await using var firstProxy = StartProxy(dll, firstPipe);
        await using var secondProxy = StartProxy(dll, secondPipe);

        using var firstInitialize = await firstProxy.InitializeAsync();
        using var secondInitialize = await secondProxy.InitializeAsync();

        await AnalyzeGraphAsync(firstProxy, _firstGraphPath);

        var firstState = await ReadSessionAsync(firstProxy);
        var secondState = await ReadSessionAsync(secondProxy);
        Assert.True(firstState.HasGraphLoaded);
        Assert.Equal(1, firstState.AnalysisGeneration);
        Assert.False(secondState.HasGraphLoaded);
        Assert.Equal(0, secondState.AnalysisGeneration);

        using var missingRpc = await secondProxy.CallToolAsync(
            "lifeblood_lookup",
            new { symbolId = "type:Shared.First" });
        var missingResult = missingRpc.RootElement.GetProperty("result");
        Assert.True(missingResult.GetProperty("isError").GetBoolean());
        Assert.Contains("No graph loaded", missingResult.ToString(), StringComparison.Ordinal);
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
        await using var owner = await StartDaemonAsync(dll, pipeName);
        await using var duplicate = McpProcessTestClient.Start(
            dll,
            "--shared-daemon",
            pipeName);

        await duplicate.WaitForExitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, duplicate.ExitCode);
        Assert.False(owner.HasExited);

        await using var proxy = StartProxy(dll, pipeName);
        using var initialize = await proxy.InitializeAsync();
        var session = await ReadSessionAsync(proxy);
        Assert.False(session.HasGraphLoaded);
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

    private static string UniquePipeName()
        => $"lifeblood-process-test-{Guid.NewGuid():N}";

    private static async Task<McpProcessTestClient> StartDaemonAsync(
        string dll,
        string pipeName)
    {
        var daemon = McpProcessTestClient.Start(
            dll,
            "--shared-daemon",
            pipeName);
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

    private static McpProcessTestClient StartProxy(string dll, string pipeName)
        => McpProcessTestClient.Start(
            dll,
            "--shared",
            "--shared-pipe",
            pipeName);

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
            session.GetProperty("analysisGeneration").GetInt64());
    }

    private readonly record struct SessionState(
        bool HasGraphLoaded,
        long AnalysisGeneration);
}

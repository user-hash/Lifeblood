using System.Text.Json;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// End-to-end integration coverage for the actual Program.cs stdio transport.
/// The tests boot the compiled server through dotnet, speak JSON-RPC over real
/// redirected streams, and pin stdout purity plus initialize/tools-list health.
/// </summary>
[Collection(McpProcessTestCollection.Name)]
public class McpStdioLoopTests
{
    [SkippableFact]
    public async Task McpServer_OverStdio_InitializeHandshake_ReturnsSpecCompliantFrame()
    {
        var dll = McpProcessTestClient.LocateServerDll();
        Skip.IfNot(File.Exists(dll),
            $"Server dll not found at {dll}. Run `dotnet build tests/Lifeblood.Tests` first.");

        await using var process = McpProcessTestClient.Start(dll);
        await process.WriteLineAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"lifeblood-stdio-test","version":"0.0"}}}""");

        var line = await process.ReadJsonLineAsync(TimeSpan.FromSeconds(30));
        using var document = McpProcessTestClient.AssertValidJsonRpcLine(line);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("id").GetInt32());
        Assert.True(root.TryGetProperty("result", out var result), "initialize must return a result");
        Assert.True(result.TryGetProperty("protocolVersion", out var protocolVersion),
            "result.protocolVersion required by MCP spec");
        Assert.False(string.IsNullOrEmpty(protocolVersion.GetString()));
        Assert.True(result.TryGetProperty("capabilities", out _),
            "result.capabilities required by MCP spec");
        Assert.True(result.TryGetProperty("serverInfo", out var serverInfo));
        Assert.Equal("lifeblood", serverInfo.GetProperty("name").GetString());
    }

    [SkippableFact]
    public async Task McpServer_OverStdio_ToolsList_AllLinesAreValidJsonRpc()
    {
        var dll = McpProcessTestClient.LocateServerDll();
        Skip.IfNot(File.Exists(dll),
            $"Server dll not found at {dll}. Run `dotnet build tests/Lifeblood.Tests` first.");

        await using var process = McpProcessTestClient.Start(dll);
        await process.WriteLineAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"lifeblood-stdio-test","version":"0.0"}}}""");
        using var initialize = McpProcessTestClient.AssertValidJsonRpcLine(
            await process.ReadJsonLineAsync(TimeSpan.FromSeconds(30)));

        await process.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var toolsListLine = await process.ReadJsonLineAsync(TimeSpan.FromSeconds(15));
        using var document = McpProcessTestClient.AssertValidJsonRpcLine(toolsListLine);
        var root = document.RootElement;

        Assert.Equal(2, root.GetProperty("id").GetInt32());
        Assert.False(root.TryGetProperty("error", out var error),
            $"tools/list returned a JSON-RPC error: {(error.ValueKind == JsonValueKind.Object ? error.ToString() : "n/a")}");
        Assert.True(root.TryGetProperty("result", out var result));
        Assert.True(result.TryGetProperty("tools", out var tools));
        Assert.Equal(JsonValueKind.Array, tools.ValueKind);
        Assert.True(tools.GetArrayLength() > 0, "Server should advertise at least one tool.");
    }

    [SkippableFact]
    public async Task McpServer_OverStdio_Stdout_ContainsOnlyJsonRpcFrames_NoBanners()
    {
        var dll = McpProcessTestClient.LocateServerDll();
        Skip.IfNot(File.Exists(dll),
            $"Server dll not found at {dll}. Run `dotnet build tests/Lifeblood.Tests` first.");

        await using var process = McpProcessTestClient.Start(dll);
        var stdoutLines = new List<string>();

        await process.WriteLineAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"lifeblood-stdio-test","version":"0.0"}}}""");
        stdoutLines.Add(await process.ReadJsonLineAsync(TimeSpan.FromSeconds(30)));

        await process.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        stdoutLines.Add(await process.ReadJsonLineAsync(TimeSpan.FromSeconds(15)));

        stdoutLines.AddRange(
            await process.CloseInputAndDrainStdoutAsync(TimeSpan.FromSeconds(10)));

        Assert.NotEmpty(stdoutLines);
        foreach (var line in stdoutLines)
        {
            using var frame = McpProcessTestClient.AssertValidJsonRpcLine(line);
        }
    }
}

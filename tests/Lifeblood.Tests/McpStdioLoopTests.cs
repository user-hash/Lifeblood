using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// End-to-end integration coverage for the actual <c>Program.cs</c> stdio
/// I/O loop in <c>Lifeblood.Server.Mcp</c>. Every other MCP test in this
/// assembly exercises <c>McpDispatcher</c> / <c>ToolHandler</c> in-process;
/// none of them spawn the real compiled dll and read/write real pipes. That
/// gap is why the v0.6.1 <c>tools/list</c> serialization regression escaped
/// to users (Claude Code saw a <c>-32603</c> on the wire and refused to
/// reconnect — the dispatcher tests were all green).
///
/// These tests close the gap. They boot the real <c>Lifeblood.Server.Mcp.dll</c>
/// via <c>dotnet &lt;dll&gt;</c>, speak real JSON-RPC 2.0 over stdin/stdout,
/// and pin two classes of architectural invariant at the transport boundary:
///
///  1. <b>Stdout purity.</b> MCP over stdio reserves stdout exclusively for
///     JSON-RPC frames. Every line on stdout MUST parse as JSON-RPC 2.0.
///     Any future <c>Console.WriteLine</c> regression (banner, log, stray
///     printf, unflushed build warning, anything) will fail this assertion
///     because the non-JSON line will either refuse to parse or mis-align
///     the framing.
///  2. <b>Happy-path transport.</b> <c>initialize</c> → <c>tools/list</c> →
///     <c>tools/call</c> all round-trip through the real reader/writer and
///     produce spec-compliant responses. Pins the Program.cs read-line →
///     dispatch → write-line loop against regressions that the in-process
///     tests cannot see (flushing, framing, deserialization options, etc.).
///
/// INV-TEST-001 compliance: the tests are SkippableFact and explicitly Skip
/// when the server dll isn't where the test expects it (e.g. if someone runs
/// individual test classes without a full build). Real failures (process
/// crashes, non-JSON output on stdout, malformed responses) are loud
/// assertions.
/// </summary>
public class McpStdioLoopTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string LocateServerDll()
    {
        var baseDir = AppContext.BaseDirectory;
        return Path.Combine(baseDir, "Lifeblood.Server.Mcp.dll");
    }

    private static Process StartServer(string dllPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { dllPath },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = Encoding.UTF8,
        };
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet process");
        return proc;
    }

    private static async Task SendAsync(Process proc, string json)
    {
        await proc.StandardInput.WriteLineAsync(json);
        await proc.StandardInput.FlushAsync();
    }

    private static async Task<string> ReadJsonLineAsync(Process proc, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        string? line = null;
        while (!cts.IsCancellationRequested)
        {
            var readTask = proc.StandardOutput.ReadLineAsync();
            var completed = await Task.WhenAny(readTask, Task.Delay(timeout, cts.Token));
            if (completed != readTask)
            {
                throw new TimeoutException($"No stdout line received within {timeout.TotalSeconds:F0}s");
            }
            line = await readTask;
            if (line == null)
            {
                throw new InvalidOperationException("Server closed stdout unexpectedly");
            }
            // Skip blank lines — Program.cs never writes them, but a
            // well-behaved reader should tolerate stray whitespace.
            if (!string.IsNullOrWhiteSpace(line)) return line;
        }
        throw new TimeoutException("Timed out waiting for a non-empty stdout line");
    }

    private static JsonDocument AssertValidJsonRpcLine(string line)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            throw new Xunit.Sdk.XunitException(
                $"Stdout line is not valid JSON — MCP stdio requires stdout to carry ONLY JSON-RPC frames. " +
                $"Offending line: <{line}>. Parse error: {ex.Message}");
        }

        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.True(root.TryGetProperty("jsonrpc", out var jsonrpc), "Missing 'jsonrpc' field");
        Assert.Equal("2.0", jsonrpc.GetString());
        return doc;
    }

    [SkippableFact]
    public async Task McpServer_OverStdio_InitializeHandshake_ReturnsSpecCompliantFrame()
    {
        var dll = LocateServerDll();
        Skip.IfNot(File.Exists(dll),
            $"Server dll not found at {dll}. Run `dotnet build tests/Lifeblood.Tests` first.");

        using var proc = StartServer(dll);
        try
        {
            await SendAsync(proc, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"lifeblood-stdio-test","version":"0.0"}}}""");

            var line = await ReadJsonLineAsync(proc, TimeSpan.FromSeconds(30));
            using var doc = AssertValidJsonRpcLine(line);
            var root = doc.RootElement;

            Assert.Equal(1, root.GetProperty("id").GetInt32());
            Assert.True(root.TryGetProperty("result", out var result), "initialize must return a result");
            Assert.True(result.TryGetProperty("protocolVersion", out var pv), "result.protocolVersion required by MCP spec");
            Assert.False(string.IsNullOrEmpty(pv.GetString()));
            Assert.True(result.TryGetProperty("capabilities", out _), "result.capabilities required by MCP spec");
            Assert.True(result.TryGetProperty("serverInfo", out var serverInfo));
            Assert.Equal("lifeblood", serverInfo.GetProperty("name").GetString());
        }
        finally
        {
            ShutdownServer(proc);
        }
    }

    [SkippableFact]
    public async Task McpServer_OverStdio_ToolsList_AllLinesAreValidJsonRpc()
    {
        var dll = LocateServerDll();
        Skip.IfNot(File.Exists(dll),
            $"Server dll not found at {dll}. Run `dotnet build tests/Lifeblood.Tests` first.");

        using var proc = StartServer(dll);
        try
        {
            await SendAsync(proc, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"lifeblood-stdio-test","version":"0.0"}}}""");
            _ = AssertValidJsonRpcLine(await ReadJsonLineAsync(proc, TimeSpan.FromSeconds(30)));

            await SendAsync(proc, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
            var toolsListLine = await ReadJsonLineAsync(proc, TimeSpan.FromSeconds(15));
            using var doc = AssertValidJsonRpcLine(toolsListLine);
            var root = doc.RootElement;

            Assert.Equal(2, root.GetProperty("id").GetInt32());
            // The INV-TOOLREG-001 wire-format regression manifested HERE:
            // tools/list threw -32603 because McpToolInfo was a required init
            // type with [JsonIgnore]. An error response carries an "error"
            // field instead of "result"; asserting on "result" pins that the
            // wire serialization is healthy.
            Assert.False(root.TryGetProperty("error", out var err),
                $"tools/list returned a JSON-RPC error — wire serialization may be broken. error: {(err.ValueKind == JsonValueKind.Object ? err.ToString() : "n/a")}");
            Assert.True(root.TryGetProperty("result", out var result));
            Assert.True(result.TryGetProperty("tools", out var tools));
            Assert.Equal(JsonValueKind.Array, tools.ValueKind);
            Assert.True(tools.GetArrayLength() > 0, "Server should advertise at least one tool");
        }
        finally
        {
            ShutdownServer(proc);
        }
    }

    [SkippableFact]
    public async Task McpServer_OverStdio_Stdout_ContainsOnlyJsonRpcFrames_NoBanners()
    {
        // THE architectural assertion: after a normal initialize + tools/list
        // + shutdown cycle, every single line on stdout must parse as valid
        // JSON-RPC. If a future change adds Console.WriteLine for debugging
        // (banner, log, print, whatever) instead of Console.Error.WriteLine,
        // a non-JSON line appears on stdout and this test fails loudly with
        // the offending line in the error message. This is the class of
        // regression that broke Claude Code reconnect silently in the past
        // and that no in-process dispatcher test could ever catch.
        var dll = LocateServerDll();
        Skip.IfNot(File.Exists(dll),
            $"Server dll not found at {dll}. Run `dotnet build tests/Lifeblood.Tests` first.");

        using var proc = StartServer(dll);
        var stdoutLines = new List<string>();
        try
        {
            await SendAsync(proc, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"lifeblood-stdio-test","version":"0.0"}}}""");
            stdoutLines.Add(await ReadJsonLineAsync(proc, TimeSpan.FromSeconds(30)));

            await SendAsync(proc, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
            stdoutLines.Add(await ReadJsonLineAsync(proc, TimeSpan.FromSeconds(15)));

            // Close stdin — Program.cs treats that as EOF and exits cleanly.
            proc.StandardInput.Close();

            // Drain any residual stdout. If the server writes a shutdown
            // banner to stdout on the way out, it'll appear here and fail
            // the parse assertion.
            var tail = await proc.StandardOutput.ReadToEndAsync();
            foreach (var line in tail.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!string.IsNullOrWhiteSpace(line)) stdoutLines.Add(line);
            }

            await WaitForExitAsync(proc, TimeSpan.FromSeconds(10));

            Assert.NotEmpty(stdoutLines);
            foreach (var line in stdoutLines)
            {
                _ = AssertValidJsonRpcLine(line);
            }
        }
        finally
        {
            ShutdownServer(proc);
        }
    }

    private static void ShutdownServer(Process proc)
    {
        try
        {
            if (!proc.HasExited)
            {
                try { proc.StandardInput.Close(); } catch { /* ignore */ }
                if (!proc.WaitForExit(5000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                }
            }
        }
        catch { /* best-effort shutdown */ }
    }

    private static async Task WaitForExitAsync(Process proc, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Server did not exit within {timeout.TotalSeconds:F0}s after stdin close");
        }
    }
}

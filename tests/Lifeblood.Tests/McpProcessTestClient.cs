using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Lifeblood.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class McpProcessTestCollection
{
    public const string Name = "MCP process integration";
}

/// <summary>
/// Reusable owner for real Lifeblood MCP child processes. Process-level tests
/// use this instead of open-coding stdin/stdout framing and cleanup in every
/// fixture. The owner always drains stderr and kills the complete process tree
/// if graceful EOF shutdown does not finish in time.
/// </summary>
internal sealed class McpProcessTestClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Process _process;
    private readonly Task<string> _stderrDrain;
    private int _nextRequestId;
    private bool _inputClosed;
    private bool _disposed;

    private McpProcessTestClient(Process process)
    {
        _process = process;
        _stderrDrain = process.StandardError.ReadToEndAsync();
    }

    public int ProcessId => _process.Id;

    public bool HasExited => _process.HasExited;

    public int ExitCode => _process.ExitCode;

    public static string LocateServerDll()
        => Path.Combine(AppContext.BaseDirectory, "Lifeblood.Server.Mcp.dll");

    public static McpProcessTestClient Start(string dllPath, params string[] arguments)
    {
        var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(dotnetHost))
        {
            dotnetHost = "dotnet";
        }

        var start = new ProcessStartInfo
        {
            FileName = dotnetHost,
            WorkingDirectory = Path.GetDirectoryName(dllPath) ?? AppContext.BaseDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add(dllPath);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var process = Process.Start(start)
            ?? throw new InvalidOperationException("Failed to start Lifeblood MCP process.");
        return new McpProcessTestClient(process);
    }

    public async Task WriteLineAsync(string json)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_inputClosed)
        {
            throw new InvalidOperationException("The process stdin stream is already closed.");
        }

        await _process.StandardInput.WriteLineAsync(json);
        await _process.StandardInput.FlushAsync();
    }

    public async Task<JsonDocument> InitializeAsync(TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var json = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "lifeblood-process-test", version = "0.0" },
            },
        }, JsonOpts);
        return await SendRequestAsync(json, timeout ?? TimeSpan.FromSeconds(30));
    }

    public async Task<JsonDocument> CallToolAsync(
        string toolName,
        object? arguments = null,
        TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var json = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method = "tools/call",
            @params = new
            {
                name = toolName,
                arguments = arguments ?? new { },
            },
        }, JsonOpts);
        return await SendRequestAsync(json, timeout ?? TimeSpan.FromSeconds(30));
    }

    public async Task<JsonDocument> SendRequestAsync(string json, TimeSpan timeout)
    {
        await WriteLineAsync(json);
        var line = await ReadJsonLineAsync(timeout);
        return AssertValidJsonRpcLine(line);
    }

    public async Task<string> ReadJsonLineAsync(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync(cts.Token);
                if (line == null)
                {
                    var stderr = _stderrDrain.IsCompletedSuccessfully ? _stderrDrain.Result : "<still running>";
                    throw new InvalidOperationException(
                        $"MCP process {_process.Id} closed stdout unexpectedly. stderr: {stderr}");
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    return line;
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"No stdout line received from MCP process {_process.Id} within {timeout.TotalSeconds:F0}s.");
        }
    }

    public async Task<IReadOnlyList<string>> CloseInputAndDrainStdoutAsync(TimeSpan timeout)
    {
        CloseInput();
        var tailTask = _process.StandardOutput.ReadToEndAsync();
        await WaitForExitAsync(timeout);
        var tail = await tailTask;
        return tail
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
    }

    public void CloseInput()
    {
        if (_inputClosed)
        {
            return;
        }

        _inputClosed = true;
        try
        {
            _process.StandardInput.Close();
        }
        catch (InvalidOperationException)
        {
            // Process already exited and closed its redirected handles.
        }
    }

    public async Task WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"MCP process {_process.Id} did not exit within {timeout.TotalSeconds:F0}s.");
        }
    }

    public async Task TerminateAsync()
    {
        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Process exited between HasExited and Kill.
            }
        }

        try
        {
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Failed to terminate MCP process {_process.Id}.");
        }
    }

    public static async Task WaitForPipeAsync(string pipeName, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        Exception? lastFailure = null;

        while (Stopwatch.GetTimestamp() < deadline)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                using var attempt = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                await pipe.ConnectAsync(attempt.Token);
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
            {
                lastFailure = ex;
                await Task.Delay(50);
            }
        }

        throw new TimeoutException(
            $"Named pipe '{pipeName}' did not accept connections within {timeout.TotalSeconds:F0}s.",
            lastFailure);
    }

    public static JsonDocument AssertValidJsonRpcLine(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            throw new Xunit.Sdk.XunitException(
                "Stdout line is not valid JSON. MCP stdout must contain only JSON-RPC frames. " +
                $"Offending line: <{line}>. Parse error: {ex.Message}");
        }

        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.True(root.TryGetProperty("jsonrpc", out var jsonrpc), "Missing 'jsonrpc' field.");
        Assert.Equal("2.0", jsonrpc.GetString());
        return document;
    }

    public static JsonDocument ParseToolPayload(JsonDocument response)
    {
        var root = response.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            throw new Xunit.Sdk.XunitException($"JSON-RPC tool call failed: {error}");
        }

        var result = root.GetProperty("result");
        if (result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
        {
            throw new Xunit.Sdk.XunitException($"MCP tool returned isError=true: {result}");
        }

        var text = result.GetProperty("content")[0].GetProperty("text").GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new Xunit.Sdk.XunitException("MCP tool response did not contain a text payload.");
        }

        return JsonDocument.Parse(text);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseInput();

        if (!_process.HasExited)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await _process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Process exited between the timeout and Kill.
                }
            }
        }

        if (!_process.HasExited)
        {
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        await _stderrDrain.WaitAsync(TimeSpan.FromSeconds(5));
        _process.Dispose();
    }
}

using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// Optional multi-agent transport. In normal mode each MCP client owns one
/// stdio server process and therefore one retained Roslyn heap. In shared
/// mode each client still launches a tiny stdio process, but the process
/// proxies JSON-RPC frames to a workspace-keyed daemon over a named pipe.
/// The daemon owns the single GraphSession, so every attached agent sees the
/// same freshest analyze result and the large Roslyn state is retained once.
/// </summary>
internal static class SharedMcpTransport
{
    private const string SharedFlag = "--shared";
    private const string SharedDaemonFlag = "--shared-daemon";
    private const string SharedKeyFlag = "--shared-key";
    private const string SharedPipeFlag = "--shared-pipe";
    private const string SharedSessionEnv = "LIFEBLOOD_SHARED_SESSION";
    private const string SharedSessionKeyEnv = "LIFEBLOOD_SHARED_SESSION_KEY";
    private const string SharedPipeNameEnv = "LIFEBLOOD_SHARED_PIPE_NAME";
    private const string SharedProxyTraceEnv = "LIFEBLOOD_SHARED_PROXY_TRACE";

    public static bool IsSharedProxyRequested(string[] args)
        => args.Any(a => string.Equals(a, SharedFlag, StringComparison.Ordinal))
           || ReadFlag(SharedSessionEnv);

    public static bool IsSharedDaemon(string[] args)
        => args.Length >= 2 && string.Equals(args[0], SharedDaemonFlag, StringComparison.Ordinal);

    public static string ReadDaemonPipeName(string[] args)
        => args.Length >= 2 ? args[1] : throw new ArgumentException("Missing shared daemon pipe name.");

    public static async Task RunProxyAsync(
        string[] args,
        TextReader input,
        TextWriter output,
        JsonSerializerOptions jsonOpts,
        bool strictJson,
        CancellationToken cancellationToken,
        Action<string>? logError = null)
    {
        var pipeName = ResolvePipeName(args);
        await EnsureDaemonAsync(pipeName, cancellationToken, logError);
        logError?.Invoke($"Lifeblood MCP shared proxy attached to daemon pipe '{pipeName}'.");

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await input.ReadLineAsync();
            }
            catch (Exception ex)
            {
                logError?.Invoke($"stdin read failed: {ex.Message}");
                break;
            }

            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonRpcRequest? request;
            try
            {
                request = McpJsonRequestParser.DeserializeRequest(line, jsonOpts, strictJson);
                if (request == null) continue;
            }
            catch (JsonException ex)
            {
                logError?.Invoke($"Parse error before shared proxy forward: {ex.Message}");
                WriteResponse(output, McpServerLoop.BuildParseError(), jsonOpts, logError);
                continue;
            }

            var expectsResponse = request.Id != null;
            var canonicalFrame = JsonSerializer.Serialize(request, jsonOpts);
            TraceProxyFrame(canonicalFrame);
            try
            {
                var responseLine = await ForwardFrameAsync(pipeName, canonicalFrame, expectsResponse, cancellationToken);
                if (expectsResponse && !string.IsNullOrWhiteSpace(responseLine))
                {
                    output.WriteLine(responseLine);
                    output.Flush();
                }
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested) break;
                logError?.Invoke($"Shared daemon forward failed: {ex.Message}");
                await EnsureDaemonAsync(pipeName, cancellationToken, logError);
                if (expectsResponse)
                {
                    WriteResponse(output, BuildProxyError(request.Id, ex), jsonOpts, logError);
                }
            }
        }
    }

    public static async Task RunDaemonAsync(
        string pipeName,
        JsonSerializerOptions jsonOpts,
        bool strictJson,
        ToolJsonCompatibilityMode jsonCompatibilityMode,
        CancellationToken cancellationToken,
        Action<string>? logError = null)
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName(pipeName), out var ownsMutex);
        if (!ownsMutex)
        {
            logError?.Invoke($"Lifeblood MCP shared daemon for '{pipeName}' is already running.");
            return;
        }

        using var host = McpServerHost.Create(jsonCompatibilityMode);
        logError?.Invoke($"Lifeblood MCP shared daemon listening on pipe '{pipeName}'.");

        var clientTasks = new List<Task>();
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                break;
            }
            catch (Exception ex)
            {
                pipe.Dispose();
                logError?.Invoke($"Named pipe accept failed: {ex.Message}");
                continue;
            }

            clientTasks.RemoveAll(t => t.IsCompleted);
            clientTasks.Add(Task.Run(
                () => HandleDaemonClientAsync(pipe, host.Dispatcher.Dispatch, jsonOpts, strictJson, cancellationToken, logError),
                cancellationToken));
        }

        try
        {
            await Task.WhenAll(clientTasks);
        }
        catch
        {
            // Client tasks already log per-connection failures.
        }
    }

    private static async Task HandleDaemonClientAsync(
        NamedPipeServerStream pipe,
        Func<JsonRpcRequest, JsonRpcResponse?> dispatch,
        JsonSerializerOptions jsonOpts,
        bool strictJson,
        CancellationToken cancellationToken,
        Action<string>? logError)
    {
        await using (pipe)
        using (var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
        await using (var writer = new StreamWriter(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
        {
            AutoFlush = true,
        })
        {
            await McpServerLoop.RunAsync(reader, writer, dispatch, jsonOpts, strictJson, cancellationToken, logError);
        }
    }

    private static async Task<string?> ForwardFrameAsync(
        string pipeName,
        string line,
        bool expectsResponse,
        CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        await pipe.ConnectAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
        {
            AutoFlush = true,
        };

        await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);

        if (!expectsResponse)
        {
            return null;
        }

        var response = await reader.ReadLineAsync(cancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromMinutes(10), cancellationToken);
        if (response == null)
        {
            throw new IOException("Shared daemon closed the pipe without a response.");
        }

        return response;
    }

    private static async Task EnsureDaemonAsync(
        string pipeName,
        CancellationToken cancellationToken,
        Action<string>? logError)
    {
        if (CanConnect(pipeName, timeoutMs: 100))
        {
            return;
        }

        StartDaemonProcess(pipeName, logError);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (CanConnect(pipeName, timeoutMs: 250))
            {
                return;
            }

            await Task.Delay(150, cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for shared Lifeblood daemon '{pipeName}' to start.");
    }

    private static void StartDaemonProcess(string pipeName, Action<string>? logError)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            throw new InvalidOperationException("Cannot locate current process path to start the shared daemon.");
        }

        var daemonArgs = new List<string>();

        var entryAssembly = Assembly.GetEntryAssembly()?.Location;
        if (LooksLikeDotnetHost(processPath) && !string.IsNullOrEmpty(entryAssembly))
        {
            daemonArgs.Add(entryAssembly);
        }

        daemonArgs.Add(SharedDaemonFlag);
        daemonArgs.Add(pipeName);

        var psi = new ProcessStartInfo
        {
            FileName = processPath,
            Arguments = string.Join(" ", daemonArgs.Select(QuoteArgument)),
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            logError?.Invoke($"Failed to start shared daemon: {ex.Message}");
            throw;
        }
    }

    private static bool CanConnect(string pipeName, int timeoutMs)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            pipe.Connect(timeoutMs);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool LooksLikeDotnetHost(string processPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(processPath);
        return string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase);
    }

    private static string QuoteArgument(string value)
        => value.Length == 0 || value.Any(char.IsWhiteSpace) || value.Contains('"')
            ? "\"" + value.Replace("\"", "\\\"") + "\""
            : value;

    private static JsonRpcResponse BuildProxyError(JsonElement? id, Exception ex) => new()
    {
        Id = id,
        Error = new JsonRpcError
        {
            Code = -32603,
            Message = $"Shared Lifeblood daemon unavailable: {ex.Message}",
            Data = new
            {
                phase = "shared-proxy",
                recoverable = true,
                recovery = "Retry the request. The proxy will restart the workspace-keyed shared daemon if it has exited.",
            },
        },
    };

    private static void WriteResponse(
        TextWriter output,
        JsonRpcResponse response,
        JsonSerializerOptions jsonOpts,
        Action<string>? logError)
    {
        try
        {
            output.WriteLine(JsonSerializer.Serialize(response, jsonOpts));
            output.Flush();
        }
        catch (Exception ex)
        {
            logError?.Invoke($"Response write failed (transport): {ex.Message}");
        }
    }

    private static string ResolvePipeName(string[] args)
    {
        var explicitPipe = ReadArgValue(args, SharedPipeFlag)
            ?? Environment.GetEnvironmentVariable(SharedPipeNameEnv);
        if (!string.IsNullOrWhiteSpace(explicitPipe))
        {
            return SanitizePipeName(explicitPipe);
        }

        var key = ReadArgValue(args, SharedKeyFlag)
            ?? Environment.GetEnvironmentVariable(SharedSessionKeyEnv)
            ?? Directory.GetCurrentDirectory();
        var normalizedKey = Path.GetFullPath(key).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey.ToUpperInvariant())))[..24];
        return $"lifeblood-mcp-{hash}";
    }

    private static string SanitizePipeName(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.Trim())
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-');
        }

        return sb.Length == 0 ? "lifeblood-mcp-default" : sb.ToString();
    }

    private static string MutexName(string pipeName)
        => "LifebloodMcpShared-" + pipeName;

    private static string? ReadArgValue(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool ReadFlag(string environmentVariableName)
    {
        var raw = Environment.GetEnvironmentVariable(environmentVariableName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" or "shared" => true,
            _ => false,
        };
    }

    private static void TraceProxyFrame(string canonicalFrame)
    {
        var path = Environment.GetEnvironmentVariable(SharedProxyTraceEnv);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.AppendAllText(path, canonicalFrame + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // Trace is diagnostic-only; never alter MCP behavior.
        }
    }
}

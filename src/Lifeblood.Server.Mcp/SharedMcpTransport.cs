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
    private const string HandshakeKind = "lifeblood.shared.handshake";
    private const int SharedProtocolVersion = 1;
    private const string SharedFlag = "--shared";
    private const string SharedDaemonFlag = "--shared-daemon";
    private const string SharedKeyFlag = "--shared-key";
    private const string SharedPipeFlag = "--shared-pipe";
    private const string SharedSessionEnv = "LIFEBLOOD_SHARED_SESSION";
    private const string SharedSessionKeyEnv = "LIFEBLOOD_SHARED_SESSION_KEY";
    private const string SharedPipeNameEnv = "LIFEBLOOD_SHARED_PIPE_NAME";
    private const string SharedProxyTraceEnv = "LIFEBLOOD_SHARED_PROXY_TRACE";
    private const string SharedDaemonAutostartEnv = "LIFEBLOOD_SHARED_DAEMON_AUTOSTART";

    public static bool IsSharedProxyRequested(string[] args)
        => args.Any(a => string.Equals(a, SharedFlag, StringComparison.Ordinal))
           || ReadFlag(SharedSessionEnv);

    public static bool IsSharedDaemon(string[] args)
        => args.Length >= 2 && string.Equals(args[0], SharedDaemonFlag, StringComparison.Ordinal);

    public static SharedHostIdentity ResolveDaemonIdentity(string[] args)
    {
        if (args.Length < 2)
        {
            throw new ArgumentException("Missing shared daemon pipe name.");
        }

        return ResolveHostIdentity(args, args[1]);
    }

    public static async Task RunProxyAsync(
        string[] args,
        TextReader input,
        TextWriter output,
        JsonSerializerOptions jsonOpts,
        bool strictJson,
        CancellationToken cancellationToken,
        Action<string>? logError = null)
    {
        var identity = ResolveHostIdentity(args);
        await EnsureDaemonAsync(identity, cancellationToken, logError);
        logError?.Invoke(
            $"Lifeblood MCP shared proxy found daemon pipe '{identity.PipeName}' " +
            $"for workspace '{identity.WorkspaceRoot}'.");

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
                var responseLine = await ForwardFrameAsync(identity, canonicalFrame, expectsResponse, jsonOpts, cancellationToken);
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
                Exception reportedFailure = ex;
                try
                {
                    await EnsureDaemonAsync(identity, cancellationToken, logError);
                }
                catch (Exception recoveryEx) when (recoveryEx is IOException
                    or TimeoutException
                    or InvalidOperationException
                    or System.ComponentModel.Win32Exception
                    or OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    reportedFailure = recoveryEx;
                    logError?.Invoke($"Shared daemon recovery failed: {recoveryEx.Message}");
                }
                if (expectsResponse)
                {
                    WriteResponse(output, BuildProxyError(request.Id, reportedFailure), jsonOpts, logError);
                }
            }
        }
    }

    public static async Task RunDaemonAsync(
        SharedHostIdentity identity,
        JsonSerializerOptions jsonOpts,
        bool strictJson,
        ToolJsonCompatibilityMode jsonCompatibilityMode,
        CancellationToken cancellationToken,
        Action<string>? logError = null)
    {
        var pipeName = identity.PipeName;
        using var mutex = new Mutex(initiallyOwned: true, MutexName(pipeName), out var ownsMutex);
        if (!ownsMutex)
        {
            logError?.Invoke($"Lifeblood MCP shared daemon for '{pipeName}' is already running.");
            return;
        }

        using var host = McpServerHost.Create(jsonCompatibilityMode, identity.WorkspaceRoot);
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
                () => HandleDaemonClientAsync(
                    pipe,
                    identity,
                    host.Dispatcher.Dispatch,
                    jsonOpts,
                    strictJson,
                    cancellationToken,
                    logError),
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
        SharedHostIdentity identity,
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
            if (!await AcceptHandshakeAsync(reader, writer, identity, jsonOpts, cancellationToken, logError))
            {
                return;
            }

            await McpServerLoop.RunAsync(reader, writer, dispatch, jsonOpts, strictJson, cancellationToken, logError);
        }
    }

    private static async Task<string?> ForwardFrameAsync(
        SharedHostIdentity identity,
        string line,
        bool expectsResponse,
        JsonSerializerOptions jsonOpts,
        CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            identity.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        await pipe.ConnectAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
        {
            AutoFlush = true,
        };

        await PerformClientHandshakeAsync(reader, writer, identity, jsonOpts, cancellationToken);
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
        SharedHostIdentity identity,
        CancellationToken cancellationToken,
        Action<string>? logError)
    {
        var pipeName = identity.PipeName;
        if (CanConnect(pipeName, timeoutMs: 100))
        {
            return;
        }

        if (!ReadFlag(SharedDaemonAutostartEnv, defaultValue: true))
        {
            throw new IOException(
                $"Shared Lifeblood daemon '{pipeName}' is unavailable and automatic startup is disabled.");
        }

        StartDaemonProcess(identity, logError);

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

    private static void StartDaemonProcess(SharedHostIdentity identity, Action<string>? logError)
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
        daemonArgs.Add(identity.PipeName);
        daemonArgs.Add(SharedKeyFlag);
        daemonArgs.Add(identity.WorkspaceRoot);

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

    private static SharedHostIdentity ResolveHostIdentity(string[] args, string? daemonPipeName = null)
    {
        var workspaceRoot = ResolveWorkspaceRoot(args);
        var pipeName = daemonPipeName == null
            ? ResolvePipeName(args, workspaceRoot)
            : SanitizePipeName(daemonPipeName);
        var assembly = typeof(SharedMcpTransport).Assembly;
        return new SharedHostIdentity(
            ProtocolVersion: SharedProtocolVersion,
            ServerVersion: ServerIdentity.ResolveServerVersion(),
            BuildIdentity: assembly.ManifestModule.ModuleVersionId.ToString("N"),
            WorkspaceRoot: workspaceRoot,
            PipeName: pipeName);
    }

    private static string ResolvePipeName(string[] args, string workspaceRoot)
    {
        var explicitPipe = ReadArgValue(args, SharedPipeFlag)
            ?? Environment.GetEnvironmentVariable(SharedPipeNameEnv);
        if (!string.IsNullOrWhiteSpace(explicitPipe))
        {
            return SanitizePipeName(explicitPipe);
        }

        var hashKey = OperatingSystem.IsWindows() ? workspaceRoot.ToUpperInvariant() : workspaceRoot;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashKey)))[..24];
        return $"lifeblood-mcp-{hash}";
    }

    private static string ResolveWorkspaceRoot(string[] args)
    {
        var key = ReadArgValue(args, SharedKeyFlag)
            ?? Environment.GetEnvironmentVariable(SharedSessionKeyEnv)
            ?? Directory.GetCurrentDirectory();
        return WorkspacePathIdentity.ResolveWorkspaceRoot(key);
    }

    private static async Task PerformClientHandshakeAsync(
        StreamReader reader,
        StreamWriter writer,
        SharedHostIdentity identity,
        JsonSerializerOptions jsonOpts,
        CancellationToken cancellationToken)
    {
        var request = SharedHandshakeRequest.From(identity);
        await writer.WriteLineAsync(
            JsonSerializer.Serialize(request, jsonOpts).AsMemory(),
            cancellationToken);
        await writer.FlushAsync(cancellationToken);

        var line = await reader.ReadLineAsync(cancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        if (line == null)
        {
            throw new IOException("Shared daemon closed the pipe during identity handshake.");
        }

        SharedHandshakeResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<SharedHandshakeResponse>(line, jsonOpts);
        }
        catch (JsonException ex)
        {
            throw new IOException("Shared daemon returned an invalid identity handshake.", ex);
        }

        if (response == null || !string.Equals(response.Kind, HandshakeKind, StringComparison.Ordinal))
        {
            throw new IOException("Shared daemon did not return a Lifeblood identity handshake.");
        }

        if (!response.Accepted)
        {
            throw new IOException(response.Error ?? "Shared daemon rejected the identity handshake.");
        }

        if (response.ProtocolVersion != identity.ProtocolVersion
            || !string.Equals(response.ServerVersion, identity.ServerVersion, StringComparison.Ordinal)
            || !string.Equals(response.BuildIdentity, identity.BuildIdentity, StringComparison.Ordinal)
            || !WorkspaceRootsEqual(response.WorkspaceRoot, identity.WorkspaceRoot))
        {
            throw new IOException("Shared daemon accepted the handshake with a different identity.");
        }
    }

    private static async Task<bool> AcceptHandshakeAsync(
        StreamReader reader,
        StreamWriter writer,
        SharedHostIdentity identity,
        JsonSerializerOptions jsonOpts,
        CancellationToken cancellationToken,
        Action<string>? logError)
    {
        string? line;
        try
        {
            line = await reader.ReadLineAsync(cancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                logError?.Invoke($"Shared handshake read failed: {ex.Message}");
            }
            return false;
        }

        if (line == null)
        {
            return false;
        }

        SharedHandshakeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<SharedHandshakeRequest>(line, jsonOpts);
        }
        catch (JsonException ex)
        {
            await WriteHandshakeResponseAsync(
                writer,
                SharedHandshakeResponse.Rejected(identity, "Invalid shared transport handshake JSON."),
                jsonOpts,
                cancellationToken);
            logError?.Invoke($"Shared handshake parse failed: {ex.Message}");
            return false;
        }

        var rejection = ValidateHandshake(request, identity);
        var response = rejection == null
            ? SharedHandshakeResponse.AcceptedIdentity(identity)
            : SharedHandshakeResponse.Rejected(identity, rejection);
        await WriteHandshakeResponseAsync(writer, response, jsonOpts, cancellationToken);
        if (rejection != null)
        {
            logError?.Invoke($"Shared handshake rejected: {rejection}");
            return false;
        }

        return true;
    }

    private static string? ValidateHandshake(SharedHandshakeRequest? request, SharedHostIdentity identity)
    {
        if (request == null || !string.Equals(request.Kind, HandshakeKind, StringComparison.Ordinal))
        {
            return "Connection did not begin with a Lifeblood shared transport handshake.";
        }

        if (request.ProtocolVersion != identity.ProtocolVersion)
        {
            return $"Shared transport protocol mismatch: client {request.ProtocolVersion}, daemon {identity.ProtocolVersion}.";
        }

        if (!string.Equals(request.ServerVersion, identity.ServerVersion, StringComparison.Ordinal)
            || !string.Equals(request.BuildIdentity, identity.BuildIdentity, StringComparison.Ordinal))
        {
            return $"Shared daemon build mismatch: client {request.ServerVersion}/{request.BuildIdentity}, " +
                   $"daemon {identity.ServerVersion}/{identity.BuildIdentity}.";
        }

        if (!WorkspaceRootsEqual(request.WorkspaceRoot, identity.WorkspaceRoot))
        {
            return $"Shared daemon workspace mismatch: client '{request.WorkspaceRoot}', " +
                   $"daemon '{identity.WorkspaceRoot}'.";
        }

        return null;
    }

    private static async Task WriteHandshakeResponseAsync(
        StreamWriter writer,
        SharedHandshakeResponse response,
        JsonSerializerOptions jsonOpts,
        CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(
            JsonSerializer.Serialize(response, jsonOpts).AsMemory(),
            cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    private static bool WorkspaceRootsEqual(string? left, string right)
        => WorkspacePathIdentity.Equal(left, right);

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

    private static bool ReadFlag(string environmentVariableName, bool defaultValue = false)
    {
        var raw = Environment.GetEnvironmentVariable(environmentVariableName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" or "shared" => true,
            "0" or "false" or "no" or "off" => false,
            _ => defaultValue,
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

    internal sealed record SharedHostIdentity(
        int ProtocolVersion,
        string ServerVersion,
        string BuildIdentity,
        string WorkspaceRoot,
        string PipeName);

    private sealed record SharedHandshakeRequest(
        string Kind,
        int ProtocolVersion,
        string ServerVersion,
        string BuildIdentity,
        string WorkspaceRoot)
    {
        public static SharedHandshakeRequest From(SharedHostIdentity identity)
            => new(
                HandshakeKind,
                identity.ProtocolVersion,
                identity.ServerVersion,
                identity.BuildIdentity,
                identity.WorkspaceRoot);
    }

    private sealed record SharedHandshakeResponse(
        string Kind,
        bool Accepted,
        int ProtocolVersion,
        string ServerVersion,
        string BuildIdentity,
        string WorkspaceRoot,
        string? Error)
    {
        public static SharedHandshakeResponse AcceptedIdentity(SharedHostIdentity identity)
            => From(identity, accepted: true, error: null);

        public static SharedHandshakeResponse Rejected(SharedHostIdentity identity, string error)
            => From(identity, accepted: false, error);

        private static SharedHandshakeResponse From(
            SharedHostIdentity identity,
            bool accepted,
            string? error)
            => new(
                HandshakeKind,
                accepted,
                identity.ProtocolVersion,
                identity.ServerVersion,
                identity.BuildIdentity,
                identity.WorkspaceRoot,
                error);
    }
}

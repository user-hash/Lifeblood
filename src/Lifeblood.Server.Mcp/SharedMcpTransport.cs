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
    private const string MaintenanceDrainMethod = "lifeblood/shared/drain";
    private const int SharedProtocolVersion = 2;
    private const string SharedFlag = "--shared";
    private const string SharedDaemonFlag = "--shared-daemon";
    private const string SharedKeyFlag = "--shared-key";
    private const string SharedPipeFlag = "--shared-pipe";
    private const string SharedSessionEnv = "LIFEBLOOD_SHARED_SESSION";
    private const string SharedSessionKeyEnv = "LIFEBLOOD_SHARED_SESSION_KEY";
    private const string SharedPipeNameEnv = "LIFEBLOOD_SHARED_PIPE_NAME";
    private const string SharedProxyTraceEnv = "LIFEBLOOD_SHARED_PROXY_TRACE";
    private const string SharedDaemonAutostartEnv = "LIFEBLOOD_SHARED_DAEMON_AUTOSTART";
    private const string SharedIdleSecondsEnv = "LIFEBLOOD_SHARED_IDLE_SECONDS";
    private static readonly string[] SharedCapabilities =
    {
        "persistent-connection",
        "client-lease",
        "request-activity",
        "request-cancellation",
        "idle-drain",
        "status-v1",
    };

    internal static int ProtocolVersion => SharedProtocolVersion;

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
        var clientId = $"client_{Guid.NewGuid():N}";
        SharedProxyConnection? connection = null;
        var queuedLines = new Queue<string>();
        Task<string?>? pendingRead = null;
        var inputClosed = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested && !inputClosed)
            {
                string? line;
                if (queuedLines.Count > 0)
                {
                    line = queuedLines.Dequeue();
                }
                else
                {
                    try
                    {
                        pendingRead ??= input.ReadLineAsync(cancellationToken).AsTask();
                        line = await pendingRead;
                        pendingRead = null;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        logError?.Invoke($"stdin read failed: {ex.Message}");
                        break;
                    }
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
                    if (connection == null)
                    {
                        await EnsureDaemonAsync(identity, cancellationToken, logError);
                        connection = await SharedProxyConnection.ConnectAsync(
                            identity,
                            clientId,
                            jsonOpts,
                            cancellationToken);
                        logError?.Invoke(
                            $"Lifeblood MCP shared proxy connected to daemon pipe '{identity.PipeName}' " +
                            $"for workspace '{identity.WorkspaceRoot}'.");
                    }

                    using var activeForwardCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var responseTask = connection.ForwardAsync(
                        canonicalFrame,
                        expectsResponse,
                        activeForwardCancellation.Token);
                    var activeRequestId = McpServerLoop.RequestIdKey(request.Id);

                    while (expectsResponse
                           && !responseTask.IsCompleted
                           && !cancellationToken.IsCancellationRequested)
                    {
                        pendingRead ??= input.ReadLineAsync(cancellationToken).AsTask();
                        var completed = await Task.WhenAny(responseTask, pendingRead);
                        if (ReferenceEquals(completed, responseTask))
                            break;

                        var concurrentLine = await pendingRead;
                        pendingRead = null;
                        if (concurrentLine == null)
                        {
                            inputClosed = true;
                            activeForwardCancellation.Cancel();
                            break;
                        }
                        if (string.IsNullOrWhiteSpace(concurrentLine))
                            continue;

                        if (TryParseCancellationFor(
                                concurrentLine,
                                activeRequestId,
                                jsonOpts,
                                strictJson,
                                out var cancellationFrame))
                        {
                            TraceProxyFrame(cancellationFrame);
                            await connection.ForwardAsync(
                                cancellationFrame,
                                expectsResponse: false,
                                cancellationToken);
                        }
                        else
                        {
                            queuedLines.Enqueue(concurrentLine);
                        }
                    }

                    var responseLine = await responseTask;
                    if (expectsResponse && !string.IsNullOrWhiteSpace(responseLine))
                    {
                        output.WriteLine(responseLine);
                        output.Flush();
                    }
                }
                catch (Exception ex) when (IsRecoverableTransportFailure(ex))
                {
                    if (connection != null)
                    {
                        await connection.DisposeAsync();
                        connection = null;
                    }
                    if (cancellationToken.IsCancellationRequested || inputClosed) break;
                    logError?.Invoke($"Shared daemon forward failed: {ex.Message}");
                    Exception reportedFailure = ex;
                    try
                    {
                        await EnsureDaemonAsync(identity, cancellationToken, logError);
                    }
                    catch (Exception recoveryEx) when (IsRecoverableTransportFailure(recoveryEx))
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
        finally
        {
            if (connection != null)
                await connection.DisposeAsync();
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

        using var lifecycle = new SharedDaemonLifecycle(ReadIdleTimeout());
        using var daemonLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifecycle.ShutdownToken);
        using var host = McpServerHost.Create(
            jsonCompatibilityMode,
            identity.WorkspaceRoot,
            lifecycle);
        logError?.Invoke($"Lifeblood MCP shared daemon listening on pipe '{pipeName}'.");

        var clientTasks = new List<Task>();
        while (!daemonLifetime.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(daemonLifetime.Token);
            }
            catch (OperationCanceledException) when (daemonLifetime.IsCancellationRequested)
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

            clientTasks.RemoveAll(task => task.IsCompleted);
            clientTasks.Add(Task.Run(async () =>
            {
                try
                {
                    await HandleDaemonClientAsync(
                        pipe,
                        identity,
                        lifecycle,
                        host.Dispatcher.Dispatch,
                        () => host.InFlightAnalysisCount,
                        jsonOpts,
                        strictJson,
                        daemonLifetime.Token,
                        logError);
                }
                catch (OperationCanceledException) when (daemonLifetime.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    logError?.Invoke($"Shared client connection failed: {ex.Message}");
                }
            }));
        }

        try
        {
            await Task.WhenAll(clientTasks);
        }
        catch
        {
            // Client tasks already log per-connection failures.
        }
        finally
        {
            lifecycle.MarkStopped();
        }
    }

    private static async Task HandleDaemonClientAsync(
        NamedPipeServerStream pipe,
        SharedHostIdentity identity,
        SharedDaemonLifecycle lifecycle,
        Func<JsonRpcRequest, CancellationToken, JsonRpcResponse?> dispatch,
        Func<int> inFlightAnalysisCount,
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
            using var clientLease = await AcceptHandshakeAsync(
                reader,
                writer,
                identity,
                lifecycle,
                jsonOpts,
                cancellationToken,
                logError);
            if (clientLease == null)
            {
                return;
            }

            JsonRpcResponse? DispatchWithActivity(
                JsonRpcRequest request,
                CancellationToken requestCancellation)
            {
                using var activity = lifecycle.BeginRequest(clientLease.LeaseId);
                if (string.Equals(request.Method, MaintenanceDrainMethod, StringComparison.Ordinal))
                {
                    if (request.Id == null)
                        return null;

                    var coordinatedDrain = request.Params is { } parameters
                        && parameters.ValueKind == JsonValueKind.Object
                        && parameters.TryGetProperty("coordinatedDrain", out var coordinated)
                        && coordinated.ValueKind == JsonValueKind.True;
                    return new JsonRpcResponse
                    {
                        Id = request.Id,
                        Result = lifecycle.RequestMaintenanceDrain(
                            clientLease.LeaseId,
                            inFlightAnalysisCount(),
                            coordinatedDrain),
                    };
                }

                return dispatch(request, requestCancellation);
            }

            await McpServerLoop.RunAsync(
                reader,
                writer,
                DispatchWithActivity,
                jsonOpts,
                strictJson,
                cancellationToken,
                logError);
        }
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

    private static bool IsRecoverableTransportFailure(Exception exception)
        => exception is IOException
            or TimeoutException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or OperationCanceledException;

    private static bool TryParseCancellationFor(
        string line,
        string? activeRequestId,
        JsonSerializerOptions jsonOpts,
        bool strictJson,
        out string canonicalFrame)
    {
        canonicalFrame = string.Empty;
        if (activeRequestId == null)
            return false;

        try
        {
            var request = McpJsonRequestParser.DeserializeRequest(line, jsonOpts, strictJson);
            if (request == null
                || !McpServerLoop.TryGetCancellationTarget(request, out var targetId)
                || !string.Equals(targetId, activeRequestId, StringComparison.Ordinal))
            {
                return false;
            }

            canonicalFrame = JsonSerializer.Serialize(request, jsonOpts);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

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

    private static async Task<SharedHandshakeResponse> PerformClientHandshakeAsync(
        StreamReader reader,
        StreamWriter writer,
        SharedHostIdentity identity,
        string clientId,
        JsonSerializerOptions jsonOpts,
        CancellationToken cancellationToken)
    {
        var request = SharedHandshakeRequest.From(identity, clientId);
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

        if (string.IsNullOrWhiteSpace(response.DaemonInstanceId)
            || string.IsNullOrWhiteSpace(response.ClientLeaseId)
            || response.Capabilities == null
            || !SharedCapabilities.All(response.Capabilities.Contains))
        {
            throw new IOException("Shared daemon accepted the handshake without the required persistent-lease contract.");
        }

        return response;
    }

    private static async Task<SharedDaemonLifecycle.SharedClientLease?> AcceptHandshakeAsync(
        StreamReader reader,
        StreamWriter writer,
        SharedHostIdentity identity,
        SharedDaemonLifecycle lifecycle,
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
            return null;
        }

        if (line == null)
        {
            return null;
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
                SharedHandshakeResponse.Rejected(
                    identity,
                    lifecycle.CaptureStatus(),
                    "Invalid shared transport handshake JSON."),
                jsonOpts,
                cancellationToken);
            logError?.Invoke($"Shared handshake parse failed: {ex.Message}");
            return null;
        }

        var rejection = ValidateHandshake(request, identity);
        SharedDaemonLifecycle.SharedClientLease? lease = null;
        if (rejection == null)
        {
            try
            {
                lease = lifecycle.AcquireClient(request!.ClientId, request.Capabilities);
            }
            catch (InvalidOperationException ex)
            {
                rejection = ex.Message;
            }
        }

        var response = rejection == null
            ? SharedHandshakeResponse.AcceptedIdentity(
                identity,
                lifecycle.CaptureStatus(),
                lease!.LeaseId)
            : SharedHandshakeResponse.Rejected(identity, lifecycle.CaptureStatus(), rejection);
        try
        {
            await WriteHandshakeResponseAsync(writer, response, jsonOpts, cancellationToken);
        }
        catch
        {
            lease?.Dispose();
            throw;
        }
        if (rejection != null)
        {
            lease?.Dispose();
            logError?.Invoke($"Shared handshake rejected: {rejection}");
            return null;
        }

        return lease;
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

        if (string.IsNullOrWhiteSpace(request.ClientId))
        {
            return "Shared client identity is required for a persistent lease.";
        }

        if (request.Capabilities == null
            || !SharedCapabilities.All(request.Capabilities.Contains))
        {
            return "Shared client does not advertise the persistent lease/activity/status contract required by this protocol.";
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

    private static TimeSpan? ReadIdleTimeout()
        => ParseIdleTimeout(Environment.GetEnvironmentVariable(SharedIdleSecondsEnv));

    internal static TimeSpan? ParseIdleTimeout(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (double.TryParse(
                raw,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var seconds)
            && double.IsFinite(seconds)
            && seconds >= 0)
        {
            try
            {
                return TimeSpan.FromSeconds(seconds);
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        return null;
    }

    private sealed class SharedProxyConnection : IAsyncDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private int _disposed;

        private SharedProxyConnection(
            NamedPipeClientStream pipe,
            StreamReader reader,
            StreamWriter writer,
            string clientLeaseId)
        {
            _pipe = pipe;
            _reader = reader;
            _writer = writer;
            ClientLeaseId = clientLeaseId;
        }

        public string ClientLeaseId { get; }

        public static async Task<SharedProxyConnection> ConnectAsync(
            SharedHostIdentity identity,
            string clientId,
            JsonSerializerOptions jsonOpts,
            CancellationToken cancellationToken)
        {
            var pipe = new NamedPipeClientStream(
                ".",
                identity.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            StreamReader? reader = null;
            StreamWriter? writer = null;
            try
            {
                await pipe.ConnectAsync(cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                reader = new StreamReader(
                    pipe,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false,
                    leaveOpen: true);
                writer = new StreamWriter(
                    pipe,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    leaveOpen: true)
                {
                    AutoFlush = true,
                };

                var handshake = await PerformClientHandshakeAsync(
                    reader,
                    writer,
                    identity,
                    clientId,
                    jsonOpts,
                    cancellationToken);
                return new SharedProxyConnection(
                    pipe,
                    reader,
                    writer,
                    handshake.ClientLeaseId!);
            }
            catch
            {
                if (writer != null)
                {
                    try { await writer.DisposeAsync(); }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                }
                try { reader?.Dispose(); }
                catch (ObjectDisposedException) { }
                try { await pipe.DisposeAsync(); }
                catch (ObjectDisposedException) { }
                throw;
            }
        }

        public async Task<string?> ForwardAsync(
            string line,
            bool expectsResponse,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                await _writer.WriteLineAsync(line.AsMemory(), cancellationToken);
                await _writer.FlushAsync(cancellationToken);
            }
            finally
            {
                _writeGate.Release();
            }

            if (!expectsResponse)
                return null;

            var response = await _reader.ReadLineAsync(cancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromMinutes(10), cancellationToken);
            return response
                ?? throw new IOException("Shared daemon closed the persistent pipe without a response.");
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try { await _writer.DisposeAsync(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            try { _reader.Dispose(); }
            catch (ObjectDisposedException) { }
            try { await _pipe.DisposeAsync(); }
            catch (ObjectDisposedException) { }
            _writeGate.Dispose();
        }
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
        string WorkspaceRoot,
        string ClientId,
        string[] Capabilities)
    {
        public static SharedHandshakeRequest From(
            SharedHostIdentity identity,
            string clientId)
            => new(
                HandshakeKind,
                identity.ProtocolVersion,
                identity.ServerVersion,
                identity.BuildIdentity,
                identity.WorkspaceRoot,
                clientId,
                SharedCapabilities);
    }

    private sealed record SharedHandshakeResponse(
        string Kind,
        bool Accepted,
        int ProtocolVersion,
        string ServerVersion,
        string BuildIdentity,
        string WorkspaceRoot,
        string DaemonInstanceId,
        string? ClientLeaseId,
        int ProcessId,
        DateTimeOffset StartedAtUtc,
        bool IdleEvictionEnabled,
        double? IdleTimeoutSeconds,
        string[] Capabilities,
        string? Error)
    {
        public static SharedHandshakeResponse AcceptedIdentity(
            SharedHostIdentity identity,
            SharedDaemonStatusSnapshot status,
            string clientLeaseId)
            => From(identity, status, accepted: true, clientLeaseId, error: null);

        public static SharedHandshakeResponse Rejected(
            SharedHostIdentity identity,
            SharedDaemonStatusSnapshot status,
            string error)
            => From(identity, status, accepted: false, clientLeaseId: null, error);

        private static SharedHandshakeResponse From(
            SharedHostIdentity identity,
            SharedDaemonStatusSnapshot status,
            bool accepted,
            string? clientLeaseId,
            string? error)
            => new(
                HandshakeKind,
                accepted,
                identity.ProtocolVersion,
                identity.ServerVersion,
                identity.BuildIdentity,
                identity.WorkspaceRoot,
                status.DaemonInstanceId,
                clientLeaseId,
                Environment.ProcessId,
                status.StartedAtUtc,
                status.IdleTimeout.HasValue,
                status.IdleTimeout?.TotalSeconds,
                SharedCapabilities,
                error);
    }
}

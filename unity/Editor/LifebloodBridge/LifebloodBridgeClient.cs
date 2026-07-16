using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using MCPForUnity.Editor.Helpers;
using Debug = UnityEngine.Debug;

namespace Lifeblood.UnityBridge
{
    /// <summary>
    /// Manages Unity's proxy connection to the workspace-shared Lifeblood MCP
    /// daemon. The short-lived proxy communicates via JSON-RPC 2.0 over
    /// stdin/stdout; the daemon owns the one retained semantic base shared by
    /// Unity and every agent configured with the same workspace key.
    ///
    /// Architecture: this is a pure outer adapter. It translates
    /// JObject tool calls into JSON-RPC requests and deserializes responses.
    /// No Lifeblood domain types leak into Unity.
    /// </summary>
    public sealed class LifebloodBridgeClient : IDisposable
    {
        private static LifebloodBridgeClient _instance;
        public static LifebloodBridgeClient Instance => _instance ??= new LifebloodBridgeClient();

        private Process _process;
        private StreamWriter _stdin;
        private StreamReader _stdout;
        private int _nextId = 1;
        private bool _initialized;
        private readonly object _lock = new();
        private readonly PendingToolCallRegistry<JObject> _pendingCalls = new();

        /// <summary>
        /// Timeout for individual tool calls. Five minutes leaves margin for a
        /// cold analysis of a large Unity workspace while keeping pipe failure
        /// finite and observable.
        /// </summary>
        private const int ToolCallTimeoutMs = 300_000;

        /// <summary>Timeout for the MCP initialize handshake.</summary>
        private const int InitTimeoutMs = 15_000;

        /// <summary>Whether the Lifeblood server has been started and initialized.</summary>
        public bool IsConnected => _process is { HasExited: false } && _initialized;

        /// <summary>
        /// Resolve the installed Lifeblood tool used by every client. Preferring
        /// the global-tool shim over a repository DLL prevents Debug/Release MVID
        /// drift from splitting Unity and agent proxies across incompatible hosts.
        /// </summary>
        public static string ServerCommand
        {
            get
            {
                var custom = EditorPrefs.GetString("Lifeblood_McpCommand", "");
                if (!string.IsNullOrWhiteSpace(custom))
                    return custom;

                var environmentCommand = Environment.GetEnvironmentVariable("LIFEBLOOD_MCP_COMMAND");
                if (!string.IsNullOrWhiteSpace(environmentCommand))
                    return environmentCommand;

                var executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? "lifeblood-mcp.exe"
                    : "lifeblood-mcp";
                var globalTool = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".dotnet",
                    "tools",
                    executableName);
                return File.Exists(globalTool) ? globalTool : executableName;
            }
        }

        /// <summary>
        /// Call a Lifeblood MCP tool. Starts the server if not running.
        /// Returns the tool result content, or an error object.
        /// Thread-safe via lock — Unity MCP dispatches on main thread anyway.
        /// </summary>
        public JObject CallTool(string toolName, JObject arguments = null)
        {
            lock (_lock)
            {
                try
                {
                    EnsureStarted();

                    var id = _nextId++;
                    var request = new JObject
                    {
                        ["jsonrpc"] = McpProtocolConstants.JsonRpcVersion,
                        ["id"] = id,
                        ["method"] = McpProtocolConstants.MethodToolsCall,
                        ["params"] = new JObject
                        {
                            ["name"] = toolName,
                            ["arguments"] = arguments ?? new JObject()
                        }
                    };

                    _stdin.WriteLine(request.ToString(Formatting.None));
                    _stdin.Flush();

                    // Read response with timeout — prevents hanging forever if server dies
                    var line = ReadLineWithTimeout(_stdout, ToolCallTimeoutMs);
                    if (line == null)
                    {
                        Kill();
                        return ErrorResult("Lifeblood server closed or timed out");
                    }

                    var response = JObject.Parse(line);

                    // Check for JSON-RPC error
                    if (response["error"] != null)
                        return ErrorResult(response["error"]["message"]?.ToString() ?? "Unknown error");

                    return response["result"] as JObject ?? new JObject();
                }
                catch (Exception ex)
                {
                    Kill();
                    return ErrorResult($"Bridge error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Starts a sidecar call on a worker and returns a polling receipt before
        /// Unity MCP's short synchronous gateway deadline. A later
        /// <c>action:"status"</c> call retrieves the same task-owned terminal
        /// result. One coordinator owns this lifecycle for every bridge tool, so
        /// analyze, diagnostics, reference search, and future expensive calls do
        /// not each invent timeout or result-publication state.
        /// </summary>
        public object CallToolWithPolling(string toolName, JObject arguments = null)
        {
            var forwarded = arguments == null
                ? new JObject()
                : (JObject)arguments.DeepClone();
            forwarded.Remove("action");
            if (string.Equals(arguments?["action"]?.ToString(), "status", StringComparison.OrdinalIgnoreCase))
                return PollToolCall(toolName);

            try
            {
                // ServerCommand reads Unity Editor state. Complete process start
                // and the MCP handshake on the main thread before Task.Run; the
                // worker then performs only process/stream I/O.
                lock (_lock)
                    EnsureStarted();
            }
            catch (Exception ex)
            {
                return new ErrorResponse($"Lifeblood bridge start failed: {ex.Message}");
            }

            var requestIdentity = forwarded.ToString(Formatting.None);
            var admission = _pendingCalls.Admit(
                toolName,
                requestIdentity,
                () => Task.Run(() => CallTool(toolName, forwarded)),
                out _);
            if (admission == PendingCallAdmission.ConflictingArguments)
            {
                return new ErrorResponse(
                    $"A Lifeblood call for '{toolName}' is already awaiting status. " +
                    "Poll it before starting the same tool with different arguments.");
            }

            return Pending(toolName);
        }

        private object PollToolCall(string toolName)
        {
            var state = _pendingCalls.Poll(toolName, out var pending);
            if (state == PendingCallPollState.Missing)
                return new ErrorResponse($"No pending Lifeblood call for '{toolName}'.");
            if (state == PendingCallPollState.Pending)
                return Pending(toolName);

            try
            {
                return pending.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                return new ErrorResponse($"Lifeblood call '{toolName}' failed: {ex.Message}");
            }
        }

        private static PendingResponse Pending(string toolName)
            => new PendingResponse(
                $"Lifeblood call '{toolName}' is in progress.",
                pollIntervalSeconds: 0.25);

        /// <summary>
        /// Polling form used by the Unity custom-tool surface. The bridge owns
        /// the current Unity project path; callers may still select retained vs
        /// read-only analysis, incremental fallback policy, and define profiles.
        /// </summary>
        public object AnalyzeCurrentProjectWithPolling(JObject arguments = null)
        {
            var args = arguments == null
                ? new JObject()
                : (JObject)arguments.DeepClone();
            // Admission and status must derive the same call identity. The
            // bridge-owned project root therefore participates in every poll,
            // not only the initial request.
            args["projectPath"] = Path.GetDirectoryName(Application.dataPath);
            return CallToolWithPolling("lifeblood_analyze", args);
        }

        private void EnsureStarted()
        {
            if (_process is { HasExited: false } && _initialized)
                return;

            Kill(); // Clean up any dead process

            var unityRoot = Path.GetDirectoryName(Application.dataPath);
            if (string.IsNullOrEmpty(unityRoot))
                throw new InvalidOperationException("Unity project root could not be resolved.");

            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ServerCommand,
                    Arguments = $"--shared --shared-key {QuoteArgument(unityRoot)}",
                    WorkingDirectory = unityRoot,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };

            _process.Start();
            _stdin = _process.StandardInput;
            _stdout = new StreamReader(_process.StandardOutput.BaseStream);

            // Drain stderr asynchronously (diagnostics, don't block)
            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Debug.Log($"[Lifeblood] {e.Data}");
            };
            _process.BeginErrorReadLine();

            // MCP handshake: initialize. Params carry protocolVersion,
            // capabilities, and clientInfo per MCP spec 2024-11-05.
            // INV-MCP-003: every wire constant comes from McpProtocolConstants,
            // mirrored from Lifeblood.Connectors.Mcp.McpProtocolSpec.
            var initRequest = new JObject
            {
                ["jsonrpc"] = McpProtocolConstants.JsonRpcVersion,
                ["id"] = _nextId++,
                ["method"] = McpProtocolConstants.MethodInitialize,
                ["params"] = new JObject
                {
                    ["protocolVersion"] = McpProtocolConstants.SupportedVersion,
                    ["capabilities"] = new JObject(),
                    ["clientInfo"] = new JObject
                    {
                        ["name"] = McpProtocolConstants.ClientInfoName,
                        ["version"] = McpProtocolConstants.ClientInfoVersion,
                    },
                },
            };

            _stdin.WriteLine(initRequest.ToString(Formatting.None));
            _stdin.Flush();

            var initResponse = ReadLineWithTimeout(_stdout, InitTimeoutMs);
            if (initResponse == null)
                throw new InvalidOperationException("Lifeblood server failed to respond to initialize (timed out after 15s)");

            // Send canonical initialized notification (no id = notification).
            // The notification method name is sourced from
            // McpProtocolConstants.NotificationInitialized (mirrored from
            // Lifeblood.Connectors.Mcp.McpProtocolSpec.Notifications.Initialized).
            // The legacy bare-initialized alias is deprecated and must not
            // be sent by first-party clients — the source-of-truth ratchet
            // test enforces this on CI.
            _stdin.WriteLine(new JObject
            {
                ["jsonrpc"] = McpProtocolConstants.JsonRpcVersion,
                ["method"] = McpProtocolConstants.NotificationInitialized,
            }.ToString(Formatting.None));
            _stdin.Flush();

            _initialized = true;

            Debug.Log("[Lifeblood] Bridge connected to shared MCP session");
        }

        private void Kill()
        {
            _initialized = false;

            try
            {
                if (_process is { HasExited: false })
                {
                    _stdin?.Close();
                    if (!_process.WaitForExit(2000))
                        _process.Kill();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Best effort cleanup — process may already be gone
            }

            _process?.Dispose();
            _process = null;
            _stdin = null;
            _stdout = null;
        }

        /// <summary>
        /// Read a line from the server with timeout and process health monitoring.
        /// Returns null if the server process exits, the stream closes, or the timeout
        /// expires. On timeout, kills the server to unblock the pipe reader.
        /// </summary>
        private string ReadLineWithTimeout(StreamReader reader, int timeoutMs)
        {
            var readTask = System.Threading.Tasks.Task.Run(() =>
            {
                try { return reader.ReadLine(); }
                catch (ObjectDisposedException) { return null; }
                catch (IOException) { return null; }
            });

            // Poll: either the read completes, the process dies, or we time out.
            // Polling at 100ms is fine — this is editor code, not audio thread.
            var deadline = System.Environment.TickCount + timeoutMs;
            while (System.Environment.TickCount < deadline)
            {
                if (readTask.IsCompleted)
                    return readTask.Result;

                // Early exit: process died while we were waiting
                if (_process == null || _process.HasExited)
                    return null;

                System.Threading.Thread.Sleep(100);
            }

            // Timeout — kill the server to unblock the pipe
            Debug.LogWarning($"[Lifeblood] Response timed out after {timeoutMs / 1000}s — killing server");
            Kill();
            return null;
        }

        private static JObject ErrorResult(string message)
        {
            return new JObject
            {
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = message
                    }
                },
                ["isError"] = true
            };
        }

        private static string QuoteArgument(string value)
            => $"\"{value.Replace("\"", "\\\"")}\"";

        public void Dispose() => Kill();

        // Clean up on domain reload (Unity recompilation)
        [InitializeOnLoadMethod]
        static void RegisterCleanup()
        {
            AssemblyReloadEvents.beforeAssemblyReload += () => _instance?.Dispose();
            EditorApplication.quitting += () => _instance?.Dispose();
        }
    }
}

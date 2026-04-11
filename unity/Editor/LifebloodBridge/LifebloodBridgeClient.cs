using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Lifeblood.UnityBridge
{
    /// <summary>
    /// Manages a Lifeblood MCP server as a child process.
    /// Communicates via JSON-RPC 2.0 over stdin/stdout.
    /// Singleton — one server per Unity Editor session.
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
        private bool _analyzed;
        private readonly object _lock = new();

        /// <summary>
        /// Timeout for individual tool calls. Cold analysis on a 75-module Unity
        /// workspace runs around 90 seconds, so 5 minutes covers large projects
        /// with margin.
        /// </summary>
        private const int ToolCallTimeoutMs = 300_000;

        /// <summary>Timeout for the MCP initialize handshake.</summary>
        private const int InitTimeoutMs = 15_000;

        /// <summary>Whether the Lifeblood server has been started and initialized.</summary>
        public bool IsConnected => _process is { HasExited: false } && _initialized;

        /// <summary>Whether a project has been analyzed (semantic state loaded).</summary>
        public bool IsAnalyzed => IsConnected && _analyzed;

        /// <summary>
        /// Path to the Lifeblood MCP server DLL. Resolved once at startup.
        /// Uses EditorPrefs for override, falls back to sibling directory convention.
        /// </summary>
        public static string ServerPath
        {
            get
            {
                var custom = EditorPrefs.GetString("Lifeblood_ServerPath", "");
                if (!string.IsNullOrEmpty(custom) && File.Exists(custom))
                    return custom;

                // Convention: Lifeblood repo is a sibling of the Unity project
                var unityRoot = Path.GetDirectoryName(Application.dataPath);
                var siblingDll = Path.GetFullPath(Path.Combine(
                    unityRoot, "..", "Lifeblood",
                    "src", "Lifeblood.Server.Mcp", "bin", "Debug", "net8.0",
                    "Lifeblood.Server.Mcp.dll"));

                if (File.Exists(siblingDll)) return siblingDll;

                // Fallback: try environment variable
                var envPath = Environment.GetEnvironmentVariable("LIFEBLOOD_SERVER_DLL");
                if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
                    return envPath;

                return null;
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

                    // Track analyze state
                    if (toolName == "lifeblood_analyze" && response["result"] != null)
                    {
                        var content = response["result"]?["content"];
                        if (content is JArray arr && arr.Count > 0)
                        {
                            var text = arr[0]?["text"]?.ToString() ?? "";
                            _analyzed = text.StartsWith("Loaded:") || text.StartsWith("Incremental:");
                        }
                    }

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
        /// Analyze the current Unity project. Must be called before query tools.
        /// Pass incremental=true after the first analysis for fast re-analyze.
        /// </summary>
        public JObject AnalyzeCurrentProject(bool incremental = false)
        {
            var unityRoot = Path.GetDirectoryName(Application.dataPath);
            var args = new JObject
            {
                ["projectPath"] = unityRoot
            };
            if (incremental)
                args["incremental"] = true;
            return CallTool("lifeblood_analyze", args);
        }

        private void EnsureStarted()
        {
            if (_process is { HasExited: false } && _initialized)
                return;

            Kill(); // Clean up any dead process

            var dllPath = ServerPath;
            if (dllPath == null)
                throw new InvalidOperationException(
                    "Lifeblood server not found. Set EditorPrefs 'Lifeblood_ServerPath' " +
                    "or place the Lifeblood repo as a sibling directory.");

            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"\"{dllPath}\"",
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
            _analyzed = false;

            Debug.Log("[Lifeblood] Bridge connected to MCP server");
        }

        private void Kill()
        {
            _initialized = false;
            _analyzed = false;

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

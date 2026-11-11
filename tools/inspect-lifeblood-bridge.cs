using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using Lifeblood.UnityBridge;
using UnityEditor;
using UnityEngine;

namespace Lifeblood.Tooling
{
    public static class BridgeObservation
    {
        public static object Read()
        {
            var type = typeof(LifebloodBridgeClient);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var client = type.GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            object proxy = new { status = "uninitialized" };
            if (client != null)
            {
                var gate = type.GetField("_lock", flags).GetValue(client);
                if (Monitor.TryEnter(gate))
                {
                    try
                    {
                        var process = (Process)type.GetField("_process", flags).GetValue(client);
                        if (process != null && !process.HasExited)
                            proxy = new { status = "observed", processId = process.Id,
                                startedCommand = process.StartInfo.FileName,
                                runtimeRoot = process.StartInfo.EnvironmentVariables["DOTNET_ROOT_X64"],
                                initialized = (bool)type.GetField("_initialized", flags).GetValue(client) };
                        else proxy = new { status = "notRunning" };
                    }
                    catch (InvalidOperationException) { proxy = new { status = "unavailable" }; }
                    finally { Monitor.Exit(gate); }
                }
                else proxy = new { status = "busy", reason = "The bridge is serving a call; no wait or reconnect was attempted." };
            }
            return new { schemaVersion = 1, observedAtUtc = DateTime.UtcNow.ToString("o"),
                projectRoot = Path.GetDirectoryName(Application.dataPath),
                configuredCommand = LifebloodBridgeClient.ServerCommand,
                configuredRuntimeRoot = EditorPrefs.GetString("Lifeblood_DotnetRootX64", ""),
                loadedBridge = new { assemblyPath = type.Assembly.Location,
                    moduleVersionId = type.Assembly.ManifestModule.ModuleVersionId },
                proxy,
                limitations = new[] { "Point observation only; no proxy start, tool call, refresh, analysis, preference or source write was requested.",
                    "The bridge does not retain serverInfo from its handshake; its initialized flag is not a new daemon identity or liveness check." } };
        }
    }
}

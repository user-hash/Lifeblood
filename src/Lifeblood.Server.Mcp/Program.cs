using System.Text.Json;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// MCP server entry point. Owns process-level transport selection only:
/// classic stdio, shared stdio proxy, or shared named-pipe daemon. The
/// semantic session and tool graph live in <see cref="McpServerHost"/> so
/// every transport gets identical Lifeblood behavior.
/// </summary>
class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    static async Task Main(string[] args)
    {
        // MCP / JSON-RPC over stdio mandates UTF-8 (per the protocol spec);
        // pin stdin and stdout explicitly so the host process codepage does
        // not silently mangle multi-byte characters in JSON args or responses.
        Console.InputEncoding = System.Text.Encoding.UTF8;
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var jsonCompatibilityMode = ToolJsonCompatibilityModeReader.ReadFromEnvironment(
            "LIFEBLOOD_JSON_COMPAT",
            "LIFEBLOOD_STRICT_JSON");
        var strictJson = jsonCompatibilityMode == ToolJsonCompatibilityMode.Strict;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        };

        if (SharedMcpTransport.IsSharedDaemon(args))
        {
            // The shared daemon outlives the stdio proxy that spawned it. Do
            // not keep the proxy client's stdout/stderr handles open, and do
            // not let progress logging fill an unread redirected pipe.
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            await SharedMcpTransport.RunDaemonAsync(
                SharedMcpTransport.ResolveDaemonIdentity(args),
                JsonOpts,
                strictJson,
                jsonCompatibilityMode,
                cts.Token,
                logError: null);
            return;
        }

        if (SharedMcpTransport.IsSharedProxyRequested(args))
        {
            using var proxyReader = new StreamReader(Console.OpenStandardInput());
            await SharedMcpTransport.RunProxyAsync(
                args,
                proxyReader,
                Console.Out,
                JsonOpts,
                strictJson,
                cts.Token,
                logError: msg => Console.Error.WriteLine(msg));
            return;
        }

        using var host = McpServerHost.Create(jsonCompatibilityMode);

        Console.Error.WriteLine("Lifeblood MCP server starting...");

        using var reader = new StreamReader(Console.OpenStandardInput());
        await McpServerLoop.RunAsync(
            reader,
            Console.Out,
            host.Dispatcher.Dispatch,
            JsonOpts,
            strictJson,
            cts.Token,
            logError: msg => Console.Error.WriteLine(msg));

        Console.Error.WriteLine("Lifeblood MCP server stopped.");
    }
}

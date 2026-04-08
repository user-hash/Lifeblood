using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Analysis;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// MCP server entry point. Reads JSON-RPC from stdin, writes to stdout.
/// Diagnostics go to stderr (never pollute the protocol stream).
/// </summary>
class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    static async Task Main()
    {
        IFileSystem fs = new PhysicalFileSystem();
        var session = new GraphSession(fs);
        IBlastRadiusProvider blastRadius = new BlastRadiusBridge();
        var handler = new ToolHandler(session, blastRadius);

        // Graceful shutdown on Ctrl+C or SIGTERM (container/process manager signals)
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

        Console.Error.WriteLine("Lifeblood MCP server starting...");

        using var reader = new StreamReader(Console.OpenStandardInput());

        while (!cts.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync();
            if (line == null) break; // stdin closed
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var request = JsonSerializer.Deserialize<JsonRpcRequest>(line, JsonOpts);
                if (request == null) continue;

                var response = Dispatch(request, handler, session);
                if (response == null) continue; // Notifications get no response
                var json = JsonSerializer.Serialize(response, JsonOpts);
                Console.WriteLine(json);
                Console.Out.Flush();
            }
            catch (System.Text.Json.JsonException ex)
            {
                // JSON-RPC 2.0: parse error → respond with -32700, id: null
                Console.Error.WriteLine($"Parse error: {ex.Message}");
                var errorResponse = JsonSerializer.Serialize(new JsonRpcResponse
                {
                    Error = new JsonRpcError { Code = -32700, Message = "Parse error" },
                }, JsonOpts);
                Console.WriteLine(errorResponse);
                Console.Out.Flush();
            }
            catch (Exception ex)
            {
                // JSON-RPC 2.0: internal error → respond with -32603
                Console.Error.WriteLine($"Internal error: {ex.Message}");
                var internalError = JsonSerializer.Serialize(new JsonRpcResponse
                {
                    Error = new JsonRpcError { Code = -32603, Message = $"Internal error: {ex.Message}" },
                }, JsonOpts);
                Console.WriteLine(internalError);
                Console.Out.Flush();
            }
        }

        // Clean up write-side resources (AdhocWorkspace, compilations)
        session.Dispose();
        Console.Error.WriteLine("Lifeblood MCP server stopped.");
    }

    static JsonRpcResponse? Dispatch(JsonRpcRequest request, ToolHandler handler, GraphSession session)
    {
        var response = new JsonRpcResponse { Id = request.Id };

        switch (request.Method)
        {
            case "initialize":
                response.Result = new McpInitializeResult
                {
                    ServerInfo = new McpServerInfo
                    {
                        Name = "lifeblood",
                        Version = "1.0.0",
                    },
                };
                break;

            case "initialized":
                // Notification (no ID) gets no response per JSON-RPC 2.0
                if (request.Id == null) return null;
                response.Result = new { };
                break;

            case "tools/list":
                response.Result = new { tools = ToolRegistry.GetTools(session.HasCompilationState) };
                break;

            case "tools/call":
                if (!request.Params.HasValue)
                {
                    response.Error = new JsonRpcError { Code = -32602, Message = "params is required for tools/call" };
                    break;
                }
                var callParams = request.Params.Value;

                if (!callParams.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                {
                    response.Error = new JsonRpcError { Code = -32602, Message = "name (string) is required in tools/call params" };
                    break;
                }

                var toolName = nameEl.GetString() ?? "";
                JsonElement? arguments = null;
                if (callParams.TryGetProperty("arguments", out var argsEl))
                    arguments = argsEl;

                response.Result = handler.Handle(toolName, arguments);
                break;

            case "ping":
                response.Result = new { };
                break;

            default:
                response.Error = new JsonRpcError
                {
                    Code = -32601,
                    Message = $"Method not found: {request.Method}",
                };
                break;
        }

        return response;
    }

    /// <summary>
    /// Composition root bridge: delegates to Analysis.BlastRadiusAnalyzer
    /// without letting Connectors.Mcp depend on Analysis directly.
    /// </summary>
    private sealed class BlastRadiusBridge : IBlastRadiusProvider
    {
        public BlastRadiusResult Analyze(SemanticGraph graph, string targetSymbolId, int maxDepth = 10)
            => Analysis.BlastRadiusAnalyzer.Analyze(graph, targetSymbolId, maxDepth);
    }
}

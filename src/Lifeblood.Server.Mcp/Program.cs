using System.Text.Json;

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
        var handler = new ToolHandler();

        Console.Error.WriteLine("Lifeblood MCP server starting...");

        using var reader = new StreamReader(Console.OpenStandardInput());

        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line == null) break; // stdin closed
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var request = JsonSerializer.Deserialize<JsonRpcRequest>(line, JsonOpts);
                if (request == null) continue;

                var response = Dispatch(request, handler);
                var json = JsonSerializer.Serialize(response, JsonOpts);
                Console.WriteLine(json);
                Console.Out.Flush();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error processing message: {ex.Message}");
            }
        }

        Console.Error.WriteLine("Lifeblood MCP server stopped.");
    }

    static JsonRpcResponse Dispatch(JsonRpcRequest request, ToolHandler handler)
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
                // Notification, no response needed. But send empty result if ID present.
                if (request.Id != null)
                    response.Result = new { };
                else
                    return null!; // notifications don't get responses
                break;

            case "tools/list":
                response.Result = new { tools = ToolRegistry.GetTools() };
                break;

            case "tools/call":
                var toolName = "";
                JsonElement? arguments = null;

                if (request.Params.HasValue)
                {
                    var p = request.Params.Value;
                    if (p.TryGetProperty("name", out var nameEl))
                        toolName = nameEl.GetString() ?? "";
                    if (p.TryGetProperty("arguments", out var argsEl))
                        arguments = argsEl;
                }

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
}

using System.Text.Json;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// The MCP stdio read-dispatch-write loop, extracted from
/// <see cref="Program"/> so it is testable through its public API (the same
/// no-visibility-tricks ethos as <see cref="McpDispatcher"/>) and so its
/// resilience contract can be ratcheted.
///
/// Resilience contract (INV-MCP-TRANSPORT-RESILIENCE-001): the loop is
/// single-flight by construction — it reads one line, dispatches it
/// synchronously, then writes one response before reading the next, so an MCP
/// client's parallel tool batch is serialized server-side and no two
/// compilation requests ever run concurrently. NO single request — a dispatch
/// fault, a response that fails to serialize, or a write to a broken output
/// pipe — may terminate the loop and close the transport for every other
/// pending and future call. Faults are logged and turned into id-correlated
/// JSON-RPC error responses (so the client can match the failure to its
/// request instead of seeing an opaque transport drop); a genuinely-closed
/// stdin still ends the loop cleanly via the null-line sentinel.
/// </summary>
public static class McpServerLoop
{
    public static async Task RunAsync(
        TextReader input,
        TextWriter output,
        Func<JsonRpcRequest, JsonRpcResponse?> dispatch,
        JsonSerializerOptions jsonOpts,
        bool strictJson,
        CancellationToken cancellationToken,
        Action<string>? logError = null)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await input.ReadLineAsync();
            }
            catch (Exception ex)
            {
                // A read fault on a half-open pipe is terminal for the session,
                // but it must end the loop cleanly — never bubble out of Main.
                logError?.Invoke($"stdin read failed: {ex.Message}");
                break;
            }

            if (line == null) break;             // stdin closed — clean shutdown
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonRpcResponse? response;
            try
            {
                var request = McpJsonRequestParser.DeserializeRequest(line, jsonOpts, strictJson);
                if (request == null) continue;

                try
                {
                    response = dispatch(request);
                }
                catch (Exception ex)
                {
                    // McpDispatcher.Dispatch is contracted not to throw; this is
                    // the backstop that keeps one bad request from killing the
                    // transport, and echoes the request id so the client can
                    // correlate the failure. INV-MCP-TRANSPORT-RESILIENCE-001.
                    logError?.Invoke($"Dispatch fault on '{request.Method}': {ex.Message}");
                    response = BuildInternalError(request.Id, request.Method, ex);
                }

                if (response == null) continue;  // notification — no response body
            }
            catch (JsonException ex)
            {
                // Parse failed before we had a request — id is genuinely unknown.
                logError?.Invoke($"Parse error: {ex.Message}");
                response = BuildParseError();
            }

            // Write failures (broken pipe, serialization fault) are logged and
            // swallowed: the loop continues, and a truly-closed stdin ends it
            // via the null-line sentinel above. They never escape to terminate
            // the process mid-session.
            TryWriteResponse(output, response, jsonOpts, logError);
        }
    }

    /// <summary>
    /// Structured internal-error response with the request id echoed and a
    /// recovery-posture envelope in JSON-RPC <c>data</c>.
    /// </summary>
    internal static JsonRpcResponse BuildInternalError(JsonElement? id, string method, Exception ex) => new()
    {
        Id = id,
        Error = new JsonRpcError
        {
            Code = -32603,
            Message = $"Internal error handling '{method}': {ex.Message}",
            Data = new
            {
                phase = "dispatch",
                method,
                exceptionType = ex.GetType().FullName ?? ex.GetType().Name,
                recoverable = true,
                recovery = "The server is single-flight (serial stdio loop); a fault on one call does not " +
                           "affect the transport. Retry this call; if it persists, re-run lifeblood_analyze, " +
                           "then reconnect the MCP server.",
            },
        },
    };

    /// <summary>Parse-error response. Id is unknown (parse failed) so it stays null per JSON-RPC 2.0.</summary>
    internal static JsonRpcResponse BuildParseError() => new()
    {
        Error = new JsonRpcError
        {
            Code = -32700,
            Message = "Parse error",
            Data = new { phase = "parse", recoverable = true },
        },
    };

    private static void TryWriteResponse(
        TextWriter output,
        JsonRpcResponse response,
        JsonSerializerOptions jsonOpts,
        Action<string>? logError)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(response, jsonOpts);
        }
        catch (Exception ex)
        {
            // A response that cannot serialize must still produce an
            // id-correlated error rather than silence (which reads as a
            // transport drop to a client awaiting that id).
            logError?.Invoke($"Response serialize failed: {ex.Message}");
            try
            {
                json = JsonSerializer.Serialize(
                    new JsonRpcResponse
                    {
                        Id = response.Id,
                        Error = new JsonRpcError { Code = -32603, Message = "Response serialization failed" },
                    },
                    jsonOpts);
            }
            catch
            {
                return;
            }
        }

        try
        {
            output.WriteLine(json);
            output.Flush();
        }
        catch (Exception ex)
        {
            // Broken output pipe. Do NOT rethrow — the prior design let this
            // escape the catch handler and terminate the process, closing the
            // transport permanently. INV-MCP-TRANSPORT-RESILIENCE-001.
            logError?.Invoke($"Response write failed (transport): {ex.Message}");
        }
    }
}

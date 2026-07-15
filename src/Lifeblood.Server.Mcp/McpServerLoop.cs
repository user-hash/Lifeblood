using System.Text.Json;
using Lifeblood.Connectors.Mcp;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// The MCP stdio read-dispatch-write loop, extracted from
/// <see cref="Program"/> so its transport resilience and cancellation
/// contracts are directly testable.
///
/// INV-MCP-TRANSPORT-RESILIENCE-001 keeps one active request per connection
/// and preserves response order. While that request runs, the loop continues
/// reading cancellation notifications for its id and queues every other frame.
/// Cross-client concurrency remains owned by the shared daemon, session gate,
/// and analysis coordinator. A dispatch, serialization, or broken-output fault
/// on one call never terminates the connection's future calls.
/// </summary>
public static class McpServerLoop
{
    public static async Task RunAsync(
        TextReader input,
        TextWriter output,
        Func<JsonRpcRequest, CancellationToken, JsonRpcResponse?> dispatch,
        JsonSerializerOptions jsonOpts,
        bool strictJson,
        CancellationToken cancellationToken,
        Action<string>? logError = null)
    {
        var queuedLines = new Queue<string>();
        Task<string?>? pendingRead = null;
        var inputClosed = false;

        while (!cancellationToken.IsCancellationRequested
               && (!inputClosed || queuedLines.Count > 0))
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

            if (line == null)
                break;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonRpcRequest request;
            try
            {
                var parsed = McpJsonRequestParser.DeserializeRequest(line, jsonOpts, strictJson);
                if (parsed == null)
                    continue;
                request = parsed;
            }
            catch (JsonException ex)
            {
                logError?.Invoke($"Parse error: {ex.Message}");
                TryWriteResponse(output, BuildParseError(), jsonOpts, logError);
                continue;
            }

            // A cancellation notification has meaning only while its target
            // request is active. A late notification is an idempotent no-op.
            if (TryGetCancellationTarget(request, out _))
                continue;

            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            var activeRequestId = RequestIdKey(request.Id);
            var dispatchTask = Task.Run(
                () => DispatchSafely(request, dispatch, requestCancellation.Token, logError),
                CancellationToken.None);

            while (!dispatchTask.IsCompleted
                   && !cancellationToken.IsCancellationRequested
                   && !inputClosed)
            {
                try
                {
                    pendingRead ??= input.ReadLineAsync(cancellationToken).AsTask();
                    var completed = await Task.WhenAny(dispatchTask, pendingRead);
                    if (ReferenceEquals(completed, dispatchTask))
                        break;

                    var concurrentLine = await pendingRead;
                    pendingRead = null;
                    if (concurrentLine == null)
                    {
                        inputClosed = true;
                        if (queuedLines.Count == 0)
                            requestCancellation.Cancel();
                        break;
                    }
                    if (string.IsNullOrWhiteSpace(concurrentLine))
                        continue;

                    if (TryParseCancellationFor(
                            concurrentLine,
                            activeRequestId,
                            jsonOpts,
                            strictJson))
                    {
                        requestCancellation.Cancel();
                    }
                    else
                    {
                        queuedLines.Enqueue(concurrentLine);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    requestCancellation.Cancel();
                    break;
                }
                catch (Exception ex)
                {
                    logError?.Invoke(
                        $"stdin read failed while '{request.Method}' was active: {ex.Message}");
                    inputClosed = true;
                    requestCancellation.Cancel();
                    break;
                }
            }

            var response = await dispatchTask;
            if (response != null)
                TryWriteResponse(output, response, jsonOpts, logError);
        }
    }

    private static JsonRpcResponse? DispatchSafely(
        JsonRpcRequest request,
        Func<JsonRpcRequest, CancellationToken, JsonRpcResponse?> dispatch,
        CancellationToken cancellationToken,
        Action<string>? logError)
    {
        try
        {
            return dispatch(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return request.Id == null ? null : BuildRequestCancelled(request.Id, request.Method);
        }
        catch (Exception ex)
        {
            logError?.Invoke($"Dispatch fault on '{request.Method}': {ex.Message}");
            return BuildInternalError(request.Id, request.Method, ex);
        }
    }

    internal static bool TryGetCancellationTarget(
        JsonRpcRequest request,
        out string targetId)
    {
        targetId = string.Empty;
        var parameterName = request.Method switch
        {
            McpProtocolSpec.Notifications.Cancelled => "requestId",
            McpProtocolSpec.Notifications.CancelRequest => "id",
            _ => null,
        };
        if (parameterName == null
            || request.Params is not { } parameters
            || parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty(parameterName, out var id))
        {
            return false;
        }

        targetId = RequestIdKey(id) ?? string.Empty;
        return targetId.Length > 0;
    }

    internal static string? RequestIdKey(JsonElement? id)
        => id is { } value && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.GetRawText()
            : null;

    private static bool TryParseCancellationFor(
        string line,
        string? activeRequestId,
        JsonSerializerOptions jsonOpts,
        bool strictJson)
    {
        if (activeRequestId == null)
            return false;

        try
        {
            var request = McpJsonRequestParser.DeserializeRequest(line, jsonOpts, strictJson);
            return request != null
                && TryGetCancellationTarget(request, out var targetId)
                && string.Equals(targetId, activeRequestId, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static JsonRpcResponse BuildInternalError(
        JsonElement? id,
        string method,
        Exception ex) => new()
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
                    recovery = "This connection keeps one active request; a fault on one call does not " +
                           "affect later traffic. Retry this call; if it persists, re-run " +
                           "lifeblood_analyze, then reconnect the MCP server.",
                },
            },
        };

    internal static JsonRpcResponse BuildRequestCancelled(
        JsonElement? id,
        string method) => new()
        {
            Id = id,
            Error = new JsonRpcError
            {
                Code = -32800,
                Message = $"Request cancelled: {method}",
                Data = new
                {
                    phase = "dispatch",
                    method,
                    cancelled = true,
                    retryable = true,
                },
            },
        };

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
            logError?.Invoke($"Response serialize failed: {ex.Message}");
            try
            {
                json = JsonSerializer.Serialize(
                    new JsonRpcResponse
                    {
                        Id = response.Id,
                        Error = new JsonRpcError
                        {
                            Code = -32603,
                            Message = "Response serialization failed",
                        },
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
            logError?.Invoke($"Response write failed (transport): {ex.Message}");
        }
    }
}

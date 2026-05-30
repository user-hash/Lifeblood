using System.Text;
using System.Text.Json;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-MCP-TRANSPORT-RESILIENCE-001 / LB-TRACK-20260530-029. The stdio loop must
/// be single-flight and crash-proof: no dispatch fault, serialization fault, or
/// broken-pipe write may terminate the loop and close the transport for every
/// other call, and every internal error echoes the request id so the client can
/// correlate the failure instead of seeing an opaque transport drop.
/// </summary>
public class McpServerLoopTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public async Task NormalRequest_RoundTrips_WithIdEchoed()
    {
        var input = new StringReader("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"ok\"}\n");
        var output = new StringWriter();

        await McpServerLoop.RunAsync(
            input, output,
            req => new JsonRpcResponse { Id = req.Id, Result = new { ok = true } },
            JsonOpts, strictJson: false, CancellationToken.None);

        var written = output.ToString();
        Assert.Contains("\"id\":7", written);
        Assert.Contains("\"ok\":true", written);
    }

    [Fact]
    public async Task DispatchFault_EchoesId_AndLoopKeepsServing()
    {
        // First call faults; second must still be served — a fault on one
        // request cannot take down the transport.
        var input = new StringReader(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"boom\"}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ok\"}\n");
        var output = new StringWriter();
        var dispatched = 0;

        await McpServerLoop.RunAsync(
            input, output,
            req =>
            {
                dispatched++;
                if (req.Method == "boom") throw new InvalidOperationException("kaboom");
                return new JsonRpcResponse { Id = req.Id, Result = new { ok = true } };
            },
            JsonOpts, strictJson: false, CancellationToken.None);

        var written = output.ToString();
        Assert.Equal(2, dispatched);
        // Error for id 1 carries the structured envelope and echoes the id.
        Assert.Contains("\"id\":1", written);
        Assert.Contains("-32603", written);
        Assert.Contains("\"recoverable\":true", written);
        // Second request still served.
        Assert.Contains("\"id\":2", written);
        Assert.Contains("\"ok\":true", written);
    }

    [Fact]
    public async Task BrokenOutputPipe_DoesNotTerminateLoop()
    {
        // A write that throws (broken pipe) must be swallowed: the loop keeps
        // reading and dispatching every line instead of dying mid-session.
        var input = new StringReader(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ok\"}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ok\"}\n");
        var output = new ThrowingTextWriter();
        var dispatched = 0;

        var ex = await Record.ExceptionAsync(() => McpServerLoop.RunAsync(
            input, output,
            req => { dispatched++; return new JsonRpcResponse { Id = req.Id, Result = new { ok = true } }; },
            JsonOpts, strictJson: false, CancellationToken.None));

        Assert.Null(ex);              // loop never throws out of the broken write
        Assert.Equal(2, dispatched);  // both requests served despite the write fault
    }

    [Fact]
    public async Task MalformedJson_EmitsParseError_AndLoopContinues()
    {
        var input = new StringReader(
            "{ this is not json\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"ok\"}\n");
        var output = new StringWriter();

        await McpServerLoop.RunAsync(
            input, output,
            req => new JsonRpcResponse { Id = req.Id, Result = new { ok = true } },
            JsonOpts, strictJson: false, CancellationToken.None);

        var written = output.ToString();
        Assert.Contains("-32700", written);     // parse error emitted
        Assert.Contains("\"id\":5", written);   // loop recovered and served the next line
    }

    private sealed class ThrowingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => throw new IOException("broken pipe");
        public override void Write(string? value) => throw new IOException("broken pipe");
        public override void WriteLine(string? value) => throw new IOException("broken pipe");
        public override void Flush() => throw new IOException("broken pipe");
    }
}

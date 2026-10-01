using System.Text.Json;
using System.Text.Json.Serialization;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>INV-MCP-STRICT-JSON-001.</summary>
public class McpJsonRequestParserTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void DeserializeRequest_DefaultMode_AllowsDuplicatePropertiesForBackCompat()
    {
        var request = McpJsonRequestParser.DeserializeRequest(
            """{"jsonrpc":"2.0","id":1,"method":"ping","method":"tools/list"}""",
            JsonOpts,
            strictJson: false);

        Assert.NotNull(request);
        Assert.Equal("tools/list", request!.Method);
    }

    [Fact]
    public void DeserializeRequest_StrictMode_RejectsDuplicateRootProperty()
    {
        Assert.Throws<JsonException>(() =>
            McpJsonRequestParser.DeserializeRequest(
                """{"jsonrpc":"2.0","id":1,"method":"ping","method":"tools/list"}""",
                JsonOpts,
                strictJson: true));
    }

    [Fact]
    public void DeserializeRequest_StrictMode_RejectsDuplicateNestedProperty()
    {
        Assert.Throws<JsonException>(() =>
            McpJsonRequestParser.DeserializeRequest(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"lifeblood_analyze","name":"lifeblood_context"}}""",
                JsonOpts,
                strictJson: true));
    }

    [Fact]
    public void DeserializeRequest_StrictMode_RejectsUnknownEnvelopeProperty()
    {
        var ex = Assert.Throws<JsonException>(() =>
            McpJsonRequestParser.DeserializeRequest(
                """{"jsonrpc":"2.0","id":1,"method":"tools/list","bogusField":true}""",
                JsonOpts,
                strictJson: true));

        Assert.Contains("bogusField", ex.Message);
    }

    [Fact]
    public void DeserializeRequest_DefaultMode_IgnoresUnknownEnvelopePropertyForBackCompat()
    {
        var request = McpJsonRequestParser.DeserializeRequest(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list","bogusField":true}""",
            JsonOpts,
            strictJson: false);

        Assert.NotNull(request);
        Assert.Equal("tools/list", request!.Method);
    }

    [Fact]
    public void DeserializeRequest_StrictMode_AllowsUnknownPropertyInsideParams()
    {
        // params is bound to JsonElement (opaque tool-arg blob) — unknown
        // members there are validated per-tool by the handler, NOT rejected
        // at the envelope layer. Strict mode guards the envelope only.
        var request = McpJsonRequestParser.DeserializeRequest(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"lifeblood_analyze","unmappedArg":42}}""",
            JsonOpts,
            strictJson: true);

        Assert.NotNull(request);
        Assert.Equal("tools/call", request!.Method);
    }

    [Theory]
    [InlineData("""{"method":"ping","\u006dethod":"tools/list"}""")]
    [InlineData("""{"id":{"key":1,"key":2},"method":"ping"}""")]
    [InlineData("""{"method":"tools/call","params":{"arguments":[{"key":1,"key":2}]}}""")]
    [InlineData("""{"method":"tools/call","params":{"arguments":{"\u006bey":1,"key":2}}}""")]
    [InlineData("""{"method":"tools/call","params":{"arguments":{"ž":1,"\u017e":2}}}}""")]
    [InlineData("""{"method":"ping","unexpected":{"key":1,"key":2}}""")]
    [InlineData("""{"Method":"ping"}""")]
    public void DeserializeRequest_StrictMode_RejectsAmbiguousOrUnknownPropertiesInBothInputs(string json)
    {
        Assert.Throws<JsonException>(() => McpJsonRequestParser.DeserializeRequest(json, JsonOpts, true));
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        Assert.Throws<JsonException>(() => McpJsonRequestParser.DeserializeRequest(bytes, true));
    }

    [Theory]
    [InlineData("""{"method":"ping","params":{"items":[{"key":1},{"key":2}],"key":3,"Key":4}}""")]
    [InlineData("""{"id":"ž\u017e","method":"ping","params":null}""")]
    [InlineData("""{"method":"ping","params":[null,true,42,"text"]}""")]
    [InlineData("{}")]
    [InlineData("null")]
    public void DeserializeRequest_ValidFramesPreserveValuesAcrossModesAndInputs(string json)
    {
        var legacy = McpJsonRequestParser.DeserializeRequest(json, JsonOpts, false);
        foreach (var strict in new[] { false, true })
        {
            var fromString = McpJsonRequestParser.DeserializeRequest(json, JsonOpts, strict);
            var fromBytes = McpJsonRequestParser.DeserializeRequest(System.Text.Encoding.UTF8.GetBytes(json), strict);
            foreach (var request in new[] { fromString, fromBytes })
            {
                Assert.Equal(legacy?.JsonRpc, request?.JsonRpc);
                Assert.Equal(legacy?.Method, request?.Method);
                Assert.Equal(legacy?.Id?.GetRawText(), request?.Id?.GetRawText());
                Assert.Equal(legacy?.Params?.GetRawText(), request?.Params?.GetRawText());
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeserializeRequest_DepthLimitRemains64(bool strict)
    {
        var valid = "{\"params\":" + new string('[', 63) + "0" + new string(']', 63) + "}";
        var invalid = "{\"params\":" + new string('[', 64) + "0" + new string(']', 64) + "}";
        Assert.NotNull(McpJsonRequestParser.DeserializeRequest(valid, JsonOpts, strict));
        Assert.NotNull(McpJsonRequestParser.DeserializeRequest(System.Text.Encoding.UTF8.GetBytes(valid), strict));
        Assert.Throws<JsonException>(() => McpJsonRequestParser.DeserializeRequest(invalid, JsonOpts, strict));
        Assert.Throws<JsonException>(() => McpJsonRequestParser.DeserializeRequest(System.Text.Encoding.UTF8.GetBytes(invalid), strict));
    }

    [Theory]
    [InlineData("{\"method\":\"ping\",}")]
    [InlineData("{/*comment*/\"method\":\"ping\"}")]
    [InlineData("{\"method\":1}")]
    [InlineData("{} {}")]
    [InlineData("[")]
    public void DeserializeRequest_MalformedFramesRejectInBothModesAndInputs(string json)
    {
        foreach (var strict in new[] { false, true })
        {
            Assert.Throws<JsonException>(() => McpJsonRequestParser.DeserializeRequest(json, JsonOpts, strict));
            Assert.Throws<JsonException>(() => McpJsonRequestParser.DeserializeRequest(System.Text.Encoding.UTF8.GetBytes(json), strict));
        }
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("on", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    public void ReadStrictJsonFlag_RecognizesOptInValues(string value, bool expected)
    {
        const string name = "LIFEBLOOD_STRICT_JSON_TEST";
        try
        {
            Environment.SetEnvironmentVariable(name, value);

            Assert.Equal(expected, McpJsonRequestParser.ReadStrictJsonFlag(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}

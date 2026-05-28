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
        var ex = Assert.Throws<JsonException>(() =>
            McpJsonRequestParser.DeserializeRequest(
                """{"jsonrpc":"2.0","id":1,"method":"ping","method":"tools/list"}""",
                JsonOpts,
                strictJson: true));

        Assert.Contains("Duplicate JSON property 'method'", ex.Message);
    }

    [Fact]
    public void DeserializeRequest_StrictMode_RejectsDuplicateNestedProperty()
    {
        var ex = Assert.Throws<JsonException>(() =>
            McpJsonRequestParser.DeserializeRequest(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"lifeblood_analyze","name":"lifeblood_context"}}""",
                JsonOpts,
                strictJson: true));

        Assert.Contains("Duplicate JSON property 'name'", ex.Message);
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

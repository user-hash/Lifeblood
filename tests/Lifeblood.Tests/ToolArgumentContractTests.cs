using System.Text.Json;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-MCP-TOOL-ARG-CONTRACT-001. Tool argument validation is sourced from
/// typed server-edge contracts, not ad-hoc handler parsing.
/// </summary>
public class ToolArgumentContractTests
{
    [Fact]
    public void ToolDefinitions_ExposeTypedInputContracts_WithRequiredFields()
    {
        var lookup = ToolRegistry.GetDefinitions().Single(d => d.Name == "lifeblood_lookup");
        var contract = ToolInputContract.FromSchema(lookup.Name, lookup.InputSchema);

        Assert.True(contract.Arguments.TryGetValue("symbolId", out var symbolId));
        Assert.True(symbolId.Required);
        Assert.Equal(ToolArgumentType.String, symbolId.Type);
    }

    [Fact]
    public void Binder_LegacyMode_AllowsUnknownAndMissingArguments()
    {
        var binder = NewBinder();

        var result = binder.Validate(
            "lifeblood_lookup",
            JsonArgs(new { unexpected = true }),
            ToolJsonCompatibilityMode.Legacy);

        Assert.True(result.Accepted);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Binder_WarnMode_AcceptsButReportsUnknownMissingAndTypeMismatch()
    {
        var binder = NewBinder();

        var result = binder.Validate(
            "lifeblood_lookup",
            JsonArgs(new { symbolId = 42, unexpected = true }),
            ToolJsonCompatibilityMode.Warn);

        Assert.True(result.Accepted);
        Assert.Contains(result.Diagnostics, d => d.Kind == "unknown" && d.Argument == "unexpected");
        Assert.Contains(result.Diagnostics, d => d.Kind == "typeMismatch" && d.Argument == "symbolId");
    }

    [Fact]
    public void Binder_StrictMode_RejectsMissingRequiredArgument()
    {
        var binder = NewBinder();

        var result = binder.Validate(
            "lifeblood_lookup",
            JsonArgs(new { }),
            ToolJsonCompatibilityMode.Strict);

        Assert.False(result.Accepted);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("missingRequired", diagnostic.Kind);
        Assert.Equal("symbolId", diagnostic.Argument);
    }

    [Fact]
    public void Binder_StrictMode_RejectsDuplicateArgumentNames()
    {
        var binder = NewBinder();
        using var document = JsonDocument.Parse("""{"symbolId":"type:A","symbolId":"type:B"}""");

        var result = binder.Validate(
            "lifeblood_lookup",
            document.RootElement,
            ToolJsonCompatibilityMode.Strict);

        Assert.False(result.Accepted);
        Assert.Contains(result.Diagnostics, d => d.Kind == "duplicate" && d.Argument == "symbolId");
    }

    [Fact]
    public void ReadFromEnvironment_StrictJsonAlias_RemainsStrictAlias()
    {
        const string compatName = "LIFEBLOOD_JSON_COMPAT_TEST";
        const string strictName = "LIFEBLOOD_STRICT_JSON_TEST";
        try
        {
            Environment.SetEnvironmentVariable(compatName, null);
            Environment.SetEnvironmentVariable(strictName, "true");

            Assert.Equal(
                ToolJsonCompatibilityMode.Strict,
                ToolJsonCompatibilityModeReader.ReadFromEnvironment(compatName, strictName));
        }
        finally
        {
            Environment.SetEnvironmentVariable(compatName, null);
            Environment.SetEnvironmentVariable(strictName, null);
        }
    }

    [Theory]
    [InlineData("legacy", ToolJsonCompatibilityMode.Legacy)]
    [InlineData("warn", ToolJsonCompatibilityMode.Warn)]
    [InlineData("strict", ToolJsonCompatibilityMode.Strict)]
    [InlineData("true", ToolJsonCompatibilityMode.Strict)]
    [InlineData("nonsense", ToolJsonCompatibilityMode.Legacy)]
    public void Parse_RecognizesCompatibilityModes(string value, ToolJsonCompatibilityMode expected)
    {
        Assert.Equal(expected, ToolJsonCompatibilityModeReader.Parse(value));
    }

    private static JsonElement? JsonArgs(object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private static ToolArgumentBinder NewBinder()
        => new(ToolRegistry.GetDefinitions()
            .Select(d => ToolInputContract.FromSchema(d.Name, d.InputSchema)));
}

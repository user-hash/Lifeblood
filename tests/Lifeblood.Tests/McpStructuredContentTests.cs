using System.Text.Json;
using System.Diagnostics;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Server.Mcp;
using Xunit;
using Xunit.Abstractions;

namespace Lifeblood.Tests;

public sealed class McpStructuredContentTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(McpProtocolSpec.ResultFormats.Dual, false)]
    [InlineData(McpProtocolSpec.ResultFormats.Dual, true)]
    [InlineData(McpProtocolSpec.ResultFormats.StructuredOnly, false)]
    [InlineData(McpProtocolSpec.ResultFormats.StructuredOnly, true)]
    public void Projection_PreservesPayloadErrorsAndSourceWithoutCaching(string format, bool error)
    {
        var source = Result("""{"failure":"file-scope-precondition","compilationStarted":false,"envelope":{"snapshotId":"same-publication"}}""", error);
        var original = JsonSerializer.Serialize(source);

        var projected = McpStructuredContentProjection.Project(source, format);

        Assert.Equal(error, projected.IsError);
        Assert.Equal(source.Content[0].Text, projected.StructuredContent!.Value.GetRawText());
        Assert.Equal(format == McpProtocolSpec.ResultFormats.StructuredOnly ? 0 : 1, projected.Content.Length);
        Assert.Equal(original, JsonSerializer.Serialize(source));
        Assert.Null(source.StructuredContent);
        Assert.NotSame(source, projected);
    }

    [Theory]
    [InlineData("plain error")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    [InlineData("{invalid")]
    public void UnstructuredResults_ArePreservedRatherThanWrappedOrDropped(string text)
    {
        var source = Result(text, true);
        Assert.Same(source, McpStructuredContentProjection.Project(source, McpProtocolSpec.ResultFormats.StructuredOnly));
    }

    [Fact]
    public void TextAndMultiContent_AreUnchanged()
    {
        var source = Result("{}");
        Assert.Same(source, McpStructuredContentProjection.Project(source, McpProtocolSpec.ResultFormats.Text));
        source.Content = new[] { new McpContent { Text = "{}" }, new McpContent { Text = "compatibility notice" } };
        Assert.Same(source, McpStructuredContentProjection.Project(source, McpProtocolSpec.ResultFormats.StructuredOnly));
    }

    [Fact]
    public void OversizedProjection_PreservesFullTextErrorAndMetadataWithBoundedFallbackReceipt()
    {
        var source = Result("{\"large\":\"" + new string('x', McpStructuredContentProjection.MaximumProjectionCharacters) + "\"}", true);
        source.Meta = new Dictionary<string, object?> { ["existing"] = "kept" };

        var result = McpStructuredContentProjection.Project(source, McpProtocolSpec.ResultFormats.StructuredOnly);

        Assert.Same(source.Content, result.Content);
        Assert.True(result.IsError);
        Assert.Null(result.StructuredContent);
        Assert.Equal("kept", result.Meta!["existing"]);
        Assert.True(JsonSerializer.Serialize(result.Meta).Contains("projectionLimitExceeded", StringComparison.Ordinal));
        Assert.Single(source.Meta);
    }

    [Theory]
    [InlineData(McpProtocolSpec.SupportedVersion, null, McpProtocolSpec.ResultFormats.Text)]
    [InlineData(McpProtocolSpec.StructuredContentVersion, null, McpProtocolSpec.ResultFormats.Dual)]
    [InlineData(McpProtocolSpec.StructuredContentVersion, McpProtocolSpec.ResultFormats.StructuredOnly, McpProtocolSpec.ResultFormats.StructuredOnly)]
    [InlineData(McpProtocolSpec.StructuredContentVersion, McpProtocolSpec.ResultFormats.Text, McpProtocolSpec.ResultFormats.Text)]
    [InlineData("unknown-future-revision", null, McpProtocolSpec.ResultFormats.Dual)]
    public void Negotiation_SelectsPerConnectionFormatAndHonorsLegacy(string protocol, string? format, string expected)
    {
        using var host = McpServerHost.Create(ToolJsonCompatibilityMode.Strict);
        var dispatcher = host.CreateDispatcher();
        var initialized = dispatcher.Dispatch(Initialize(protocol, format));
        Assert.Null(initialized!.Error);
        var result = Assert.IsType<McpInitializeResult>(initialized.Result);
        Assert.Equal(protocol == McpProtocolSpec.SupportedVersion ? protocol : McpProtocolSpec.StructuredContentVersion, result.ProtocolVersion);

        var response = dispatcher.Dispatch(Call("lifeblood_capabilities"));
        var tool = Assert.IsType<McpToolResult>(response!.Result);

        Assert.Equal(expected != McpProtocolSpec.ResultFormats.Text, tool.StructuredContent.HasValue);
        Assert.Equal(expected == McpProtocolSpec.ResultFormats.StructuredOnly ? 0 : 1, tool.Content.Length);
        if (tool.StructuredContent is { } structured && tool.Content.Length == 1)
            Assert.Equal(tool.Content[0].Text, structured.GetRawText());
        if (protocol == McpProtocolSpec.SupportedVersion)
            Assert.Null(result.Capabilities.Experimental);
        else
            Assert.Contains(expected, JsonSerializer.Serialize(result.Capabilities.Experimental));
    }

    [Theory]
    [InlineData(McpProtocolSpec.SupportedVersion, McpProtocolSpec.ResultFormats.StructuredOnly)]
    [InlineData(McpProtocolSpec.StructuredContentVersion, "unsupported")]
    public void FailedNegotiation_DoesNotChangeExistingConnectionState(string protocol, string format)
    {
        using var host = McpServerHost.Create(ToolJsonCompatibilityMode.Strict);
        var dispatcher = host.CreateDispatcher();
        Assert.Null(dispatcher.Dispatch(Initialize(McpProtocolSpec.SupportedVersion, null))!.Error);
        Assert.NotNull(dispatcher.Dispatch(Initialize(protocol, format))!.Error);
        var result = Assert.IsType<McpToolResult>(dispatcher.Dispatch(Call("lifeblood_capabilities"))!.Result);
        Assert.Null(result.StructuredContent);
        Assert.NotEmpty(result.Content);
    }

    [Fact]
    public void IndependentConnections_KeepTextAndStructuredSchemasSeparate()
    {
        using var host = McpServerHost.Create(ToolJsonCompatibilityMode.Strict);
        var legacy = host.CreateDispatcher();
        var canonical = host.CreateDispatcher();
        Assert.Null(legacy.Dispatch(Initialize(McpProtocolSpec.SupportedVersion, null))!.Error);
        Assert.Null(canonical.Dispatch(Initialize(McpProtocolSpec.StructuredContentVersion, McpProtocolSpec.ResultFormats.StructuredOnly, McpProtocolSpec.SchemaVersions.Canonical))!.Error);
        var structured = Assert.IsType<McpToolResult>(canonical.Dispatch(Call("lifeblood_capabilities"))!.Result);
        var text = Assert.IsType<McpToolResult>(legacy.Dispatch(Call("lifeblood_capabilities"))!.Result);
        Assert.NotNull(structured.StructuredContent);
        Assert.Empty(structured.Content);
        Assert.Null(text.StructuredContent);
        Assert.Contains("schema v1", text.Content[0].Text);
        Assert.DoesNotContain("schema v1", structured.StructuredContent.Value.GetRawText());
    }

    private static McpToolResult Result(string text, bool error = false)
        => new() { Content = new[] { new McpContent { Text = text } }, IsError = error };

    [Fact]
    public void FixedProjection_MeasuresWireSizeShapingTimeAndAllocationCost()
    {
        var source = Result(JsonSerializer.Serialize(new
        {
            snapshotId = "snap_fixed", analysisGeneration = 7,
            results = Enumerable.Range(0, 8).Select(i => new
            {
                index = i, tool = "file-scoped-query", isError = false,
                payload = new { filePath = "Assets/Source" + i + ".cs", count = 1,
                    sourceCode = "public class C { string S = \"č quoted value\"; }\n", limitations = new[] { "Retained profile only" } },
                evidence = new { truthTier = "Semantic", confidence = "Proven", freshness = new { status = "incomplete", checkedFileCount = 256, uncheckedFileCount = 6000 } },
            }),
        }));
        var formats = new[] { McpProtocolSpec.ResultFormats.Text, McpProtocolSpec.ResultFormats.Dual, McpProtocolSpec.ResultFormats.StructuredOnly };
        byte[] Shape(string format) => JsonSerializer.SerializeToUtf8Bytes(McpStructuredContentProjection.Project(source, format));
        var bytes = formats.ToDictionary(format => format, Shape);
        Assert.True(bytes[McpProtocolSpec.ResultFormats.StructuredOnly].Length < bytes[McpProtocolSpec.ResultFormats.Text].Length);
        Assert.True(bytes[McpProtocolSpec.ResultFormats.Dual].Length > bytes[McpProtocolSpec.ResultFormats.Text].Length);
        const int samples = 100;
        var durations = formats.ToDictionary(format => format, _ => new double[samples]);
        var allocations = formats.ToDictionary(format => format, _ => new long[samples]);
        for (var warmup = 0; warmup < 10; warmup++) foreach (var format in formats) Shape(format);
        for (var sample = 0; sample < samples; sample++)
        {
            foreach (var format in sample % 2 == 0 ? formats : formats.Reverse())
            {
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var watch = Stopwatch.StartNew();
                Shape(format);
                watch.Stop();
                durations[format][sample] = watch.Elapsed.TotalMilliseconds;
                allocations[format][sample] = GC.GetAllocatedBytesForCurrentThread() - allocated;
            }
        }
        output.WriteLine("STRUCTURED_RESULT_MEASUREMENT=" + JsonSerializer.Serialize(new
        {
            scope = "Fixed eight-row result projection and UTF-8 serialization only; excludes dispatch, client parsing, transport, Roslyn and ExampleApp",
            samples, alternatingOrder = true,
            formats = formats.ToDictionary(format => format, format => new
            {
                bytes = bytes[format].Length,
                medianMs = durations[format].Order().ElementAt(samples / 2),
                medianAllocatedBytes = allocations[format].Order().ElementAt(samples / 2),
            }),
        }));
    }

    private static JsonRpcRequest Initialize(string protocol, string? format, string schema = McpProtocolSpec.SchemaVersions.Legacy)
        => new()
        {
            Id = JsonSerializer.SerializeToElement(1), Method = McpProtocolSpec.Methods.Initialize,
            Params = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["protocolVersion"] = protocol,
                [McpProtocolSpec.SchemaVersions.InitializeParameter] = schema,
                ["capabilities"] = new { experimental = format == null ? new Dictionary<string, string>()
                    : new Dictionary<string, string> { [McpProtocolSpec.ResultFormats.Capability] = format } },
            }),
        };

    private static JsonRpcRequest Call(string name)
        => new() { Id = JsonSerializer.SerializeToElement(2), Method = McpProtocolSpec.Methods.ToolsCall,
            Params = JsonSerializer.SerializeToElement(new { name, arguments = new { } }) };
}

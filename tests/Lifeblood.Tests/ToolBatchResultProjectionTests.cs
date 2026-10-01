using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lifeblood.Server.Mcp;
using Xunit;
using Xunit.Abstractions;

namespace Lifeblood.Tests;

public sealed class ToolBatchResultProjectionTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private const string Identity = """{"snapshotId":"snap_fixed","analysisGeneration":7,"analysisIdentity":{"analysisKey":"fixed-inputs","profiles":["Editor","Player"]}}""";
    private const string Child = """
        {"symbolId":"type:v2|opaque-address","sourceCode":"literal \"type:v2|do-not-rewrite\"\n",
         "analyzedUnderProfile":"Editor","limitations":["Other profile sites excluded"],"truncated":true,"count":100,
         "envelope":{"snapshotId":"snap_fixed","analysisGeneration":7,
            "analysisIdentity":{"analysisKey":"fixed-inputs","profiles":["Editor","Player"]},
            "truthTier":"Semantic","confidence":"Advisory","evidenceSource":"Semantic",
            "freshness":{"status":"partial","checkedFileCount":256,"uncheckedFileCount":300},
            "limitations":["Runtime dispatch invisible"]}}
        """;

    [Fact]
    public void CompactChild_PreservesPayloadAndEvidenceWithoutChangingOriginalContent()
    {
        var original = Result(Child, isError: true);
        var row = ToolBatchResultProjection.Project(2, "query", original, true, JsonNode.Parse(Identity)!.AsObject());
        Assert.Equal(2, row.Index);
        Assert.Equal("query", row.Tool);
        Assert.True(row.IsError);
        Assert.Null(row.Content);
        Assert.Equal(Child, original.Content[0].Text);
        var expected = JsonNode.Parse(Child)!.AsObject();
        var expectedEvidence = expected["envelope"]!;
        expected.Remove("envelope");
        Assert.True(JsonNode.DeepEquals(expected, row.Payload));
        var restoredEvidence = row.Evidence!.DeepClone().AsObject();
        foreach (var field in JsonNode.Parse(Identity)!.AsObject())
            restoredEvidence[field.Key] = field.Value?.DeepClone();
        Assert.True(JsonNode.DeepEquals(expectedEvidence, restoredEvidence));
    }

    [Fact]
    public void DifferentIdentity_IsPreservedInsteadOfSilentlyRelabelled()
    {
        var child = JsonNode.Parse(Child)!.AsObject();
        child["envelope"]!["snapshotId"] = "snap_other";
        child["envelope"]!["analysisGeneration"] = 8;
        child["analysisIdentity"] = new JsonObject { ["analysisKey"] = "other-inputs" };
        var row = ToolBatchResultProjection.Project(0, "query", Result(child.ToJsonString()), true,
            JsonNode.Parse(Identity)!.AsObject());
        Assert.Equal("snap_other", row.Evidence!["snapshotId"]!.GetValue<string>());
        Assert.Equal(8, row.Evidence["analysisGeneration"]!.GetValue<int>());
        Assert.Equal("other-inputs", row.Payload!["analysisIdentity"]!["analysisKey"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("[1,2]")]
    [InlineData("null")]
    [InlineData("\"a JSON string\"")]
    public void NonObjectChild_PreservesOriginalContent(string text)
    {
        var original = Result(text);
        var row = ToolBatchResultProjection.Project(0, "query", original, true, JsonNode.Parse(Identity)!.AsObject());
        Assert.Same(original.Content, row.Content);
        Assert.Null(row.Payload);
    }

    [Fact]
    public void MultipleContentAndNonTextContent_ArePreserved()
    {
        foreach (var original in new[]
        {
            new McpToolResult { Content = new[] { new McpContent { Type = "image", Text = Child } } },
            new McpToolResult { Content = new[] { new McpContent { Type = "text", Text = Child }, new McpContent { Type = "text", Text = "notice" } } },
        })
        {
            var row = ToolBatchResultProjection.Project(0, "query", original, true, JsonNode.Parse(Identity)!.AsObject());
            Assert.Same(original.Content, row.Content);
            Assert.Null(row.Payload);
        }
    }

    [Fact]
    public void DefaultRow_KeepsTheOriginalFieldSetAndContent()
    {
        var original = Result(Child);
        var row = ToolBatchResultProjection.Project(0, "query", original, false, JsonNode.Parse(Identity)!.AsObject());
        var serialized = JsonSerializer.SerializeToNode(row, Options)!.AsObject();
        Assert.Equal(new[] { "index", "tool", "isError", "content" }, serialized.Select(p => p.Key));
        Assert.Same(original.Content, row.Content);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("\"other\"")]
    public void InvalidFormat_IsRejectedBeforeDispatch(string format)
    {
        using var args = JsonDocument.Parse("{\"calls\":[{\"tool\":\"query\"}],\"responseFormat\":" + format + "}");
        Assert.Throws<ToolBatchContractException>(() => ToolBatchRequestBinder.Bind(args.RootElement));
    }

    [Fact]
    public void FixedProjection_MeasuresWireBytesShapingTimeAndAllocations()
    {
        var shared = JsonNode.Parse(Identity)!.AsObject();
        var inputs = Enumerable.Repeat(Result(Child), 12).ToArray();
        byte[] Shape(bool compact)
            => JsonSerializer.SerializeToUtf8Bytes(new
            {
                identity = shared,
                results = inputs.Select((result, index) => ToolBatchResultProjection.Project(index, "query", result, compact, shared)).ToArray(),
            }, Options);
        var contentBytes = Shape(false);
        var compactBytes = Shape(true);
        Assert.True(compactBytes.Length < contentBytes.Length);
        const int samples = 100;
        var contentMs = new double[samples]; var compactMs = new double[samples];
        var contentAllocated = new long[samples]; var compactAllocated = new long[samples];
        void Measure(bool compact, int index)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            Shape(compact);
            watch.Stop();
            (compact ? compactAllocated : contentAllocated)[index] = GC.GetAllocatedBytesForCurrentThread() - allocated;
            (compact ? compactMs : contentMs)[index] = watch.Elapsed.TotalMilliseconds;
        }
        for (var i = 0; i < samples; i++)
        {
            Measure(i % 2 == 0, i); Measure(i % 2 != 0, i);
        }
        output.WriteLine("COMPACT_BATCH_MEASUREMENT=" + JsonSerializer.Serialize(new
        {
            scope = "Fixed 12-child wire projection and UTF-8 serialization only; excludes dispatch, Roslyn, transport and ExampleApp",
            samples, alternatingOrder = true,
            content = new { bytes = contentBytes.Length, medianMs = contentMs.Order().ElementAt(samples / 2), medianAllocatedBytes = contentAllocated.Order().ElementAt(samples / 2) },
            compact = new { bytes = compactBytes.Length, medianMs = compactMs.Order().ElementAt(samples / 2), medianAllocatedBytes = compactAllocated.Order().ElementAt(samples / 2) },
        }));
    }

    private static McpToolResult Result(string text, bool isError = false)
        => new() { IsError = isError, Content = new[] { new McpContent { Type = "text", Text = text } } };
}

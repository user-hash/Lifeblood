using System.Text.Json;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>INV-LIST-SHAPE-UNIFORM-001.</summary>
public class UniformListShapeRatchetTests
{
    public static IEnumerable<object[]> ListShapeTools => new[]
    {
        new object[] { "lifeblood_dead_code" },
        new object[] { "lifeblood_cycles" },
        new object[] { "lifeblood_blast_radius" },
        new object[] { "lifeblood_file_impact" },
        new object[] { "lifeblood_static_tables" },
        new object[] { "lifeblood_context" },
        new object[] { "lifeblood_wire_audit" },
        new object[] { "lifeblood_feature_switch_audit" },
    };

    [Theory]
    [MemberData(nameof(ListShapeTools))]
    public void ListShapeTool_HasSummarizeBooleanInInputSchema(string toolName)
    {
        var tool = ToolRegistry.GetDefinitions().FirstOrDefault(d => d.Name == toolName);
        Assert.NotNull(tool);

        var schema = JsonSerializer.SerializeToElement(tool!.InputSchema);
        Assert.True(schema.TryGetProperty("properties", out var props),
            $"{toolName}: InputSchema must declare `properties`.");

        Assert.True(props.TryGetProperty("summarize", out var summarize),
            $"{toolName}: list-shape tool MUST expose `summarize:bool` on its input schema (INV-LIST-SHAPE-UNIFORM-001).");

        Assert.True(summarize.TryGetProperty("type", out var type),
            $"{toolName}: `summarize` property MUST declare a `type` field.");
        Assert.Equal("boolean", type.GetString());
    }

    [Theory]
    [MemberData(nameof(ListShapeTools))]
    public void ListShapeTool_HasCapParameterInInputSchema(string toolName)
    {
        var capNames = new[] { "maxResults", "maxRows", "maxTables", "maxFiles", "maxHotspots", "maxBoundaries", "maxReadingOrder", "maxMatrixEntries", "maxSites", "maxFindings", "maxDepth", "limit" };

        var tool = ToolRegistry.GetDefinitions().FirstOrDefault(d => d.Name == toolName);
        Assert.NotNull(tool);

        var schema = JsonSerializer.SerializeToElement(tool!.InputSchema);
        Assert.True(schema.TryGetProperty("properties", out var props),
            $"{toolName}: InputSchema must declare `properties`.");

        var declaredCap = capNames.FirstOrDefault(c => props.TryGetProperty(c, out _));
        Assert.NotNull(declaredCap);
    }

    [Theory]
    [MemberData(nameof(ListShapeTools))]
    public void ListShapeTool_DescriptionMentionsSummarizeOrTruncated(string toolName)
    {
        var tool = ToolRegistry.GetDefinitions().FirstOrDefault(d => d.Name == toolName);
        Assert.NotNull(tool);

        var description = tool!.Description ?? "";
        Assert.True(
            description.Contains("summarize", System.StringComparison.OrdinalIgnoreCase)
            || description.Contains("truncat", System.StringComparison.OrdinalIgnoreCase),
            $"{toolName}: list-shape tool description SHOULD reference `summarize` or `truncat*`.");
    }
}

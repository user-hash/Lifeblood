using System.Text.Json;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Eternal-shape ratchet for INV-LIST-SHAPE-UNIFORM-001 (LB-TRACK-20260524-026 + -027).
///
/// Every MCP read-side tool whose response carries an unbounded list shape
/// MUST expose the `summarize:bool` + per-direction `maxResults`-equivalent
/// cap on its input schema. This ratchet enumerates the load-bearing list-
/// shape tools and asserts each carries the trio. The whitelist is the
/// architecture decision; the ratchet refuses to let it silently drift.
///
/// Adding a new list-shape tool: extend <see cref="ListShapeTools"/> with
/// its name and (if a cap arg uses a different keyword) the property name
/// of its cap parameter. Removing one from the list requires evidence the
/// tool's response is provably bounded (single-row reply, scalar answer,
/// etc.) — every tool that returns an array of arbitrary size belongs here.
///
/// This ratchet is eternal — it pins the WIRE-SHAPE contract, not any
/// single tool's correctness. Future list-shape tools shipping without the
/// trio fail the build here, not at dogfood time.
/// </summary>
public class UniformListShapeRatchetTests
{
    /// <summary>
    /// Tools whose response is an unbounded list. Each MUST expose
    /// <c>summarize:bool</c> + at least one cap argument on its input
    /// schema. The cap-parameter name varies (<c>maxResults</c>,
    /// <c>maxRows</c>, <c>maxTables</c>, <c>maxFiles</c>, …) per tool —
    /// any cap-shaped parameter satisfies the trio.
    /// </summary>
    public static IEnumerable<object[]> ListShapeTools => new[]
    {
        new object[] { "lifeblood_dead_code" },
        new object[] { "lifeblood_cycles" },
        new object[] { "lifeblood_blast_radius" },
        new object[] { "lifeblood_file_impact" },
        new object[] { "lifeblood_static_tables" },
        new object[] { "lifeblood_context" },
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
            $"{toolName}: list-shape tool MUST expose `summarize:bool` on its input schema (INV-LIST-SHAPE-UNIFORM-001). " +
            $"Other list-shape tools shipped with this trio; uniformity is the contract.");

        Assert.True(summarize.TryGetProperty("type", out var type),
            $"{toolName}: `summarize` property MUST declare a `type` field.");
        Assert.Equal("boolean", type.GetString());
    }

    [Theory]
    [MemberData(nameof(ListShapeTools))]
    public void ListShapeTool_HasCapParameterInInputSchema(string toolName)
    {
        // Any of these names satisfies the cap-arg requirement. Tools own
        // their cap vocabulary (rows vs results vs tables vs files vs ...),
        // but every list-shape tool must offer SOMETHING to bound the array.
        var capNames = new[] { "maxResults", "maxRows", "maxTables", "maxFiles", "maxHotspots", "maxBoundaries", "maxReadingOrder", "maxMatrixEntries", "maxSites", "maxDepth", "limit" };

        var tool = ToolRegistry.GetDefinitions().FirstOrDefault(d => d.Name == toolName);
        Assert.NotNull(tool);

        var schema = JsonSerializer.SerializeToElement(tool!.InputSchema);
        Assert.True(schema.TryGetProperty("properties", out var props),
            $"{toolName}: InputSchema must declare `properties`.");

        var declaredCap = capNames.FirstOrDefault(c => props.TryGetProperty(c, out _));
        Assert.NotNull(declaredCap);
    }

    /// <summary>
    /// Companion ratchet: the tool description SHOULD mention either
    /// `summarize` or `truncated` so callers reading the tool catalog know
    /// the shape. Not a hard wire contract — but a soft drift guard against
    /// list-shape tools that quietly land without documenting the shortcut.
    /// </summary>
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
            $"{toolName}: list-shape tool description SHOULD reference `summarize` or `truncat*` so the catalog surfaces the wire-shape shortcut. Current description: \"{description[..System.Math.Min(120, description.Length)]}…\"");
    }
}

using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-TELEMETRY-002. Pins the emitted-telemetry-event surface so a
/// new event is a deliberate edit and stays advertised through ServerIdentity.
/// </summary>
public class McpTelemetryEventsTests
{
    [Fact]
    public void All_IsNonEmptyAndDistinct()
    {
        Assert.NotEmpty(McpTelemetryEvents.All);
        Assert.Equal(McpTelemetryEvents.All.Length, McpTelemetryEvents.All.Distinct().Count());
    }

    [Fact]
    public void All_MatchesDocumentedSurface()
    {
        // Change-detector: adding/removing an emitted event must update this list
        // AND McpTelemetryEvents.All (which ServerIdentity advertises verbatim).
        var expected = new[]
        {
            "lifeblood.tool.success_result",
            "lifeblood.tool.error_result",
            "lifeblood.tool.exception",
            "lifeblood.tool.response_json",
            "lifeblood.tool.truncated",
            "lifeblood.tool.arguments",
            "lifeblood.analyze.result",
            "lifeblood.analyze.fallback",
            "lifeblood.analyze.phase",
            "lifeblood.cache.lookup",
        };

        Assert.Equal(expected, McpTelemetryEvents.All);
    }
}

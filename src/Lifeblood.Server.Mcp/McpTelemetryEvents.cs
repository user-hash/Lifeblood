namespace Lifeblood.Server.Mcp;

/// <summary>
/// Single source of truth for the telemetry event names emitted at the MCP
/// server edge and advertised through <c>lifeblood_capabilities</c>.
///
/// <para>INV-TELEMETRY-002: emit sites reference these constants and
/// <see cref="ServerIdentity"/> advertises exactly <see cref="All"/>, so an
/// emitted-but-unadvertised event cannot drift past <c>McpTelemetryEventsTests</c>.
/// <c>lifeblood.cache.lookup</c> is emitted from <c>Lifeblood.Connectors.Mcp</c>
/// (a lower assembly that cannot reference this type); its literal there mirrors
/// <see cref="CacheLookup"/> and is covered by the advertised-set ratchet.</para>
/// </summary>
public static class McpTelemetryEvents
{
    public const string ToolSuccessResult = "lifeblood.tool.success_result";
    public const string ToolErrorResult = "lifeblood.tool.error_result";
    public const string ToolException = "lifeblood.tool.exception";
    public const string ToolResponseJson = "lifeblood.tool.response_json";
    public const string ToolTruncated = "lifeblood.tool.truncated";
    public const string ToolArguments = "lifeblood.tool.arguments";
    public const string AnalyzeResult = "lifeblood.analyze.result";
    public const string AnalyzeFallback = "lifeblood.analyze.fallback";
    public const string AnalyzePhase = "lifeblood.analyze.phase";
    public const string CacheLookup = "lifeblood.cache.lookup";

    /// <summary>
    /// Every event name the server emits, in advertisement order.
    /// <see cref="ServerIdentity"/> publishes this array verbatim.
    /// </summary>
    public static readonly string[] All =
    {
        ToolSuccessResult,
        ToolErrorResult,
        ToolException,
        ToolResponseJson,
        ToolTruncated,
        ToolArguments,
        AnalyzeResult,
        AnalyzeFallback,
        AnalyzePhase,
        CacheLookup,
    };
}

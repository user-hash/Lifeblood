using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lifeblood.Server.Mcp;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonRpcRequest))]
internal sealed partial class McpJsonSerializerContext : JsonSerializerContext
{
    private static readonly ConditionalWeakTable<JsonSerializerOptions, McpJsonSerializerContext> Cache = new();

    public static McpJsonSerializerContext For(JsonSerializerOptions options)
        => Cache.GetValue(options, static opts => new McpJsonSerializerContext(new JsonSerializerOptions(opts)));
}

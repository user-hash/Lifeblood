using System.Text.Json.Serialization;

namespace Lifeblood.Server.Mcp;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonRpcRequest))]
public sealed partial class McpJsonSerializerContext : JsonSerializerContext
{
}

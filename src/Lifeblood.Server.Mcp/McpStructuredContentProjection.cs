using System.Text.Json;
using Lifeblood.Connectors.Mcp;

namespace Lifeblood.Server.Mcp;

/// <summary>Connection-local projection after declaration-schema selection; shared handler results are never mutated.</summary>
internal static class McpStructuredContentProjection
{
    internal const int MaximumProjectionCharacters = 2 * 1024 * 1024;

    internal static McpToolResult Project(McpToolResult source, string format)
    {
        if (format == McpProtocolSpec.ResultFormats.Text) return source;
        if (source.Content.Length != 1 || source.Content[0].Type != "text") return source;
        var text = source.Content[0].Text;
        if (text.Length > MaximumProjectionCharacters)
        {
            var metadata = source.Meta == null ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(source.Meta);
            metadata["lifeblood/structuredContent"] = new
            {
                status = "projectionLimitExceeded",
                maximumCharacters = MaximumProjectionCharacters,
                actualCharacters = text.Length,
                fallback = "Original text content is preserved. Use a scoped/compact tool projection for structured consumption.",
            };
            return new()
            {
                Content = source.Content,
                IsError = source.IsError,
                Meta = metadata,
            };
        }
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return source;
            return new()
            {
                Content = format == McpProtocolSpec.ResultFormats.StructuredOnly ? Array.Empty<McpContent>() : source.Content,
                IsError = source.IsError,
                StructuredContent = document.RootElement.Clone(),
                Meta = source.Meta,
            };
        }
        catch (JsonException) { return source; }
    }
}

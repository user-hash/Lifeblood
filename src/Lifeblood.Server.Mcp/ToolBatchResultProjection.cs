using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Lifeblood.Domain.Workspaces;

namespace Lifeblood.Server.Mcp;

internal sealed record ToolBatchResponse(
    string Execution, int CallCount, string SnapshotId, long AnalysisGeneration,
    string WorkspaceRoot, WorkspaceAnalysisDescriptor? AnalysisIdentity, ToolBatchResultRow[] Results)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseFormat { get; init; }
}

internal sealed record ToolBatchResultRow(int Index, string Tool, bool IsError)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public McpContent[]? Content { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Payload { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Evidence { get; init; }
}

/// <summary>
/// Wire-only projection of already dispatched results. Compact rows share only
/// equal publication identity; tool-specific evidence and opaque content survive.
/// </summary>
internal static class ToolBatchResultProjection
{
    internal static JsonObject ProjectResponse(ToolBatchResponse response, JsonObject sharedIdentity,
        JsonSerializerOptions options)
    {
        var payload = JsonSerializer.SerializeToNode(response, options)!.AsObject();
        if (response.ResponseFormat == "compact") RemoveSharedIdentity(payload, sharedIdentity);
        return payload;
    }

    internal static ToolBatchResultRow Project(int index, string tool, McpToolResult result,
        bool compact, JsonObject sharedIdentity)
    {
        var row = new ToolBatchResultRow(index, tool, result.IsError == true);
        if (!compact || result.Content.Length != 1 || result.Content[0].Type != "text")
            return row with { Content = result.Content };

        JsonObject? payload;
        try { payload = JsonNode.Parse(result.Content[0].Text) as JsonObject; }
        catch (JsonException) { payload = null; }
        if (payload == null) return row with { Content = result.Content };

        var evidence = payload["envelope"] as JsonObject;
        if (evidence != null)
        {
            payload.Remove("envelope");
            RemoveSharedIdentity(evidence, sharedIdentity);
        }
        RemoveSharedIdentity(payload, sharedIdentity);
        return row with { Payload = payload, Evidence = evidence };
    }

    private static void RemoveSharedIdentity(JsonObject payload, JsonObject sharedIdentity)
    {
        foreach (var field in sharedIdentity)
            if (payload.TryGetPropertyValue(field.Key, out var value) && JsonNode.DeepEquals(value, field.Value))
                payload.Remove(field.Key);
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Domain.Graph;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// Projects declaration-address fields while their publication is leased.
/// Code, documentation, paths and arbitrary string values remain opaque.
/// The producer supplies predecessor addresses; this edge owns no ID grammar.
/// Batch result rows already carry their prepared child projection and are terminal
/// here, including canonical candidate guidance that must not be projected twice.
/// </summary>
internal static class McpCanonicalIdCompatibility
{
    internal static readonly string LegacyNotice = $"This response uses schema v1. Opt in with {Lifeblood.Connectors.Mcp.McpProtocolSpec.SchemaVersions.InitializeParameter}: {Lifeblood.Connectors.Mcp.McpProtocolSpec.SchemaVersions.Canonical} for collision-safe declaration IDs. V1 remains supported for at least one full minor release after v2 ships; its removal release has not been announced.";

    private static readonly HashSet<string> AddressFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "canonicalId", "parentId", "typeId", "fieldId", "methodId", "memberId", "sourceId", "targetId", "otherEndId",
        "symbols", "dependencies", "dependants", "implementations", "candidates", "ambiguousTypeCandidates",
        "affected", "affectedSymbols", "cycles", "roots", "path", "hotspots", "readingOrder",
    };

    private static bool IsAddressField(string name)
        => name.EndsWith("SymbolId", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("SymbolIds", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("TypeIds", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("MethodIds", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("FieldIds", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("TypeId", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("MethodId", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("FieldId", StringComparison.OrdinalIgnoreCase) || AddressFields.Contains(name);

    internal static JsonElement? NormalizeInputs(JsonElement? arguments, SemanticGraph? graph, ISymbolResolver resolver, bool skipManifest = true)
    {
        if (arguments == null || graph == null) return arguments;
        var node = JsonNode.Parse(arguments.Value.GetRawText());
        Visit(node, (owner, name, value) =>
        {
            if (!IsAddressField(name) || graph.FindByPredecessorId(value).Count == 0) return value;
            var resolved = resolver.Resolve(graph, value);
            if (resolved.CanonicalId != null) return resolved.CanonicalId;
            throw new CanonicalAddressAmbiguityException(value, resolved.Candidates);
        }, skipBatchCalls: true, skipManifest: skipManifest);
        return JsonSerializer.SerializeToElement(node);
    }

    internal static McpToolResult PrepareLegacyResult(
        string toolName, McpToolResult source, SemanticGraph? graph,
        Func<string, string?>? predecessorLookup, JsonSerializerOptions options, bool projectUnresolvedAddresses = true)
    {
        if (source.LegacyProjection != null) return source;
        var ambiguous = new Dictionary<string, string>(StringComparer.Ordinal);
        var unavailable = new HashSet<string>(StringComparer.Ordinal);
        var predecessors = new Dictionary<string, string?>(StringComparer.Ordinal);
        var content = new List<McpContent>();
        foreach (var item in source.Content)
        {
            if (item.Type != "text") { content.Add(item); continue; }
            JsonObject? payload;
            try { payload = JsonNode.Parse(item.Text) as JsonObject; }
            catch (JsonException) { payload = null; }
            if (payload == null)
            {
                content.Add(item);
                var notice = new JsonObject();
                AddLegacyNotice(notice);
                content.Add(new McpContent { Type = "text", Text = notice.ToJsonString(options) });
                continue;
            }
            Visit(payload, (owner, name, value) =>
            {
                if (!IsAddressField(name) && !(name.Equals("id", StringComparison.OrdinalIgnoreCase) && IsSymbolObject(owner)))
                    return value;
                if (!predecessors.TryGetValue(value, out var predecessor))
                {
                    var symbol = graph?.GetSymbol(value);
                    predecessor = symbol != null && symbol.Properties.TryGetValue(SymbolPropertyKeys.LegacyCanonicalId, out var recorded)
                        ? recorded : symbol != null ? value : predecessorLookup?.Invoke(value);
                    predecessors[value] = predecessor;
                }
                if (predecessor == null)
                {
                    if (projectUnresolvedAddresses && value.Contains('|')) unavailable.Add(value);
                    return value;
                }
                if (predecessor == value) return value;
                if (graph?.FindByPredecessorId(predecessor).Count > 1) ambiguous[value] = predecessor;
                return predecessor;
            }, skipBatchResults: toolName == "lifeblood_batch");
            if (ambiguous.Count > 0 || unavailable.Count > 0)
            {
                payload = new JsonObject
                {
                    ["error"] = true,
                    ["failure"] = unavailable.Count > 0 ? "legacy-canonical-id-unavailable" : "legacy-canonical-id-ambiguity",
                    ["tool"] = toolName,
                    ["message"] = "Schema v1 cannot safely address these declarations. Initialize with schema v2 and choose a canonical candidate.",
                    ["unavailableCanonicalIds"] = JsonSerializer.SerializeToNode(unavailable.OrderBy(id => id, StringComparer.Ordinal), options),
                    ["canonicalCandidates"] = JsonSerializer.SerializeToNode(ambiguous.OrderBy(p => p.Key, StringComparer.Ordinal)
                        .Select(p => new { canonicalId = p.Key, legacyCanonicalId = p.Value }), options),
                    ["envelope"] = payload["envelope"]?.DeepClone(),
                };
            }
            AddLegacyNotice(payload);
            content.Add(new McpContent { Type = item.Type, Text = payload.ToJsonString(options) });
        }
        source.LegacyProjection = new McpToolResult { Content = content.ToArray(), IsError = ambiguous.Count > 0 || unavailable.Count > 0 ? true : source.IsError };
        return source;
    }

    internal static void AddLegacyNotice(JsonObject payload)
    {
        var owner = payload["envelope"] as JsonObject ?? payload;
        if (owner["limitations"] is not JsonArray limitations)
            owner["limitations"] = limitations = new JsonArray();
        if (!limitations.Any(n => n?.GetValue<string>() == LegacyNotice)) limitations.Add(LegacyNotice);
    }

    private static bool IsSymbolObject(JsonObject owner)
        => owner["kind"] is JsonValue kind && kind.TryGetValue<string>(out var value)
            && Enum.TryParse<SymbolKind>(value, out _) && (owner.ContainsKey("name") || owner.ContainsKey("qualifiedName"));

    private static void Visit(JsonNode? node, Func<JsonObject, string, string, string> project, bool skipBatchCalls = false, bool skipManifest = false,
        bool skipBatchResults = false,
        JsonObject? owner = null, string field = "")
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                if (property.Key.Equals("properties", StringComparison.OrdinalIgnoreCase)
                    || (skipBatchCalls && property.Key.Equals("calls", StringComparison.OrdinalIgnoreCase))
                    || (skipBatchResults && property.Key.Equals("results", StringComparison.OrdinalIgnoreCase))
                    || (skipManifest && property.Key.Equals("manifest", StringComparison.OrdinalIgnoreCase))) continue;
                if (property.Value is JsonValue value && value.TryGetValue<string>(out var text))
                    obj[property.Key] = project(obj, property.Key, text);
                else Visit(property.Value, project, skipBatchCalls, skipManifest, skipBatchResults, obj, property.Key);
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (owner != null && array[index] is JsonValue value && value.TryGetValue<string>(out var text))
                    array[index] = project(owner, field, text);
                else Visit(array[index], project, skipBatchCalls, skipManifest, skipBatchResults, owner, field);
            }
        }
    }

    internal sealed class CanonicalAddressAmbiguityException(string input, string[] candidates) : Exception("The predecessor address identifies multiple declarations.")
    {
        internal string Input { get; } = input;
        internal string[] Candidates { get; } = candidates;
    }
}

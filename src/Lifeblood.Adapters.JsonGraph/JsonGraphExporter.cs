using System.Text.Json;
using System.Text.Json.Serialization;
using Lifeblood.Application.Ports.GraphIO;
using Lifeblood.Domain.Graph;

namespace Lifeblood.Adapters.JsonGraph;

/// <summary>
/// Exports a SemanticGraph to JSON conforming to schemas/graph.schema.json.
/// </summary>
public sealed class JsonGraphExporter : IGraphExporter
{
    public string Format => "json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    public void Export(SemanticGraph graph, Stream destination)
    {
        var doc = new
        {
            version = "1.0",
            symbols = graph.Symbols.Select(s => new
            {
                id = s.Id,
                name = s.Name,
                qualifiedName = s.QualifiedName,
                kind = s.Kind.ToString().ToLowerInvariant(),
                filePath = s.FilePath,
                line = s.Line,
                parentId = s.ParentId,
                visibility = s.Visibility.ToString().ToLowerInvariant(),
                isAbstract = s.IsAbstract,
                isStatic = s.IsStatic,
                properties = s.Properties.Count > 0 ? s.Properties : null,
            }),
            edges = graph.Edges.Select(e => new
            {
                sourceId = e.SourceId,
                targetId = e.TargetId,
                kind = e.Kind.ToString().Substring(0, 1).ToLowerInvariant() + e.Kind.ToString().Substring(1),
                evidence = new
                {
                    kind = e.Evidence.Kind.ToString().ToLowerInvariant(),
                    adapterName = e.Evidence.AdapterName,
                    sourceSpan = e.Evidence.SourceSpan,
                    confidence = e.Evidence.Confidence,
                },
                properties = e.Properties.Count > 0 ? e.Properties : null,
            }),
        };

        JsonSerializer.Serialize(destination, doc, Options);
    }
}

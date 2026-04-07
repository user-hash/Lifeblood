using System.Text.Json;
using System.Text.Json.Serialization;
using Lifeblood.Application.Ports.GraphIO;
using Lifeblood.Domain.Graph;

namespace Lifeblood.Adapters.JsonGraph;

/// <summary>
/// Exports a SemanticGraph to JSON conforming to schemas/graph.schema.json.
/// Uses the same DTO classes as JsonGraphImporter for round-trip fidelity.
/// </summary>
public sealed class JsonGraphExporter : IGraphExporter
{
    public const string SchemaVersion = "1.0";

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
        var doc = new JsonGraphDocument
        {
            Version = SchemaVersion,
            Symbols = graph.Symbols.Select(s => new JsonSymbol
            {
                Id = s.Id,
                Name = s.Name,
                QualifiedName = s.QualifiedName,
                Kind = s.Kind,
                FilePath = s.FilePath,
                Line = s.Line,
                ParentId = s.ParentId,
                Visibility = s.Visibility,
                IsAbstract = s.IsAbstract,
                IsStatic = s.IsStatic,
                Properties = s.Properties.Count > 0
                    ? new Dictionary<string, string>(s.Properties)
                    : null,
            }).ToArray(),
            Edges = graph.Edges.Select(e => new JsonEdge
            {
                SourceId = e.SourceId,
                TargetId = e.TargetId,
                Kind = e.Kind,
                Evidence = new JsonEvidence
                {
                    Kind = e.Evidence.Kind,
                    AdapterName = e.Evidence.AdapterName,
                    SourceSpan = e.Evidence.SourceSpan,
                    Confidence = e.Evidence.Confidence,
                },
                Properties = e.Properties.Count > 0
                    ? new Dictionary<string, string>(e.Properties)
                    : null,
            }).ToArray(),
        };

        JsonSerializer.Serialize(destination, doc, Options);
    }
}

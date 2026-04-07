using System.Text.Json;
using System.Text.Json.Serialization;
using Lifeblood.Application.Ports.GraphIO;
using Lifeblood.Domain.Graph;

namespace Lifeblood.Adapters.JsonGraph;

/// <summary>
/// Exports a GraphDocument to JSON conforming to schemas/graph.schema.json.
/// Preserves protocol metadata: version, language, adapter capabilities.
/// Uses the same DTO classes as JsonGraphImporter for round-trip fidelity.
/// </summary>
public sealed class JsonGraphExporter : IGraphExporter
{
    public string Format => "json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Export(GraphDocument document, Stream destination)
    {
        var graph = document.Graph;
        var adapter = document.Adapter;

        var doc = new JsonGraphDocument
        {
            Version = document.Version,
            Language = !string.IsNullOrEmpty(document.Language) ? document.Language : null,
            Adapter = adapter != null ? new JsonAdapter
            {
                Name = adapter.AdapterName,
                Version = adapter.AdapterVersion,
                Capabilities = new JsonCapabilities
                {
                    DiscoverSymbols = adapter.CanDiscoverSymbols,
                    TypeResolution = adapter.TypeResolution,
                    CallResolution = adapter.CallResolution,
                    ImplementationResolution = adapter.ImplementationResolution,
                    CrossModuleReferences = adapter.CrossModuleReferences,
                    OverrideResolution = adapter.OverrideResolution,
                },
            } : null,
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
                    ? new Dictionary<string, string>(s.Properties) : null,
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
                    ? new Dictionary<string, string>(e.Properties) : null,
            }).ToArray(),
        };

        JsonSerializer.Serialize(destination, doc, Options);
    }
}

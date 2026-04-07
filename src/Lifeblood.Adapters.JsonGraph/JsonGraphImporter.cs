using System.Text.Json;
using System.Text.Json.Serialization;
using Lifeblood.Application.Ports.GraphIO;
using Lifeblood.Domain.Graph;

namespace Lifeblood.Adapters.JsonGraph;

/// <summary>
/// Imports a SemanticGraph from JSON conforming to schemas/graph.schema.json.
/// INV-ADAPT-003: External adapters communicate via JSON graph schema only.
/// </summary>
public sealed class JsonGraphImporter : IGraphImporter
{
    public string Format => "json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public SemanticGraph Import(Stream source)
    {
        var doc = JsonSerializer.Deserialize<JsonGraphDocument>(source, Options)
            ?? throw new InvalidOperationException("Failed to deserialize graph JSON");

        var builder = new GraphBuilder();

        // Import symbols
        if (doc.Symbols != null)
        {
            foreach (var js in doc.Symbols)
            {
                builder.AddSymbol(new Symbol
                {
                    Id = js.Id ?? "",
                    Name = js.Name ?? "",
                    QualifiedName = js.QualifiedName ?? "",
                    Kind = js.Kind,
                    FilePath = js.FilePath ?? "",
                    Line = js.Line,
                    ParentId = js.ParentId ?? "",
                    Visibility = js.Visibility,
                    IsAbstract = js.IsAbstract,
                    IsStatic = js.IsStatic,
                    Properties = js.Properties ?? new(),
                });
            }
        }

        // Import edges (explicit — GraphBuilder will add Contains from ParentId)
        if (doc.Edges != null)
        {
            foreach (var je in doc.Edges)
            {
                builder.AddEdge(new Edge
                {
                    SourceId = je.SourceId ?? "",
                    TargetId = je.TargetId ?? "",
                    Kind = je.Kind,
                    Evidence = je.Evidence != null
                        ? new Evidence
                        {
                            Kind = je.Evidence.Kind,
                            AdapterName = je.Evidence.AdapterName ?? "",
                            SourceSpan = je.Evidence.SourceSpan ?? "",
                            Confidence = je.Evidence.Confidence,
                        }
                        : Evidence.Default,
                    Properties = je.Properties ?? new(),
                });
            }
        }

        return builder.Build();
    }
}

// JSON DTOs — match schemas/graph.schema.json (camelCase)

internal sealed class JsonGraphDocument
{
    public string? Version { get; set; }
    public string? Language { get; set; }
    public JsonSymbol[]? Symbols { get; set; }
    public JsonEdge[]? Edges { get; set; }
}

internal sealed class JsonSymbol
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? QualifiedName { get; set; }
    public SymbolKind Kind { get; set; }
    public string? FilePath { get; set; }
    public int Line { get; set; }
    public string? ParentId { get; set; }
    public Visibility Visibility { get; set; }
    public bool IsAbstract { get; set; }
    public bool IsStatic { get; set; }
    public Dictionary<string, string>? Properties { get; set; }
}

internal sealed class JsonEdge
{
    public string? SourceId { get; set; }
    public string? TargetId { get; set; }
    public EdgeKind Kind { get; set; }
    public JsonEvidence? Evidence { get; set; }
    public Dictionary<string, string>? Properties { get; set; }
}

internal sealed class JsonEvidence
{
    public EvidenceKind Kind { get; set; }
    public string? AdapterName { get; set; }
    public string? SourceSpan { get; set; }
    public float Confidence { get; set; } = 1.0f;
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Lifeblood.Application.Ports.GraphIO;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;

namespace Lifeblood.Adapters.JsonGraph;

/// <summary>
/// Imports a GraphDocument from JSON conforming to schemas/graph.schema.json.
/// Preserves protocol metadata: version, language, adapter capabilities.
/// INV-ADAPT-003: External adapters communicate via JSON graph schema only.
/// </summary>
public sealed class JsonGraphImporter : IGraphImporter
{
    public string Format => "json";

    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public GraphDocument ImportDocument(Stream source)
    {
        var doc = JsonSerializer.Deserialize<JsonGraphDocument>(source, Options)
            ?? throw new InvalidOperationException("Failed to deserialize graph JSON");

        var builder = new GraphBuilder();

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

        // Map adapter metadata if present
        AdapterCapability? capability = null;
        if (doc.Adapter != null)
        {
            capability = new AdapterCapability
            {
                Language = doc.Language ?? "",
                AdapterName = doc.Adapter.Name ?? "",
                AdapterVersion = doc.Adapter.Version ?? "",
                CanDiscoverSymbols = doc.Adapter.Capabilities?.DiscoverSymbols ?? false,
                TypeResolution = doc.Adapter.Capabilities?.TypeResolution ?? ConfidenceLevel.None,
                CallResolution = doc.Adapter.Capabilities?.CallResolution ?? ConfidenceLevel.None,
                ImplementationResolution = doc.Adapter.Capabilities?.ImplementationResolution ?? ConfidenceLevel.None,
                CrossModuleReferences = doc.Adapter.Capabilities?.CrossModuleReferences ?? ConfidenceLevel.None,
                OverrideResolution = doc.Adapter.Capabilities?.OverrideResolution ?? ConfidenceLevel.None,
            };
        }

        return new GraphDocument
        {
            Version = doc.Version ?? GraphDocument.CurrentVersion,
            Language = doc.Language ?? "",
            Adapter = capability,
            Graph = builder.Build(),
        };
    }
}

// JSON DTOs — match schemas/graph.schema.json (camelCase)

internal sealed class JsonGraphDocument
{
    public string? Version { get; set; }
    public string? Language { get; set; }
    public JsonAdapter? Adapter { get; set; }
    public JsonSymbol[]? Symbols { get; set; }
    public JsonEdge[]? Edges { get; set; }
}

internal sealed class JsonAdapter
{
    public string? Name { get; set; }
    public string? Version { get; set; }
    public JsonCapabilities? Capabilities { get; set; }
}

internal sealed class JsonCapabilities
{
    public bool DiscoverSymbols { get; set; }
    public ConfidenceLevel TypeResolution { get; set; }
    public ConfidenceLevel CallResolution { get; set; }
    public ConfidenceLevel ImplementationResolution { get; set; }
    public ConfidenceLevel CrossModuleReferences { get; set; }
    public ConfidenceLevel OverrideResolution { get; set; }
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
    public ConfidenceLevel Confidence { get; set; } = ConfidenceLevel.Proven;
}

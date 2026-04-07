using System.Text.Json;
using Lifeblood.Domain.Rules;

namespace Lifeblood.CLI;

/// <summary>
/// Loads architecture rules from a JSON file conforming to schemas/rules.schema.json.
/// </summary>
internal static class RulesLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static ArchitectureRule[] Load(string path)
    {
        var json = File.ReadAllText(path);
        var doc = JsonSerializer.Deserialize<RulesDocument>(json, Options);
        return doc?.Rules ?? Array.Empty<ArchitectureRule>();
    }

    private sealed class RulesDocument
    {
        public ArchitectureRule[]? Rules { get; set; }
    }
}

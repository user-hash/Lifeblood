using System.Text.Json;
using Lifeblood.Application.Ports.Analysis;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Domain.Rules;

namespace Lifeblood.CLI;

/// <summary>
/// Loads architecture rules from a JSON file conforming to schemas/rules.schema.json.
/// Implements IRuleProvider — previously a static class with direct File.ReadAllText.
/// </summary>
internal sealed class RulesLoader : IRuleProvider
{
    private readonly IFileSystem _fs;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public RulesLoader(IFileSystem fs) => _fs = fs;

    public ArchitectureRule[] LoadRules(string path)
    {
        var json = _fs.ReadAllText(path);
        var doc = JsonSerializer.Deserialize<RulesDocument>(json, Options);
        return doc?.Rules ?? Array.Empty<ArchitectureRule>();
    }

    private sealed class RulesDocument
    {
        public ArchitectureRule[]? Rules { get; set; }
    }
}

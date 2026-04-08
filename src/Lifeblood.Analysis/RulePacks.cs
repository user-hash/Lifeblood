using System.Text.Json;
using Lifeblood.Domain.Rules;

namespace Lifeblood.Analysis;

/// <summary>
/// Central rule pack resolution. Built-in packs are embedded as assembly resources.
/// Also provides shared JSON parsing so consumers don't duplicate deserialization.
///
/// Architecture: Analysis depends only on Domain. This class uses no I/O —
/// embedded resources come from the assembly, JSON parsing is pure string→object.
/// File I/O stays in composition roots (CLI, Server.Mcp).
/// </summary>
public static class RulePacks
{
    private static readonly string[] Names = ["hexagonal", "clean-architecture", "lifeblood"];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Available built-in pack names. Use with <c>--rules hexagonal</c> etc.
    /// </summary>
    public static IReadOnlyList<string> BuiltIn => Names;

    /// <summary>
    /// Resolves a built-in pack by name. Returns null if not a known pack.
    /// </summary>
    public static ArchitectureRule[]? ResolveBuiltIn(string name)
    {
        var stream = typeof(RulePacks).Assembly.GetManifestResourceStream($"RulePacks.{name}");
        if (stream == null) return null;

        using (stream)
            return JsonSerializer.Deserialize<RulesDocument>(stream, JsonOpts)?.Rules;
    }

    /// <summary>
    /// Parses a rules JSON string into an array of architecture rules.
    /// Expects the standard schema: <c>{"rules": [...]}</c>.
    /// </summary>
    public static ArchitectureRule[]? ParseJson(string json)
    {
        return JsonSerializer.Deserialize<RulesDocument>(json, JsonOpts)?.Rules;
    }

    private sealed class RulesDocument
    {
        public ArchitectureRule[]? Rules { get; set; }
    }
}

using System.Text.Json;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Domain.Rules;
using Lifeblood.Domain.Workspaces;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// Composition-boundary rule resolution. File I/O and built-in pack lookup
/// happen once; the resulting rules and their semantic identity cannot drift.
/// </summary>
internal sealed class AnalysisRuleSetResolver
{
    private static readonly JsonSerializerOptions CanonicalJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IFileSystem _fileSystem;

    public AnalysisRuleSetResolver(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    public ResolvedRuleSet Resolve(string? requestedSource, string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(requestedSource))
            return ResolvedRuleSet.None;

        var source = requestedSource.Trim();
        var builtIn = Lifeblood.Analysis.RulePacks.ResolveBuiltIn(source);
        if (builtIn != null)
            return Create(builtIn, RuleSetSourceKind.BuiltIn, source);

        if (!Path.IsPathRooted(source) && string.IsNullOrWhiteSpace(workspaceRoot))
            throw new InvalidOperationException("A relative rules path requires an explicit workspace root.");

        var path = Path.IsPathRooted(source)
            ? Path.GetFullPath(source)
            : Path.GetFullPath(Path.Combine(workspaceRoot!, source));
        if (!_fileSystem.FileExists(path))
            throw new FileNotFoundException(
                $"Rules source '{requestedSource}' is neither a built-in pack nor an existing file.",
                path);

        var rules = Lifeblood.Analysis.RulePacks.ParseJson(_fileSystem.ReadAllText(path));
        if (rules == null)
            throw new InvalidDataException($"Rules file '{path}' does not contain a rules array.");

        return Create(rules, RuleSetSourceKind.File, path);
    }

    private static ResolvedRuleSet Create(
        ArchitectureRule[] rules,
        RuleSetSourceKind sourceKind,
        string source)
    {
        var canonical = JsonSerializer.Serialize(rules, CanonicalJson);
        var content = ContentFingerprint.ComputeUtf8("lifeblood.rules-content.v1", canonical);
        return new ResolvedRuleSet(
            rules,
            new RuleSetIdentity(sourceKind, source, content));
    }

    internal sealed class ResolvedRuleSet
    {
        private static readonly ContentFingerprint EmptyContent = ContentFingerprint.ComputeUtf8(
            "lifeblood.rules-content.v1",
            JsonSerializer.Serialize(Array.Empty<ArchitectureRule>(), CanonicalJson));

        public static ResolvedRuleSet None { get; } = new(
            rules: null,
            new RuleSetIdentity(RuleSetSourceKind.None, source: null, EmptyContent));

        public ResolvedRuleSet(ArchitectureRule[]? rules, RuleSetIdentity identity)
        {
            Rules = rules;
            Identity = identity;
        }

        public ArchitectureRule[]? Rules { get; }

        public RuleSetIdentity Identity { get; }

        public string? Source => Identity.Source;
    }
}

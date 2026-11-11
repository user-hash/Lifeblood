namespace Lifeblood.Domain.Results;

/// <summary>Options and reference observations from one retained compilation; no build or runtime execution.</summary>
public sealed class CompilationEnvironmentReport
{
    public string Status { get; init; } = "Unsupported";
    public string ModuleName { get; init; } = "";
    public CompilationFileOwnership FileOwnership { get; init; } = new();
    public IReadOnlyDictionary<string, string> Compiler { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
    public CompilationParseEnvironment[] ParseEnvironments { get; init; } = Array.Empty<CompilationParseEnvironment>();
    public int ParseEnvironmentCount { get; init; }
    public int OmittedParseEnvironmentCount => ParseEnvironmentCount - ParseEnvironments.Length;
    public CompilationReferenceEnvironment[] References { get; init; } = Array.Empty<CompilationReferenceEnvironment>();
    public int ReferenceCount { get; init; }
    public int OmittedReferenceCount => ReferenceCount - References.Length;
    public IReadOnlyDictionary<string, string> DiagnosticOptions { get; init; } = new Dictionary<string, string>();
    public int DiagnosticOptionCount { get; init; }
    public int OmittedDiagnosticOptionCount => DiagnosticOptionCount - DiagnosticOptions.Count;
}

public sealed class CompilationParseEnvironment
{
    public int SourceTreeCount { get; init; }
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
    public string[] DefinesActive { get; init; } = Array.Empty<string>();
    public int DefinesActiveCount { get; init; }
    public int OmittedDefineCount => DefinesActiveCount - DefinesActive.Length;
    public IReadOnlyDictionary<string, string> Features { get; init; } = new Dictionary<string, string>();
    public int FeatureCount { get; init; }
    public int OmittedFeatureCount => FeatureCount - Features.Count;
}

public sealed class CompilationReferenceEnvironment
{
    public string Kind { get; init; } = "";
    public string? Display { get; init; }
    public string? FilePath { get; init; }
    public bool EmbedInteropTypes { get; init; }
    public string[] Aliases { get; init; } = Array.Empty<string>();
    public int AliasCount { get; init; }
    public int OmittedAliasCount => AliasCount - Aliases.Length;
}

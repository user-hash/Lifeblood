namespace Lifeblood.Domain.Capabilities;

/// <summary>
/// What a language adapter can do for graph extraction. Declared honestly.
/// INV-ADAPT-001: Every adapter declares capabilities honestly.
/// </summary>
public sealed class AdapterCapability
{
    public string Language { get; init; } = "";
    public string AdapterName { get; init; } = "";
    public string AdapterVersion { get; init; } = "";
    public bool CanDiscoverSymbols { get; init; }
    public ConfidenceLevel TypeResolution { get; init; }
    public ConfidenceLevel CallResolution { get; init; }
    public ConfidenceLevel ImplementationResolution { get; init; }
    public ConfidenceLevel CrossModuleReferences { get; init; }
    public ConfidenceLevel OverrideResolution { get; init; }
}

/// <summary>
/// What workspace-level operations are available after loading a project.
/// Separate from AdapterCapability because these are session-dependent.
/// A JSON graph load has no workspace ops; a Roslyn project load has all of them.
/// </summary>
public sealed class WorkspaceCapability
{
    public bool CanDiagnose { get; init; }
    public bool CanCompileCheck { get; init; }
    public bool CanFindReferences { get; init; }
    public bool CanRename { get; init; }
    public bool CanFormat { get; init; }
    public bool CanExecute { get; init; }

    /// <summary>
    /// Trust level for code execution.
    /// "trusted-local" = in-process, blocklist + AST checks, suitable for local dogfooding.
    /// "process-isolated" = separate process, real kill timeout, filesystem/network restrictions.
    /// </summary>
    public string ExecutionTrustLevel { get; init; } = "none";

    public static readonly WorkspaceCapability None = new();

    public static readonly WorkspaceCapability RoslynFull = new()
    {
        CanDiagnose = true,
        CanCompileCheck = true,
        CanFindReferences = true,
        CanRename = true,
        CanFormat = true,
        CanExecute = true,
        ExecutionTrustLevel = "trusted-local",
    };
}

public enum ConfidenceLevel
{
    None,
    BestEffort,
    Proven,
}

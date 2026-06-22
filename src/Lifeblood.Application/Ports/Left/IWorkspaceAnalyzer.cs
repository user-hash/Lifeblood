using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;

namespace Lifeblood.Application.Ports.Left;

/// <summary>
/// PRIMARY adapter port. Left side. Workspace-scoped.
/// INV-PORT-001: The primary contract is workspace → graph.
/// </summary>
public interface IWorkspaceAnalyzer
{
    SemanticGraph AnalyzeWorkspace(string projectRoot, AnalysisConfig config);
    AdapterCapability Capability { get; }
}

public sealed class AnalysisConfig
{
    public string[] ExcludePatterns { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Optional anchored glob filters matched against project-relative POSIX
    /// paths before a source file enters Roslyn compilation. Grammar mirrors
    /// <c>lifeblood_dead_code pathExclude</c>: <c>*</c> matches any run
    /// including <c>/</c>, <c>?</c> matches one character, all other
    /// characters are literal. INV-ANALYZE-EXCLUDEPATHS-001.
    /// </summary>
    public string[] ExcludePathGlobs { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Optional authoritative changed-file set supplied by an editor/build
    /// integration. When non-null, incremental analyze bounds its source-file
    /// scan to these project-relative or absolute paths instead of deriving
    /// candidates from every tracked file's mtime. Content hashes still decide
    /// whether a listed source file actually needs graph replacement.
    /// </summary>
    public string[]? AuthoritativeChangedFiles { get; init; }

    /// <summary>
    /// When true, the adapter retains full CSharpCompilation objects after graph extraction.
    /// Required for write-side tools (FindReferences, Rename, Execute, CompileCheck).
    /// When false (default), compilations are downgraded to lightweight metadata references
    /// after extraction — dramatically reducing memory for large workspaces.
    /// </summary>
    public bool RetainCompilations { get; init; }

    /// <summary>
    /// Policy for incremental re-analyze when the adapter detects drift it
    /// cannot honor cheaply (module set changed, descriptor file edited, no
    /// prior cache). Default <c>false</c>: the adapter returns
    /// <see cref="IncrementalAnalyzeResult"/> with
    /// <see cref="IncrementalMode.Rejected"/> and a
    /// <see cref="FallbackReason"/> so the caller decides whether to widen
    /// scope explicitly. <c>true</c>: the adapter silently widens to a full
    /// re-analyze and returns <see cref="IncrementalMode.FullFallback"/>
    /// with the reason populated so the caller still sees what happened.
    ///
    /// Eternal-repo posture: scope-widening is a policy decision and lives
    /// with the caller, not the adapter. Internal callers like the auto-
    /// refresh-if-stale path deliberately opt in (their contract is "make
    /// state fresh"); user-facing callers receive the rejection and surface
    /// it so the agent can reason about cache miss patterns.
    ///
    /// INV-ANALYZE-FALLBACK-001.
    /// </summary>
    public bool AllowFullFallback { get; init; }

    /// <summary>
    /// INV-MULTI-DEFINE-ANALYZE-001. Define-profile names to analyze under.
    /// Null or empty = single-profile back-compat (default Editor identity).
    /// Non-empty = multi-profile union analyze: the adapter resolves each
    /// name against the injected <see cref="IDefineProfileResolver"/>,
    /// compiles every module once per active profile, extracts edges per
    /// profile, attributes the active profile name, and unions edge profile
    /// sets at the graph-builder dedup seam. INV-MULTI-DEFINE-EDGE-PROFILES-001.
    /// </summary>
    public string[]? DefineProfiles { get; init; }
}

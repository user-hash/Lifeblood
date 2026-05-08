using Lifeblood.Domain.Graph;

namespace Lifeblood.Application.Ports.Left;

/// <summary>
/// Result of an adapter's incremental re-analyze attempt. Replaces the
/// pre-INV-ANALYZE-FALLBACK-001 tuple <c>(SemanticGraph, int)</c>, which
/// silently widened to a full re-analyze when the adapter detected
/// drift (module set changed, project descriptor edited, etc).
///
/// Wire-shape carries three fields the caller needs to make a clean
/// policy decision without consulting adapter internals:
/// <list type="bullet">
///   <item><see cref="Mode"/> — what the adapter actually did. The
///         caller must inspect this before reading <see cref="Graph"/>.</item>
///   <item><see cref="Reason"/> — why incremental could not be honored
///         (populated when <see cref="Mode"/> is anything other than
///         <see cref="IncrementalMode.Incremental"/>).</item>
///   <item><see cref="Detail"/> — optional human-readable adapter-specific
///         scope hint (e.g. "asmdef edit detected") so the caller can
///         render a useful diagnostic without parsing the reason enum.</item>
/// </list>
///
/// <see cref="Graph"/> is null when <see cref="Mode"/> is
/// <see cref="IncrementalMode.Rejected"/>, populated otherwise. Callers
/// MUST guard. INV-ANALYZE-FALLBACK-001.
/// </summary>
public sealed record IncrementalAnalyzeResult
{
    public required IncrementalMode Mode { get; init; }

    /// <summary>The updated graph. Non-null when <see cref="Mode"/> is
    /// <see cref="IncrementalMode.Incremental"/> or
    /// <see cref="IncrementalMode.FullFallback"/>; null when
    /// <see cref="IncrementalMode.Rejected"/>.</summary>
    public SemanticGraph? Graph { get; init; }

    /// <summary>Number of source files that triggered re-extraction. For
    /// the full-fallback path this counts every tracked file (the entire
    /// workspace is re-analyzed). For Rejected this is 0.</summary>
    public int ChangedFileCount { get; init; }

    /// <summary>Why incremental was downgraded. Populated when
    /// <see cref="Mode"/> is <see cref="IncrementalMode.FullFallback"/>
    /// or <see cref="IncrementalMode.Rejected"/>. Null on the happy path.</summary>
    public FallbackReason? Reason { get; init; }

    /// <summary>Optional adapter-specific human-readable detail, e.g.
    /// the descriptor file kind (asmdef / csproj / pyproject) that
    /// triggered the fallback. Free-form, never structurally parsed.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// What the adapter actually did when asked for an incremental re-analyze.
/// </summary>
public enum IncrementalMode
{
    /// <summary>Incremental walk completed cleanly. <see cref="IncrementalAnalyzeResult.Graph"/>
    /// reflects the updated state. <see cref="IncrementalAnalyzeResult.Reason"/> is null.</summary>
    Incremental,

    /// <summary>Adapter detected drift but the caller had set
    /// <see cref="AnalysisConfig.AllowFullFallback"/>=true, so the adapter
    /// widened to a full re-analyze. <see cref="IncrementalAnalyzeResult.Graph"/>
    /// reflects the freshly re-analyzed state.
    /// <see cref="IncrementalAnalyzeResult.Reason"/> names the trigger.</summary>
    FullFallback,

    /// <summary>Adapter detected drift and the caller had not opted into
    /// full fallback (<see cref="AnalysisConfig.AllowFullFallback"/>=false).
    /// No work was done. <see cref="IncrementalAnalyzeResult.Graph"/> is null.
    /// <see cref="IncrementalAnalyzeResult.Reason"/> names the trigger.
    /// Caller decides next step: re-run with
    /// <see cref="AnalysisConfig.AllowFullFallback"/>=true, switch to
    /// <see cref="IWorkspaceAnalyzer.AnalyzeWorkspace"/>, or surface the
    /// rejection to the agent.</summary>
    Rejected,
}

/// <summary>
/// Adapter-agnostic taxonomy of conditions that prevent a clean
/// incremental re-analyze. Every adapter MUST classify its drift
/// detections into one of these buckets. Adapter-specific descriptors
/// (Unity asmdefs, .NET csprojs, Python pyproject.toml, Node package.json
/// etc.) all map to <see cref="ModuleDescriptorChanged"/>; the
/// <see cref="IncrementalAnalyzeResult.Detail"/> field carries the
/// adapter-specific kind for human consumption.
/// </summary>
public enum FallbackReason
{
    /// <summary>No prior analysis snapshot exists. The very first call
    /// with <c>incremental:true</c> against a fresh adapter instance
    /// always lands here. Replaces the pre-fix
    /// <see cref="System.InvalidOperationException"/> defensive throw.</summary>
    NoPriorAnalysis,

    /// <summary>The discoverable module set changed since the last
    /// analysis (modules added, removed, or renamed). The adapter cannot
    /// safely walk only changed files because module-level facts
    /// (dependencies, BCL ownership, capability flags) need re-derivation.</summary>
    ModuleSetChanged,

    /// <summary>A module-level descriptor file was edited (csproj edit,
    /// asmdef edit, project manifest change). Per-file extraction
    /// replacement is unsafe because the descriptor change can flip
    /// references, BCL ownership, or compilation flags. The
    /// <see cref="IncrementalAnalyzeResult.Detail"/> field carries the
    /// adapter-specific descriptor kind (e.g. <c>"asmdef"</c>,
    /// <c>"csproj"</c>).</summary>
    ModuleDescriptorChanged,
}

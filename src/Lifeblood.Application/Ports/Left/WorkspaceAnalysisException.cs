namespace Lifeblood.Application.Ports.Left;

/// <summary>
/// Structured failure from the workspace analyze pipeline. A workspace analyzer
/// that hits an unexpected fault (e.g. a NullReference after Unity asset-import
/// churn, LB-INBOX-012) MUST wrap it in this exception so the failure reaches
/// the wire as a phase/module/file-scoped diagnostic instead of an opaque
/// "Object reference not set to an instance of an object." Agents use full
/// analyze as the trust reset; an unstructured collapse there forces a blind
/// fall back to grep/IDE with no idea which phase is unsafe.
///
/// Language-agnostic — any <see cref="IWorkspaceAnalyzer"/> implementation can
/// throw it. INV-ANALYZE-STRUCTURED-FAILURE-001.
/// </summary>
public sealed class WorkspaceAnalysisException : Exception
{
    /// <summary>Coarse pipeline phase the failure occurred in (e.g. "discovery", "compilation", "graph-build").</summary>
    public string Phase { get; }

    /// <summary>Module being processed when the failure occurred, if known.</summary>
    public string? Module { get; }

    /// <summary>Source file being processed when the failure occurred, if known.</summary>
    public string? FilePath { get; }

    /// <summary>Define profile active when the failure occurred (multi-profile analyze), if known.</summary>
    public string? Profile { get; }

    /// <summary>
    /// True when the failure happened before any module compilation was created
    /// (discovery / descriptor parsing / module-symbol phase). Lets a caller
    /// tell "the workspace shape is bad" apart from "a specific module failed to
    /// compile or extract".
    /// </summary>
    public bool FailedBeforeCompilation { get; }

    public WorkspaceAnalysisException(
        string phase,
        string? module,
        string? filePath,
        string? profile,
        bool failedBeforeCompilation,
        Exception inner)
        : base(BuildMessage(phase, module, filePath, profile, inner), inner)
    {
        Phase = phase;
        Module = module;
        FilePath = filePath;
        Profile = profile;
        FailedBeforeCompilation = failedBeforeCompilation;
    }

    private static string BuildMessage(string phase, string? module, string? filePath, string? profile, Exception inner)
    {
        var where = phase;
        if (!string.IsNullOrEmpty(profile)) where += $", profile '{profile}'";
        if (!string.IsNullOrEmpty(module)) where += $", module '{module}'";
        if (!string.IsNullOrEmpty(filePath)) where += $", file '{filePath}'";
        return $"Workspace analyze failed during {where}: {inner.GetType().Name}: {inner.Message}";
    }
}

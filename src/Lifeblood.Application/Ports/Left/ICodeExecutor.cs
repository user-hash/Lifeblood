using Lifeblood.Domain.Results;

namespace Lifeblood.Application.Ports.Left;

/// <summary>
/// Executes code snippets against a loaded workspace.
/// Language-agnostic contract — Roslyn implements with CSharpScript,
/// a future Python adapter could implement with exec().
/// </summary>
public interface ICodeExecutor
{
    CodeExecutionResult Execute(string code, string[]? imports = null, int timeoutMs = 5000);

    /// <summary>
    /// Typed-options overload. Use this when supplying
    /// <see cref="CodeExecutionRequest.TargetProfile"/> or other fields
    /// that don't fit the back-compatible signature.
    /// </summary>
    CodeExecutionResult Execute(CodeExecutionRequest request);
}

/// <summary>
/// Typed request for <see cref="ICodeExecutor.Execute(CodeExecutionRequest)"/>.
/// Reserved as a separate record so future per-call policy
/// (target-profile hinting, allow-host-network, deterministic seed,
/// etc.) can be added without further signature churn — same pattern
/// as <see cref="FindReferencesOptions"/> and <see cref="DiagnosticsRequest"/>.
/// </summary>
public sealed class CodeExecutionRequest
{
    public required string Code { get; init; }
    public string[]? Imports { get; init; }
    public int TimeoutMs { get; init; } = 5000;

    /// <summary>
    /// Target runtime profile hint. <c>"host"</c> (default) is the only
    /// execution profile: scripts run in-process against the running .NET
    /// runtime's BCL so they can share the retained Roslyn workspace state.
    /// Non-host values are accepted for backward compatibility but do not
    /// swap reference packs; the executor still runs against <c>"host"</c>
    /// and surfaces the limitation on
    /// <see cref="CodeExecutionResult.TargetRuntimeWarnings"/>.
    /// </summary>
    public string TargetProfile { get; init; } = "host";
}

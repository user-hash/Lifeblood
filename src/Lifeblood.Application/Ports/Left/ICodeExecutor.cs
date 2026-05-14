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
/// (target-profile selection, allow-host-network, deterministic seed,
/// etc.) can be added without further signature churn — same pattern
/// as <see cref="FindReferencesOptions"/> and <see cref="DiagnosticsRequest"/>.
/// </summary>
public sealed class CodeExecutionRequest
{
    public required string Code { get; init; }
    public string[]? Imports { get; init; }
    public int TimeoutMs { get; init; } = 5000;

    /// <summary>
    /// Target runtime profile. <c>"host"</c> (default) uses the running
    /// .NET runtime's BCL — the existing pre-P4 behavior.
    /// <c>"net-standard-2.1"</c> swaps in NETStandard.Library.Ref.
    /// <c>"net-6.0"</c> swaps in Microsoft.NETCore.App.Ref 6.x. Unknown
    /// values fall back to <c>"host"</c> with a warning. When the
    /// requested ref-pack isn't installed locally the executor falls
    /// back to <c>"host"</c> and surfaces a diagnostic on
    /// <see cref="CodeExecutionResult.TargetRuntimeWarnings"/>.
    /// </summary>
    public string TargetProfile { get; init; } = "host";
}

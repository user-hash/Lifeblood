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
}

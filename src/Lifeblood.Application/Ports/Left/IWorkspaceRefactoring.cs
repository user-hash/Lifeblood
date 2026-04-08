using Lifeblood.Domain.Results;

namespace Lifeblood.Application.Ports.Left;

/// <summary>
/// Workspace-level refactoring operations.
/// Returns edits, does NOT apply them — the caller (AI agent) decides.
/// Same pattern as LSP textDocument/rename.
/// </summary>
public interface IWorkspaceRefactoring
{
    TextEdit[] Rename(string symbolId, string newName);
    string Format(string code);
}

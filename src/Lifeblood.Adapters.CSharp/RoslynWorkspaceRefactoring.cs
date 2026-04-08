using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Rename;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Roslyn-backed workspace refactoring.
/// Rename returns TextEdits (does NOT apply). Format returns formatted code string.
/// Uses shared RoslynWorkspaceManager for workspace lifecycle and symbol resolution.
/// </summary>
public sealed class RoslynWorkspaceRefactoring : IWorkspaceRefactoring, IDisposable
{
    private readonly Lazy<RoslynWorkspaceManager> _manager;

    public RoslynWorkspaceRefactoring(IReadOnlyDictionary<string, CSharpCompilation> compilations)
    {
        _manager = new Lazy<RoslynWorkspaceManager>(() => new RoslynWorkspaceManager(compilations));
    }

    public TextEdit[] Rename(string symbolId, string newName)
    {
        var mgr = _manager.Value;
        if (mgr.Solution == null) return Array.Empty<TextEdit>();

        var roslynSymbol = mgr.ResolveSymbol(symbolId);
        if (roslynSymbol == null) return Array.Empty<TextEdit>();

#pragma warning disable CS0618 // Renamer.RenameSymbolAsync overload — we use the stable API
        var newSolution = Renamer.RenameSymbolAsync(mgr.Solution, roslynSymbol, default(SymbolRenameOptions), newName)
            .GetAwaiter().GetResult();
#pragma warning restore CS0618

        var edits = new List<TextEdit>();
        var changedDocs = newSolution.GetChanges(mgr.Solution).GetProjectChanges()
            .SelectMany(p => p.GetChangedDocuments());

        foreach (var docId in changedDocs)
        {
            var oldDoc = mgr.Solution.GetDocument(docId);
            var newDoc = newSolution.GetDocument(docId);
            if (oldDoc == null || newDoc == null) continue;

            var oldText = oldDoc.GetTextAsync().GetAwaiter().GetResult();
            var newText = newDoc.GetTextAsync().GetAwaiter().GetResult();
            var changes = newText.GetTextChanges(oldText);

            foreach (var change in changes)
            {
                var startLine = oldText.Lines.GetLinePosition(change.Span.Start);
                var endLine = oldText.Lines.GetLinePosition(change.Span.End);

                edits.Add(new TextEdit
                {
                    FilePath = oldDoc.FilePath ?? oldDoc.Name,
                    StartLine = startLine.Line + 1,
                    StartColumn = startLine.Character + 1,
                    EndLine = endLine.Line + 1,
                    EndColumn = endLine.Character + 1,
                    NewText = change.NewText ?? "",
                });
            }
        }

        return edits.ToArray();
    }

    public string Format(string code)
    {
        var mgr = _manager.Value;
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();
        var formatted = Formatter.Format(root, mgr.GetWorkspace());
        return formatted.ToFullString();
    }

    public void Dispose()
    {
        if (_manager.IsValueCreated)
            _manager.Value.Dispose();
    }
}

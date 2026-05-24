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

    public RoslynWorkspaceRefactoring(
        IReadOnlyDictionary<string, CSharpCompilation> compilations,
        IReadOnlyDictionary<string, string[]>? moduleDependencies = null)
    {
        _manager = new Lazy<RoslynWorkspaceManager>(
            () => new RoslynWorkspaceManager(compilations, moduleDependencies));
    }

    public TextEdit[] Rename(string symbolId, string newName)
    {
        var mgr = _manager.Value;

        // INV-RENAME-CROSS-PARTIAL-001 (LB-TRACK-20260524-025):
        // ResolveSymbol triggers EnsureWorkspace internally, so we MUST call
        // it before reading mgr.Solution. The pre-fix code checked
        // mgr.Solution == null FIRST, before any operation had warmed the
        // workspace — fresh-fixture and first-call-on-process paths returned
        // empty edit arrays for that reason alone. Long-running MCP sessions
        // accidentally hid the bug because a prior find_references / format
        // call had already triggered EnsureWorkspace.
        var roslynSymbol = mgr.ResolveSymbol(symbolId);
        if (roslynSymbol == null) return Array.Empty<TextEdit>();
        if (mgr.Solution == null) return Array.Empty<TextEdit>();

#pragma warning disable CS0618 // Renamer.RenameSymbolAsync overload — we use the stable API
        // Renamer.RenameSymbolAsync runs at Solution scope by design — it walks
        // every project in the solution looking for incoming references, so
        // cross-partial / cross-file / cross-asmdef usages are picked up
        // automatically. INV-RENAME-CROSS-PARTIAL-001. The result is a fresh
        // Solution with TextChanges applied to every document that contained
        // a reference; the wire-shape contract below projects them per-site.
        var newSolution = Renamer.RenameSymbolAsync(mgr.Solution, roslynSymbol, default(SymbolRenameOptions), newName)
            .GetAwaiter().GetResult();
#pragma warning restore CS0618

        // INV-RENAME-POINT-EDITS-001 (LB-TRACK-20260524-025): one TextEdit per
        // Roslyn-emitted Document TextChange, NOT one TextEdit per changed
        // Document. The pre-fix wire shape coalesced every document's diff
        // into a single edit spanning the whole file (startLine=1,
        // endLine=lastLine, newText=full file body), which defeated diff /
        // selective-apply on the caller side and made mechanical application
        // overwrite any concurrent local edits.
        //
        // Root cause of the pre-fix shape: `SourceText.GetTextChanges(oldText)`
        // does a brute text diff between two SourceText instances. When the
        // pre / post Roslyn documents come from different TextLoader paths
        // (workspace-loaded oldText vs Renamer-emitted newText) the two
        // SourceText instances do not share an internal container, so the
        // diff degenerates to a single change-everything TextChange even when
        // only a handful of identifier spans actually moved.
        //
        // Fix: use `Document.GetTextChangesAsync(oldDocument)` — Roslyn's
        // Document-level diff which surfaces the granular TextChanges the
        // Renamer actually applied. Each TextChange is a narrow identifier
        // span; one wire-level TextEdit per change is per-site point-edit by
        // construction.
        var edits = new List<TextEdit>();
        var changedDocs = newSolution.GetChanges(mgr.Solution).GetProjectChanges()
            .SelectMany(p => p.GetChangedDocuments());

        foreach (var docId in changedDocs)
        {
            var oldDoc = mgr.Solution.GetDocument(docId);
            var newDoc = newSolution.GetDocument(docId);
            if (oldDoc == null || newDoc == null) continue;

            // Document.GetTextChangesAsync(oldDoc) returns Roslyn's authored
            // granular changes (each rename identifier span = one TextChange),
            // NOT a brute text diff. INV-RENAME-POINT-EDITS-001.
            var changes = newDoc.GetTextChangesAsync(oldDoc).GetAwaiter().GetResult();
            var oldText = oldDoc.GetTextAsync().GetAwaiter().GetResult();

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

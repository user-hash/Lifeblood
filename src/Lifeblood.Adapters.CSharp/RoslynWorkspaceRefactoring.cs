using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Formatting;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Rename;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Roslyn-backed workspace refactoring.
/// Rename returns TextEdits (does NOT apply). Format returns formatted code string.
/// Uses AdhocWorkspace built from retained compilations.
/// </summary>
public sealed class RoslynWorkspaceRefactoring : IWorkspaceRefactoring
{
    private readonly IReadOnlyDictionary<string, CSharpCompilation> _compilations;
    private AdhocWorkspace? _workspace;
    private Solution? _solution;

    public RoslynWorkspaceRefactoring(IReadOnlyDictionary<string, CSharpCompilation> compilations)
    {
        _compilations = compilations;
    }

    public TextEdit[] Rename(string symbolId, string newName)
    {
        EnsureWorkspace();
        if (_solution == null) return Array.Empty<TextEdit>();

        var roslynSymbol = ResolveSymbol(symbolId);
        if (roslynSymbol == null) return Array.Empty<TextEdit>();

#pragma warning disable CS0618 // Renamer.RenameSymbolAsync overload — we use the stable API
        var newSolution = Renamer.RenameSymbolAsync(_solution, roslynSymbol, default(SymbolRenameOptions), newName)
            .GetAwaiter().GetResult();
#pragma warning restore CS0618

        var edits = new List<TextEdit>();
        var changedDocs = newSolution.GetChanges(_solution).GetProjectChanges()
            .SelectMany(p => p.GetChangedDocuments());

        foreach (var docId in changedDocs)
        {
            var oldDoc = _solution.GetDocument(docId);
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
        EnsureWorkspace();
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();
        var formatted = Formatter.Format(root, _workspace!);
        return formatted.ToFullString();
    }

    private ISymbol? ResolveSymbol(string symbolId)
    {
        var prefix = symbolId.IndexOf(':');
        if (prefix < 0) return null;

        var qualifiedName = symbolId.Substring(prefix + 1);
        var parenIdx = qualifiedName.IndexOf('(');
        var nameOnly = parenIdx >= 0 ? qualifiedName.Substring(0, parenIdx) : qualifiedName;
        var parts = nameOnly.Split('.');

        foreach (var compilation in _compilations.Values)
        {
            INamespaceOrTypeSymbol current = compilation.GlobalNamespace;

            foreach (var part in parts)
            {
                var member = current.GetMembers(part).FirstOrDefault();
                if (member is INamespaceOrTypeSymbol ns)
                    current = ns;
                else if (member != null)
                    return member;
                else
                    break;
            }

            if (current != compilation.GlobalNamespace && current is INamedTypeSymbol)
                return current;
        }

        return null;
    }

    private void EnsureWorkspace()
    {
        if (_workspace != null) return;

        _workspace = new AdhocWorkspace();
        var solutionInfo = SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Default);
        _workspace.AddSolution(solutionInfo);

        foreach (var (name, compilation) in _compilations)
        {
            var projectId = ProjectId.CreateNewId(name);
            var projectInfo = ProjectInfo.Create(
                projectId, VersionStamp.Default, name, name, LanguageNames.CSharp,
                metadataReferences: compilation.References);
            _workspace.AddProject(projectInfo);

            foreach (var tree in compilation.SyntaxTrees)
            {
                var docId = DocumentId.CreateNewId(projectId);
                var docInfo = DocumentInfo.Create(docId,
                    Path.GetFileName(tree.FilePath ?? "unknown.cs"),
                    sourceCodeKind: SourceCodeKind.Regular,
                    loader: TextLoader.From(TextAndVersion.Create(tree.GetText(), VersionStamp.Default)),
                    filePath: tree.FilePath);
                _workspace.AddDocument(docInfo);
            }
        }

        _solution = _workspace.CurrentSolution;
    }
}

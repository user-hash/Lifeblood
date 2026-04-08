using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Shared workspace infrastructure for write-side Roslyn operations.
/// Owns the AdhocWorkspace lifecycle, symbol resolution, and ID parsing.
/// Used by both RoslynCompilationHost and RoslynWorkspaceRefactoring.
/// </summary>
internal sealed class RoslynWorkspaceManager
{
    private readonly IReadOnlyDictionary<string, CSharpCompilation> _compilations;
    private AdhocWorkspace? _workspace;

    public RoslynWorkspaceManager(IReadOnlyDictionary<string, CSharpCompilation> compilations)
    {
        _compilations = compilations;
    }

    public AdhocWorkspace GetWorkspace()
    {
        EnsureWorkspace();
        return _workspace!;
    }

    public Solution? Solution { get; private set; }

    /// <summary>
    /// Resolve a Roslyn symbol from the workspace solution's project compilations.
    /// Must use workspace-owned compilations — standalone compilations produce symbols
    /// that Renamer/SymbolFinder cannot match against the Solution.
    /// </summary>
    public ISymbol? ResolveSymbol(string symbolId, bool fallbackToStandalone = false)
    {
        var (kind, parts) = ParseSymbolId(symbolId);
        if (kind == null || parts == null) return null;

        EnsureWorkspace();

        // Resolve from workspace projects so the symbol belongs to the Solution
        if (Solution != null)
        {
            foreach (var project in Solution.Projects)
            {
                var compilation = project.GetCompilationAsync().GetAwaiter().GetResult();
                if (compilation == null) continue;

                var found = FindInCompilation(compilation, kind, parts);
                if (found != null) return found;
            }
        }

        // Fallback to standalone compilations (for non-workspace operations like FindReferences)
        if (fallbackToStandalone)
        {
            foreach (var compilation in _compilations.Values)
            {
                var found = FindInCompilation(compilation, kind, parts);
                if (found != null) return found;
            }
        }

        return null;
    }

    internal static ISymbol? FindInCompilation(Compilation compilation, string kind, string[] parts)
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

        if (!SymbolEqualityComparer.Default.Equals(current, compilation.GlobalNamespace))
        {
            if (kind == "type" && current is INamedTypeSymbol) return current;
            if (kind == "method") return current.GetMembers().OfType<IMethodSymbol>().FirstOrDefault();
            if (kind == "field") return current.GetMembers().OfType<IFieldSymbol>().FirstOrDefault();
            if (kind == "property") return current.GetMembers().OfType<IPropertySymbol>().FirstOrDefault();
        }

        return null;
    }

    internal static (string? kind, string[]? parts) ParseSymbolId(string symbolId)
    {
        var prefix = symbolId.IndexOf(':');
        if (prefix < 0) return (null, null);

        var kind = symbolId.Substring(0, prefix);
        var qualifiedName = symbolId.Substring(prefix + 1);

        var parenIdx = qualifiedName.IndexOf('(');
        var nameOnly = parenIdx >= 0 ? qualifiedName.Substring(0, parenIdx) : qualifiedName;
        return (kind, nameOnly.Split('.'));
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

        Solution = _workspace.CurrentSolution;
    }
}

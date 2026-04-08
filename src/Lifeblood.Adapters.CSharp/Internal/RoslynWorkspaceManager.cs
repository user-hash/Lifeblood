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
        var parsed = ParseSymbolId(symbolId);
        if (parsed.Kind == null || parsed.Parts == null) return null;

        EnsureWorkspace();

        // Resolve from workspace projects so the symbol belongs to the Solution
        if (Solution != null)
        {
            foreach (var project in Solution.Projects)
            {
                var compilation = project.GetCompilationAsync().GetAwaiter().GetResult();
                if (compilation == null) continue;

                var found = FindInCompilation(compilation, parsed);
                if (found != null) return found;
            }
        }

        // Fallback to standalone compilations (for non-workspace operations like FindReferences)
        if (fallbackToStandalone)
        {
            foreach (var compilation in _compilations.Values)
            {
                var found = FindInCompilation(compilation, parsed);
                if (found != null) return found;
            }
        }

        return null;
    }

    internal static ISymbol? FindInCompilation(Compilation compilation, ParsedSymbolId parsed)
    {
        INamespaceOrTypeSymbol current = compilation.GlobalNamespace;

        foreach (var part in parsed.Parts!)
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
            if (parsed.Kind == "type" && current is INamedTypeSymbol) return current;
            if (parsed.Kind == "method")
            {
                var methods = current.GetMembers().OfType<IMethodSymbol>().ToArray();
                if (methods.Length == 0) return null;
                if (methods.Length == 1) return methods[0];

                // Overload disambiguation: match parameter signature if provided.
                // Must use same format as SymbolExtractor/EdgeExtractor: default ToDisplayString + comma separator (no space).
                if (parsed.ParamSignature != null)
                {
                    foreach (var m in methods)
                    {
                        var sig = string.Join(",", m.Parameters.Select(p => p.Type.ToDisplayString()));
                        if (sig == parsed.ParamSignature) return m;
                    }
                }
                return methods[0]; // fallback to first if no match
            }
            if (parsed.Kind == "field") return current.GetMembers().OfType<IFieldSymbol>().FirstOrDefault();
            if (parsed.Kind == "property") return current.GetMembers().OfType<IPropertySymbol>().FirstOrDefault();
        }

        return null;
    }

    /// <summary>
    /// Parse a symbol ID into kind, namespace parts, and optional parameter signature.
    /// Format: "kind:Ns.Type.Member(param1, param2)"
    /// </summary>
    internal static ParsedSymbolId ParseSymbolId(string symbolId)
    {
        var prefix = symbolId.IndexOf(':');
        if (prefix < 0) return default;

        var kind = symbolId.Substring(0, prefix);
        var qualifiedName = symbolId.Substring(prefix + 1);

        string? paramSignature = null;
        var parenIdx = qualifiedName.IndexOf('(');
        string nameOnly;
        if (parenIdx >= 0)
        {
            nameOnly = qualifiedName.Substring(0, parenIdx);
            var closeIdx = qualifiedName.LastIndexOf(')');
            if (closeIdx > parenIdx)
                paramSignature = qualifiedName.Substring(parenIdx + 1, closeIdx - parenIdx - 1);
        }
        else
        {
            nameOnly = qualifiedName;
        }

        return new ParsedSymbolId(kind, nameOnly.Split('.'), paramSignature);
    }

    internal readonly record struct ParsedSymbolId(string? Kind, string[]? Parts, string? ParamSignature);

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

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Shared workspace infrastructure for write-side Roslyn operations.
/// Owns the AdhocWorkspace lifecycle, symbol resolution, and ID parsing.
/// Used by both RoslynCompilationHost and RoslynWorkspaceRefactoring.
/// </summary>
internal sealed class RoslynWorkspaceManager : IDisposable
{
    private readonly IReadOnlyDictionary<string, CSharpCompilation> _compilations;
    private readonly IReadOnlyDictionary<string, string[]>? _moduleDependencies;
    private AdhocWorkspace? _workspace;

    public RoslynWorkspaceManager(
        IReadOnlyDictionary<string, CSharpCompilation> compilations,
        IReadOnlyDictionary<string, string[]>? moduleDependencies = null)
    {
        _compilations = compilations;
        _moduleDependencies = moduleDependencies;
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
            {
                // For methods, disambiguate overloads by parameter signature before returning.
                // Without this, FirstOrDefault() always returns the first overload,
                // making FindReferences/Rename operate on the wrong method.
                if (parsed.Kind == "method" && member is IMethodSymbol && parsed.ParamSignature != null)
                {
                    var overloads = current.GetMembers(part).OfType<IMethodSymbol>().ToArray();
                    if (overloads.Length > 1)
                    {
                        foreach (var m in overloads)
                        {
                            var sig = string.Join(",", m.Parameters.Select(p => p.Type.ToDisplayString()));
                            if (sig == parsed.ParamSignature) return m;
                        }
                    }
                }
                return member;
            }
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

        // Track project IDs by module name for ProjectReference linking.
        var projectIds = new Dictionary<string, ProjectId>(StringComparer.Ordinal);

        // Phase 1: Create all projects with metadata references.
        foreach (var (name, compilation) in _compilations)
        {
            var projectId = ProjectId.CreateNewId(name);
            projectIds[name] = projectId;

            // When module dependencies are known, exclude metadata references that correspond
            // to other workspace modules — they'll be replaced with ProjectReference links.
            // This ensures Roslyn treats cross-module symbols as the SAME symbol (not metadata
            // vs source copies), enabling FindReferences/Rename to work across assemblies.
            var metadataRefs = compilation.References;
            if (_moduleDependencies != null)
            {
                var depModuleNames = new HashSet<string>(
                    _moduleDependencies.TryGetValue(name, out var deps) ? deps : Array.Empty<string>(),
                    StringComparer.Ordinal);
                metadataRefs = metadataRefs
                    .Where(r => !IsModuleReference(r, depModuleNames))
                    .ToArray();
            }

            var projectInfo = ProjectInfo.Create(
                projectId, VersionStamp.Default, name, name, LanguageNames.CSharp,
                metadataReferences: metadataRefs);
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

        // Phase 2: Add ProjectReference links between modules.
        // This replaces the downgraded PE metadata references with proper project links,
        // so Roslyn's SymbolFinder can follow cross-assembly references.
        if (_moduleDependencies != null)
        {
            var solution = _workspace.CurrentSolution;
            foreach (var (name, deps) in _moduleDependencies)
            {
                if (!projectIds.TryGetValue(name, out var projectId)) continue;
                foreach (var dep in deps)
                {
                    if (projectIds.TryGetValue(dep, out var depProjectId))
                        solution = solution.AddProjectReference(projectId, new ProjectReference(depProjectId));
                }
            }
            _workspace.TryApplyChanges(solution);
        }

        Solution = _workspace.CurrentSolution;
    }

    /// <summary>
    /// Returns true if a metadata reference corresponds to a known workspace module.
    /// These are replaced by ProjectReference links for cross-assembly symbol resolution.
    /// Checks the PE assembly identity name (e.g., "WriteSideApp.Core"), not the file path,
    /// because downgraded in-memory PE references have no file path.
    /// </summary>
    private static bool IsModuleReference(MetadataReference reference, HashSet<string> moduleNames)
    {
        if (reference is not PortableExecutableReference peRef) return false;

        // For PE references, the Display property is the assembly name for in-memory images
        // or the file path for file-based references. Check both patterns.
        var display = peRef.Display ?? "";

        // File-based reference: "C:\path\to\WriteSideApp.Core.dll"
        if (display.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || display.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileNameWithoutExtension(display);
            return moduleNames.Contains(fileName);
        }

        // In-memory reference: Display is the assembly name (e.g., "WriteSideApp.Core").
        // Don't use Path.GetFileNameWithoutExtension — it treats dots as file extensions.
        return moduleNames.Contains(display);
    }

    public void Dispose()
    {
        _workspace?.Dispose();
        _workspace = null;
        Solution = null;
    }
}

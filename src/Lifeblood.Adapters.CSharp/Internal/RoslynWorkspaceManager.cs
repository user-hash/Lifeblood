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

    /// <summary>
    /// Resolve a parsed symbol ID against a compilation by walking namespaces, then types,
    /// then matching the final member by kind (and signature for methods).
    ///
    /// CRITICAL — never silently fall back to an unrelated member. Earlier versions of this
    /// resolver returned `methods[0]` when no overload matched the requested signature, which
    /// caused FindReferences to report call sites of completely different methods. The current
    /// rules:
    ///   - If a path part isn't found as a namespace/type while walking, return null.
    ///   - If the member name isn't found on the resolved container, return null.
    ///   - If a method has multiple overloads and the requested signature matches none, return null.
    ///   - If a method has a single overload, return it (lenient — sig may be approximate).
    ///   - If no parameter signature was provided, return the first overload.
    /// </summary>
    internal static ISymbol? FindInCompilation(Compilation compilation, ParsedSymbolId parsed)
    {
        if (parsed.Parts == null || parsed.Parts.Length == 0) return null;

        // Walk every part EXCEPT the last as namespaces or types.
        // The last part is the member we're actually looking up — kind-filtered below.
        INamespaceOrTypeSymbol current = compilation.GlobalNamespace;
        for (int i = 0; i < parsed.Parts.Length - 1; i++)
        {
            var part = parsed.Parts[i];
            // Prefer namespaces, then types — both can be parents of further members.
            // Filter explicitly so a stray non-namespace member with a colliding name
            // (very rare in practice) cannot derail the walk.
            var next = current.GetMembers(part).OfType<INamespaceOrTypeSymbol>().FirstOrDefault();
            if (next == null) return null;
            current = next;
        }

        var lastName = parsed.Parts[^1];

        // Kind-filtered member lookup. The lookup is by EXACT name only — no broad
        // enumeration of all members on `current`. If the name doesn't exist as the
        // requested kind, the answer is "not found", not "pick something arbitrary".
        switch (parsed.Kind)
        {
            case "type":
                {
                    var type = current.GetMembers(lastName).OfType<INamedTypeSymbol>().FirstOrDefault();
                    if (type != null) return type;
                    // Top-level type whose name was the only part — current is still global ns.
                    return null;
                }

            case "method":
                {
                    var methods = current.GetMembers(lastName).OfType<IMethodSymbol>().ToArray();
                    if (methods.Length == 0) return null;

                    // No signature requested → return first overload (caller is intentionally fuzzy).
                    if (parsed.ParamSignature == null)
                        return methods[0];

                    // Signature requested → must match exactly. Never silently return a wrong overload.
                    // Use CanonicalSymbolFormat so we compare against the SAME format the graph uses
                    // — the input symbolId came from a graph builder, so this is the only correct way
                    // to compare signatures across the source/metadata boundary.
                    foreach (var m in methods)
                    {
                        var sig = CanonicalSymbolFormat.BuildParamSignature(m);
                        if (sig == parsed.ParamSignature) return m;
                    }

                    // Single overload + signature requested but mismatched: lenient pass-through.
                    // The user named the method correctly; their signature might just be in a
                    // slightly-different display format than Roslyn's default. Returning the
                    // method is safe because there's no other "Add" they could have meant.
                    if (methods.Length == 1) return methods[0];

                    return null;
                }

            case "field":
                return current.GetMembers(lastName).OfType<IFieldSymbol>().FirstOrDefault();

            case "property":
                {
                    var prop = current.GetMembers(lastName).OfType<IPropertySymbol>().FirstOrDefault();
                    if (prop != null) return prop;
                    // Events share the property: prefix in Lifeblood's symbol ID grammar.
                    return current.GetMembers(lastName).OfType<IEventSymbol>().FirstOrDefault();
                }

            default:
                return null;
        }
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

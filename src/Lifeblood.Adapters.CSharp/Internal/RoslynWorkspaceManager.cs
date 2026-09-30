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
    /// Resolve a unique declaration from retained compilations for observations,
    /// or workspace-owned compilations for edits. Renamer requires symbols from
    /// its Solution; read queries do not require an editable workspace.
    /// </summary>
    public ISymbol? ResolveSymbol(string symbolId, bool useStandaloneCompilations = false)
    {
        var parsed = ParseSymbolId(symbolId);
        if (parsed.Kind == null || parsed.Parts == null) return null;

        // Compare normalized declaration identities across all owners before
        // selecting a source copy. Metadata replicas do not add candidates.
        var candidates = new List<ISymbol>();
        if (useStandaloneCompilations)
        {
            foreach (var compilation in _compilations.Values)
                candidates.AddRange(FindCandidatesInCompilation(compilation, parsed));
        }
        else
        {
            EnsureWorkspace();
            foreach (var project in Solution!.Projects)
            {
                var compilation = project.GetCompilationAsync().GetAwaiter().GetResult();
                if (compilation != null)
                    candidates.AddRange(FindCandidatesInCompilation(compilation, parsed));
            }
        }
        return SelectUniqueDeclaration(candidates);
    }

    /// <summary>
    /// Resolve only a unique declaration. Legacy addresses can denote several
    /// legal overloads, generic owners or assemblies; those require a v2 ID.
    /// Truncated method names are accepted only when the declaration is unique.
    /// </summary>
    internal static ISymbol? FindInCompilation(Compilation compilation, ParsedSymbolId parsed)
        => SelectUniqueDeclaration(FindCandidatesInCompilation(compilation, parsed));

    private static ISymbol? SelectUniqueDeclaration(IEnumerable<ISymbol> candidates)
    {
        var declarations = candidates.GroupBy(
            s => CanonicalSymbolFormat.BuildSymbolId(s, CanonicalSymbolIdVersion.AssemblyQualifiedV2),
            StringComparer.Ordinal).Take(2).ToArray();
        return declarations.Length == 1
            ? declarations[0].OrderByDescending(s => s.Locations.Any(l => l.IsInSource)).First()
            : null;
    }

    private static IEnumerable<ISymbol> FindCandidatesInCompilation(Compilation compilation, ParsedSymbolId parsed)
    {
        if (parsed.Version2Parts != null)
            return FindVersion2Candidates(compilation, parsed);
        if (parsed.Parts == null || parsed.Parts.Length == 0)
            return Array.Empty<ISymbol>();

        var matches = new List<ISymbol>();
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols.Prepend(compilation.Assembly))
        {
            IEnumerable<INamespaceOrTypeSymbol> containers = new[] { assembly.GlobalNamespace };
            foreach (var part in parsed.Parts[..^1])
                containers = containers.SelectMany(c => c.GetMembers(part).OfType<INamespaceOrTypeSymbol>()).ToArray();
            var lastName = parsed.Parts[^1];
            foreach (var container in containers)
            {
                IEnumerable<ISymbol> members = lastName.StartsWith("this[", StringComparison.Ordinal)
                    ? container.GetMembers().OfType<IPropertySymbol>().Where(p => p.IsIndexer)
                    : container.GetMembers(lastName);
                foreach (var member in members)
                {
                    if (!(parsed.Kind switch {
                        "type" => member is INamedTypeSymbol,
                        "method" => member is IMethodSymbol,
                        "field" => member is IFieldSymbol,
                        "property" => member is IPropertySymbol or IEventSymbol,
                        _ => false,
                    })) continue;
                    var id = CanonicalSymbolFormat.BuildSymbolId(member);
                    if (!id.StartsWith(parsed.Kind + ":", StringComparison.Ordinal)) continue;
                    var requestedId = parsed.CompleteId;
                    if (parsed.Kind == "method" && parsed.ParamSignature == null)
                        id = id[..id.IndexOf('(')];
                    if (id == requestedId) matches.Add(member);
                }
            }
        }
        return matches;
    }

    /// <summary>
    /// Parse a symbol ID into kind, namespace parts, and optional parameter signature.
    /// Format: "kind:Ns.Type.Member(param1, param2)"
    ///
    /// Special handling for IL-style constructor names: instance constructors carry the
    /// literal name <c>.ctor</c> and static constructors <c>.cctor</c> (leading dot baked
    /// into <c>IMethodSymbol.Name</c>). A naive <c>Split('.')</c> on
    /// <c>App.Service..ctor</c> produces <c>["App", "Service", "", "ctor"]</c> — the
    /// empty middle segment makes the namespace/type walker fail at
    /// <c>GetMembers("")</c>. Detect the <c>..ctor</c> / <c>..cctor</c> suffix and
    /// preserve the literal member name as a single trailing part. INV-CANONICAL-ID-
    /// PARITY-001 / LB-TRACK-20260519-023.
    /// </summary>
    internal static ParsedSymbolId ParseSymbolId(string symbolId)
    {
        var prefix = symbolId.IndexOf(':');
        if (prefix < 0) return default;

        var kind = symbolId.Substring(0, prefix);
        var qualifiedName = symbolId.Substring(prefix + 1);

        if (qualifiedName.StartsWith("v2|", StringComparison.Ordinal))
        {
            var components = qualifiedName.Split('|');
            if (components.Length != 5) return default;
            var decoded = components.Skip(1).Select(Uri.UnescapeDataString).ToArray();
            if (!decoded[1].StartsWith("T:", StringComparison.Ordinal)) return default;
            return new ParsedSymbolId(kind, Array.Empty<string>(), null, decoded, symbolId);
        }

        string? paramSignature = null;
        var parenIdx = qualifiedName.IndexOf('(');
        string nameOnly;
        if (parenIdx >= 0)
        {
            nameOnly = qualifiedName.Substring(0, parenIdx);
            var closeIdx = qualifiedName.LastIndexOf(')');
            if (closeIdx <= parenIdx || closeIdx != qualifiedName.Length - 1) return default;
            paramSignature = qualifiedName.Substring(parenIdx + 1, closeIdx - parenIdx - 1);
        }
        else
        {
            nameOnly = qualifiedName;
        }

        return new ParsedSymbolId(kind, SplitPreservingCtorNames(nameOnly), paramSignature, CompleteId: symbolId);
    }

    private static string[] SplitPreservingCtorNames(string nameOnly)
    {
        // Order matters: ".cctor" check must come before ".ctor" because the
        // ".cctor" suffix also ends in ".ctor". An EndsWith(".ctor") check
        // would steal the ".cctor" case.
        const string CctorSuffix = "..cctor";
        const string CtorSuffix = "..ctor";

        if (nameOnly.EndsWith(CctorSuffix, System.StringComparison.Ordinal))
            return SplitWithLiteralLast(nameOnly, CctorSuffix, ".cctor");
        if (nameOnly.EndsWith(CtorSuffix, System.StringComparison.Ordinal))
            return SplitWithLiteralLast(nameOnly, CtorSuffix, ".ctor");
        var indexer = nameOnly.IndexOf(".this[", StringComparison.Ordinal);
        if (indexer >= 0)
            return nameOnly[..indexer].Split('.').Append(nameOnly[(indexer + 1)..]).ToArray();
        return nameOnly.Split('.');
    }

    private static string[] SplitWithLiteralLast(string nameOnly, string suffix, string literalLast)
    {
        // suffix is "..ctor" / "..cctor" — strip the WHOLE suffix (including the
        // separator dot) so the remaining prefix is the dotted container path.
        // For "App.Service..ctor" with suffix "..ctor" (length 6), prefix becomes
        // "App.Service" (length 11). Empty prefix means the id named a constructor
        // with no containing path — defensive, not a valid C# shape.
        var prefix = nameOnly.Substring(0, nameOnly.Length - suffix.Length);
        if (prefix.Length == 0) return new[] { literalLast };
        var parts = prefix.Split('.');
        var result = new string[parts.Length + 1];
        System.Array.Copy(parts, result, parts.Length);
        result[parts.Length] = literalLast;
        return result;
    }

    internal readonly record struct ParsedSymbolId(string? Kind, string[]? Parts, string? ParamSignature,
        string[]? Version2Parts = null, string? CompleteId = null);

    private static IEnumerable<ISymbol> FindVersion2Candidates(Compilation compilation, ParsedSymbolId parsed)
    {
        var parts = parsed.Version2Parts!;
        var assemblies = compilation.SourceModule.ReferencedAssemblySymbols.Prepend(compilation.Assembly)
            .Where(a => a.Identity.ToString() == parts[0]);
        var matches = new List<ISymbol>();
        foreach (var assembly in assemblies)
        {
            IEnumerable<INamespaceOrTypeSymbol> containers = new[] { assembly.GlobalNamespace };
            foreach (var segment in parts[1][2..].Split('.'))
            {
                var aritySeparator = segment.LastIndexOf('`');
                var name = aritySeparator < 0 ? segment : segment[..aritySeparator];
                var arity = 0;
                if (aritySeparator >= 0 && !int.TryParse(segment[(aritySeparator + 1)..], out arity))
                    return Array.Empty<ISymbol>();
                containers = containers.SelectMany(c => c.GetMembers(name).OfType<INamespaceOrTypeSymbol>())
                    .Where(c => c is INamespaceSymbol ? aritySeparator < 0 : ((INamedTypeSymbol)c).Arity == arity)
                    .ToArray();
            }
            foreach (var owner in containers.OfType<INamedTypeSymbol>())
            {
                // Indexers have a source lookup name of this[] and a metadata name
                // of Item. Match the persisted metadata locator on both surfaces.
                IEnumerable<ISymbol> candidates = parsed.Kind == "type" ? new[] { owner }
                    : owner.GetMembers().Where(m => m.MetadataName == parts[2]);
                foreach (var candidate in candidates)
                {
                    if (CanonicalSymbolFormat.BuildSymbolId(candidate, CanonicalSymbolIdVersion.AssemblyQualifiedV2) == parsed.CompleteId)
                        matches.Add(candidate);
                }
            }
        }
        return matches;
    }

    private void EnsureWorkspace()
    {
        if (_workspace != null) return;

        _workspace = new AdhocWorkspace();
        var solutionInfo = SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Default);
        _workspace.AddSolution(solutionInfo);

        // Track project IDs by module name for ProjectReference linking.
        var projectIds = new Dictionary<string, ProjectId>(StringComparer.Ordinal);

        // Create all projects with metadata references.
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
                projectId, VersionStamp.Default, name, compilation.AssemblyName ?? name, LanguageNames.CSharp,
                metadataReferences: metadataRefs,
                parseOptions: compilation.SyntaxTrees.FirstOrDefault()?.Options,
                compilationOptions: compilation.Options);
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

        // Add ProjectReference links between modules. Replaces the downgraded
        // PE metadata references with proper project links so Roslyn's
        // SymbolFinder can follow cross-assembly references.
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

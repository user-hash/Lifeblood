using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using DomainSymbolKind = Lifeblood.Domain.Graph.SymbolKind;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Reference implementation. Left-side adapter. Workspace-scoped.
/// Parses .csproj files directly (no MSBuild dependency).
/// Uses Roslyn CSharpCompilation for semantic analysis.
/// Cross-module resolution: compilations built in dependency order with CompilationReferences.
/// INV-ADAPT-002: C# adapter is the reference. Most complete, best tested.
/// </summary>
public sealed class RoslynWorkspaceAnalyzer : IWorkspaceAnalyzer
{
    private readonly IFileSystem _fs;
    private readonly RoslynModuleDiscovery _discovery;
    private readonly RoslynSymbolExtractor _symbolExtractor = new();
    private readonly RoslynEdgeExtractor _edgeExtractor = new();

    /// <summary>
    /// BCL references loaded once, reused for all compilations.
    /// Uses the runtime directory to get all framework assemblies.
    /// </summary>
    private static readonly Lazy<MetadataReference[]> BclReferences = new(LoadBclReferences);

    public RoslynWorkspaceAnalyzer(IFileSystem fs)
    {
        _fs = fs;
        _discovery = new RoslynModuleDiscovery(fs);
    }

    public AdapterCapability Capability => RoslynCapabilityDescriptor.Capability;

    public SemanticGraph AnalyzeWorkspace(string projectRoot, AnalysisConfig config)
    {
        var modules = _discovery.DiscoverModules(projectRoot);
        var builder = new GraphBuilder();
        var compilations = new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal);

        // Phase 1: Create module symbols
        foreach (var module in modules)
        {
            builder.AddSymbol(new Symbol
            {
                Id = SymbolIds.Module(module.Name),
                Name = module.Name,
                QualifiedName = module.Name,
                Kind = DomainSymbolKind.Module,
                Properties = module.Properties,
            });
        }

        // Phase 1.5: Topological sort — build leaf modules first so dependents can reference them
        var sorted = TopologicalSort(modules);

        // Phase 1.6: Build compilations in dependency order with cross-module references
        foreach (var module in sorted)
        {
            var depCompilations = module.Dependencies
                .Where(compilations.ContainsKey)
                .Select(d => compilations[d])
                .ToArray();

            var compilation = CreateCompilation(module, projectRoot, config, depCompilations);
            if (compilation != null)
                compilations[module.Name] = compilation;
        }

        // Phase 2: Extract symbols and edges from each compilation
        foreach (var module in modules)
        {
            if (!compilations.TryGetValue(module.Name, out var compilation)) continue;

            var moduleId = SymbolIds.Module(module.Name);

            foreach (var tree in compilation.SyntaxTrees)
            {
                if (string.IsNullOrEmpty(tree.FilePath)) continue;

                var model = compilation.GetSemanticModel(tree);
                var relPath = Path.GetRelativePath(projectRoot, tree.FilePath).Replace('\\', '/');

                // File symbol
                var fileId = SymbolIds.File(relPath);
                builder.AddSymbol(new Symbol
                {
                    Id = fileId,
                    Name = Path.GetFileName(tree.FilePath),
                    QualifiedName = $"{module.Name}/{relPath}",
                    Kind = DomainSymbolKind.File,
                    FilePath = relPath,
                    ParentId = moduleId,
                });

                // Type/method/field symbols
                var symbols = _symbolExtractor.Extract(model, tree.GetRoot(), relPath, fileId);
                builder.AddSymbols(symbols);

                // Dependency edges (calls, references, inherits, implements)
                var edges = _edgeExtractor.Extract(model, tree.GetRoot());
                builder.AddEdges(edges);
            }
        }

        // Phase 3: Module dependency edges
        var moduleNames = new HashSet<string>(modules.Select(m => m.Name), StringComparer.Ordinal);
        foreach (var module in modules)
        {
            var sourceId = SymbolIds.Module(module.Name);
            foreach (var dep in module.Dependencies)
            {
                if (!moduleNames.Contains(dep)) continue;
                builder.AddEdge(new Edge
                {
                    SourceId = sourceId,
                    TargetId = SymbolIds.Module(dep),
                    Kind = EdgeKind.DependsOn,
                    Evidence = new Evidence
                    {
                        Kind = EvidenceKind.Semantic,
                        AdapterName = "Roslyn",
                        Confidence = ConfidenceLevel.Proven,
                    },
                });
            }
        }

        return builder.Build();
    }

    /// <summary>
    /// Build a CSharpCompilation for a module, with full BCL + cross-module references.
    /// Dependency compilations are passed in so Roslyn can resolve types across project boundaries.
    /// </summary>
    private CSharpCompilation? CreateCompilation(
        ModuleInfo module, string projectRoot, AnalysisConfig config,
        CSharpCompilation[] dependencyCompilations)
    {
        var sourceFiles = module.FilePaths
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(_fs.FileExists);

        if (config.ExcludePatterns.Length > 0)
        {
            sourceFiles = sourceFiles.Where(f =>
            {
                var rel = Path.GetRelativePath(projectRoot, f);
                return !config.ExcludePatterns.Any(p => rel.Contains(p, StringComparison.OrdinalIgnoreCase));
            });
        }

        var trees = sourceFiles
            .Select(f =>
            {
                try { return CSharpSyntaxTree.ParseText(_fs.ReadAllText(f), path: f); }
                catch (IOException) { return null; }
            })
            .Where(t => t != null)
            .ToArray();

        if (trees.Length == 0) return null;

        // BCL references (cached, loaded once)
        var references = new List<MetadataReference>(BclReferences.Value);

        // Cross-module references: add CompilationReference for each resolved dependency
        foreach (var dep in dependencyCompilations)
            references.Add(dep.ToMetadataReference());

        return CSharpCompilation.Create(
            module.Name,
            trees!,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <summary>
    /// Load all .NET runtime assemblies as metadata references.
    /// Cached once — same for all modules in the workspace.
    /// </summary>
    private static MetadataReference[] LoadBclReferences()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir == null) return Array.Empty<MetadataReference>();

        return Directory.GetFiles(runtimeDir, "*.dll")
            .Select(path =>
            {
                try { return (MetadataReference)MetadataReference.CreateFromFile(path); }
                catch { return null; }
            })
            .Where(r => r != null)
            .ToArray()!;
    }

    /// <summary>
    /// Topological sort: returns modules in dependency-first order.
    /// Leaf modules (no deps) come first, dependents come last.
    /// Handles cycles gracefully — breaks them by processing what's available.
    /// </summary>
    private static ModuleInfo[] TopologicalSort(ModuleInfo[] modules)
    {
        var lookup = modules.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var visited = new Dictionary<string, bool>(StringComparer.Ordinal); // true = permanent, false = temporary
        var result = new List<ModuleInfo>();

        foreach (var module in modules)
        {
            if (!visited.ContainsKey(module.Name))
                Visit(module, lookup, visited, result);
        }

        return result.ToArray();
    }

    private static void Visit(
        ModuleInfo module,
        Dictionary<string, ModuleInfo> lookup,
        Dictionary<string, bool> visited,
        List<ModuleInfo> result)
    {
        if (visited.TryGetValue(module.Name, out var permanent))
        {
            if (permanent) return; // already processed
            return; // cycle detected — break it by skipping (dependency order is best-effort)
        }

        visited[module.Name] = false; // temporary mark

        foreach (var dep in module.Dependencies)
        {
            if (lookup.TryGetValue(dep, out var depModule))
                Visit(depModule, lookup, visited, result);
        }

        visited[module.Name] = true; // permanent mark
        result.Add(module);
    }
}

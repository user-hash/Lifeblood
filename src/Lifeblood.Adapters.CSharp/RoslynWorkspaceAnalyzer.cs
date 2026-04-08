using System.Text.Json;
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

    /// <summary>
    /// Retained after AnalyzeWorkspace — available for write-side tools (diagnostics, execution, refactoring).
    /// Previously discarded after extraction. Now preserved for bidirectional Roslyn support.
    /// </summary>
    private Dictionary<string, CSharpCompilation>? _compilations;

    /// <summary>
    /// Compilations built during analysis, keyed by module (assembly) name.
    /// Null before AnalyzeWorkspace is called. Available for write-side operations after analysis.
    /// </summary>
    public IReadOnlyDictionary<string, CSharpCompilation>? Compilations => _compilations;

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
        _compilations = new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal);

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
                .Where(_compilations.ContainsKey)
                .Select(d => _compilations[d])
                .ToArray();

            var compilation = CreateCompilation(module, projectRoot, config, depCompilations);
            if (compilation != null)
                _compilations[module.Name] = compilation;
        }

        // Phase 2: Extract symbols and edges from each compilation
        foreach (var module in modules)
        {
            if (!_compilations.TryGetValue(module.Name, out var compilation)) continue;

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
    /// Build a CSharpCompilation for a module, with full BCL + NuGet + cross-module references.
    /// Dependency compilations are passed in so Roslyn can resolve types across project boundaries.
    /// NuGet packages resolved from obj/project.assets.json (generated by dotnet restore).
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

        // NuGet package references from project.assets.json
        var nugetRefs = ResolveNuGetReferences(module, projectRoot);
        references.AddRange(nugetRefs);

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
    /// Resolve NuGet package assemblies from obj/project.assets.json.
    /// This file is generated by 'dotnet restore' and maps each package to its compile-time DLLs.
    /// Falls back gracefully if the file doesn't exist (pre-restore state).
    /// </summary>
    private MetadataReference[] ResolveNuGetReferences(ModuleInfo module, string projectRoot)
    {
        if (!module.Properties.TryGetValue("projectFile", out var relCsproj))
            return Array.Empty<MetadataReference>();

        var csprojPath = Path.GetFullPath(Path.Combine(projectRoot, relCsproj));
        var objDir = Path.Combine(Path.GetDirectoryName(csprojPath)!, "obj");
        var assetsPath = Path.Combine(objDir, "project.assets.json");

        if (!_fs.FileExists(assetsPath))
            return Array.Empty<MetadataReference>();

        try
        {
            var json = _fs.ReadAllText(assetsPath);
            using var doc = JsonDocument.Parse(json);

            // Find the package folder (usually ~/.nuget/packages/)
            var packageFolders = new List<string>();
            if (doc.RootElement.TryGetProperty("packageFolders", out var folders))
            {
                foreach (var folder in folders.EnumerateObject())
                    packageFolders.Add(folder.Name);
            }

            if (packageFolders.Count == 0)
                return Array.Empty<MetadataReference>();

            // Find the target framework matching net8.0 (or first available)
            if (!doc.RootElement.TryGetProperty("targets", out var targets))
                return Array.Empty<MetadataReference>();

            JsonElement targetPackages = default;
            foreach (var target in targets.EnumerateObject())
            {
                targetPackages = target.Value;
                break; // Use first target framework
            }

            if (targetPackages.ValueKind != JsonValueKind.Object)
                return Array.Empty<MetadataReference>();

            var references = new List<MetadataReference>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var package in targetPackages.EnumerateObject())
            {
                if (!package.Value.TryGetProperty("compile", out var compileAssets))
                    continue;

                // Package name is "PackageName/Version"
                var pkgId = package.Name;

                foreach (var asset in compileAssets.EnumerateObject())
                {
                    var relativeDll = asset.Name;
                    if (relativeDll == "_._") continue; // Placeholder, no actual DLL
                    if (!relativeDll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;

                    foreach (var folder in packageFolders)
                    {
                        var dllPath = Path.Combine(folder, pkgId.ToLowerInvariant(), relativeDll);
                        if (!seen.Add(dllPath)) continue;

                        if (File.Exists(dllPath) && !IsNativeDll(dllPath))
                        {
                            try
                            {
                                references.Add(MetadataReference.CreateFromFile(dllPath));
                            }
                            catch { /* Skip unloadable assemblies */ }
                        }
                    }
                }
            }

            return references.ToArray();
        }
        catch
        {
            return Array.Empty<MetadataReference>(); // Graceful degradation
        }
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
            .Where(path => !IsNativeDll(path))
            .Select(path =>
            {
                try { return (MetadataReference)MetadataReference.CreateFromFile(path); }
                catch { return null; }
            })
            .Where(r => r != null)
            .ToArray()!;
    }

    /// <summary>
    /// Filter out native (non-.NET) DLLs that Roslyn cannot load as metadata.
    /// These cause CS0009 "Metadata file could not be opened" errors.
    /// Check by attempting to read PE metadata — native DLLs have no CLI header.
    /// </summary>
    private static bool IsNativeDll(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var peReader = new System.Reflection.PortableExecutable.PEReader(stream);
            return !peReader.HasMetadata;
        }
        catch
        {
            return true; // If we can't read it, treat as native
        }
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

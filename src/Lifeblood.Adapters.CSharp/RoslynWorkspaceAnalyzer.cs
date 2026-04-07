using Lifeblood.Adapters.CSharp.Internal;
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
/// INV-ADAPT-002: C# adapter is the reference. Most complete, best tested.
/// </summary>
public sealed class RoslynWorkspaceAnalyzer : IWorkspaceAnalyzer
{
    private readonly RoslynModuleDiscovery _discovery = new();
    private readonly RoslynSymbolExtractor _symbolExtractor = new();
    private readonly RoslynEdgeExtractor _edgeExtractor = new();

    public AdapterCapability Capability => RoslynCapabilityDescriptor.Capability;

    public SemanticGraph AnalyzeWorkspace(string projectRoot, AnalysisConfig config)
    {
        var modules = _discovery.DiscoverModules(projectRoot);
        var builder = new GraphBuilder();
        var compilations = new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal);

        // Phase 1: Create module symbols and compilations
        foreach (var module in modules)
        {
            var moduleId = SymbolIds.Module(module.Name);
            builder.AddSymbol(new Symbol
            {
                Id = moduleId,
                Name = module.Name,
                QualifiedName = module.Name,
                Kind = DomainSymbolKind.Module,
                Properties = module.Properties,
            });

            var compilation = CreateCompilation(module, projectRoot, config);
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

    private static CSharpCompilation? CreateCompilation(
        ModuleInfo module, string projectRoot, AnalysisConfig config)
    {
        var sourceFiles = module.FilePaths
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(File.Exists);

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
                try { return CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f); }
                catch { return null; }
            })
            .Where(t => t != null)
            .ToArray();

        if (trees.Length == 0) return null;

        var references = new List<MetadataReference>();
        var objectAssembly = typeof(object).Assembly.Location;
        if (!string.IsNullOrEmpty(objectAssembly) && File.Exists(objectAssembly))
            references.Add(MetadataReference.CreateFromFile(objectAssembly));

        var runtimeDir = Path.GetDirectoryName(objectAssembly);
        if (runtimeDir != null)
        {
            var systemRuntime = Path.Combine(runtimeDir, "System.Runtime.dll");
            if (File.Exists(systemRuntime))
                references.Add(MetadataReference.CreateFromFile(systemRuntime));
        }

        return CSharpCompilation.Create(
            module.Name,
            trees!,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}

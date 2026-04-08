using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Microsoft.CodeAnalysis.CSharp;
using DomainSymbolKind = Lifeblood.Domain.Graph.SymbolKind;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Reference implementation. Left-side adapter. Workspace-scoped.
/// Orchestrates: module discovery → streaming compilation → symbol/edge extraction → graph build.
///
/// Memory architecture:
///   Default mode (RetainCompilations=false): each module is compiled, extracted, then
///   downgraded to a lightweight PE metadata reference (~10-100KB vs ~200MB). Peak memory
///   is O(1 compilation + N downgraded refs). Safe for 100+ module projects.
///
///   Retained mode (RetainCompilations=true): full compilations are kept for write-side
///   tools (FindReferences, Rename, Execute). Higher memory but necessary for interactive use.
///
/// INV-ADAPT-002: C# adapter is the reference. Most complete, best tested.
/// </summary>
public sealed class RoslynWorkspaceAnalyzer : IWorkspaceAnalyzer
{
    private readonly IFileSystem _fs;
    private readonly RoslynModuleDiscovery _discovery;
    private readonly RoslynSymbolExtractor _symbolExtractor = new();
    private readonly RoslynEdgeExtractor _edgeExtractor = new();

    private Dictionary<string, CSharpCompilation>? _compilations;

    /// <summary>
    /// Compilations retained during analysis (only when RetainCompilations=true).
    /// Null when streaming mode was used. Available for write-side operations after analysis.
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

        // Phase 1: Create module symbols (lightweight — just names and metadata)
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

        // Phase 2+3: Streaming compilation + extraction.
        // Each module is compiled, extracted, then downgraded (unless RetainCompilations=true).
        // Memory: O(1 compilation) instead of O(N compilations).
        var refCache = new SharedMetadataReferenceCache();
        var compilationBuilder = new ModuleCompilationBuilder(_fs, refCache);

        _compilations = compilationBuilder.ProcessInOrder(
            modules, projectRoot, config,
            processor: (module, compilation) =>
            {
                var moduleId = SymbolIds.Module(module.Name);

                foreach (var tree in compilation.SyntaxTrees)
                {
                    if (string.IsNullOrEmpty(tree.FilePath)) continue;

                    var model = compilation.GetSemanticModel(tree);
                    var relPath = Path.GetRelativePath(projectRoot, tree.FilePath).Replace('\\', '/');

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

                    builder.AddSymbols(_symbolExtractor.Extract(model, tree.GetRoot(), relPath, fileId));
                    builder.AddEdges(_edgeExtractor.Extract(model, tree.GetRoot()));
                }
            });

        // Phase 4: Module dependency edges
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
}

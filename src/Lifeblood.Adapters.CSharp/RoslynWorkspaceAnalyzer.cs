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
/// Incremental re-analyze:
///   After the first full analysis, subsequent calls with the same projectRoot can use
///   IncrementalAnalyze() to only recompile modules whose files changed. The per-file
///   extraction cache enables surgical replacement without full reprocessing.
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
    private AnalysisSnapshot? _snapshot;

    /// <summary>
    /// Compilations retained during analysis (only when RetainCompilations=true).
    /// Null when streaming mode was used. Available for write-side operations after analysis.
    /// </summary>
    public IReadOnlyDictionary<string, CSharpCompilation>? Compilations => _compilations;

    /// <summary>True if a previous analysis produced a snapshot that can be incrementally updated.</summary>
    public bool HasSnapshot => _snapshot != null;

    public RoslynWorkspaceAnalyzer(IFileSystem fs)
    {
        _fs = fs;
        _discovery = new RoslynModuleDiscovery(fs);
    }

    public AdapterCapability Capability => RoslynCapabilityDescriptor.Capability;

    public SemanticGraph AnalyzeWorkspace(string projectRoot, AnalysisConfig config)
    {
        var modules = _discovery.DiscoverModules(projectRoot);

        var snapshot = new AnalysisSnapshot
        {
            ProjectRoot = projectRoot,
            Modules = modules,
        };

        // Phase 1: Create module symbols (lightweight — just names and metadata)
        foreach (var module in modules)
        {
            snapshot.ModuleSymbols.Add(new Symbol
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
                    var fileSymbol = new Symbol
                    {
                        Id = fileId,
                        Name = Path.GetFileName(tree.FilePath),
                        QualifiedName = $"{module.Name}/{relPath}",
                        Kind = DomainSymbolKind.File,
                        FilePath = relPath,
                        ParentId = moduleId,
                    };

                    var symbols = _symbolExtractor.Extract(model, tree.GetRoot(), relPath, fileId);
                    var edges = _edgeExtractor.Extract(model, tree.GetRoot());

                    snapshot.ReplaceFile(fileId, fileSymbol, symbols, edges);

                    // Record file timestamp for incremental change detection
                    snapshot.FileTimestamps[tree.FilePath] = _fs.GetLastWriteTimeUtc(tree.FilePath);
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
                snapshot.ModuleEdges.Add(new Edge
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

        _snapshot = snapshot;
        return snapshot.RebuildGraph();
    }

    /// <summary>
    /// Incremental re-analyze. Only recompiles modules whose source files changed since the
    /// last analysis. Returns an updated graph built from cached per-file data with changed
    /// files replaced.
    ///
    /// Returns (graph, changedFileCount). If changedFileCount == 0, the graph is unchanged.
    ///
    /// Limitations (v1):
    /// - Does not cascade to dependent modules when an API surface changes.
    ///   If you change a public type signature in module A, module B's edges referencing
    ///   that type may be stale. Do a full re-analyze to fix this.
    /// - Module additions/removals trigger a full re-analyze automatically.
    /// </summary>
    public (SemanticGraph graph, int changedFileCount) IncrementalAnalyze(AnalysisConfig config)
    {
        if (_snapshot == null)
            throw new InvalidOperationException("No previous analysis. Call AnalyzeWorkspace first.");

        var projectRoot = _snapshot.ProjectRoot;

        // Rediscover modules — cheap, just XML parsing
        var currentModules = _discovery.DiscoverModules(projectRoot);

        // If module set changed (added/removed), fall back to full re-analyze
        var prevModuleNames = new HashSet<string>(_snapshot.Modules.Select(m => m.Name), StringComparer.Ordinal);
        var currModuleNames = new HashSet<string>(currentModules.Select(m => m.Name), StringComparer.Ordinal);
        if (!prevModuleNames.SetEquals(currModuleNames))
        {
            var graph = AnalyzeWorkspace(projectRoot, config);
            // Return all files as "changed" since we did a full re-analyze
            return (graph, _snapshot!.FileTimestamps.Count);
        }

        // Detect changed files by timestamp comparison
        var changedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // absolute paths
        var changedModules = new HashSet<string>(StringComparer.Ordinal); // module names
        var moduleByFile = BuildModuleFileIndex(currentModules);

        foreach (var module in currentModules)
        {
            foreach (var filePath in module.FilePaths)
            {
                if (!filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                if (!_fs.FileExists(filePath)) continue;

                var currentTimestamp = _fs.GetLastWriteTimeUtc(filePath);
                if (_snapshot.FileTimestamps.TryGetValue(filePath, out var prevTimestamp)
                    && currentTimestamp == prevTimestamp)
                    continue;

                changedFiles.Add(filePath);
                changedModules.Add(module.Name);
            }
        }

        // Also check for deleted files (in previous snapshot but not in current modules)
        var currentFilePaths = new HashSet<string>(
            currentModules.SelectMany(m => m.FilePaths), StringComparer.OrdinalIgnoreCase);
        foreach (var prevFile in _snapshot.FileTimestamps.Keys)
        {
            if (!currentFilePaths.Contains(prevFile))
            {
                var relPath = Path.GetRelativePath(projectRoot, prevFile).Replace('\\', '/');
                var fileId = SymbolIds.File(relPath);
                _snapshot.RemoveFile(fileId);
                _snapshot.FileTimestamps.Remove(prevFile);
            }
        }

        if (changedFiles.Count == 0)
            return (_snapshot.RebuildGraph(), 0);

        // Recompile only changed modules
        var modulesToRecompile = currentModules
            .Where(m => changedModules.Contains(m.Name))
            .ToArray();

        var refCache = new SharedMetadataReferenceCache();
        var compilationBuilder = new ModuleCompilationBuilder(_fs, refCache);

        // For incremental, we always retain compilations (MCP server mode)
        var newCompilations = compilationBuilder.ProcessInOrder(
            modulesToRecompile, projectRoot, config,
            processor: (module, compilation) =>
            {
                var moduleId = SymbolIds.Module(module.Name);

                foreach (var tree in compilation.SyntaxTrees)
                {
                    if (string.IsNullOrEmpty(tree.FilePath)) continue;

                    // Only re-extract changed files
                    if (!changedFiles.Contains(tree.FilePath)) continue;

                    var model = compilation.GetSemanticModel(tree);
                    var relPath = Path.GetRelativePath(projectRoot, tree.FilePath).Replace('\\', '/');

                    var fileId = SymbolIds.File(relPath);
                    var fileSymbol = new Symbol
                    {
                        Id = fileId,
                        Name = Path.GetFileName(tree.FilePath),
                        QualifiedName = $"{module.Name}/{relPath}",
                        Kind = DomainSymbolKind.File,
                        FilePath = relPath,
                        ParentId = moduleId,
                    };

                    var symbols = _symbolExtractor.Extract(model, tree.GetRoot(), relPath, fileId);
                    var edges = _edgeExtractor.Extract(model, tree.GetRoot());

                    _snapshot.ReplaceFile(fileId, fileSymbol, symbols, edges);
                    _snapshot.FileTimestamps[tree.FilePath] = _fs.GetLastWriteTimeUtc(tree.FilePath);
                }
            });

        // Merge retained compilations: update changed modules, keep unchanged
        if (_compilations != null && newCompilations != null)
        {
            foreach (var (name, comp) in newCompilations)
                _compilations[name] = comp;
        }
        else if (newCompilations != null)
        {
            _compilations = newCompilations;
        }

        // Update module-level data (dependencies may have changed)
        _snapshot.Modules = currentModules;
        _snapshot.ModuleSymbols.Clear();
        _snapshot.ModuleEdges.Clear();

        foreach (var module in currentModules)
        {
            _snapshot.ModuleSymbols.Add(new Symbol
            {
                Id = SymbolIds.Module(module.Name),
                Name = module.Name,
                QualifiedName = module.Name,
                Kind = DomainSymbolKind.Module,
                Properties = module.Properties,
            });
        }

        var moduleNameSet = new HashSet<string>(currentModules.Select(m => m.Name), StringComparer.Ordinal);
        foreach (var module in currentModules)
        {
            var sourceId = SymbolIds.Module(module.Name);
            foreach (var dep in module.Dependencies)
            {
                if (!moduleNameSet.Contains(dep)) continue;
                _snapshot.ModuleEdges.Add(new Edge
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

        return (_snapshot.RebuildGraph(), changedFiles.Count);
    }

    private static Dictionary<string, string> BuildModuleFileIndex(ModuleInfo[] modules)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in modules)
            foreach (var file in module.FilePaths)
                index[file] = module.Name;
        return index;
    }
}

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
    private readonly IDefineProfileResolver _profileResolver;

    private Dictionary<string, CSharpCompilation>? _compilations;
    private AnalysisSnapshot? _snapshot;

    /// <summary>
    /// INV-MULTI-DEFINE-IOP-001. Name of the profile whose compilations are
    /// retained for write-side / IOperation tools. Equals the first profile
    /// in the active list (default Editor on Unity workspaces; only profile
    /// in single-profile back-compat). Null until first AnalyzeWorkspace
    /// call. Subsequent multi-profile passes use streaming mode and downgrade
    /// after extraction so peak memory stays at single-profile baseline.
    /// </summary>
    public string? RetainedProfileName { get; private set; }

    /// <summary>
    /// Compilations retained during analysis (only when RetainCompilations=true).
    /// Null when streaming mode was used. Available for write-side operations after analysis.
    /// </summary>
    public IReadOnlyDictionary<string, CSharpCompilation>? Compilations => _compilations;

    /// <summary>
    /// Module dependency map: module name → array of dependency module names.
    /// Available after analysis. Used by write-side tools to build AdhocWorkspace
    /// with proper ProjectReference links for cross-assembly FindReferences/Rename.
    /// </summary>
    public IReadOnlyDictionary<string, string[]>? ModuleDependencies => _moduleDependencies;
    private Dictionary<string, string[]>? _moduleDependencies;

    /// <summary>True if a previous analysis produced a snapshot that can be incrementally updated.</summary>
    public bool HasSnapshot => _snapshot != null;

    /// <summary>
    /// Files the analyzer declined to process during the most recent
    /// AnalyzeWorkspace / IncrementalAnalyze call. Empty when everything
    /// listed in the module csprojs parsed cleanly. Consumers surface this
    /// in the analyze response so users can see WHICH files were silently
    /// dropped and WHY.
    /// </summary>
    public IReadOnlyList<Lifeblood.Domain.Results.SkippedFile> SkippedFiles =>
        _snapshot?.SkippedFiles as IReadOnlyList<Lifeblood.Domain.Results.SkippedFile>
        ?? System.Array.Empty<Lifeblood.Domain.Results.SkippedFile>();

    public RoslynWorkspaceAnalyzer(IFileSystem fs)
        : this(fs, new DefaultDefineProfileResolver())
    {
    }

    /// <summary>INV-MULTI-DEFINE-RESOLVER-001 injection seam.</summary>
    public RoslynWorkspaceAnalyzer(IFileSystem fs, IDefineProfileResolver profileResolver)
    {
        _fs = fs;
        _discovery = new RoslynModuleDiscovery(fs);
        _profileResolver = profileResolver;
    }

    public AdapterCapability Capability => RoslynCapabilityDescriptor.Capability;

    /// <summary>Optional per-module progress callback. Set before calling AnalyzeWorkspace.</summary>
    public Action<string, int, int>? OnModuleProgress { get; set; }

    public SemanticGraph AnalyzeWorkspace(string projectRoot, AnalysisConfig config)
    {
        var modules = _discovery.DiscoverModules(projectRoot);

        var snapshot = new AnalysisSnapshot
        {
            ProjectRoot = projectRoot,
            Modules = modules,
        };

        // Record csproj file timestamps so incremental re-analyze can detect
        // csproj edits (which change discovered facts like BclOwnership) and
        // force module re-discovery + recompile. See INV-BCL-005 in
        // .claude/plans/bcl-ownership-fix.md.
        foreach (var module in modules)
        {
            if (!module.Properties.TryGetValue("projectFile", out var relCsproj)) continue;
            var csprojAbs = Path.GetFullPath(Path.Combine(projectRoot, relCsproj));
            if (_fs.FileExists(csprojAbs))
                snapshot.CsprojTimestamps[csprojAbs] = _fs.GetLastWriteTimeUtc(csprojAbs);
        }

        // Record *.asmdef timestamps. Unity workspaces declare module-level
        // options on asmdefs; their on-disk csprojs are generated from those
        // declarations. Editing an asmdef without forcing Unity to regenerate
        // csprojs leaves the on-disk csproj stale, so the csproj-timestamp
        // tracker alone misses the change. INV-UNITY-002.
        foreach (var asmdefAbs in _fs.FindFiles(projectRoot, "*.asmdef", recursive: true))
        {
            try
            {
                snapshot.AsmdefTimestamps[asmdefAbs] = _fs.GetLastWriteTimeUtc(asmdefAbs);
            }
            catch
            {
                // Best-effort scan; permission errors on individual files
                // shouldn't fail the entire analyze.
            }
        }

        // Create module symbols (lightweight — just names and metadata).
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

        // Streaming compilation + extraction: each module is compiled, extracted,
        // then downgraded (unless RetainCompilations=true). Memory: O(1 compilation)
        // instead of O(N compilations). Set known module assemblies so the edge
        // extractor creates cross-module edges (metadata symbols from other
        // analyzed modules are tracked, not filtered).
        _edgeExtractor.KnownModuleAssemblies = new HashSet<string>(
            modules.Select(m => m.Name), StringComparer.Ordinal);

        var refCache = new SharedMetadataReferenceCache();
        var compilationBuilder = new ModuleCompilationBuilder(_fs, refCache);

        // Full analyze: reset the snapshot's skipped-file list before the
        // pipeline runs so we don't accumulate stale entries from prior
        // incremental updates. Incremental analyze intentionally appends
        // rather than replaces — see the IncrementalAnalyze path.
        snapshot.SkippedFiles.Clear();
        // Full analyze starts with no prior knowledge of any module — clear
        // any carry-over downgraded refs so a re-analyze on the same
        // analyzer instance does not inherit stale PE images from a prior
        // project root. INV-INCREMENTAL-XREF-001.
        snapshot.DowngradedRefs.Clear();
        // Merge discovery-level skips (csproj lists a .cs file that doesn't
        // exist on disk) into the snapshot so users see them in the
        // analyze response alongside compilation-level skips.
        snapshot.SkippedFiles.AddRange(_discovery.LastDiscoverySkipped);

        // INV-MULTI-DEFINE-ANALYZE-001. Resolve the set of define profiles
        // to compile each module under. Default single-profile flow: the
        // resolver returns one identity Editor profile, no tagging happens,
        // wire shape stays byte-stable with pre-Wave-6 behavior. Multi-
        // profile flow: requested profile names (from AnalysisConfig) are
        // matched against resolver output; missing names throw so caller
        // sees the typo. First profile pass populates the snapshot via
        // ReplaceFile; subsequent passes use AppendProfileEdges to add
        // profile-tagged edges that GraphBuilder dedup-unions.
        var activeProfiles = ResolveActiveProfiles(projectRoot, config);
        var multiProfile = activeProfiles.Count > 1;
        RetainedProfileName = activeProfiles.Count > 0 ? activeProfiles[0].Name : null;

        for (var profileIndex = 0; profileIndex < activeProfiles.Count; profileIndex++)
        {
            var profile = activeProfiles[profileIndex];
            var isFirstProfile = profileIndex == 0;
            var profileTag = multiProfile ? profile.Name : null;
            var profileModules = ApplyProfileToModules(modules, profile);

            // INV-MULTI-DEFINE-IOP-001. First profile retains compilations per
            // caller config (typically true for write-side / IOperation tool
            // support). Subsequent profile passes force streaming mode so
            // their compilations downgrade after extraction — peak RAM stays
            // at single-profile baseline regardless of profile count.
            var profileConfig = isFirstProfile
                ? config
                : new AnalysisConfig
                {
                    ExcludePatterns = config.ExcludePatterns,
                    AllowFullFallback = config.AllowFullFallback,
                    DefineProfiles = config.DefineProfiles,
                    RetainCompilations = false,
                };

            var profileCompilations = compilationBuilder.ProcessInOrder(
                profileModules, projectRoot, profileConfig,
                onModuleProgress: OnModuleProgress,
                skippedCollector: isFirstProfile ? snapshot.SkippedFiles : null,
                carryDowngraded: isFirstProfile ? snapshot.DowngradedRefs : null,
                processor: (module, compilation) =>
                {
                    var moduleId = SymbolIds.Module(module.Name);

                    foreach (var tree in compilation.SyntaxTrees)
                    {
                        if (string.IsNullOrEmpty(tree.FilePath)) continue;
                        if (tree.FilePath.StartsWith("<")) continue;

                        var model = compilation.GetSemanticModel(tree);
                        var relPath = Path.GetRelativePath(projectRoot, tree.FilePath).Replace('\\', '/');

                        var fileId = SymbolIds.File(relPath);
                        var rawEdges = _edgeExtractor.Extract(model, tree.GetRoot(), relPath);
                        var taggedEdges = EdgeProfileTagger.Tag(rawEdges, profileTag);

                        if (isFirstProfile)
                        {
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
                            snapshot.ReplaceFile(fileId, fileSymbol, symbols, taggedEdges);
                            snapshot.FileTimestamps[tree.FilePath] = _fs.GetLastWriteTimeUtc(tree.FilePath);
                        }
                        else
                        {
                            snapshot.AppendProfileEdges(fileId, taggedEdges);
                        }
                    }
                });

            if (isFirstProfile) _compilations = profileCompilations;
        }

        // Module dependency edges.
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

        // Capture module dependency map for write-side workspace construction
        _moduleDependencies = BuildModuleDependencyMap(modules);

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
    public IncrementalAnalyzeResult IncrementalAnalyze(AnalysisConfig config)
    {
        // INV-ANALYZE-FALLBACK-001: NoPriorAnalysis is always Rejected.
        // We have no projectRoot to AnalyzeWorkspace against without a
        // snapshot, so AllowFullFallback cannot help here. The caller's
        // remediation is fixed: invoke AnalyzeWorkspace explicitly first.
        if (_snapshot == null)
        {
            return new IncrementalAnalyzeResult
            {
                Mode = IncrementalMode.Rejected,
                Graph = null,
                ChangedFileCount = 0,
                Reason = FallbackReason.NoPriorAnalysis,
                Detail = "No previous analysis snapshot. Call AnalyzeWorkspace first.",
            };
        }

        var projectRoot = _snapshot.ProjectRoot;

        // Rediscover modules — cheap, just XML parsing
        var currentModules = _discovery.DiscoverModules(projectRoot);

        // INV-ANALYZE-FALLBACK-001 site 1: module set drift. If modules were
        // added/removed since the snapshot we cannot safely walk per-file
        // (module-level facts like dependencies and BCL ownership need
        // re-derivation). Branch on the caller's AllowFullFallback policy.
        var prevModuleNames = new HashSet<string>(_snapshot.Modules.Select(m => m.Name), StringComparer.Ordinal);
        var currModuleNames = new HashSet<string>(currentModules.Select(m => m.Name), StringComparer.Ordinal);
        if (!prevModuleNames.SetEquals(currModuleNames))
        {
            return HandleFallback(
                config,
                projectRoot,
                FallbackReason.ModuleSetChanged,
                detail: $"Module set drift detected: previous={prevModuleNames.Count}, current={currModuleNames.Count}.");
        }

        // INV-ANALYZE-FALLBACK-001 site 2: descriptor (asmdef) drift.
        // Any *.asmdef edit, addition, or removal forces a full re-analyze
        // on this round. Unity csprojs
        // are generated from asmdefs; an asmdef edit not yet flushed
        // through Unity's csproj regeneration would leave the on-disk
        // csproj stale and the incremental walk would miss the change.
        // The check is symmetric — added or removed asmdef files also
        // trigger the full path. INV-UNITY-002. Reported as the
        // adapter-agnostic ModuleDescriptorChanged with Detail naming the
        // descriptor kind for human consumption.
        if (HasAsmdefDrift(projectRoot))
        {
            return HandleFallback(
                config,
                projectRoot,
                FallbackReason.ModuleDescriptorChanged,
                detail: "Unity asmdef edit/add/remove detected (descriptorKind=asmdef).");
        }

        // Detect changed files by timestamp comparison
        var changedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // absolute paths
        var changedModules = new HashSet<string>(StringComparer.Ordinal); // module names
        var moduleByFile = BuildModuleFileIndex(currentModules);

        // INV-BCL-005: csproj edits change discovered module facts (BclOwnership,
        // ExternalDllPaths, Dependencies) and require re-discovery + recompile —
        // not just per-file extraction replacement. Detect csproj-only edits FIRST,
        // mark every .cs file in those modules as changed, and let the existing
        // .cs-file loop add any source-only changes on top.
        // See .claude/plans/bcl-ownership-fix.md §8 for the failure mode this prevents.
        var csprojChangedModules = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in currentModules)
        {
            if (!module.Properties.TryGetValue("projectFile", out var relCsproj)) continue;
            var csprojAbs = Path.GetFullPath(Path.Combine(projectRoot, relCsproj));
            if (!_fs.FileExists(csprojAbs)) continue;

            var currentCsprojTs = _fs.GetLastWriteTimeUtc(csprojAbs);
            if (_snapshot.CsprojTimestamps.TryGetValue(csprojAbs, out var prevCsprojTs)
                && currentCsprojTs == prevCsprojTs)
                continue;

            csprojChangedModules.Add(module.Name);
            _snapshot.CsprojTimestamps[csprojAbs] = currentCsprojTs;
        }

        foreach (var module in currentModules)
        {
            // If this module's csproj changed, force every .cs file in it to be
            // recompiled even if no source-file timestamp changed. The new
            // ModuleInfo from rediscovery already has the fresh BclOwnership;
            // marking the files as changed routes them through the existing
            // recompilation pipeline below.
            bool csprojForcedRecompile = csprojChangedModules.Contains(module.Name);

            foreach (var filePath in module.FilePaths)
            {
                if (!filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                if (!_fs.FileExists(filePath)) continue;

                if (csprojForcedRecompile)
                {
                    changedFiles.Add(filePath);
                    changedModules.Add(module.Name);
                    continue;
                }

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

        // Symmetric pruning for the persistent downgraded-refs carry. Since
        // module-set drift goes through HandleFallback (full re-analyze or
        // Rejected), we know prevModuleNames.SetEquals(currModuleNames) here
        // and there are no stale module entries to evict. Asmdef drift is
        // handled by the same gate. Defensive sanity check would only fire
        // if the invariant above broke. INV-INCREMENTAL-XREF-001.

        if (changedFiles.Count == 0)
            return new IncrementalAnalyzeResult
            {
                Mode = IncrementalMode.Incremental,
                Graph = _snapshot.RebuildGraph(),
                ChangedFileCount = 0,
            };

        // Recompile only changed modules
        var modulesToRecompile = currentModules
            .Where(m => changedModules.Contains(m.Name))
            .ToArray();

        // Ensure cross-module edge extraction uses the full module set
        _edgeExtractor.KnownModuleAssemblies = new HashSet<string>(
            currentModules.Select(m => m.Name), StringComparer.Ordinal);

        var refCache = new SharedMetadataReferenceCache();
        var compilationBuilder = new ModuleCompilationBuilder(_fs, refCache);

        // Incremental: remove any previously-tracked skipped-file entries
        // for the modules we're about to recompile so the rerun produces a
        // fresh view of them. Skipped files for UNTOUCHED modules are
        // preserved because incremental never revisits those modules and
        // the user's existing list is still accurate.
        _snapshot.SkippedFiles.RemoveAll(sf =>
            changedModules.Contains(sf.ModuleName));

        // For incremental, we always retain compilations (MCP server mode).
        // INV-INCREMENTAL-XREF-001: thread the snapshot-owned downgraded
        // refs through so the changed modules' compilations resolve types
        // from UNCHANGED dependent modules via the carry. The carry is
        // mutated in-place; on return it reflects the current world (new
        // PE refs for recompiled modules, prior PE refs preserved for
        // untouched dependencies).
        var newCompilations = compilationBuilder.ProcessInOrder(
            modulesToRecompile, projectRoot, config,
            skippedCollector: _snapshot.SkippedFiles,
            carryDowngraded: _snapshot.DowngradedRefs,
            processor: (module, compilation) =>
            {
                var moduleId = SymbolIds.Module(module.Name);

                foreach (var tree in compilation.SyntaxTrees)
                {
                    if (string.IsNullOrEmpty(tree.FilePath)) continue;
                    if (tree.FilePath.StartsWith("<")) continue;

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
                    var edges = _edgeExtractor.Extract(model, tree.GetRoot(), relPath);

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

        _moduleDependencies = BuildModuleDependencyMap(currentModules);

        return new IncrementalAnalyzeResult
        {
            Mode = IncrementalMode.Incremental,
            Graph = _snapshot.RebuildGraph(),
            ChangedFileCount = changedFiles.Count,
        };
    }

    /// <summary>
    /// INV-ANALYZE-FALLBACK-001: branch on caller policy. Either widen scope
    /// to a full re-analyze (and report what happened) or refuse and surface
    /// the rejection so the caller decides next step. Adapter does not own
    /// the policy choice — caller does, via <see cref="AnalysisConfig.AllowFullFallback"/>.
    /// </summary>
    private IncrementalAnalyzeResult HandleFallback(
        AnalysisConfig config,
        string projectRoot,
        FallbackReason reason,
        string detail)
    {
        if (config.AllowFullFallback)
        {
            var graph = AnalyzeWorkspace(projectRoot, config);
            return new IncrementalAnalyzeResult
            {
                Mode = IncrementalMode.FullFallback,
                Graph = graph,
                ChangedFileCount = _snapshot!.FileTimestamps.Count,
                Reason = reason,
                Detail = detail,
            };
        }

        return new IncrementalAnalyzeResult
        {
            Mode = IncrementalMode.Rejected,
            Graph = null,
            ChangedFileCount = 0,
            Reason = reason,
            Detail = detail,
        };
    }

    private static Dictionary<string, string[]> BuildModuleDependencyMap(ModuleInfo[] modules)
    {
        var map = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var module in modules)
            map[module.Name] = module.Dependencies;
        return map;
    }

    /// <summary>
    /// INV-MULTI-DEFINE-ANALYZE-001. Resolve which define profiles to compile
    /// each module under. Empty / null <see cref="AnalysisConfig.DefineProfiles"/>
    /// returns the default resolver's first profile only (back-compat).
    /// Non-empty config narrows the resolver's profile list to caller-requested
    /// names; an unknown name throws so the caller sees the typo eagerly.
    /// </summary>
    private IReadOnlyList<DefineProfile> ResolveActiveProfiles(string projectRoot, AnalysisConfig config)
    {
        var available = _profileResolver.ResolveProfiles(projectRoot);
        if (config.DefineProfiles == null || config.DefineProfiles.Length == 0)
            return available.Count > 0 ? new[] { available[0] } : Array.Empty<DefineProfile>();

        var byName = available.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var selected = new List<DefineProfile>();
        foreach (var name in config.DefineProfiles)
        {
            if (!byName.TryGetValue(name, out var profile))
                throw new ArgumentException(
                    $"Unknown define profile '{name}'. Resolver '{_profileResolver.GetType().Name}' returned: {string.Join(", ", available.Select(p => p.Name))}.",
                    nameof(config));
            selected.Add(profile);
        }
        return selected;
    }

    /// <summary>INV-MULTI-DEFINE-APPLIER-001.</summary>
    private static ModuleInfo[] ApplyProfileToModules(ModuleInfo[] modules, DefineProfile profile)
    {
        var result = new ModuleInfo[modules.Length];
        for (var i = 0; i < modules.Length; i++)
            result[i] = DefineProfileApplier.WithProfileDefines(modules[i], profile);
        return result;
    }

    private static Dictionary<string, string> BuildModuleFileIndex(ModuleInfo[] modules)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in modules)
            foreach (var file in module.FilePaths)
                index[file] = module.Name;
        return index;
    }

    /// <summary>
    /// True when any *.asmdef under <paramref name="projectRoot"/> has a
    /// different mtime than the snapshot, has been added since the
    /// snapshot, or has been removed. INV-UNITY-002.
    /// </summary>
    private bool HasAsmdefDrift(string projectRoot)
    {
        if (_snapshot == null) return false;

        var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asmdefAbs in _fs.FindFiles(projectRoot, "*.asmdef", recursive: true))
        {
            current.Add(asmdefAbs);
            DateTime currentTs;
            try { currentTs = _fs.GetLastWriteTimeUtc(asmdefAbs); }
            catch { continue; }

            if (!_snapshot.AsmdefTimestamps.TryGetValue(asmdefAbs, out var prevTs)) return true; // new
            if (currentTs != prevTs) return true;                                                 // edited
        }

        // Removed file? Snapshot tracked it, current scan missed it.
        foreach (var prev in _snapshot.AsmdefTimestamps.Keys)
        {
            if (!current.Contains(prev)) return true;
        }

        return false;
    }
}

using System.Security.Cryptography;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.PathClassification;
using Lifeblood.Domain.Results;
using Lifeblood.Domain.Workspaces;
using Microsoft.CodeAnalysis;
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
public sealed class RoslynWorkspaceAnalyzer :
    IWorkspaceAnalyzer,
    IWorkspaceInputFingerprintProvider,
    IOperationFactProvider
{
    private readonly IFileSystem _fs;
    private readonly RoslynModuleDiscovery _discovery;
    private readonly CompilationTreeExtractor _treeExtractor = new();
    private readonly IDefineProfileResolver _profileResolver;
    private readonly NuGetReferenceResolver _nugetResolver;

    private Dictionary<string, CSharpCompilation>? _compilations;
    private AnalysisSnapshot? _snapshot;
    private WorkspaceSourcePathMap? _sourcePaths;
    private PackageSourceVisibilityReport? _packageSourceVisibility;
    private ProfileApplicabilityReport? _profileApplicability;

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
    /// INV-MULTI-DEFINE-INCREMENTAL-001. Profile names the most recent
    /// <see cref="AnalyzeWorkspace"/> ran under. <see cref="IncrementalAnalyze"/>
    /// replays this set over changed files so per-edge <c>Profiles[]</c>
    /// provenance survives a file-touch. <see cref="GraphSession"/> echoes
    /// it onto incremental analyze responses.
    /// </summary>
    public IReadOnlyList<string> RetainedProfileNames { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Compilations retained during analysis when RetainCompilations=true.
    /// Null under streaming mode. Available for write-side operations after analysis.
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
    /// Content-authoritative receipt for the exact source and descriptor
    /// inputs retained by the current adapter snapshot.
    /// </summary>
    public SourceFingerprint? CurrentSourceFingerprint => _snapshot == null
        ? null
        : _snapshot.BuildSourceFingerprint(
            _sourcePaths ?? WorkspaceSourcePathMap.Create(_snapshot.ProjectRoot));

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

    /// <summary>
    /// Current Unity package source visibility receipt. Populated for
    /// Unity-shaped workspaces during full and incremental analyze, null for
    /// non-Unity workspaces or before the first completed analyze.
    /// </summary>
    public PackageSourceVisibilityReport? PackageSourceVisibility => _packageSourceVisibility;

    /// <summary>
    /// Current define-profile module applicability receipt. Populated after
    /// full or incremental analyze for C# workspaces; the MCP layer projects it
    /// without re-deriving profile membership.
    /// </summary>
    public ProfileApplicabilityReport? ProfileApplicability => _profileApplicability;

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
        _nugetResolver = new NuGetReferenceResolver(fs);
    }

    public AdapterCapability Capability => RoslynCapabilityDescriptor.Capability;

    public WorkspaceAnalysisInputs CaptureAnalysisInputs(string projectRoot, AnalysisConfig config)
    {
        var modules = _discovery.DiscoverModules(projectRoot);
        var activeProfiles = ResolveActiveProfiles(projectRoot, config);
        var applicableModules = ApplyProfilesToModules(modules, activeProfiles);
        var packageWorkspace = UnityPackageSourceVisibilityBuilder.Discover(_fs, projectRoot);
        var sourcePaths = packageWorkspace.SourcePaths;
        var sourceContent = new Dictionary<string, ContentFingerprint>(StringComparer.OrdinalIgnoreCase);
        var excludePathGlobs = PathGlobMatcher.Compile(config.ExcludePathGlobs);

        foreach (var path in applicableModules
            .SelectMany(module => module.FilePaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || !_fs.FileExists(path))
            {
                continue;
            }

            var relativePath = sourcePaths.ToWorkspacePath(path);
            if (config.ExcludePatterns.Any(pattern =>
                    relativePath.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                || PathGlobMatcher.MatchesAny(excludePathGlobs, relativePath))
            {
                continue;
            }

            try
            {
                sourceContent[path] = SourceContentHasher.HashText(_fs.ReadAllText(path));
            }
            catch (IOException)
            {
                // Mirrors ModuleCompilationBuilder: an I/O-unreadable source
                // never enters the compilation or its retained fingerprint.
            }
        }

        var asmdefContent = new Dictionary<string, ContentFingerprint>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _fs.FindFiles(projectRoot, "*.asmdef", recursive: true))
        {
            try { asmdefContent[path] = HashTextDescriptor(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        var referenceContent = CaptureReferenceContent(applicableModules, projectRoot);
        var packageVisibilityContent =
            UnityPackageSourceVisibilityBuilder.CaptureInputFingerprints(_fs, packageWorkspace);
        var sourceFingerprint = WorkspaceInputFingerprintBuilder.Build(
            sourcePaths,
            sourceContent,
            _discovery.LastDescriptorContentHashes,
            asmdefContent,
            referenceContent,
            packageVisibilityContent);
        return new WorkspaceAnalysisInputs(
            activeProfiles.Select(profile => profile.Name),
            sourceFingerprint);
    }

    /// <summary>Optional per-module progress callback. Set before calling AnalyzeWorkspace.</summary>
    public Action<string, int, int>? OnModuleProgress { get; set; }

    /// <summary>
    /// Create an isolated incremental candidate. Roslyn compilations and
    /// metadata references are immutable and shared by value; every mutable
    /// dictionary/cache that an incremental pass can replace is copied.
    /// Publishing or discarding the returned analyzer therefore cannot mutate
    /// this committed analyzer.
    /// </summary>
    public RoslynWorkspaceAnalyzer ForkForIncrementalCandidate()
    {
        if (_snapshot == null)
            throw new InvalidOperationException("Cannot fork before the first successful workspace analysis.");

        return new RoslynWorkspaceAnalyzer(_fs, _profileResolver)
        {
            _snapshot = _snapshot.ForkForCandidate(),
            _compilations = _compilations == null
                ? null
                : new Dictionary<string, CSharpCompilation>(_compilations, StringComparer.Ordinal),
            _moduleDependencies = _moduleDependencies?.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToArray(),
                StringComparer.Ordinal),
            _sourcePaths = _sourcePaths,
            _packageSourceVisibility = _packageSourceVisibility,
            _profileApplicability = _profileApplicability,
            RetainedProfileName = RetainedProfileName,
            RetainedProfileNames = RetainedProfileNames.ToArray(),
            OnModuleProgress = OnModuleProgress,
        };
    }

    public SemanticGraph AnalyzeWorkspace(string projectRoot, AnalysisConfig config)
    {
        // INV-ANALYZE-STRUCTURED-FAILURE-001: track a coarse progress cursor so
        // any unexpected fault (e.g. a NullReference after Unity asset-import
        // churn, LB-INBOX-012) surfaces as a phase/module/file-scoped
        // WorkspaceAnalysisException instead of a raw exception on the wire.
        var phase = "discovery";
        string? cursorModule = null;
        string? cursorFile = null;
        string? cursorProfile = null;
        var reachedCompilation = false;
        try
        {
            var modules = _discovery.DiscoverModules(projectRoot);
            var activeProfiles = ResolveActiveProfiles(projectRoot, config);
            var applicableModules = ApplyProfilesToModules(modules, activeProfiles);
            var packageWorkspace = UnityPackageSourceVisibilityBuilder.Discover(_fs, projectRoot);
            var sourcePaths = packageWorkspace.SourcePaths;
            var packageSourceVisibility = UnityPackageSourceVisibilityBuilder.Build(
                _fs,
                packageWorkspace,
                applicableModules,
                config);
            var profileApplicability = BuildProfileApplicabilityReport(
                projectRoot,
                modules,
                activeProfiles);

            var snapshot = new AnalysisSnapshot
            {
                ProjectRoot = projectRoot,
                Modules = modules,
                ExcludePatterns = NormalizeExcludePatterns(config.ExcludePatterns),
                ExcludePathGlobs = NormalizeExcludePathGlobs(config.ExcludePathGlobs),
            };
            ReplaceDictionary(
                snapshot.DescriptorContentHashes,
                _discovery.LastDescriptorContentHashes);

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
                    snapshot.AsmdefContentHashes[asmdefAbs] = HashTextDescriptor(asmdefAbs);
                }
                catch
                {
                    // Best-effort scan; permission errors on individual files
                    // shouldn't fail the entire analyze.
                }
            }

            CaptureReferenceInputs(snapshot, applicableModules);
            CapturePackageVisibilityInputs(snapshot, packageWorkspace);

            // Create module symbols (lightweight — just names and metadata).
            foreach (var module in applicableModules)
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
            var knownModuleAssemblies = new HashSet<string>(
                applicableModules.Select(m => m.Name), StringComparer.Ordinal);

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
            // project root. INV-INCREMENTAL-XREF-001 + INV-MULTI-DEFINE-INCREMENTAL-001.
            snapshot.DowngradedRefsByProfile.Clear();
            // Merge discovery-level skips (csproj lists a .cs file that doesn't
            // exist on disk) into the snapshot so users see them in the
            // analyze response alongside compilation-level skips.
            var applicableModuleNames = new HashSet<string>(
                applicableModules.Select(module => module.Name),
                StringComparer.Ordinal);
            snapshot.SkippedFiles.AddRange(_discovery.LastDiscoverySkipped.Where(
                skipped => applicableModuleNames.Contains(skipped.ModuleName)));

            // INV-MULTI-DEFINE-ANALYZE-001 + INV-MULTI-DEFINE-INCREMENTAL-001.
            var multiProfile = activeProfiles.Count > 1;
            RetainedProfileName = activeProfiles.Count > 0 ? activeProfiles[0].Name : null;
            RetainedProfileNames = activeProfiles.Select(p => p.Name).ToArray();
            snapshot.ActiveProfiles = activeProfiles;

            phase = "compilation";
            reachedCompilation = true;

            for (var profileIndex = 0; profileIndex < activeProfiles.Count; profileIndex++)
            {
                var profile = activeProfiles[profileIndex];
                var isFirstProfile = profileIndex == 0;
                var profileTag = multiProfile ? profile.Name : null;
                cursorProfile = profile.Name;
                var profileModules = ApplyProfileToModules(modules, profile);
                var profileOwnedModuleNames = GetProfileOwnedModuleNames(
                    modules,
                    activeProfiles,
                    profileIndex);
                var profileSkippedFiles = isFirstProfile
                    ? snapshot.SkippedFiles
                    : new List<SkippedFile>();

                // INV-MULTI-DEFINE-IOP-001. First profile retains compilations per
                // caller config (typically true for write-side / IOperation tool
                // support). Subsequent profile passes force streaming mode so
                // their compilations downgrade after extraction — peak RAM stays
                // at single-profile baseline regardless of profile count.
                // Each module's first applicable profile owns its symbols,
                // source hashes, and skipped-file accounting.
                var profileConfig = isFirstProfile
                    ? config
                    : new AnalysisConfig
                    {
                        ExcludePatterns = config.ExcludePatterns,
                        ExcludePathGlobs = config.ExcludePathGlobs,
                        AuthoritativeChangedFiles = config.AuthoritativeChangedFiles,
                        AllowFullFallback = config.AllowFullFallback,
                        DefineProfiles = config.DefineProfiles,
                        RetainCompilations = false,
                    };

                // INV-MULTI-DEFINE-INCREMENTAL-001. Per-profile carry. Each profile
                // pass writes its own PE images into a dict keyed by the profile
                // name. Incremental re-analyze reads back the SAME profile's dict
                // so changed-modules' compilations resolve cross-project references
                // under the matching defines. Without per-profile keying, non-first
                // profile incremental passes silently bind every cross-project
                // dependency to the first profile's PE image (or to nothing if the
                // first profile pass left it null) and drop the edge.
                var profileCarry = GetOrCreateProfileCarry(snapshot, profile.Name);

                var profileCompilations = compilationBuilder.ProcessInOrder(
                    profileModules, projectRoot, profileConfig,
                    onModuleProgress: (name, idx, total) =>
                    {
                        cursorModule = name;
                        OnModuleProgress?.Invoke(name, idx, total);
                    },
                    skippedCollector: profileSkippedFiles,
                    carryDowngraded: profileCarry,
                    processor: (module, compilation) =>
                    {
                        cursorModule = module.Name;
                        var ownsSymbols = profileOwnedModuleNames.Contains(module.Name);
                        var extractedFiles = _treeExtractor.Extract(
                            compilation,
                            projectRoot,
                            module.Name,
                            profile.Name,
                            profileTag,
                            ownsSymbols,
                            knownModuleAssemblies,
                            sourcePaths: sourcePaths);

                        foreach (var extracted in extractedFiles)
                        {
                            cursorFile = extracted.PathIdentity.GraphPath;
                            if (ownsSymbols)
                            {
                                snapshot.ReplaceFile(
                                    extracted.FileId,
                                    extracted.FileSymbol!,
                                    extracted.Symbols!,
                                    extracted.Edges);
                                // Source-generated trees have no on-disk file. Tracking them in
                                // FileTimestamps makes the incremental deleted-file prune treat
                                // them as removed every run (they are never in module.FilePaths),
                                // silently dropping every generated symbol. INV-INCREMENTAL-XREF-001.
                                if (!extracted.PathIdentity.IsGenerated && _fs.FileExists(extracted.TreePath))
                                {
                                    snapshot.FileTimestamps[extracted.TreePath] =
                                        _fs.GetLastWriteTimeUtc(extracted.TreePath);
                                }
                            }
                            else
                            {
                                snapshot.AppendProfileEdges(extracted.FileId, extracted.Edges);
                            }
                        }
                        return true;
                    },
                    contentHashCollector: (path, hash) => snapshot.FileContentHashes[path] = hash,
                    sourcePaths: sourcePaths);

                if (!isFirstProfile)
                {
                    snapshot.SkippedFiles.AddRange(profileSkippedFiles.Where(
                        skipped => profileOwnedModuleNames.Contains(skipped.ModuleName)));
                }

                if (isFirstProfile) _compilations = profileCompilations;
            }

            phase = "module-edges";
            cursorModule = null;
            cursorFile = null;
            cursorProfile = null;

            // Module dependency edges.
            var moduleNames = new HashSet<string>(applicableModules.Select(m => m.Name), StringComparer.Ordinal);
            foreach (var module in applicableModules)
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
            _moduleDependencies = BuildModuleDependencyMap(
                activeProfiles.Count == 0
                    ? Array.Empty<ModuleInfo>()
                    : ApplyProfileToModules(modules, activeProfiles[0]));
            _sourcePaths = sourcePaths;
            _packageSourceVisibility = packageSourceVisibility;
            _profileApplicability = profileApplicability;

            _snapshot = snapshot;
            phase = "graph-build";
            return snapshot.RebuildGraph();
        }
        catch (WorkspaceAnalysisException)
        {
            throw;
        }
        catch (ArgumentException)
        {
            // Deliberate caller-input validation (e.g. an unknown define-profile
            // name from ResolveActiveProfiles) propagates as its typed contract.
            // INV-ANALYZE-STRUCTURED-FAILURE-001 wraps only UNEXPECTED faults, not
            // validation the caller is meant to catch by type.
            throw;
        }
        catch (Exception ex)
        {
            // INV-ANALYZE-STRUCTURED-FAILURE-001: never let an unexpected fault
            // escape the analyze pipeline raw. Wrap with the cursor so the wire
            // carries phase/module/file/profile context instead of an opaque message.
            throw new WorkspaceAnalysisException(
                phase, cursorModule, cursorFile, cursorProfile, !reachedCompilation, ex);
        }
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
                AcceptedChanges = AcceptedChangeSet.Create(ResolveChangeScanMode(config)),
                Reason = FallbackReason.NoPriorAnalysis,
                Detail = "No previous analysis snapshot. Call AnalyzeWorkspace first.",
            };
        }

        var projectRoot = _snapshot.ProjectRoot;

        var requestedExcludePatterns = NormalizeExcludePatterns(config.ExcludePatterns);
        var requestedExcludePathGlobs = NormalizeExcludePathGlobs(config.ExcludePathGlobs);
        if (!SamePathExclusions(_snapshot.ExcludePatterns, requestedExcludePatterns)
            || !SamePathExclusions(_snapshot.ExcludePathGlobs, requestedExcludePathGlobs))
        {
            return HandleFallback(
                config,
                projectRoot,
                FallbackReason.AnalysisScopeChanged,
                detail: "Analysis source-exclusion set changed; full re-analyze required to add/remove files from the cached graph scope.");
        }

        // Rediscover modules — cheap, just XML parsing
        var currentModules = _discovery.DiscoverModules(projectRoot);
        var snapshotProfiles = _snapshot.ActiveProfiles;
        var applicableCurrentModules = ApplyProfilesToModules(currentModules, snapshotProfiles);
        var profileApplicability = BuildProfileApplicabilityReport(
            projectRoot,
            currentModules,
            snapshotProfiles);

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

        if (HasReferenceInputDrift(applicableCurrentModules, projectRoot))
        {
            return HandleFallback(
                config,
                projectRoot,
                FallbackReason.ModuleDescriptorChanged,
                detail: "Referenced binary or source-generator input changed (descriptorKind=reference).");
        }

        var packageWorkspace = UnityPackageSourceVisibilityBuilder.Discover(_fs, projectRoot);
        var sourcePaths = packageWorkspace.SourcePaths;
        var packageSourceVisibility = UnityPackageSourceVisibilityBuilder.Build(
            _fs,
            packageWorkspace,
            applicableCurrentModules,
            config);
        CapturePackageVisibilityInputs(_snapshot, packageWorkspace);

        // Detect changed files by timestamp + content-hash comparison.
        // An editor/build integration can pass an authoritative changed-file
        // set to bound the source walk; descriptor-triggered recompiles still
        // win because they change module facts rather than source text.
        var changedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // absolute paths
        var changedModules = new HashSet<string>(StringComparer.Ordinal); // module names
        var previousModuleByFile = BuildModuleFileIndex(_snapshot.Modules);
        var authoritativeChangedFiles = NormalizeAuthoritativeChangedFiles(projectRoot, config.AuthoritativeChangedFiles);
        var scanMode = ResolveChangeScanMode(config);
        var mtimeTouchedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contentChangedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var descriptorForcedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            var currentDescriptorHash = _discovery.LastDescriptorContentHashes.TryGetValue(csprojAbs, out var discoveredHash)
                ? discoveredHash
                : HashTextDescriptor(csprojAbs);
            var contentChanged = !_snapshot.DescriptorContentHashes.TryGetValue(csprojAbs, out var previousDescriptorHash)
                || previousDescriptorHash != currentDescriptorHash;
            _snapshot.CsprojTimestamps[csprojAbs] = currentCsprojTs;
            _snapshot.DescriptorContentHashes[csprojAbs] = currentDescriptorHash;
            if (!contentChanged)
                continue;

            csprojChangedModules.Add(module.Name);
        }


        // Keep solution/project descriptor membership exact. A descriptor
        // change that leaves discovered module facts unchanged still changes
        // the source fingerprint and therefore the committed AnalysisKey.
        ReplaceDictionary(
            _snapshot.DescriptorContentHashes,
            _discovery.LastDescriptorContentHashes);

        foreach (var module in applicableCurrentModules)
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
                    TrackSourceTouch(filePath, mtimeTouchedFiles, contentChangedFiles);
                    changedFiles.Add(filePath);
                    descriptorForcedFiles.Add(filePath);
                    changedModules.Add(module.Name);
                    continue;
                }

                if (authoritativeChangedFiles != null
                    && !authoritativeChangedFiles.Contains(filePath))
                {
                    continue;
                }

                var currentTimestamp = _fs.GetLastWriteTimeUtc(filePath);
                var timestampChanged = !_snapshot.FileTimestamps.TryGetValue(filePath, out var prevTimestamp)
                    || currentTimestamp != prevTimestamp;

                if (!timestampChanged
                    && authoritativeChangedFiles == null
                    && _snapshot.FileContentHashes.ContainsKey(filePath))
                {
                    continue;
                }

                if (timestampChanged)
                    mtimeTouchedFiles.Add(filePath);

                if (HasSourceContentChanged(filePath, out var currentHash))
                {
                    contentChangedFiles.Add(filePath);
                    changedFiles.Add(filePath);
                    changedModules.Add(module.Name);
                }
                else
                {
                    _snapshot.FileTimestamps[filePath] = currentTimestamp;
                    if (currentHash != null)
                        _snapshot.FileContentHashes[filePath] = currentHash;
                }
            }
        }

        // Also check for deleted files (in previous snapshot but not in current modules)
        var currentFilePaths = new HashSet<string>(
            applicableCurrentModules.SelectMany(m => m.FilePaths), StringComparer.OrdinalIgnoreCase);
        var deletedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prevFile in _snapshot.FileTimestamps.Keys.ToArray())
        {
            if (!currentFilePaths.Contains(prevFile))
            {
                var relPath = sourcePaths.ToWorkspacePath(prevFile);
                var fileId = SymbolIds.File(relPath);
                if (previousModuleByFile.TryGetValue(prevFile, out var moduleName))
                    changedModules.Add(moduleName);
                _snapshot.RemoveFile(fileId);
                _snapshot.FileTimestamps.Remove(prevFile);
                _snapshot.FileContentHashes.Remove(prevFile);
                deletedFiles.Add(prevFile);
            }
        }

        // Symmetric pruning for the persistent downgraded-refs carry. Since
        // module-set drift goes through HandleFallback (full re-analyze or
        // Rejected), we know prevModuleNames.SetEquals(currModuleNames) here
        // and there are no stale module entries to evict. Asmdef drift is
        // handled by the same gate. Defensive sanity check would only fire
        // if the invariant above broke. INV-INCREMENTAL-XREF-001.

        if (changedFiles.Count == 0 && deletedFiles.Count == 0)
        {
            _sourcePaths = sourcePaths;
            _packageSourceVisibility = packageSourceVisibility;
            _profileApplicability = profileApplicability;
            return new IncrementalAnalyzeResult
            {
                Mode = IncrementalMode.Incremental,
                Graph = _snapshot.RebuildGraph(),
                AcceptedChanges = BuildAcceptedChanges(
                    sourcePaths,
                    scanMode,
                    changedFiles,
                    mtimeTouchedFiles,
                    contentChangedFiles,
                    descriptorForcedFiles,
                    deletedFiles),
            };
        }

        // Recompile only changed modules
        var modulesToRecompile = applicableCurrentModules
            .Where(m => changedModules.Contains(m.Name))
            .ToArray();

        // Ensure cross-module edge extraction uses the full module set
        var knownModuleAssemblies = new HashSet<string>(
            applicableCurrentModules.Select(m => m.Name), StringComparer.Ordinal);

        var refCache = new SharedMetadataReferenceCache();
        var compilationBuilder = new ModuleCompilationBuilder(_fs, refCache);

        // Incremental: remove any previously-tracked skipped-file entries
        // for the modules we're about to recompile so the rerun produces a
        // fresh view of them. Skipped files for UNTOUCHED modules are
        // preserved because incremental never revisits those modules and
        // the user's existing list is still accurate.
        _snapshot.SkippedFiles.RemoveAll(sf =>
            changedModules.Contains(sf.ModuleName));

        // INV-MULTI-DEFINE-INCREMENTAL-001 + INV-INCREMENTAL-XREF-001.
        // Replay the snapshot's profile set over changed files. Count == 1
        // collapses to a single null-tagged pass (byte-stable single-profile
        // back-compat). Count >= 2 tags edges per profile + AppendProfileEdges
        // unions them at RebuildGraph time. Defensive _default-profile fallback
        // covers the unreachable case where ActiveProfiles is empty.
        var snapshotMultiProfile = snapshotProfiles.Count > 1;
        var retainedProfileModules = snapshotProfiles.Count > 0
            ? ApplyProfileToModules(currentModules, snapshotProfiles[0])
            : applicableCurrentModules;
        Dictionary<string, Microsoft.CodeAnalysis.CSharp.CSharpCompilation>? newCompilations = null;

        var profilesToReplay = snapshotProfiles.Count > 0
            ? snapshotProfiles
            : (IReadOnlyList<DefineProfile>)new[]
            {
                new DefineProfile
                {
                    Name = "_default",
                    AddDefines = Array.Empty<string>(),
                    RemoveDefines = Array.Empty<string>(),
                },
            };

        for (var profileIndex = 0; profileIndex < profilesToReplay.Count; profileIndex++)
        {
            var profile = profilesToReplay[profileIndex];
            var isFirstProfile = profileIndex == 0;
            var profileTag = snapshotMultiProfile ? profile.Name : null;
            var profileModulesToRecompile = snapshotProfiles.Count > 0
                ? ApplyProfileToModules(modulesToRecompile, profile)
                : modulesToRecompile;
            var profileOwnedModuleNames = snapshotProfiles.Count > 0
                ? GetProfileOwnedModuleNames(currentModules, snapshotProfiles, profileIndex)
                : new HashSet<string>(
                    profileModulesToRecompile.Select(module => module.Name),
                    StringComparer.Ordinal);
            var profileSkippedFiles = isFirstProfile
                ? _snapshot.SkippedFiles
                : new List<SkippedFile>();

            // INV-MULTI-DEFINE-IOP-001. First profile retains compilations;
            // subsequent profiles force streaming so peak RAM stays at the
            // single-profile baseline. Each module's first applicable profile
            // owns its symbols and skipped-file accounting.
            var profileConfig = isFirstProfile
                ? config
                : new AnalysisConfig
                {
                    ExcludePatterns = config.ExcludePatterns,
                    ExcludePathGlobs = config.ExcludePathGlobs,
                    AuthoritativeChangedFiles = config.AuthoritativeChangedFiles,
                    AllowFullFallback = config.AllowFullFallback,
                    DefineProfiles = config.DefineProfiles,
                    RetainCompilations = false,
                };

            // INV-MULTI-DEFINE-INCREMENTAL-001. Per-profile carry. Recover the
            // snapshot's PE images for THIS profile so changed modules can
            // resolve unchanged cross-project dependencies under the matching
            // defines. Pre-fix this was first-profile-only — non-first profile
            // passes received null carry and the local `downgraded` dict in
            // ProcessInOrder started empty over a subset of modules, silently
            // dropping every cross-project edge whose target lived in an
            // unchanged module.
            var profileCarry = GetOrCreateProfileCarry(_snapshot, profile.Name);
            PruneProfileCarry(profileCarry, ApplyProfileToModules(currentModules, profile));

            var profileCompilations = compilationBuilder.ProcessInOrder(
                profileModulesToRecompile, projectRoot, profileConfig,
                skippedCollector: profileSkippedFiles,
                carryDowngraded: profileCarry,
                processor: (module, compilation) =>
                {
                    var ownsSymbols = profileOwnedModuleNames.Contains(module.Name);
                    var extractedFiles = _treeExtractor.Extract(
                        compilation,
                        projectRoot,
                        module.Name,
                        profile.Name,
                        profileTag,
                        ownsSymbols,
                        knownModuleAssemblies,
                        changedFiles,
                        sourcePaths);

                    foreach (var extracted in extractedFiles)
                    {
                        // Re-extract changed disk files AND every source-generated tree
                        // (no on-disk file) of this recompiling module: generated output
                        // can shift with any source edit and is not tracked per disk-file,
                        // so it must be rebuilt whenever its module recompiles, never pruned
                        // as a phantom deleted file. INV-INCREMENTAL-XREF-001.
                        if (ownsSymbols)
                        {
                            _snapshot.ReplaceFile(
                                extracted.FileId,
                                extracted.FileSymbol!,
                                extracted.Symbols!,
                                extracted.Edges);
                            // Same disk-file-lifecycle guard as the full path: never track a
                            // source-generated tree's timestamp. INV-INCREMENTAL-XREF-001.
                            if (!extracted.PathIdentity.IsGenerated && _fs.FileExists(extracted.TreePath))
                            {
                                _snapshot.FileTimestamps[extracted.TreePath] =
                                    _fs.GetLastWriteTimeUtc(extracted.TreePath);
                            }
                        }
                        else
                        {
                            _snapshot.AppendProfileEdges(extracted.FileId, extracted.Edges);
                        }
                    }
                    return true;
                },
                contentHashCollector: (path, hash) => _snapshot.FileContentHashes[path] = hash,
                sourcePaths: sourcePaths);

            if (!isFirstProfile)
            {
                _snapshot.SkippedFiles.AddRange(profileSkippedFiles.Where(
                    skipped => profileOwnedModuleNames.Contains(skipped.ModuleName)));
            }

            if (isFirstProfile) newCompilations = profileCompilations;
        }

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

        if (_compilations != null)
        {
            var retainedModuleNames = new HashSet<string>(
                retainedProfileModules.Select(module => module.Name),
                StringComparer.Ordinal);
            foreach (var name in _compilations.Keys.Where(
                         name => !retainedModuleNames.Contains(name)).ToArray())
            {
                _compilations.Remove(name);
            }
        }

        // Update module-level data (dependencies may have changed)
        _snapshot.Modules = currentModules;
        _snapshot.ModuleSymbols.Clear();
        _snapshot.ModuleEdges.Clear();

        foreach (var module in applicableCurrentModules)
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

        var moduleNameSet = new HashSet<string>(
            applicableCurrentModules.Select(m => m.Name),
            StringComparer.Ordinal);
        foreach (var module in applicableCurrentModules)
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

        _moduleDependencies = BuildModuleDependencyMap(retainedProfileModules);
        _sourcePaths = sourcePaths;
        _packageSourceVisibility = packageSourceVisibility;
        _profileApplicability = profileApplicability;

        return new IncrementalAnalyzeResult
        {
            Mode = IncrementalMode.Incremental,
            Graph = _snapshot.RebuildGraph(),
            AcceptedChanges = BuildAcceptedChanges(
                sourcePaths,
                scanMode,
                changedFiles,
                mtimeTouchedFiles,
                contentChangedFiles,
                descriptorForcedFiles,
                deletedFiles),
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
                AcceptedChanges = CaptureFullFallbackAcceptedChanges(),
                Reason = reason,
                Detail = detail,
            };
        }

        return new IncrementalAnalyzeResult
        {
            Mode = IncrementalMode.Rejected,
            Graph = null,
            AcceptedChanges = AcceptedChangeSet.Create(ResolveChangeScanMode(config)),
            Reason = reason,
            Detail = detail,
        };
    }

    /// <summary>
    /// Describes a completed full analysis when it was performed as the
    /// caller-approved fallback for an incremental request. A full pass
    /// reanalyzes every tracked source; it does not pretend each file's mtime
    /// or content changed when those causes were not measured.
    /// </summary>
    public AcceptedChangeSet CaptureFullFallbackAcceptedChanges()
    {
        var snapshot = _snapshot
            ?? throw new InvalidOperationException("No completed analysis snapshot is available.");
        var sourcePaths = _sourcePaths ?? WorkspaceSourcePathMap.Create(snapshot.ProjectRoot);
        return AcceptedChangeSet.Create(
            ChangeScanMode.FullFallback,
            fullFallback: true,
            reanalyzedSourceFiles: ToWorkspacePaths(
                sourcePaths,
                snapshot.FileTimestamps.Keys));
    }

    public PackageSourceVisibilityFile? ResolvePackageSource(string filePath)
    {
        if (_packageSourceVisibility == null || _snapshot == null || string.IsNullOrWhiteSpace(filePath))
            return null;

        string query;
        try
        {
            var fullPath = Path.IsPathRooted(filePath)
                ? Path.GetFullPath(filePath)
                : Path.GetFullPath(Path.Combine(_snapshot.ProjectRoot, filePath));
            query = (_sourcePaths ?? WorkspaceSourcePathMap.Create(_snapshot.ProjectRoot))
                .ToWorkspacePath(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            query = filePath.Trim().Replace('\\', '/');
        }

        foreach (var package in _packageSourceVisibility.Packages)
        {
            foreach (var file in package.Files)
            {
                if (PathComparer.Equals(file.Path, query))
                    return file;
            }
        }

        return null;
    }

    /// <summary>
    /// Stream occurrence-level facts from the retained primary profile or an
    /// exact-input-verified ephemeral compilation of another committed
    /// profile. Non-primary compilations are released module-by-module and
    /// never become another retained semantic base. INV-OPERATION-FACTS-001.
    /// </summary>
    public OperationFactScanReceipt ScanOperationFacts(
        OperationFactQuery query,
        Func<OperationFact, bool> consume,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(consume);
        if (query.MaxFacts <= 0)
            throw new ArgumentOutOfRangeException(nameof(query), "maxFacts must be greater than zero.");

        var snapshot = _snapshot
            ?? throw new InvalidOperationException("Operation facts require a completed workspace analysis.");
        var availableProfiles = snapshot.ActiveProfiles
            .Select(profile => profile.Name)
            .ToArray();
        var profileName = query.ProfileScope ?? RetainedProfileName
            ?? throw new InvalidOperationException("Operation facts require an analyzed define profile.");
        var profile = snapshot.ActiveProfiles.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, profileName, StringComparison.Ordinal));
        if (profile == null)
        {
            throw new ArgumentException(
                $"Requested profile '{profileName}' is not part of the committed analysis. " +
                $"Available profiles: {string.Join(", ", availableProfiles)}.",
                nameof(query));
        }

        if (string.Equals(profileName, RetainedProfileName, StringComparison.Ordinal)
            && _compilations is { Count: > 0 } retained)
        {
            return new RoslynOperationFactProvider(retained, profileName, availableProfiles)
                .Scan(query, consume, cancellationToken);
        }

        var profileModules = ApplyProfileToModules(snapshot.Modules, profile);
        var selectedModules = profileModules;
        if (!string.IsNullOrWhiteSpace(query.ModuleScope))
        {
            selectedModules = profileModules
                .Where(module => string.Equals(module.Name, query.ModuleScope, StringComparison.Ordinal))
                .ToArray();
        }

        var carryAvailable = snapshot.DowngradedRefsByProfile.TryGetValue(profileName, out var committedCarry);
        var modulesToCompile = !carryAvailable && selectedModules.Length < profileModules.Length
            ? profileModules
            : selectedModules;
        var drift = FindEphemeralOperationInputDrift(snapshot, modulesToCompile, cancellationToken);
        if (drift.Length > 0)
        {
            return new OperationFactScanReceipt
            {
                Status = OperationFactScanStatus.Rejected,
                RejectionReason = OperationFactRejectionReason.InputDrift,
                ProfileScope = profileName,
                AvailableProfiles = availableProfiles,
                ExecutionMode = OperationFactExecutionMode.EphemeralProfileCompilation,
                InputIdentityVerifiedAtStart = false,
                AdditionalSemanticBaseCount = 0,
                CompiledModuleCount = 0,
                ScannedModuleCount = 0,
                ScannedFileCount = 0,
                ObservedOperationCount = 0,
                EmittedFactCount = 0,
                Truncated = false,
                StoppedByConsumer = false,
                Limitations = new[]
                {
                    "Committed analysis inputs changed before ephemeral profile execution: " +
                    string.Join(", ", drift),
                },
            };
        }

        var carry = carryAvailable
            ? new Dictionary<string, MetadataReference>(committedCarry!, StringComparer.Ordinal)
            : new Dictionary<string, MetadataReference>(StringComparer.Ordinal);
        var config = new AnalysisConfig
        {
            ExcludePatterns = snapshot.ExcludePatterns,
            ExcludePathGlobs = snapshot.ExcludePathGlobs,
            DefineProfiles = new[] { profileName },
            RetainCompilations = false,
        };

        var compiledModules = 0;
        var scannedModules = 0;
        var scannedFiles = 0;
        var observedOperations = 0;
        var emittedFacts = 0;
        var truncated = false;
        var stoppedByConsumer = false;
        var builder = new ModuleCompilationBuilder(_fs, new SharedMetadataReferenceCache());
        builder.ProcessInOrder(
            modulesToCompile,
            snapshot.ProjectRoot,
            config,
            processor: (module, compilation) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                compiledModules++;
                var probingPastLimit = emittedFacts >= query.MaxFacts;
                var scopedQuery = CopyOperationFactQuery(
                    query,
                    profileName,
                    probingPastLimit ? 1 : query.MaxFacts - emittedFacts);
                var foundPastLimit = false;
                var receipt = new RoslynOperationFactProvider(
                        new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal)
                        {
                            [module.Name] = compilation,
                        },
                        profileName,
                        availableProfiles)
                    .Scan(
                        scopedQuery,
                        fact =>
                        {
                            if (probingPastLimit)
                            {
                                foundPastLimit = true;
                                return false;
                            }
                            return consume(fact);
                        },
                        cancellationToken);

                scannedModules += receipt.ScannedModuleCount;
                scannedFiles += receipt.ScannedFileCount;
                observedOperations += receipt.ObservedOperationCount;
                if (!probingPastLimit)
                    emittedFacts += receipt.EmittedFactCount;

                if (foundPastLimit || receipt.Truncated)
                {
                    truncated = true;
                    return false;
                }
                if (receipt.StoppedByConsumer)
                {
                    stoppedByConsumer = true;
                    return false;
                }
                return true;
            },
            carryDowngraded: carry,
            moduleUniverse: profileModules,
            sourcePaths: _sourcePaths ?? WorkspaceSourcePathMap.Create(snapshot.ProjectRoot));

        return new OperationFactScanReceipt
        {
            Status = OperationFactScanStatus.Completed,
            ProfileScope = profileName,
            AvailableProfiles = availableProfiles,
            ExecutionMode = OperationFactExecutionMode.EphemeralProfileCompilation,
            InputIdentityVerifiedAtStart = true,
            AdditionalSemanticBaseCount = 0,
            CompiledModuleCount = compiledModules,
            ScannedModuleCount = scannedModules,
            ScannedFileCount = scannedFiles,
            ObservedOperationCount = observedOperations,
            EmittedFactCount = emittedFacts,
            Truncated = truncated,
            StoppedByConsumer = stoppedByConsumer,
        };
    }

    private string[] FindEphemeralOperationInputDrift(
        AnalysisSnapshot snapshot,
        ModuleInfo[] modules,
        CancellationToken cancellationToken)
    {
        var drift = new SortedSet<string>(PathComparer);
        var sourcePaths = _sourcePaths ?? WorkspaceSourcePathMap.Create(snapshot.ProjectRoot);
        var excludeGlobs = PathGlobMatcher.Compile(snapshot.ExcludePathGlobs);
        foreach (var path in modules
                     .SelectMany(module => module.FilePaths)
                     .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                     .Distinct(PathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = sourcePaths.ToWorkspacePath(path);
            if (snapshot.ExcludePatterns.Any(pattern =>
                    relative.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                || PathGlobMatcher.MatchesAny(excludeGlobs, relative))
            {
                continue;
            }

            if (!_fs.FileExists(path)
                || !snapshot.FileContentHashes.TryGetValue(path, out var committedHash))
            {
                drift.Add(relative);
                continue;
            }

            try
            {
                if (SourceContentHasher.HashText(_fs.ReadAllText(path)) != committedHash)
                    drift.Add(relative);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                drift.Add(relative);
            }
        }

        foreach (var module in modules)
        {
            var currentPaths = GetReferenceInputPaths(new[] { module }, snapshot.ProjectRoot);
            var committedPaths = snapshot.ReferenceInputPathsByModule.TryGetValue(module.Name, out var paths)
                ? paths.ToHashSet(PathComparer)
                : new HashSet<string>(PathComparer);
            if (!committedPaths.SetEquals(currentPaths))
                drift.Add($"reference-set:{module.Name}");

            foreach (var path in committedPaths.Concat(currentPaths).Distinct(PathComparer))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var display = Path.GetRelativePath(snapshot.ProjectRoot, path).Replace('\\', '/');
                if (!_fs.FileExists(path)
                    || !snapshot.ReferenceContentHashes.TryGetValue(path, out var committedHash))
                {
                    drift.Add(display);
                    continue;
                }

                try
                {
                    if (HashBinaryDescriptor(path) != committedHash)
                        drift.Add(display);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    drift.Add(display);
                }
            }
        }

        return drift.Take(16).ToArray();
    }

    private static OperationFactQuery CopyOperationFactQuery(
        OperationFactQuery source,
        string profileScope,
        int maxFacts)
        => new()
        {
            ModuleScope = source.ModuleScope,
            ProfileScope = profileScope,
            FilePaths = source.FilePaths,
            ContainingSymbolIds = source.ContainingSymbolIds,
            TargetSymbolIds = source.TargetSymbolIds,
            IncludeKinds = source.IncludeKinds,
            IncludeImplicit = source.IncludeImplicit,
            MaxFacts = maxFacts,
        };

    private static AcceptedChangeSet BuildAcceptedChanges(
        WorkspaceSourcePathMap sourcePaths,
        ChangeScanMode scanMode,
        IEnumerable<string> reanalyzedSourceFiles,
        IEnumerable<string> mtimeTouchedSourceFiles,
        IEnumerable<string> contentChangedSourceFiles,
        IEnumerable<string> descriptorForcedSourceFiles,
        IEnumerable<string> deletedSourceFiles)
        => AcceptedChangeSet.Create(
            scanMode,
            reanalyzedSourceFiles: ToWorkspacePaths(sourcePaths, reanalyzedSourceFiles),
            mtimeTouchedSourceFiles: ToWorkspacePaths(sourcePaths, mtimeTouchedSourceFiles),
            contentChangedSourceFiles: ToWorkspacePaths(sourcePaths, contentChangedSourceFiles),
            descriptorForcedSourceFiles: ToWorkspacePaths(sourcePaths, descriptorForcedSourceFiles),
            deletedSourceFiles: ToWorkspacePaths(sourcePaths, deletedSourceFiles));

    private static string[] ToWorkspacePaths(
        WorkspaceSourcePathMap sourcePaths,
        IEnumerable<string> absolutePaths)
        => absolutePaths
            .Select(sourcePaths.ToWorkspacePath)
            .ToArray();

    private static ChangeScanMode ResolveChangeScanMode(AnalysisConfig config)
        => config.AuthoritativeChangedFiles == null
            ? ChangeScanMode.FilesystemPrefilter
            : ChangeScanMode.AuthoritativeChangedSet;

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

    /// <summary>
    /// INV-MULTI-DEFINE-INCREMENTAL-001. Recover the snapshot's per-module
    /// downgraded-ref dict for <paramref name="profileName"/>, creating an
    /// empty one if absent. The returned reference is the same instance the
    /// snapshot stores, so <see cref="ModuleCompilationBuilder.ProcessInOrder"/>'s
    /// write-back lands on the snapshot directly.
    /// </summary>
    private static Dictionary<string, Microsoft.CodeAnalysis.MetadataReference> GetOrCreateProfileCarry(
        AnalysisSnapshot snapshot, string profileName)
    {
        if (!snapshot.DowngradedRefsByProfile.TryGetValue(profileName, out var carry))
        {
            carry = new Dictionary<string, Microsoft.CodeAnalysis.MetadataReference>(StringComparer.Ordinal);
            snapshot.DowngradedRefsByProfile[profileName] = carry;
        }
        return carry;
    }

    /// <summary>INV-MULTI-DEFINE-APPLIER-001.</summary>
    private static ModuleInfo[] ApplyProfileToModules(ModuleInfo[] modules, DefineProfile profile)
    {
        return modules
            .Where(module => IsModuleApplicable(module, profile))
            .Select(module => DefineProfileApplier.WithProfileDefines(module, profile))
            .ToArray();
    }

    private static ModuleInfo[] ApplyProfilesToModules(
        ModuleInfo[] modules,
        IReadOnlyList<DefineProfile> profiles)
    {
        return modules
            .Where(module => profiles.Any(profile => IsModuleApplicable(module, profile)))
            .ToArray();
    }

    private ProfileApplicabilityReport BuildProfileApplicabilityReport(
        string projectRoot,
        ModuleInfo[] modules,
        IReadOnlyList<DefineProfile> profiles)
    {
        var profileNames = profiles
            .Select(profile => profile.Name)
            .ToArray();
        var includedCounts = profileNames.ToDictionary(
            profile => profile,
            _ => 0,
            StringComparer.Ordinal);
        var excludedCounts = profileNames.ToDictionary(
            profile => profile,
            _ => 0,
            StringComparer.Ordinal);

        var shapedModules = modules
            .OrderBy(module => module.Name, StringComparer.Ordinal)
            .Select(module =>
            {
                var included = new List<string>();
                var excluded = new List<string>();
                var exclusions = new List<ProfileApplicabilityExclusion>();

                foreach (var profile in profiles)
                {
                    if (IsModuleApplicable(module, profile))
                    {
                        included.Add(profile.Name);
                        includedCounts[profile.Name]++;
                        continue;
                    }

                    excluded.Add(profile.Name);
                    excludedCounts[profile.Name]++;
                    exclusions.Add(new ProfileApplicabilityExclusion
                    {
                        Profile = profile.Name,
                        Reason = ResolveProfileExclusionReason(module, profile),
                    });
                }

                module.Properties.TryGetValue("projectFile", out var projectFile);
                return new ProfileApplicabilityModule
                {
                    Name = module.Name,
                    ProjectFile = projectFile,
                    UnityProjectType = module.UnityProjectType,
                    IsEditorOnly = module.IsEditorOnly,
                    IncludedProfiles = included.ToArray(),
                    ExcludedProfiles = excluded.ToArray(),
                    Exclusions = exclusions.ToArray(),
                };
            })
            .ToArray();

        return new ProfileApplicabilityReport
        {
            IsUnityWorkspace = _fs.DirectoryExists(Path.Combine(projectRoot, "Library"))
                || modules.Any(module => !string.IsNullOrWhiteSpace(module.UnityProjectType)),
            Profiles = profileNames,
            Modules = shapedModules,
            IncludedModuleCountsByProfile = includedCounts,
            ExcludedModuleCountsByProfile = excludedCounts,
        };
    }

    private static HashSet<string> GetProfileOwnedModuleNames(
        ModuleInfo[] modules,
        IReadOnlyList<DefineProfile> profiles,
        int profileIndex)
    {
        var owned = new HashSet<string>(StringComparer.Ordinal);
        if (profileIndex < 0 || profileIndex >= profiles.Count)
            return owned;

        foreach (var module in modules)
        {
            if (!IsModuleApplicable(module, profiles[profileIndex]))
                continue;

            var ownedEarlier = false;
            for (var earlierIndex = 0; earlierIndex < profileIndex; earlierIndex++)
            {
                if (!IsModuleApplicable(module, profiles[earlierIndex]))
                    continue;

                ownedEarlier = true;
                break;
            }

            if (!ownedEarlier)
                owned.Add(module.Name);
        }

        return owned;
    }

    private static bool IsModuleApplicable(ModuleInfo module, DefineProfile profile)
        => profile.IncludeEditorOnlyModules || !module.IsEditorOnly;

    private static string ResolveProfileExclusionReason(ModuleInfo module, DefineProfile profile)
        => module.IsEditorOnly && !profile.IncludeEditorOnlyModules
            ? ProfileApplicabilityReason.EditorOnlyModuleExcludedByProfile
            : ProfileApplicabilityReason.ModuleExcludedByProfile;

    private static void PruneProfileCarry(
        IDictionary<string, MetadataReference> carry,
        IEnumerable<ModuleInfo> applicableModules)
    {
        var applicableNames = new HashSet<string>(
            applicableModules.Select(module => module.Name),
            StringComparer.Ordinal);
        foreach (var name in carry.Keys.Where(
                     name => !applicableNames.Contains(name)).ToArray())
        {
            carry.Remove(name);
        }
    }

    private static string[] NormalizeExcludePatterns(string[]? patterns)
        => patterns?
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(pattern => pattern.Trim().Replace('\\', '/'))
            .ToArray()
           ?? Array.Empty<string>();

    private static string[] NormalizeExcludePathGlobs(string[]? globs)
        => globs?
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g.Trim().Replace('\\', '/'))
            .ToArray()
           ?? Array.Empty<string>();

    private static bool SamePathExclusions(string[] left, string[] right)
        => new HashSet<string>(left, StringComparer.OrdinalIgnoreCase)
            .SetEquals(right);

    private static StringComparer PathComparer { get; }
        = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static HashSet<string>? NormalizeAuthoritativeChangedFiles(
        string projectRoot,
        string[]? changedFiles)
    {
        if (changedFiles == null) return null;

        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in changedFiles)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var trimmed = raw.Trim();
            string fullPath;
            try
            {
                fullPath = Path.IsPathRooted(trimmed)
                    ? Path.GetFullPath(trimmed)
                    : Path.GetFullPath(Path.Combine(projectRoot, trimmed));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            normalized.Add(fullPath);
        }

        return normalized;
    }

    private void TrackSourceTouch(
        string filePath,
        HashSet<string> mtimeTouchedFiles,
        HashSet<string> contentChangedFiles)
    {
        try
        {
            var currentTimestamp = _fs.GetLastWriteTimeUtc(filePath);
            if (_snapshot == null
                || !_snapshot.FileTimestamps.TryGetValue(filePath, out var prevTimestamp)
                || currentTimestamp != prevTimestamp)
            {
                mtimeTouchedFiles.Add(filePath);
            }
        }
        catch
        {
            // A file that cannot be statted is still recompilation-worthy
            // when the caller reached this path through descriptor drift.
        }

        if (HasSourceContentChanged(filePath, out _))
            contentChangedFiles.Add(filePath);
    }

    private bool HasSourceContentChanged(string filePath, out ContentFingerprint? currentHash)
    {
        currentHash = null;
        if (_snapshot == null) return true;

        try
        {
            currentHash = SourceContentHasher.HashText(_fs.ReadAllText(filePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }

        return !_snapshot.FileContentHashes.TryGetValue(filePath, out var previousHash)
            || previousHash != currentHash;
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
        var currentHashes = new Dictionary<string, ContentFingerprint>(StringComparer.OrdinalIgnoreCase);
        foreach (var asmdefAbs in _fs.FindFiles(projectRoot, "*.asmdef", recursive: true))
        {
            current.Add(asmdefAbs);
            DateTime currentTs;
            try { currentTs = _fs.GetLastWriteTimeUtc(asmdefAbs); }
            catch { continue; }

            ContentFingerprint currentHash;
            try { currentHash = HashTextDescriptor(asmdefAbs); }
            catch { return true; }
            currentHashes[asmdefAbs] = currentHash;

            if (!_snapshot.AsmdefContentHashes.TryGetValue(asmdefAbs, out var previousHash)) return true;
            if (previousHash != currentHash) return true;

            // Timestamp-only churn is harmless once content equality is
            // proven, but retain the fresh prefilter for the next pass.
            _snapshot.AsmdefTimestamps[asmdefAbs] = currentTs;
        }

        // Removed file? Snapshot tracked it, current scan missed it.
        foreach (var prev in _snapshot.AsmdefTimestamps.Keys)
        {
            if (!current.Contains(prev)) return true;
        }


        ReplaceDictionary(_snapshot.AsmdefContentHashes, currentHashes);

        return false;
    }

    private bool HasReferenceInputDrift(ModuleInfo[] modules, string projectRoot)
    {
        if (_snapshot == null) return false;

        var currentPaths = GetReferenceInputPaths(modules, projectRoot);
        if (!_snapshot.ReferenceContentHashes.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(currentPaths))
        {
            return true;
        }

        foreach (var path in currentPaths)
        {
            DateTime timestamp;
            try { timestamp = _fs.GetLastWriteTimeUtc(path); }
            catch { return true; }

            if (_snapshot.ReferenceTimestamps.TryGetValue(path, out var previousTimestamp)
                && previousTimestamp == timestamp)
            {
                continue;
            }

            ContentFingerprint currentHash;
            try { currentHash = HashBinaryDescriptor(path); }
            catch { return true; }

            if (!_snapshot.ReferenceContentHashes.TryGetValue(path, out var previousHash)
                || previousHash != currentHash)
            {
                return true;
            }

            _snapshot.ReferenceTimestamps[path] = timestamp;
        }

        return false;
    }

    private void CaptureReferenceInputs(AnalysisSnapshot snapshot, ModuleInfo[] modules)
    {
        var content = new Dictionary<string, ContentFingerprint>(StringComparer.OrdinalIgnoreCase);
        snapshot.ReferenceInputPathsByModule.Clear();
        foreach (var module in modules)
        {
            var paths = GetReferenceInputPaths(new[] { module }, snapshot.ProjectRoot)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            snapshot.ReferenceInputPathsByModule[module.Name] = paths;
            foreach (var path in paths)
            {
                try { content[path] = HashBinaryDescriptor(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        ReplaceDictionary(snapshot.ReferenceContentHashes, content);
        foreach (var path in content.Keys)
        {
            try
            {
                snapshot.ReferenceTimestamps[path] = _fs.GetLastWriteTimeUtc(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The compilation builder owns the structured missing/unreadable
                // reference outcome. Fingerprinting must not invent a second
                // failure policy before that authoritative seam runs.
            }
        }
    }

    private void CapturePackageVisibilityInputs(
        AnalysisSnapshot snapshot,
        UnityPackageWorkspace packageWorkspace)
    {
        var content = UnityPackageSourceVisibilityBuilder.CaptureInputFingerprints(
            _fs,
            packageWorkspace);
        ReplaceDictionary(snapshot.PackageVisibilityInputHashes, content);
    }

    private Dictionary<string, ContentFingerprint> CaptureReferenceContent(
        ModuleInfo[] modules,
        string projectRoot)
    {
        var content = new Dictionary<string, ContentFingerprint>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in GetReferenceInputPaths(modules, projectRoot))
        {
            try { content[path] = HashBinaryDescriptor(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        return content;
    }

    private HashSet<string> GetReferenceInputPaths(ModuleInfo[] modules, string projectRoot)
        => modules
            .SelectMany(module => module.ExternalDllPaths
                .Concat(module.SourceGeneratorAnalyzerPaths)
                .Concat(_nugetResolver.ResolveInputPaths(module, projectRoot)))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private ContentFingerprint HashTextDescriptor(string path)
        => ContentFingerprint.ComputeUtf8(
            "lifeblood.workspace-descriptor-content.v1",
            _fs.ReadAllText(path));

    private ContentFingerprint HashBinaryDescriptor(string path)
    {
        using var stream = _fs.OpenRead(path);
        return ContentFingerprint.FromHashBytes(SHA256.HashData(stream));
    }

    private static void ReplaceDictionary<TKey, TValue>(
        Dictionary<TKey, TValue> destination,
        IReadOnlyDictionary<TKey, TValue> source)
        where TKey : notnull
    {
        destination.Clear();
        foreach (var (key, value) in source)
            destination[key] = value;
    }
}

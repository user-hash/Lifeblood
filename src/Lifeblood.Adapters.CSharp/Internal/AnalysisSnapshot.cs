using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Lifeblood.Domain.Workspaces;
using Microsoft.CodeAnalysis;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Cached per-file extraction state from a previous analysis.
/// Used by incremental re-analyze to avoid re-extracting unchanged files.
///
/// Stores symbols and edges indexed by file ID so that changed files can be
/// surgically replaced without reprocessing the entire workspace.
/// </summary>
internal sealed class AnalysisSnapshot
{
    /// <summary>Project root at analysis time.</summary>
    public required string ProjectRoot { get; init; }

    /// <summary>Modules discovered at analysis time.</summary>
    public ModuleInfo[] Modules { get; set; } = Array.Empty<ModuleInfo>();

    /// <summary>
    /// Legacy project-relative substring exclusions applied when this snapshot
    /// was compiled. Ephemeral profile scans replay the exact same source
    /// scope; incremental analysis treats changes as scope drift.
    /// </summary>
    public string[] ExcludePatterns { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Project-relative path globs excluded from compilation when this
    /// snapshot was built. Changing this set changes the graph scope, so
    /// incremental analyze must reject or widen to full before files are
    /// added to or removed from the cached snapshot. INV-ANALYZE-EXCLUDEPATHS-001.
    /// </summary>
    public string[] ExcludePathGlobs { get; set; } = Array.Empty<string>();

    /// <summary>
    /// INV-MULTI-DEFINE-INCREMENTAL-001. Profile set this snapshot was built under.
    /// Snapshot is the SSoT for which profiles the graph is under; incremental MUST
    /// replay the same set. Count == 1 keeps <c>Edge.Profiles</c> null; Count >= 2
    /// tags edges and dedup-unions at <c>GraphBuilder</c>.
    /// </summary>
    public IReadOnlyList<DefineProfile> ActiveProfiles { get; set; } = Array.Empty<DefineProfile>();

    /// <summary>Absolute file path → last-write-time-UTC at analysis time.</summary>
    public Dictionary<string, DateTime> FileTimestamps { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Absolute file path to stable hash of the source text parsed for the
    /// cached file facts. Incremental analyze uses this as the second-stage
    /// check after an mtime touch, so Unity/IDE metadata churn does not force
    /// graph replacement when the file text is unchanged.
    /// </summary>
    public Dictionary<string, ContentFingerprint> FileContentHashes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Absolute descriptor/reference path to its content-authoritative digest.
    /// This includes project/solution/asmdef descriptors and binary inputs
    /// whose contents can change semantic binding. Paths are reduced to a
    /// stable workspace-relative form when the aggregate fingerprint is built.
    /// </summary>
    public Dictionary<string, ContentFingerprint> DescriptorContentHashes { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, ContentFingerprint> AsmdefContentHashes { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, ContentFingerprint> ReferenceContentHashes { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Module name to the exact external/source-generator/resolved-NuGet input
    /// path set used by its committed compilation. Ephemeral scoped execution
    /// uses this to detect a removed or newly selected reference, not only a
    /// content change on paths that still exist.
    /// </summary>
    public Dictionary<string, string[]> ReferenceInputPathsByModule { get; }
        = new(StringComparer.Ordinal);

    /// <summary>
    /// Adapter-owned Unity package source-visibility inputs. These are not all
    /// graph source files: unbound package .cs paths affect the package
    /// visibility receipt but do not enter Roslyn until Unity regenerates
    /// project descriptors.
    /// </summary>
    public Dictionary<string, ContentFingerprint> PackageVisibilityInputHashes { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, DateTime> ReferenceTimestamps { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Absolute csproj file path → last-write-time-UTC at analysis time.
    /// Tracked separately from <see cref="FileTimestamps"/> (which only stores
    /// .cs files) because csproj edits change discovered MODULE FACTS
    /// (BclOwnership, ExternalDllPaths, Dependencies) and require full
    /// re-discovery + recompile of the affected module — not just per-file
    /// extraction replacement.
    ///
    /// See INV-BCL-005 in <c>.claude/plans/bcl-ownership-fix.md</c>: without
    /// csproj-timestamp invalidation, a user who edits a csproj to add or
    /// remove a BCL reference and then runs incremental re-analyze gets a
    /// stale BclOwnership value forever — silent re-introduction of the
    /// double-BCL bug.
    /// </summary>
    public Dictionary<string, DateTime> CsprojTimestamps { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Absolute *.asmdef file path → last-write-time-UTC at analysis time.
    /// Unity asmdefs declare assembly-level options (references, allowed
    /// platforms, defines) that the IDE-visible csproj is generated from.
    /// When the user edits an asmdef without forcing Unity to regenerate
    /// csprojs (a common workflow during refactors), the on-disk csproj
    /// stays current to the previous-state asmdef and this analyzer
    /// silently runs against a stale module model. Any asmdef-timestamp
    /// drift triggers a full re-analyze on the next round, which catches
    /// the change. INV-UNITY-002.
    /// </summary>
    public Dictionary<string, DateTime> AsmdefTimestamps { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>File ID (file:relPath) → symbols extracted from that file.</summary>
    public Dictionary<string, List<Symbol>> SymbolsByFile { get; } = new(StringComparer.Ordinal);

    /// <summary>File ID (file:relPath) → edges where the source symbol lives in that file.</summary>
    public Dictionary<string, List<Edge>> EdgesByFile { get; } = new(StringComparer.Ordinal);

    /// <summary>Module-level symbols (mod:Name) and edges (mod→mod DependsOn).</summary>
    public List<Symbol> ModuleSymbols { get; } = new();
    public List<Edge> ModuleEdges { get; } = new();

    /// <summary>
    /// Per-profile, per-module downgraded MetadataReferences (PE images
    /// emitted by the previous analyze pass). Outer dict keyed by profile
    /// name; inner dict by module name. Persisted across calls so
    /// incremental re-analyze can hand changed-modules' compilations the
    /// metadata references for UNCHANGED dependent modules — under the
    /// SAME profile defines those modules were originally compiled with.
    ///
    /// INV-INCREMENTAL-XREF-001 + INV-MULTI-DEFINE-INCREMENTAL-001. The
    /// per-profile keying is load-bearing: PE images differ across profiles
    /// because preprocessor symbols gate code inclusion (<c>#if PLAYER_ONLY</c>),
    /// so a single dict keyed only by module would silently bind every
    /// non-first profile to the first profile's metadata. The empirical
    /// repro on a 2-project workspace (caller in B, callee in A guarded by
    /// <c>#if PLAYER_ONLY</c>): touch B → incremental Player pass had no
    /// A-under-Player PE image → cross-project Player edge dropped to 0.
    ///
    /// Invariant: after every successful <see cref="RoslynWorkspaceAnalyzer.AnalyzeWorkspace"/>
    /// call, this dictionary contains exactly one inner-dict per profile in
    /// <see cref="ActiveProfiles"/>, and each inner-dict contains exactly
    /// one entry per module the analyzer successfully compiled under that
    /// profile. Incremental refreshes the entries for re-compiled modules
    /// and preserves the rest.
    /// </summary>
    public Dictionary<string, Dictionary<string, MetadataReference>> DowngradedRefsByProfile { get; }
        = new(StringComparer.Ordinal);

    /// <summary>
    /// Files the analyzer declined to process during the last run of either
    /// <c>RoslynWorkspaceAnalyzer.AnalyzeWorkspace</c> or
    /// <c>RoslynWorkspaceAnalyzer.IncrementalAnalyze</c>. Each entry carries
    /// the absolute path, a machine-readable reason code
    /// (<see cref="Lifeblood.Domain.Results.SkipReason"/>), and the owning
    /// module name when known. Replaced in place on every full analyze;
    /// appended to (not replaced) on incremental analyze because incremental
    /// does not re-walk modules that had no changed files.
    ///
    /// Without this list the analyzer silently drops non-.cs files and
    /// missing files, and users have no way to discover that their change
    /// wasn't included in the graph.
    /// </summary>
    public List<SkippedFile> SkippedFiles { get; } = new();

    /// <summary>
    /// Fork the mutable incremental cache for candidate construction. Domain
    /// symbols, edges, module descriptors, profiles, skipped-file receipts,
    /// and Roslyn metadata references are immutable after extraction and may
    /// be shared; every collection the incremental path mutates is copied.
    /// </summary>
    public AnalysisSnapshot ForkForCandidate()
    {
        var candidate = new AnalysisSnapshot
        {
            ProjectRoot = ProjectRoot,
            Modules = Modules.ToArray(),
            ExcludePatterns = ExcludePatterns.ToArray(),
            ExcludePathGlobs = ExcludePathGlobs.ToArray(),
            ActiveProfiles = ActiveProfiles.ToArray(),
        };

        CopyDictionary(FileTimestamps, candidate.FileTimestamps);
        CopyDictionary(FileContentHashes, candidate.FileContentHashes);
        CopyDictionary(DescriptorContentHashes, candidate.DescriptorContentHashes);
        CopyDictionary(AsmdefContentHashes, candidate.AsmdefContentHashes);
        CopyDictionary(ReferenceContentHashes, candidate.ReferenceContentHashes);
        foreach (var (moduleName, paths) in ReferenceInputPathsByModule)
            candidate.ReferenceInputPathsByModule[moduleName] = paths.ToArray();
        CopyDictionary(PackageVisibilityInputHashes, candidate.PackageVisibilityInputHashes);
        CopyDictionary(ReferenceTimestamps, candidate.ReferenceTimestamps);
        CopyDictionary(CsprojTimestamps, candidate.CsprojTimestamps);
        CopyDictionary(AsmdefTimestamps, candidate.AsmdefTimestamps);

        foreach (var (fileId, symbols) in SymbolsByFile)
            candidate.SymbolsByFile[fileId] = new List<Symbol>(symbols);
        foreach (var (fileId, edges) in EdgesByFile)
            candidate.EdgesByFile[fileId] = new List<Edge>(edges);

        candidate.ModuleSymbols.AddRange(ModuleSymbols);
        candidate.ModuleEdges.AddRange(ModuleEdges);
        candidate.SkippedFiles.AddRange(SkippedFiles);

        foreach (var (profileName, references) in DowngradedRefsByProfile)
        {
            candidate.DowngradedRefsByProfile[profileName] =
                new Dictionary<string, MetadataReference>(references, StringComparer.Ordinal);
        }

        return candidate;
    }

    public SourceFingerprint BuildSourceFingerprint(WorkspaceSourcePathMap sourcePaths)
        => WorkspaceInputFingerprintBuilder.Build(
            sourcePaths,
            FileContentHashes,
            DescriptorContentHashes,
            AsmdefContentHashes,
            ReferenceContentHashes,
            PackageVisibilityInputHashes);

    private static void CopyDictionary<TKey, TValue>(
        Dictionary<TKey, TValue> source,
        Dictionary<TKey, TValue> destination)
        where TKey : notnull
    {
        foreach (var (key, value) in source)
            destination[key] = value;
    }

    /// <summary>
    /// Rebuild the full graph from cached per-file data + module data.
    /// </summary>
    public SemanticGraph RebuildGraph()
    {
        var builder = new GraphBuilder();

        foreach (var sym in ModuleSymbols)
            builder.AddSymbol(sym);

        foreach (var edge in ModuleEdges)
            builder.AddEdge(edge);

        foreach (var (fileId, symbols) in SymbolsByFile)
        {
            // The file symbol itself is the first entry
            builder.AddSymbols(symbols);
        }

        foreach (var (fileId, edges) in EdgesByFile)
        {
            builder.AddEdges(edges);
        }

        return builder.Build();
    }

    /// <summary>
    /// Replace all cached data for a specific file with new extraction results.
    /// </summary>
    public void ReplaceFile(string fileId, Symbol fileSymbol, List<Symbol> symbols, List<Edge> edges)
    {
        var allSymbols = new List<Symbol>(symbols.Count + 1) { fileSymbol };
        allSymbols.AddRange(symbols);
        SymbolsByFile[fileId] = allSymbols;
        EdgesByFile[fileId] = edges;
    }

    /// <summary>
    /// INV-MULTI-DEFINE-ANALYZE-001. Append edges from a follow-up profile
    /// pass to an already-replaced file. GraphBuilder dedup-unions Profiles[]
    /// at <see cref="RebuildGraph"/> time. Symbol set is not appended —
    /// re-extracting symbols under a different profile re-discovers the same
    /// declarations; symbol union happens via id-based dedup in GraphBuilder.
    /// </summary>
    public void AppendProfileEdges(string fileId, List<Edge> edges)
    {
        if (EdgesByFile.TryGetValue(fileId, out var existing))
        {
            existing.AddRange(edges);
        }
        else
        {
            EdgesByFile[fileId] = edges;
        }
    }

    /// <summary>
    /// Remove all cached data for a file that no longer exists.
    /// </summary>
    public void RemoveFile(string fileId)
    {
        SymbolsByFile.Remove(fileId);
        EdgesByFile.Remove(fileId);
    }
}

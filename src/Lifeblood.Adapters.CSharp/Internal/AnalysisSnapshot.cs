using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
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
    /// INV-MULTI-DEFINE-INCREMENTAL-001. The profile set this snapshot was
    /// built under. Owned here (not on <see cref="Lifeblood.Application.Ports.Left.AnalysisConfig"/>)
    /// because the snapshot is the SSoT for "which profiles is this graph under?" — a
    /// follow-up incremental MUST replay the same set so per-edge <c>Profiles[]</c>
    /// provenance survives. Set once at full-analyze time. Single-profile back-compat:
    /// Count == 1 keeps <c>Edge.Profiles</c> null; Count >= 2 tags edges + dedup-unions
    /// at <c>GraphBuilder</c>. Empty only for a pre-Wave-6 hypothetical (snapshot
    /// not serialized, so unreachable in practice — caller invariant enforced at the
    /// analyzer's writer site).
    /// </summary>
    public IReadOnlyList<DefineProfile> ActiveProfiles { get; set; } = Array.Empty<DefineProfile>();

    /// <summary>Absolute file path → last-write-time-UTC at analysis time.</summary>
    public Dictionary<string, DateTime> FileTimestamps { get; } = new(StringComparer.OrdinalIgnoreCase);

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
    /// Per-module downgraded MetadataReferences (PE images emitted by the
    /// previous analyze pass). Persisted across calls so incremental
    /// re-analyze can hand changed-modules' compilations the metadata
    /// references for UNCHANGED dependent modules.
    ///
    /// The drift class this guards (INV-INCREMENTAL-XREF-001): when this
    /// dictionary lived as a local in <c>ModuleCompilationBuilder.ProcessInOrder</c>
    /// and was discarded at end-of-call, full analyze populated it for every
    /// module; incremental analyze called <c>ProcessInOrder</c> with only the
    /// <c>modulesToRecompile</c> subset, so unchanged dependencies had no
    /// metadata reference, every cross-module symbol bound to a Roslyn
    /// error symbol, and the corresponding edges were silently dropped by
    /// <c>GraphBuilder</c>'s dangling-edge filter. Empirical repro on a
    /// multi-module Unity workspace showed cross-module edges silently
    /// dropping on a single-file touch in proportion to the
    /// unchanged-module fan-in. Minimal synthetic repro in
    /// <c>IncrementalAnalyzeTests.IncrementalAnalyze_CrossModuleEdges_*</c>:
    /// 5 → 1 (-4) on a 1-file touch in a 2-module project.
    ///
    /// Invariant: after every successful <c>ProcessInOrder</c> call, this
    /// dictionary contains exactly one entry per module that the analyzer
    /// successfully compiled (including unchanged modules carried forward
    /// from previous full analyze, with their stale-but-still-valid PE
    /// metadata). INV-INCREMENTAL-XREF-001.
    /// </summary>
    public Dictionary<string, MetadataReference> DowngradedRefs { get; }
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

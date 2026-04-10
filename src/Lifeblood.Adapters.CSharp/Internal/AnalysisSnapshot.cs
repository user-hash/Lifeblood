using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Graph;

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

    /// <summary>File ID (file:relPath) → symbols extracted from that file.</summary>
    public Dictionary<string, List<Symbol>> SymbolsByFile { get; } = new(StringComparer.Ordinal);

    /// <summary>File ID (file:relPath) → edges where the source symbol lives in that file.</summary>
    public Dictionary<string, List<Edge>> EdgesByFile { get; } = new(StringComparer.Ordinal);

    /// <summary>Module-level symbols (mod:Name) and edges (mod→mod DependsOn).</summary>
    public List<Symbol> ModuleSymbols { get; } = new();
    public List<Edge> ModuleEdges { get; } = new();

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
    /// Remove all cached data for a file that no longer exists.
    /// </summary>
    public void RemoveFile(string fileId)
    {
        SymbolsByFile.Remove(fileId);
        EdgesByFile.Remove(fileId);
    }
}

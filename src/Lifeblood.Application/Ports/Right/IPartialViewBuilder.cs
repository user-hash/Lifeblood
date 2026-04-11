using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Domain.Graph;

namespace Lifeblood.Application.Ports.Right;

/// <summary>
/// Right-side builder that stitches every partial declaration of a type
/// into a single combined source view. Agents asking "show me this class"
/// on a partial type want all the source, not whichever partial happened
/// to win the last-write-wins dedup in the graph. Partial-type
/// unification is already a read model on
/// <see cref="SymbolResolutionResult.DeclarationFilePaths"/>; this port
/// adds the source-stitching step that sits on top.
///
/// Added 2026-04-11 (Phase 6) to close DAWG R2. Implementation depends
/// on <see cref="IFileSystem"/> only (no Roslyn), so read-only /
/// streaming-mode workspaces still get the feature.
/// </summary>
public interface IPartialViewBuilder
{
    /// <summary>
    /// Build the combined source view for a partial type.
    /// <paramref name="projectRoot"/> is the absolute path to the
    /// workspace root, used to resolve the relative
    /// <see cref="Symbol.FilePath"/> values stored on File symbols in
    /// the graph. When the graph was loaded from a JSON file (no
    /// on-disk source), pass the empty string — the builder will emit
    /// a diagnostic in each segment instead of reading source.
    /// </summary>
    PartialViewResult Build(SemanticGraph graph, string typeSymbolId, string projectRoot);
}

/// <summary>
/// Combined-source result for a single type. <see cref="CanonicalId"/>
/// and <see cref="Name"/> echo the input type. <see cref="Segments"/>
/// carries one entry per partial declaration, in the deterministic order
/// produced by <see cref="ISymbolResolver"/>'s partial-type read model.
/// <see cref="CombinedSource"/> is the concatenation — convenient for
/// display — with per-segment headers so the caller can see which file
/// contributed which range.
/// </summary>
public sealed record PartialViewResult(
    string CanonicalId,
    string Name,
    PartialSegment[] Segments,
    string CombinedSource,
    string Diagnostic = "");

public sealed record PartialSegment(
    string FilePath,
    int StartLine,
    int LineCount,
    string Source);

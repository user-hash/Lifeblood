using System.Text;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Domain.Graph;

namespace Lifeblood.Connectors.Mcp;

/// <summary>
/// Reference implementation of <see cref="IPartialViewBuilder"/>. Takes a
/// type symbol id and stitches every partial declaration's source into a
/// single combined view with per-file headers so the consumer can see
/// which file contributed which range.
///
/// Discovery of partial files reuses the existing resolver partial-type
/// read model: walk the type's incoming <see cref="EdgeKind.Contains"/>
/// edges from <see cref="SymbolKind.File"/> symbols. The source read
/// itself goes through <see cref="IFileSystem"/> — no Roslyn required,
/// so read-only / streaming-mode workspaces still get the feature.
///
/// Added 2026-04-11 (Phase 6 / B6) to close DAWG R2.
/// </summary>
public sealed class LifebloodPartialViewBuilder : IPartialViewBuilder
{
    private readonly IFileSystem _fs;

    public LifebloodPartialViewBuilder(IFileSystem fs)
    {
        _fs = fs;
    }

    public PartialViewResult Build(SemanticGraph graph, string typeSymbolId, string projectRoot)
    {
        var sym = graph.GetSymbol(typeSymbolId);
        if (sym == null)
        {
            return new PartialViewResult(
                CanonicalId: typeSymbolId,
                Name: "",
                Segments: System.Array.Empty<PartialSegment>(),
                CombinedSource: "",
                Diagnostic: $"Symbol not found: {typeSymbolId}");
        }
        if (sym.Kind != SymbolKind.Type)
        {
            return new PartialViewResult(
                CanonicalId: typeSymbolId,
                Name: sym.Name,
                Segments: System.Array.Empty<PartialSegment>(),
                CombinedSource: "",
                Diagnostic: $"Symbol is {sym.Kind}, not a Type. Partial view only applies to type declarations.");
        }

        // Collect every file that Contains this type via an incoming edge.
        var files = new List<string>();
        foreach (int idx in graph.GetIncomingEdgeIndexes(typeSymbolId))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind != EdgeKind.Contains) continue;
            var fileSymbol = graph.GetSymbol(edge.SourceId);
            if (fileSymbol?.Kind != SymbolKind.File) continue;
            if (!string.IsNullOrEmpty(fileSymbol.FilePath))
                files.Add(fileSymbol.FilePath);
        }
        if (files.Count == 0 && !string.IsNullOrEmpty(sym.FilePath))
            files.Add(sym.FilePath);

        files = files
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, System.StringComparer.OrdinalIgnoreCase)
            .ToList();

        var segments = new List<PartialSegment>(files.Count);
        var combined = new StringBuilder();
        foreach (var relativePath in files)
        {
            var absolutePath = Path.IsPathRooted(relativePath)
                ? relativePath
                : Path.GetFullPath(Path.Combine(projectRoot ?? "", relativePath));
            string source;
            try { source = _fs.ReadAllText(absolutePath); }
            catch (IOException ex)
            {
                source = $"/* failed to read {relativePath}: {ex.Message} */";
            }
            catch (UnauthorizedAccessException ex)
            {
                source = $"/* access denied {relativePath}: {ex.Message} */";
            }

            var lineCount = 1 + source.Count(c => c == '\n');
            segments.Add(new PartialSegment(
                FilePath: relativePath,
                StartLine: 1,
                LineCount: lineCount,
                Source: source));

            combined.Append("// ═══ ").Append(relativePath).Append(" ═══").Append('\n');
            combined.Append(source);
            if (source.Length == 0 || source[source.Length - 1] != '\n')
                combined.Append('\n');
            combined.Append('\n');
        }

        return new PartialViewResult(
            CanonicalId: typeSymbolId,
            Name: sym.Name,
            Segments: segments.ToArray(),
            CombinedSource: combined.ToString());
    }
}

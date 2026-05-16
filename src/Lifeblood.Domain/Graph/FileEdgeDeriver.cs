using Lifeblood.Domain.Capabilities;

namespace Lifeblood.Domain.Graph;

internal static class FileEdgeDeriver
{
    public static void AddDerivedFileEdges(
        IReadOnlyDictionary<string, Symbol> symbols,
        IDictionary<EdgeIdentityKey, Edge> edges)
    {
        var symbolToFile = BuildSymbolToFileIndex(symbols);
        var explicitFileReferences = ExplicitFileReferencePairs(edges.Values);
        var fileEdgeCounts = CountCrossFileEdges(edges.Values, symbolToFile);

        foreach (var ((sourceFileId, targetFileId), count) in fileEdgeCounts)
        {
            if (explicitFileReferences.Contains((sourceFileId, targetFileId)))
                continue;

            var fileEdge = new Edge
            {
                SourceId = sourceFileId,
                TargetId = targetFileId,
                Kind = EdgeKind.References,
                Evidence = new Evidence
                {
                    Kind = EvidenceKind.Inferred,
                    AdapterName = "GraphBuilder",
                    Confidence = ConfidenceLevel.Proven,
                },
                Properties = new Dictionary<string, string>
                {
                    ["edgeCount"] = count.ToString(),
                },
            };
            edges.TryAdd(EdgeIdentity.KeyFor(fileEdge), fileEdge);
        }
    }

    private static Dictionary<string, string> BuildSymbolToFileIndex(
        IReadOnlyDictionary<string, Symbol> symbols)
    {
        var symbolToFile = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var symbol in symbols.Values)
        {
            if (symbol.Kind == SymbolKind.File)
            {
                symbolToFile[symbol.Id] = symbol.Id;
            }
            else if (!string.IsNullOrEmpty(symbol.FilePath))
            {
                var fileId = "file:" + symbol.FilePath.Replace('\\', '/');
                if (symbols.ContainsKey(fileId))
                    symbolToFile[symbol.Id] = fileId;
            }
        }
        return symbolToFile;
    }

    private static HashSet<(string SourceId, string TargetId)> ExplicitFileReferencePairs(
        IEnumerable<Edge> edges)
    {
        var pairs = new HashSet<(string, string)>();
        foreach (var edge in edges)
        {
            if (edge.Kind != EdgeKind.References) continue;
            if (!edge.SourceId.StartsWith("file:", StringComparison.Ordinal)) continue;
            if (!edge.TargetId.StartsWith("file:", StringComparison.Ordinal)) continue;

            pairs.Add((edge.SourceId, edge.TargetId));
        }
        return pairs;
    }

    private static Dictionary<(string SourceFileId, string TargetFileId), int> CountCrossFileEdges(
        IEnumerable<Edge> edges,
        IReadOnlyDictionary<string, string> symbolToFile)
    {
        var counts = new Dictionary<(string, string), int>();
        foreach (var edge in edges)
        {
            if (edge.Kind == EdgeKind.Contains) continue;

            if (!symbolToFile.TryGetValue(edge.SourceId, out var sourceFileId)) continue;
            if (!symbolToFile.TryGetValue(edge.TargetId, out var targetFileId)) continue;
            if (string.Equals(sourceFileId, targetFileId, StringComparison.Ordinal)) continue;

            var key = (sourceFileId, targetFileId);
            counts.TryGetValue(key, out var count);
            counts[key] = count + 1;
        }
        return counts;
    }
}

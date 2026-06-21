using Lifeblood.Application.Ports.Analysis;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.PathClassification;

namespace Lifeblood.Connectors.Mcp;

/// <summary>
/// Implements IMcpGraphProvider. Serves the semantic graph to AI agents via MCP tools.
/// INV-CONN-002: Read-only. Does not modify the graph.
/// INV-CONN-001: Depends on Application ports only — never on Analysis or Adapters directly.
/// </summary>
public sealed class LifebloodMcpProvider : IMcpGraphProvider
{
    private readonly IBlastRadiusProvider _blastRadius;

    public LifebloodMcpProvider(IBlastRadiusProvider blastRadius)
    {
        _blastRadius = blastRadius;
    }

    public Symbol? LookupSymbol(SemanticGraph graph, string symbolId)
    {
        return graph.GetSymbol(symbolId);
    }

    public string[] GetDependencies(SemanticGraph graph, string symbolId)
    {
        var deps = new HashSet<string>(StringComparer.Ordinal);

        foreach (int idx in graph.GetOutgoingEdgeIndexes(symbolId))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind != EdgeKind.Contains)
                deps.Add(edge.TargetId);
        }

        return deps.ToArray();
    }

    public string[] GetDependants(SemanticGraph graph, string symbolId)
    {
        var dependants = new HashSet<string>(StringComparer.Ordinal);

        foreach (int idx in graph.GetIncomingEdgeIndexes(symbolId))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind != EdgeKind.Contains)
                dependants.Add(edge.SourceId);
        }

        return dependants.ToArray();
    }

    public EdgeDetail[] GetDependencyEdges(SemanticGraph graph, string symbolId)
    {
        var result = new List<EdgeDetail>();
        foreach (int idx in graph.GetOutgoingEdgeIndexes(symbolId))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind == EdgeKind.Contains) continue;
            result.Add(new EdgeDetail
            {
                OtherEndId = edge.TargetId,
                Kind = edge.Kind,
                CallSite = edge.CallSite,
                Profiles = edge.Profiles,
            });
        }
        return result.ToArray();
    }

    public EdgeDetail[] GetDependantEdges(SemanticGraph graph, string symbolId)
    {
        var result = new List<EdgeDetail>();
        foreach (int idx in graph.GetIncomingEdgeIndexes(symbolId))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind == EdgeKind.Contains) continue;
            result.Add(new EdgeDetail
            {
                OtherEndId = edge.SourceId,
                Kind = edge.Kind,
                CallSite = edge.CallSite,
                Profiles = edge.Profiles,
            });
        }
        return result.ToArray();
    }

    public string[] GetBlastRadius(SemanticGraph graph, string symbolId, int maxDepth = 10)
    {
        var result = _blastRadius.Analyze(graph, symbolId, maxDepth);
        return result.AffectedSymbolIds;
    }

    public BlastRadiusGroups ClassifyBlastRadius(
        SemanticGraph graph, string symbolId, int maxDepth = 10, int maxResults = 10)
    {
        var affected = _blastRadius.Analyze(graph, symbolId, maxDepth).AffectedSymbolIds;

        // Independent one-hop direct count (transitive can be 100x for popular types).
        // Counts DISTINCT source symbols, matching the contract that
        // <see cref="GetDependants"/> publishes — a single source with two
        // edge kinds (e.g. a method that both Calls and References the target)
        // collapses to ONE direct dependant, not two.
        var directSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (int idx in graph.GetIncomingEdgeIndexes(symbolId))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind != EdgeKind.Contains) directSources.Add(edge.SourceId);
        }
        int directDependants = directSources.Count;

        var moduleOf = CreateModuleResolver(graph);
        var bucketLists = NewBucketLists();
        var moduleLists = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var aff in affected)
        {
            var sym = graph.GetSymbol(aff);
            bucketLists[ClassifyBucket(sym?.FilePath ?? "")].Add(aff);

            var module = moduleOf(aff);
            if (!moduleLists.TryGetValue(module, out var list))
                moduleLists[module] = list = new List<string>();
            list.Add(aff);
        }

        return new BlastRadiusGroups
        {
            TotalAffected = affected.Length,
            DirectDependants = directDependants,
            ByBucket = ShapeGroups(bucketLists, maxResults),
            ByModule = ShapeGroups(moduleLists, maxResults),
        };
    }

    public EdgeGroupResult ClassifyEdges(
        SemanticGraph graph, IReadOnlyList<EdgeDetail> edges, EdgeGroupOptions options)
    {
        var includeBuckets = options.IncludeBuckets is { Count: > 0 }
            ? new HashSet<string>(options.IncludeBuckets, StringComparer.OrdinalIgnoreCase)
            : null;

        var moduleOf = CreateModuleResolver(graph);
        var survivors = new List<EdgeDetail>(edges.Count);
        var bucketLists = NewBucketLists();
        var moduleLists = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var edge in edges)
        {
            var sym = graph.GetSymbol(edge.OtherEndId);
            var bucket = ClassifyBucket(sym?.FilePath ?? "");

            if (options.ExcludeTests && bucket == "Test") continue;
            if (options.ExcludeGenerated && bucket == "Generated") continue;
            if (includeBuckets != null && !includeBuckets.Contains(bucket)) continue;

            survivors.Add(edge);
            if (options.GroupByBucket) bucketLists[bucket].Add(edge.OtherEndId);
            if (options.GroupByModule)
            {
                var module = moduleOf(edge.OtherEndId);
                if (!moduleLists.TryGetValue(module, out var list))
                    moduleLists[module] = list = new List<string>();
                list.Add(edge.OtherEndId);
            }
        }

        return new EdgeGroupResult
        {
            Edges = survivors.ToArray(),
            TotalBeforeFilter = edges.Count,
            ByBucket = options.GroupByBucket ? ShapeGroups(bucketLists, options.PreviewPerGroup) : null,
            ByModule = options.GroupByModule ? ShapeGroups(moduleLists, options.PreviewPerGroup) : null,
        };
    }

    /// <summary>The four canonical path buckets, pre-seeded empty so a bucket
    /// with zero members is still indexable during the classification pass.</summary>
    private static Dictionary<string, List<string>> NewBucketLists() =>
        new(StringComparer.Ordinal)
        {
            ["Production"] = new(),
            ["Test"]       = new(),
            ["Editor"]     = new(),
            ["Generated"]  = new(),
        };

    /// <summary>
    /// Module resolver with a per-call memo: walk a symbol's Parent chain to its
    /// containing <see cref="SymbolKind.Module"/>, capping at 16 hops. Shared by
    /// <see cref="ClassifyBlastRadius"/> and <see cref="ClassifyEdges"/> so module
    /// attribution is one implementation. Unresolvable symbols map to
    /// <c>"(unknown)"</c>.
    /// </summary>
    private static Func<string, string> CreateModuleResolver(SemanticGraph graph)
    {
        var cache = new Dictionary<string, string>(StringComparer.Ordinal);
        return id =>
        {
            if (cache.TryGetValue(id, out var cached)) return cached;
            string? cursor = id;
            int hops = 0;
            while (cursor != null && hops++ < 16)
            {
                var sym = graph.GetSymbol(cursor);
                if (sym == null) break;
                if (sym.Kind == SymbolKind.Module) { cache[id] = sym.Name; return sym.Name; }
                cursor = sym.ParentId;
            }
            cache[id] = "(unknown)";
            return "(unknown)";
        };
    }

    /// <summary>
    /// Shape raw per-group id lists into <see cref="GroupedBucket"/> entries:
    /// drop empty groups, order by descending count, cap each preview at
    /// <paramref name="preview"/> (0 = counts only). Shared by both grouping
    /// queries so the wire shape cannot drift between them.
    /// </summary>
    private static IReadOnlyDictionary<string, GroupedBucket> ShapeGroups(
        Dictionary<string, List<string>> src, int preview) =>
        src
            .Where(kv => kv.Value.Count > 0)
            .OrderByDescending(kv => kv.Value.Count)
            .ToDictionary(
                kv => kv.Key,
                kv => new GroupedBucket
                {
                    Count = kv.Value.Count,
                    Preview = preview <= 0
                        ? System.Array.Empty<string>()
                        : kv.Value.Take(preview).ToArray(),
                },
                StringComparer.Ordinal);

    /// <summary>
    /// Path-heuristic bucket classifier. Mirrors the conventions used by
    /// <c>lifeblood_dead_code</c>'s test detector and extends them to
    /// Editor + Generated so blast-radius triage stays consistent across
    /// tools. Production = none of the special-case rules match.
    /// </summary>
    private static string ClassifyBucket(string filePath)
        => PathBucketClassifier.Classify(filePath).ToString();

    public FileImpactResult GetFileImpact(SemanticGraph graph, string fileId)
    {
        var fileSymbol = graph.GetSymbol(fileId);
        var filePath = fileSymbol?.FilePath ?? (fileId.StartsWith("file:") ? fileId.Substring(5) : fileId);

        // Outgoing: files this file depends on (file → other via References)
        var dependsOn = new List<FileEdge>();
        foreach (int idx in graph.GetOutgoingEdgeIndexes(fileId))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind != EdgeKind.References) continue;
            var target = graph.GetSymbol(edge.TargetId);
            if (target == null || target.Kind != SymbolKind.File) continue;

            int count = 1;
            if (edge.Properties.TryGetValue("edgeCount", out var ec) && int.TryParse(ec, out var parsed))
                count = parsed;

            dependsOn.Add(new FileEdge { FileId = edge.TargetId, FilePath = target.FilePath, EdgeCount = count });
        }

        // Incoming: files that depend on this file (other → file via References)
        var dependedOnBy = new List<FileEdge>();
        foreach (int idx in graph.GetIncomingEdgeIndexes(fileId))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind != EdgeKind.References) continue;
            var source = graph.GetSymbol(edge.SourceId);
            if (source == null || source.Kind != SymbolKind.File) continue;

            int count = 1;
            if (edge.Properties.TryGetValue("edgeCount", out var ec) && int.TryParse(ec, out var parsed))
                count = parsed;

            dependedOnBy.Add(new FileEdge { FileId = edge.SourceId, FilePath = source.FilePath, EdgeCount = count });
        }

        return new FileImpactResult
        {
            FileId = fileId,
            FilePath = filePath,
            DependsOn = dependsOn.OrderByDescending(f => f.EdgeCount).ToArray(),
            DependedOnBy = dependedOnBy.OrderByDescending(f => f.EdgeCount).ToArray(),
        };
    }
}

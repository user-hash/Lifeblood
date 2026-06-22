using Lifeblood.Domain.Graph;
using Lifeblood.Domain.PathClassification;
using Lifeblood.Domain.Results;

namespace Lifeblood.Analysis;

/// <summary>
/// Graph-only Unity asmdef compile-direction check. A DirectOnly module
/// must directly declare every module it references through source edges.
/// INV-ASMDEF-CHECK-001.
/// </summary>
public static class AsmdefBoundaryAnalyzer
{
    public const string DirectOnlyReferenceClosure = "DirectOnly";

    public static AsmdefBoundaryReport Analyze(SemanticGraph graph, AsmdefBoundaryOptions? options = null)
    {
        options ??= new AsmdefBoundaryOptions();
        var summarize = options.Summarize;
        var maxResults = summarize
            ? AsmdefBoundaryOptions.SummarizeMaxResults
            : options.MaxResults <= 0
                ? AsmdefBoundaryOptions.DefaultMaxResults
                : options.MaxResults;

        var modules = graph.Symbols
            .Where(s => s.Kind == SymbolKind.Module)
            .ToDictionary(s => s.Id, s => s, StringComparer.Ordinal);
        var directOnlyModules = modules.Values
            .Where(IsDirectOnlyModule)
            .Select(s => s.Id)
            .ToHashSet(StringComparer.Ordinal);

        var declared = BuildDeclaredDependencySet(graph);
        var depsByModule = BuildDeclaredDependencyMap(graph);
        var moduleOf = CreateModuleResolver(graph);
        var violations = new Dictionary<(string SourceModuleId, string TargetModuleId), ViolationAccumulator>();
        var checkedEdges = 0;

        foreach (var edge in graph.Edges)
        {
            if (edge.Kind is EdgeKind.Contains or EdgeKind.DependsOn) continue;

            var sourceModuleId = moduleOf(edge.SourceId);
            if (sourceModuleId == null || !directOnlyModules.Contains(sourceModuleId)) continue;

            var targetModuleId = moduleOf(edge.TargetId);
            if (targetModuleId == null) continue;
            if (string.Equals(sourceModuleId, targetModuleId, StringComparison.Ordinal)) continue;

            var sourceSymbol = graph.GetSymbol(edge.SourceId);
            if (!PassesBucketFilter(sourceSymbol, options)) continue;

            checkedEdges++;
            if (declared.Contains((sourceModuleId, targetModuleId))) continue;

            var key = (sourceModuleId, targetModuleId);
            if (!violations.TryGetValue(key, out var acc))
            {
                acc = new ViolationAccumulator(edge);
                violations[key] = acc;
            }
            acc.Count++;
        }

        var all = violations
            .Select(kv => BuildViolation(kv.Key.SourceModuleId, kv.Key.TargetModuleId, kv.Value, modules, depsByModule))
            .OrderBy(v => v.SourceModuleName, StringComparer.Ordinal)
            .ThenBy(v => v.TargetModuleName, StringComparer.Ordinal)
            .ToArray();
        var returned = all.Take(maxResults).ToArray();

        return new AsmdefBoundaryReport
        {
            ModuleCount = modules.Count,
            DirectOnlyModuleCount = directOnlyModules.Count,
            SkippedModuleCount = modules.Count - directOnlyModules.Count,
            DeclaredModuleDependencyCount = declared.Count,
            CheckedCrossModuleEdgeCount = checkedEdges,
            ViolationCount = all.Length,
            ReturnedViolationCount = returned.Length,
            Truncated = all.Length > returned.Length,
            Summarize = summarize,
            MaxResults = maxResults,
            ExcludeTests = options.ExcludeTests,
            ExcludeGenerated = options.ExcludeGenerated,
            Violations = returned,
            Limitations = new[]
            {
                "Only modules whose symbol property referenceClosure is DirectOnly are enforced. SDK-style Transitive modules are skipped because transitive ProjectReference closure is valid for those workspaces.",
                "The report uses the loaded semantic graph. If Unity has not regenerated csproj files after an asmdef edit, run Unity project-file regeneration and re-analyze before treating the result as authoritative.",
            },
        };
    }

    private static bool IsDirectOnlyModule(Symbol module)
        => module.Properties.TryGetValue(SymbolPropertyKeys.ReferenceClosure, out var mode)
            && string.Equals(mode, DirectOnlyReferenceClosure, StringComparison.Ordinal);

    private static HashSet<(string SourceModuleId, string TargetModuleId)> BuildDeclaredDependencySet(SemanticGraph graph)
    {
        var set = new HashSet<(string, string)>();
        foreach (var edge in graph.Edges)
            if (edge.Kind == EdgeKind.DependsOn)
                set.Add((edge.SourceId, edge.TargetId));
        return set;
    }

    private static Dictionary<string, string[]> BuildDeclaredDependencyMap(SemanticGraph graph)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
        {
            if (edge.Kind != EdgeKind.DependsOn) continue;
            if (!map.TryGetValue(edge.SourceId, out var list))
            {
                list = new List<string>();
                map[edge.SourceId] = list;
            }
            list.Add(edge.TargetId);
        }

        return map.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
    }

    private static AsmdefBoundaryViolation BuildViolation(
        string sourceModuleId,
        string targetModuleId,
        ViolationAccumulator acc,
        IReadOnlyDictionary<string, Symbol> modules,
        IReadOnlyDictionary<string, string[]> depsByModule)
    {
        var first = acc.FirstEdge;
        return new AsmdefBoundaryViolation
        {
            SourceModuleId = sourceModuleId,
            SourceModuleName = modules.TryGetValue(sourceModuleId, out var source) ? source.Name : sourceModuleId,
            TargetModuleId = targetModuleId,
            TargetModuleName = modules.TryGetValue(targetModuleId, out var target) ? target.Name : targetModuleId,
            OffendingEdgeCount = acc.Count,
            SourceSymbolId = first.SourceId,
            TargetSymbolId = first.TargetId,
            EdgeKind = first.Kind.ToString(),
            CallSite = first.CallSite,
            Profiles = first.Profiles,
            DeclaredDependencyModuleIds = depsByModule.TryGetValue(sourceModuleId, out var deps)
                ? deps
                : Array.Empty<string>(),
        };
    }

    private static bool PassesBucketFilter(Symbol? sourceSymbol, AsmdefBoundaryOptions options)
    {
        var bucket = PathBucketClassifier.Classify(sourceSymbol?.FilePath).ToString();
        if (options.ExcludeTests && bucket == "Test") return false;
        if (options.ExcludeGenerated && bucket == "Generated") return false;
        return true;
    }

    private static Func<string, string?> CreateModuleResolver(SemanticGraph graph)
    {
        var cache = new Dictionary<string, string?>(StringComparer.Ordinal);
        return id =>
        {
            if (cache.TryGetValue(id, out var cached)) return cached;
            var resolved = ResolveModule(graph, id);
            cache[id] = resolved;
            return resolved;
        };
    }

    private static string? ResolveModule(SemanticGraph graph, string id)
    {
        var cursor = id;
        for (var hops = 0; hops < 32; hops++)
        {
            var symbol = graph.GetSymbol(cursor);
            if (symbol == null) break;
            if (symbol.Kind == SymbolKind.Module) return symbol.Id;
            if (string.IsNullOrEmpty(symbol.ParentId)) break;
            cursor = symbol.ParentId;
        }

        var visited = new HashSet<string>(StringComparer.Ordinal) { id };
        var queue = new Queue<string>();
        queue.Enqueue(id);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edgeIndex in graph.GetIncomingEdgeIndexes(current))
            {
                var edge = graph.Edges[edgeIndex];
                if (edge.Kind != EdgeKind.Contains) continue;
                if (!visited.Add(edge.SourceId)) continue;
                var parent = graph.GetSymbol(edge.SourceId);
                if (parent == null) continue;
                if (parent.Kind == SymbolKind.Module) return parent.Id;
                queue.Enqueue(parent.Id);
            }
        }

        return null;
    }

    private sealed class ViolationAccumulator
    {
        public ViolationAccumulator(Edge firstEdge) => FirstEdge = firstEdge;
        public Edge FirstEdge { get; }
        public int Count { get; set; }
    }
}

public sealed class AsmdefBoundaryOptions
{
    public const int DefaultMaxResults = 200;
    public const int SummarizeMaxResults = 25;

    public int MaxResults { get; init; } = DefaultMaxResults;
    public bool Summarize { get; init; }
    public bool ExcludeTests { get; init; }
    public bool ExcludeGenerated { get; init; }
}

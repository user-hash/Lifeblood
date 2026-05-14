using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Analysis;

/// <summary>
/// Computes the set of test classes transitively affected when a given
/// symbol or file changes. BFS over incoming non-Contains edges with
/// per-symbol distance tracking; classification of test-vs-non-test
/// reads the extractor-recorded <see cref="Symbol.Properties"/>
/// <c>"attributes"</c> string and folds matching NUnit / Unity Test
/// Framework method-level test attributes into a per-containing-type
/// summary. INV-ANALYSIS-002: Read-only. INV-TEST-IMPACT-001 /
/// LB-TRACK-20260514-007.
/// </summary>
public static class TestImpactAnalyzer
{
    /// <summary>
    /// Method-level attribute names that mark a method as a test case.
    /// Sourced from NUnit (the common .NET testing baseline), Unity Test
    /// Framework, and xUnit. Lifecycle attributes (`SetUp`,
    /// `OneTimeSetUp`, `TearDown`, `OneTimeTearDown`, `UnitySetUp`,
    /// `UnityTearDown`) are intentionally excluded — they participate
    /// in test execution but are not themselves the assertion-bearing
    /// methods a caller wants to enumerate. The complementary set on
    /// <c>UnityReachabilityAdapter</c> serves a different purpose
    /// (dead-code dispatch-entrypoint detection) so duplication of the
    /// list across the two analyzers is intentional; consolidation
    /// belongs to a separate atom if the policies ever diverge.
    /// </summary>
    private static readonly HashSet<string> TestCaseAttributes = new(StringComparer.Ordinal)
    {
        "Test",
        "TestCase",
        "TestCaseSource",
        "Theory",
        "UnityTest",
        "Fact",       // xUnit
        "Xunit.Fact",
        "Xunit.Theory",
    };

    /// <summary>
    /// Compute the test-impact report for a target symbol.
    ///
    /// When the target's <see cref="Symbol.Kind"/> is
    /// <see cref="SymbolKind.Type"/>, the BFS is multi-seeded with the
    /// type AND every member it owns via outgoing Contains edges. The
    /// canonical user query "what tests touch class Foo?" almost
    /// always means "tests that touch any member of Foo" — tests
    /// reference methods and fields, not the type itself, so
    /// References/Calls edges typically terminate at members. Without
    /// the expansion the walker walks incoming non-Contains from
    /// `type:Foo` only, sees no Calls (those bind to methods), and
    /// reports zero affected test classes even when 700+ tests touch
    /// Foo's members. INV-TEST-IMPACT-001 / LB-TRACK-20260514-007
    /// post-fix.
    /// </summary>
    public static TestImpactReport AnalyzeSymbol(SemanticGraph graph, string symbolId, int maxDepth = 12)
    {
        var sources = ExpandTypeMembers(graph, symbolId);
        return Analyze(graph, sources, symbolId, TestImpactTargetKind.Symbol, maxDepth);
    }

    /// <summary>
    /// Return the BFS source seed set for <paramref name="symbolId"/>.
    /// For a Type target, the set is the type itself plus every
    /// directly-owned member reached via outgoing Contains. For every
    /// other kind, the set is the single id (no expansion). See
    /// <see cref="AnalyzeSymbol"/> for the rationale.
    /// </summary>
    private static string[] ExpandTypeMembers(SemanticGraph graph, string symbolId)
    {
        var sym = graph.GetSymbol(symbolId);
        if (sym == null || sym.Kind != SymbolKind.Type)
            return new[] { symbolId };

        var sources = new List<string> { symbolId };
        foreach (var idx in graph.GetOutgoingEdgeIndexes(symbolId))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind == EdgeKind.Contains)
                sources.Add(edge.TargetId);
        }
        return sources.ToArray();
    }

    /// <summary>
    /// Compute the test-impact report for every symbol declared in
    /// <paramref name="filePath"/>. Treats the file as a multi-source
    /// BFS start: any test class that depends on ANY symbol in the
    /// file is impacted, distance is the minimum over every file-side
    /// source.
    /// </summary>
    public static TestImpactReport AnalyzeFile(SemanticGraph graph, string filePath, int maxDepth = 12)
    {
        var sources = new List<string>();
        var normalized = NormalizePath(filePath);
        foreach (var s in graph.Symbols)
        {
            if (string.IsNullOrEmpty(s.FilePath)) continue;
            if (NormalizePath(s.FilePath) == normalized)
                sources.Add(s.Id);
        }
        return Analyze(graph, sources, filePath, TestImpactTargetKind.File, maxDepth);
    }

    private static TestImpactReport Analyze(
        SemanticGraph graph,
        IReadOnlyCollection<string> sourceIds,
        string target,
        TestImpactTargetKind kind,
        int maxDepth)
    {
        if (sourceIds.Count == 0)
            return Empty(target, kind);

        // BFS over INCOMING non-Contains edges. Track minimum distance
        // per visited symbol — first-seen wins because BFS explores in
        // increasing-distance order. Skip Contains edges to keep the
        // walk over real dependency relationships only.
        var minDistance = new Dictionary<string, int>(StringComparer.Ordinal);
        var queue = new Queue<(string id, int depth)>();
        foreach (var sid in sourceIds)
        {
            // Sources themselves are at distance 0; their direct
            // dependants are at distance 1.
            minDistance[sid] = 0;
            queue.Enqueue((sid, 0));
        }

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;

            foreach (int idx in graph.GetIncomingEdgeIndexes(current))
            {
                var edge = graph.Edges[idx];
                if (edge.Kind == EdgeKind.Contains) continue;

                var sourceId = edge.SourceId;
                var newDistance = depth + 1;
                if (minDistance.TryGetValue(sourceId, out var existing) && existing <= newDistance) continue;

                minDistance[sourceId] = newDistance;
                queue.Enqueue((sourceId, newDistance));
            }
        }

        // Collect affected test methods and group by their containing
        // type. The containing type id is read off Symbol.ParentId, set
        // by the extractor on every method symbol.
        var classes = new Dictionary<string, (Symbol Type, List<(string Name, int Distance)> Methods)>(StringComparer.Ordinal);
        foreach (var (id, distance) in minDistance)
        {
            if (distance == 0) continue; // source itself
            var sym = graph.GetSymbol(id);
            if (sym == null) continue;
            if (sym.Kind != SymbolKind.Method) continue;
            if (!IsTestMethod(sym)) continue;

            // Walk up to the containing type.
            var containingTypeId = FindContainingType(graph, sym);
            if (containingTypeId == null) continue;
            var containingType = graph.GetSymbol(containingTypeId);
            if (containingType == null || containingType.Kind != SymbolKind.Type) continue;

            if (!classes.TryGetValue(containingTypeId, out var bucket))
            {
                bucket = (containingType, new List<(string, int)>());
                classes[containingTypeId] = bucket;
            }
            bucket.Methods.Add((sym.Name, distance));
        }

        // Build per-class impact rows. Min distance per class is the
        // min across its affected test methods.
        var rows = new List<TestClassImpact>(classes.Count);
        int directCount = 0;
        int totalMethods = 0;
        foreach (var kv in classes)
        {
            var classMin = int.MaxValue;
            foreach (var m in kv.Value.Methods)
                if (m.Distance < classMin) classMin = m.Distance;

            var confidence = classMin switch
            {
                1 => TestImpactConfidence.Direct,
                2 => TestImpactConfidence.OneHop,
                _ => TestImpactConfidence.Transitive,
            };
            if (confidence == TestImpactConfidence.Direct) directCount++;
            totalMethods += kv.Value.Methods.Count;

            // Stable method-name ordering on the wire so two runs with
            // the same graph produce byte-identical output.
            var names = kv.Value.Methods
                .Select(m => m.Name)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            rows.Add(new TestClassImpact
            {
                TypeId = kv.Key,
                Name = kv.Value.Type.Name,
                QualifiedName = kv.Value.Type.QualifiedName,
                FilePath = kv.Value.Type.FilePath,
                MinDistance = classMin,
                Confidence = confidence,
                TestMethodNames = names,
            });
        }

        // Sort by ascending distance, then by qualified name for stable
        // wire output across runs.
        rows.Sort((a, b) =>
        {
            var d = a.MinDistance.CompareTo(b.MinDistance);
            return d != 0 ? d : string.CompareOrdinal(a.QualifiedName, b.QualifiedName);
        });

        var filters = rows
            .Select(r => $"FullyQualifiedName~{(string.IsNullOrEmpty(r.QualifiedName) ? r.Name : r.QualifiedName)}")
            .ToArray();

        return new TestImpactReport
        {
            Target = target,
            TargetKind = kind,
            AffectedTestClasses = rows.ToArray(),
            TotalTestMethodCount = totalMethods,
            DirectTestClassCount = directCount,
            RecommendedFilters = filters,
        };
    }

    private static TestImpactReport Empty(string target, TestImpactTargetKind kind) => new()
    {
        Target = target,
        TargetKind = kind,
        AffectedTestClasses = Array.Empty<TestClassImpact>(),
        TotalTestMethodCount = 0,
        DirectTestClassCount = 0,
        RecommendedFilters = Array.Empty<string>(),
    };

    private static bool IsTestMethod(Symbol sym)
    {
        if (sym.Properties == null) return false;
        if (!sym.Properties.TryGetValue(SymbolPropertyKeys.Attributes, out var attrs) || string.IsNullOrEmpty(attrs))
            return false;
        foreach (var name in attrs.Split(';'))
            if (TestCaseAttributes.Contains(name))
                return true;
        return false;
    }

    /// <summary>
    /// Walk <paramref name="member"/>'s <see cref="Symbol.ParentId"/>
    /// chain to the first ancestor whose kind is <see cref="SymbolKind.Type"/>.
    /// Returns null when the chain dead-ends without a type ancestor
    /// (file-scope member, namespace-scope, etc.). Cycle-safe via a
    /// visited-set bounded by the graph's symbol count.
    /// </summary>
    private static string? FindContainingType(SemanticGraph graph, Symbol member)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { member.Id };
        var parentId = member.ParentId;
        while (!string.IsNullOrEmpty(parentId))
        {
            if (!visited.Add(parentId)) return null;
            var parent = graph.GetSymbol(parentId);
            if (parent == null) return null;
            if (parent.Kind == SymbolKind.Type) return parent.Id;
            parentId = parent.ParentId;
        }
        return null;
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
}

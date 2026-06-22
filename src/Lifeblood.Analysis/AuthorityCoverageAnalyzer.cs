using Lifeblood.Domain.Graph;
using Lifeblood.Domain.PathClassification;
using Lifeblood.Domain.Results;

namespace Lifeblood.Analysis;

/// <summary>
/// Graph-only authority coverage matrix. Walks outgoing non-Contains edges from
/// subject seeds and records the shortest path to each required authority root.
/// INV-ANALYSIS-002 / INV-AUTHORITY-COVERAGE-001.
/// </summary>
public static class AuthorityCoverageAnalyzer
{
    private const int DefaultSubjectPreviewLimit = 25;

    public static AuthorityCoverageReport Analyze(SemanticGraph graph, AuthorityCoverageOptions options)
    {
        var maxDepth = options.MaxDepth < 0 ? 0 : options.MaxDepth;
        var includeBuckets = options.IncludeBuckets is { Length: > 0 }
            ? new HashSet<string>(options.IncludeBuckets, StringComparer.OrdinalIgnoreCase)
            : null;

        var requiredRoots = options.RequiredAuthorities
            .Select(a => AuthorityRoot.Build(graph, a))
            .ToArray();
        var alternativeRoots = options.AllowedAlternatives
            .Select(a => AuthorityRoot.Build(graph, a))
            .ToArray();

        var requiredLookup = BuildTargetLookup(requiredRoots);
        var alternativeLookup = BuildTargetLookup(alternativeRoots);

        var rows = new List<AuthorityCoverageRow>(options.Subjects.Length);
        var totalExpanded = 0;
        var totalAnalyzed = 0;

        foreach (var subject in options.Subjects)
        {
            var expanded = ExpandSubjectSeeds(graph, subject);
            var analyzed = expanded
                .Where(seed => PassesBucketFilter(graph.GetSymbol(seed), options, includeBuckets))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            totalExpanded += expanded.Length;
            totalAnalyzed += analyzed.Length;
            rows.Add(AnalyzeSubject(
                graph,
                subject,
                expanded,
                analyzed,
                requiredRoots,
                alternativeRoots,
                requiredLookup,
                alternativeLookup,
                maxDepth,
                options.SubjectPreviewLimit <= 0 ? DefaultSubjectPreviewLimit : options.SubjectPreviewLimit));
        }

        return new AuthorityCoverageReport
        {
            SubjectInputs = options.Subjects.Select(s => s.Input).ToArray(),
            RequiredAuthorityIds = requiredRoots.Select(r => r.Id).ToArray(),
            AllowedAlternativeIds = alternativeRoots.Select(r => r.Id).ToArray(),
            MaxDepth = maxDepth,
            ExcludeTests = options.ExcludeTests,
            ExcludeGenerated = options.ExcludeGenerated,
            IncludeBuckets = options.IncludeBuckets ?? Array.Empty<string>(),
            SubjectCount = options.Subjects.Length,
            ExpandedSubjectCount = totalExpanded,
            AnalyzedSubjectSeedCount = totalAnalyzed,
            Rows = rows
                .OrderBy(r => r.SubjectId, StringComparer.Ordinal)
                .ToArray(),
            Limitations = new[]
            {
                "Authority coverage walks the loaded semantic graph only. Reflection, Unity serialized YAML, config files, generated runtime wiring, and out-of-graph callers can provide authorities that are invisible here.",
                "Bucket filters apply to expanded subject seeds by declaration path bucket; dependency paths are not bucket-pruned after traversal starts.",
            },
        };
    }

    private static AuthorityCoverageRow AnalyzeSubject(
        SemanticGraph graph,
        AuthorityCoverageInput subject,
        string[] expanded,
        string[] analyzed,
        AuthorityRoot[] requiredRoots,
        AuthorityRoot[] alternativeRoots,
        Dictionary<string, List<AuthorityRoot>> requiredLookup,
        Dictionary<string, List<AuthorityRoot>> alternativeLookup,
        int maxDepth,
        int subjectPreviewLimit)
    {
        var reachedRequired = new Dictionary<string, ReachCandidate>(StringComparer.Ordinal);
        var reachedAlternatives = new Dictionary<string, ReachCandidate>(StringComparer.Ordinal);

        foreach (var seed in analyzed)
        {
            WalkFromSeed(graph, seed, maxDepth, requiredLookup, alternativeLookup, reachedRequired, reachedAlternatives);
        }

        var reachedAuthorityRows = requiredRoots
            .Where(r => reachedRequired.ContainsKey(r.Id))
            .Select(r => reachedRequired[r.Id].ToReach(graph))
            .OrderBy(r => r.Distance)
            .ThenBy(r => r.AuthorityId, StringComparer.Ordinal)
            .ToArray();

        var missing = requiredRoots
            .Where(r => !reachedRequired.ContainsKey(r.Id))
            .Select(r => r.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        var alternativeRows = alternativeRoots
            .Where(r => reachedAlternatives.ContainsKey(r.Id))
            .Select(r => reachedAlternatives[r.Id].ToReach(graph))
            .OrderBy(r => r.Distance)
            .ThenBy(r => r.AuthorityId, StringComparer.Ordinal)
            .ToArray();

        var status = analyzed.Length == 0
            ? AuthorityCoverageStatus.NoSubjectSeeds
            : missing.Length == 0
                ? AuthorityCoverageStatus.RequiredReached
                : alternativeRows.Length > 0
                    ? AuthorityCoverageStatus.AllowedAlternativeReached
                    : AuthorityCoverageStatus.MissingRequired;

        var subjectSymbol = graph.GetSymbol(subject.Id);
        var bucket = PathBucketClassifier.Classify(subjectSymbol?.FilePath ?? subject.FilePath).ToString();

        return new AuthorityCoverageRow
        {
            SubjectInput = subject.Input,
            SubjectId = subject.Id,
            SubjectKind = subject.Kind,
            SubjectName = subjectSymbol?.Name ?? subject.Id,
            FilePath = subjectSymbol?.FilePath ?? subject.FilePath,
            Bucket = bucket,
            ExpandedSubjectCount = expanded.Length,
            AnalyzedSubjectSeedCount = analyzed.Length,
            SubjectSeedPreview = analyzed.Take(subjectPreviewLimit).ToArray(),
            Status = status,
            HasAllRequiredAuthority = missing.Length == 0 && analyzed.Length > 0,
            ReachedRequiredCount = reachedAuthorityRows.Length,
            MissingRequiredCount = missing.Length,
            ReachedAuthorities = reachedAuthorityRows,
            MissingAuthorities = missing,
            ReachedAllowedAlternatives = alternativeRows,
            FirstCompetingAuthority = alternativeRows.FirstOrDefault(),
        };
    }

    private static void WalkFromSeed(
        SemanticGraph graph,
        string seed,
        int maxDepth,
        Dictionary<string, List<AuthorityRoot>> requiredLookup,
        Dictionary<string, List<AuthorityRoot>> alternativeLookup,
        Dictionary<string, ReachCandidate> reachedRequired,
        Dictionary<string, ReachCandidate> reachedAlternatives)
    {
        var queue = new Queue<(string Id, int Distance)>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { seed };
        var previous = new Dictionary<string, PathBacklink>(StringComparer.Ordinal);
        queue.Enqueue((seed, 0));

        RecordMatches(seed, seed, 0, previous, requiredLookup, reachedRequired);
        RecordMatches(seed, seed, 0, previous, alternativeLookup, reachedAlternatives);

        while (queue.Count > 0)
        {
            var (current, distance) = queue.Dequeue();
            if (distance >= maxDepth) continue;

            foreach (int idx in graph.GetOutgoingEdgeIndexes(current))
            {
                var edge = graph.Edges[idx];
                if (edge.Kind == EdgeKind.Contains) continue;
                if (!visited.Add(edge.TargetId)) continue;

                var nextDistance = distance + 1;
                previous[edge.TargetId] = new PathBacklink(current, edge.Kind.ToString());
                RecordMatches(seed, edge.TargetId, nextDistance, previous, requiredLookup, reachedRequired);
                RecordMatches(seed, edge.TargetId, nextDistance, previous, alternativeLookup, reachedAlternatives);
                queue.Enqueue((edge.TargetId, nextDistance));
            }
        }
    }

    private static void RecordMatches(
        string seed,
        string matchedSymbolId,
        int distance,
        Dictionary<string, PathBacklink> previous,
        Dictionary<string, List<AuthorityRoot>> lookup,
        Dictionary<string, ReachCandidate> reached)
    {
        if (!lookup.TryGetValue(matchedSymbolId, out var roots)) return;

        foreach (var root in roots)
        {
            var candidate = new ReachCandidate(root.Id, matchedSymbolId, seed, distance, previous);
            if (!reached.TryGetValue(root.Id, out var existing)
                || candidate.IsBetterThan(existing))
            {
                reached[root.Id] = candidate;
            }
        }
    }

    private static Dictionary<string, List<AuthorityRoot>> BuildTargetLookup(AuthorityRoot[] roots)
    {
        var lookup = new Dictionary<string, List<AuthorityRoot>>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            foreach (var id in root.TargetIds)
            {
                if (!lookup.TryGetValue(id, out var list))
                {
                    list = new List<AuthorityRoot>(1);
                    lookup[id] = list;
                }
                list.Add(root);
            }
        }
        return lookup;
    }

    private static string[] ExpandSubjectSeeds(SemanticGraph graph, AuthorityCoverageInput input)
    {
        if (input.Kind == AuthorityCoverageInputKind.File)
        {
            var methods = SymbolsInFile(graph, input.FilePath)
                .Where(s => s.Kind == SymbolKind.Method)
                .Select(s => s.Id)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            if (methods.Length > 0) return methods;

            return SymbolsInFile(graph, input.FilePath)
                .Select(s => s.Id)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
        }

        var symbol = graph.GetSymbol(input.Id);
        if (symbol == null) return Array.Empty<string>();
        if (symbol.Kind == SymbolKind.Method) return new[] { symbol.Id };

        if (symbol.Kind is SymbolKind.Type or SymbolKind.Namespace or SymbolKind.File)
        {
            var methods = Descendants(graph, symbol.Id)
                .Where(s => s.Kind == SymbolKind.Method)
                .Select(s => s.Id)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            return methods.Length > 0 ? methods : new[] { symbol.Id };
        }

        return new[] { symbol.Id };
    }

    private static bool PassesBucketFilter(
        Symbol? seed,
        AuthorityCoverageOptions options,
        HashSet<string>? includeBuckets)
    {
        var bucket = PathBucketClassifier.Classify(seed?.FilePath).ToString();
        if (options.ExcludeTests && bucket == "Test") return false;
        if (options.ExcludeGenerated && bucket == "Generated") return false;
        if (includeBuckets != null && !includeBuckets.Contains(bucket)) return false;
        return true;
    }

    private static IEnumerable<Symbol> SymbolsInFile(SemanticGraph graph, string filePath)
    {
        var normalized = NormalizePath(filePath);
        return graph.Symbols.Where(s => NormalizePath(s.FilePath) == normalized);
    }

    private static IReadOnlyList<Symbol> Descendants(SemanticGraph graph, string rootId)
    {
        var result = new List<Symbol>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { rootId };
        var queue = new Queue<string>();
        queue.Enqueue(rootId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (int idx in graph.GetOutgoingEdgeIndexes(current))
            {
                var edge = graph.Edges[idx];
                if (edge.Kind != EdgeKind.Contains) continue;
                if (!visited.Add(edge.TargetId)) continue;
                var child = graph.GetSymbol(edge.TargetId);
                if (child == null) continue;
                result.Add(child);
                queue.Enqueue(edge.TargetId);
            }
        }
        return result;
    }

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    private sealed class AuthorityRoot
    {
        private AuthorityRoot(string id, string[] targetIds)
        {
            Id = id;
            TargetIds = targetIds;
        }

        public string Id { get; }
        public string[] TargetIds { get; }

        public static AuthorityRoot Build(SemanticGraph graph, AuthorityCoverageInput input)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (input.Kind == AuthorityCoverageInputKind.File)
            {
                foreach (var sym in SymbolsInFile(graph, input.FilePath))
                    ids.Add(sym.Id);
            }
            else
            {
                ids.Add(input.Id);
                foreach (var desc in Descendants(graph, input.Id))
                    ids.Add(desc.Id);
            }

            return new AuthorityRoot(input.Id, ids.OrderBy(id => id, StringComparer.Ordinal).ToArray());
        }
    }

    private sealed class ReachCandidate
    {
        private readonly Dictionary<string, PathBacklink> _previous;

        public ReachCandidate(
            string authorityId,
            string matchedSymbolId,
            string subjectSeedId,
            int distance,
            Dictionary<string, PathBacklink> previous)
        {
            AuthorityId = authorityId;
            MatchedSymbolId = matchedSymbolId;
            SubjectSeedId = subjectSeedId;
            Distance = distance;
            _previous = new Dictionary<string, PathBacklink>(previous, StringComparer.Ordinal);
        }

        public string AuthorityId { get; }
        public string MatchedSymbolId { get; }
        public string SubjectSeedId { get; }
        public int Distance { get; }

        public bool IsBetterThan(ReachCandidate other)
        {
            if (Distance != other.Distance) return Distance < other.Distance;
            var seedCompare = string.CompareOrdinal(SubjectSeedId, other.SubjectSeedId);
            if (seedCompare != 0) return seedCompare < 0;
            return string.CompareOrdinal(MatchedSymbolId, other.MatchedSymbolId) < 0;
        }

        public AuthorityCoverageReach ToReach(SemanticGraph graph)
            => new()
            {
                AuthorityId = AuthorityId,
                MatchedSymbolId = MatchedSymbolId,
                SubjectSeedId = SubjectSeedId,
                Distance = Distance,
                Path = BuildPath(graph),
            };

        private AuthorityCoveragePathStep[] BuildPath(SemanticGraph graph)
        {
            var reversed = new List<(string Id, string Via)>();
            var current = MatchedSymbolId;
            reversed.Add((current, _previous.TryGetValue(current, out var last) ? last.EdgeKind : ""));
            while (!string.Equals(current, SubjectSeedId, StringComparison.Ordinal)
                && _previous.TryGetValue(current, out var prev))
            {
                current = prev.PreviousId;
                reversed.Add((current, _previous.TryGetValue(current, out var link) ? link.EdgeKind : ""));
            }

            reversed.Reverse();
            if (reversed.Count > 0)
                reversed[0] = (reversed[0].Id, "");

            return reversed
                .Select(step =>
                {
                    var sym = graph.GetSymbol(step.Id);
                    return new AuthorityCoveragePathStep
                    {
                        SymbolId = step.Id,
                        Name = sym?.Name ?? step.Id,
                        Kind = sym?.Kind.ToString() ?? "",
                        ViaEdgeKind = step.Via,
                    };
                })
                .ToArray();
        }
    }

    private readonly record struct PathBacklink(string PreviousId, string EdgeKind);
}

public sealed class AuthorityCoverageOptions
{
    public required AuthorityCoverageInput[] Subjects { get; init; }
    public required AuthorityCoverageInput[] RequiredAuthorities { get; init; }
    public AuthorityCoverageInput[] AllowedAlternatives { get; init; } = Array.Empty<AuthorityCoverageInput>();
    public int MaxDepth { get; init; } = 6;
    public bool ExcludeTests { get; init; }
    public bool ExcludeGenerated { get; init; }
    public string[]? IncludeBuckets { get; init; }
    public int SubjectPreviewLimit { get; init; } = 25;
}

public sealed class AuthorityCoverageInput
{
    public required string Input { get; init; }
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public string FilePath { get; init; } = "";
}

public static class AuthorityCoverageInputKind
{
    public const string Symbol = "Symbol";
    public const string File = "File";
}

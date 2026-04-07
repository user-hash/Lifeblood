using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Lifeblood.Domain.Rules;

namespace Lifeblood.Analysis;

/// <summary>
/// AUDIT FIX (Hole 4): Returns violations as separate objects. Does NOT mutate the graph.
/// INV-GRAPH-004: Graph is read-only after construction.
/// </summary>
public static class RuleValidator
{
    public static Violation[] Validate(SemanticGraph graph, ArchitectureRule[] rules)
    {
        if (rules == null || rules.Length == 0) return Array.Empty<Violation>();

        var violations = new List<Violation>();
        var nsLookup = new Dictionary<string, string>(graph.Symbols.Length, StringComparer.Ordinal);
        for (int i = 0; i < graph.Symbols.Length; i++)
            nsLookup[graph.Symbols[i].Id] = graph.Symbols[i].QualifiedName;

        for (int e = 0; e < graph.Edges.Length; e++)
        {
            var edge = graph.Edges[e];
            if (edge.Kind == EdgeKind.Contains) continue;

            string srcNs = nsLookup.GetValueOrDefault(edge.SourceId, edge.SourceId);
            string tgtNs = nsLookup.GetValueOrDefault(edge.TargetId, edge.TargetId);

            for (int r = 0; r < rules.Length; r++)
            {
                if (!MatchesPattern(srcNs, rules[r].Source)) continue;

                if (rules[r].MustNotReference != null && MatchesPattern(tgtNs, rules[r].MustNotReference!))
                {
                    violations.Add(new Violation
                    {
                        SourceSymbolId = edge.SourceId,
                        TargetSymbolId = edge.TargetId,
                        SourceNamespace = srcNs,
                        TargetNamespace = tgtNs,
                        RuleBroken = $"{rules[r].Id}: {rules[r].Source} must_not_reference {rules[r].MustNotReference}",
                        EdgeIndex = e,
                    });
                    break;
                }

                if (rules[r].MayOnlyReference != null && !MatchesPattern(tgtNs, rules[r].MayOnlyReference!))
                {
                    violations.Add(new Violation
                    {
                        SourceSymbolId = edge.SourceId,
                        TargetSymbolId = edge.TargetId,
                        SourceNamespace = srcNs,
                        TargetNamespace = tgtNs,
                        RuleBroken = $"{rules[r].Id}: {rules[r].Source} may_only_reference {rules[r].MayOnlyReference}",
                        EdgeIndex = e,
                    });
                    break;
                }
            }
        }

        return violations.ToArray();
    }

    private static bool MatchesPattern(string value, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        if (pattern == "*") return true;
        if (pattern.StartsWith("*."))
            return value.EndsWith(pattern.Substring(1), StringComparison.OrdinalIgnoreCase);
        if (pattern.EndsWith(".*"))
            return value.StartsWith(pattern.Substring(0, pattern.Length - 2), StringComparison.OrdinalIgnoreCase);
        return value.Equals(pattern, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(pattern + ".", StringComparison.OrdinalIgnoreCase);
    }
}

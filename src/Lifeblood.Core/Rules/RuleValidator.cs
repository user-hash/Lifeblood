using Lifeblood.Core.Graph;

namespace Lifeblood.Core.Rules;

/// <summary>
/// Validates edges in the semantic graph against architecture rules.
/// Marks violating edges and returns a list of violations.
///
/// INV-ANALYSIS-001: Stateless. Input: graph + rules. Output: violations.
/// </summary>
public static class RuleValidator
{
    public static Violation[] Validate(SemanticGraph graph, ArchitectureRule[] rules)
    {
        if (rules == null || rules.Length == 0)
            return Array.Empty<Violation>();

        var violations = new List<Violation>();

        // Build symbol → namespace lookup
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
                var rule = rules[r];

                if (!MatchesPattern(srcNs, rule.Source))
                    continue;

                bool violated = false;
                string ruleDesc = "";

                if (rule.MustNotReference != null && MatchesPattern(tgtNs, rule.MustNotReference))
                {
                    violated = true;
                    ruleDesc = $"{rule.Source} must_not_reference {rule.MustNotReference}";
                }
                else if (rule.MayOnlyReference != null && !MatchesPattern(tgtNs, rule.MayOnlyReference))
                {
                    violated = true;
                    ruleDesc = $"{rule.Source} may_only_reference {rule.MayOnlyReference}";
                }

                if (violated)
                {
                    graph.Edges[e].IsViolation = true;
                    violations.Add(new Violation
                    {
                        SourceSymbolId = edge.SourceId,
                        TargetSymbolId = edge.TargetId,
                        SourceNamespace = srcNs,
                        TargetNamespace = tgtNs,
                        RuleBroken = ruleDesc,
                    });
                    break; // One violation per edge is enough
                }
            }
        }

        return violations.ToArray();
    }

    private static bool MatchesPattern(string value, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        if (pattern == "*") return true;

        // Wildcard prefix: "*.Tests" matches "MyApp.Tests"
        if (pattern.StartsWith("*."))
        {
            string suffix = pattern.Substring(1);
            return value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        // Wildcard suffix: "MyApp.*" matches "MyApp.Anything"
        if (pattern.EndsWith(".*"))
        {
            string prefix = pattern.Substring(0, pattern.Length - 2);
            return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        // Exact or prefix match
        return value.Equals(pattern, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(pattern + ".", StringComparison.OrdinalIgnoreCase);
    }
}

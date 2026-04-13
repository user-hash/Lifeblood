using Lifeblood.Application.Ports.Right;
using Lifeblood.Domain.Graph;

namespace Lifeblood.Connectors.Mcp;

/// <summary>
/// Reference implementation of <see cref="IDeadCodeAnalyzer"/>. Walks the
/// graph once, emits one <see cref="DeadCodeResult"/> per symbol with no
/// incoming semantic edges (ignoring <see cref="EdgeKind.Contains"/>
/// which is a structural parent→child relationship, not a reference).
///
/// Stateless per INV-ANALYSIS-001. Read-only per INV-GRAPH-004. Added
/// 2026-04-11 (Phase 6 / B5) to close DAWG R1.
///
/// Lives in <c>Lifeblood.Connectors.Mcp</c> because <c>Lifeblood.Analysis</c>
/// is Domain-only (no Application port dependency). Connectors are the
/// right home for port implementations that plug Domain-level walks into
/// Application-level contracts.
/// </summary>
public sealed class LifebloodDeadCodeAnalyzer : IDeadCodeAnalyzer
{
    private static readonly SymbolKind[] DefaultKinds = new[]
    {
        SymbolKind.Method, SymbolKind.Type, SymbolKind.Property, SymbolKind.Field,
    };

    public DeadCodeResult[] FindDeadCode(SemanticGraph graph, DeadCodeOptions options)
    {
        var kinds = options.IncludeKinds != null && options.IncludeKinds.Length > 0
            ? new HashSet<SymbolKind>(options.IncludeKinds)
            : new HashSet<SymbolKind>(DefaultKinds);

        var results = new List<DeadCodeResult>();
        foreach (var sym in graph.Symbols)
        {
            if (!kinds.Contains(sym.Kind)) continue;
            if (options.ExcludePublic && sym.Visibility == Visibility.Public) continue;
            if (options.ExcludeTests && LooksLikeTestFile(sym.FilePath)) continue;

            if (HasIncomingReference(graph, sym.Id)) continue;

            results.Add(new DeadCodeResult(
                CanonicalId: sym.Id,
                Kind: sym.Kind,
                Name: sym.Name,
                FilePath: sym.FilePath,
                Line: sym.Line,
                Reason: BuildReason(sym, options)));
        }
        return results
            .OrderBy(r => r.FilePath, StringComparer.Ordinal)
            .ThenBy(r => r.Line)
            .ThenBy(r => r.CanonicalId, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// A symbol is "dead" if no other symbol references it via a
    /// non-Contains edge. Contains is the type→member parent link,
    /// not a usage, so it's not counted.
    /// </summary>
    private static bool HasIncomingReference(SemanticGraph graph, string symbolId)
    {
        foreach (int idx in graph.GetIncomingEdgeIndexes(symbolId))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind == EdgeKind.Contains) continue;
            return true;
        }
        // A method/property implementing an interface member is reachable by definition:
        // callers invoke through the interface, which carries the Calls edges.
        // The implementing symbol has an OUTGOING Implements edge to the interface member.
        foreach (int idx in graph.GetOutgoingEdgeIndexes(symbolId))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind == EdgeKind.Implements) return true;
        }
        return false;
    }

    /// <summary>
    /// Heuristic test-file detector: any path segment named "tests" or
    /// any filename matching <c>*Tests.cs</c> or <c>*Test.cs</c>. Not
    /// exhaustive, but covers the conventional project layouts.
    /// </summary>
    private static bool LooksLikeTestFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        var lower = filePath.ToLowerInvariant();
        if (lower.Contains("/tests/") || lower.Contains("\\tests\\")) return true;
        if (lower.EndsWith("tests.cs", System.StringComparison.Ordinal)) return true;
        if (lower.EndsWith("test.cs", System.StringComparison.Ordinal)) return true;
        return false;
    }

    private static string BuildReason(Symbol sym, DeadCodeOptions options)
    {
        var scope = options.ExcludePublic ? "non-public " : "";
        return $"{scope}{sym.Kind.ToString().ToLowerInvariant()} with no incoming semantic references";
    }
}

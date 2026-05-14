using System;
using System.Linq;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.PathClassification;

namespace Lifeblood.Connectors.Mcp;

/// <summary>
/// Reference implementation of <see cref="IDeadCodeAnalyzer"/>. Walks the
/// graph once, emits one <see cref="DeadCodeResult"/> per symbol with no
/// incoming semantic edges (ignoring <see cref="EdgeKind.Contains"/>
/// which is a structural parent→child relationship, not a reference).
///
/// Stateless per INV-ANALYSIS-001. Read-only per INV-GRAPH-004.
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

    private readonly IUnityReachabilityProvider? _runtimeReachability;

    /// <summary>
    /// Default constructor — no runtime-dispatch reachability provider.
    /// Used for non-Unity workspaces and unit tests that want the pure
    /// graph-walk classification.
    /// </summary>
    public LifebloodDeadCodeAnalyzer() { }

    /// <summary>
    /// Inject a runtime-dispatch reachability provider (Unity, ASP.NET,
    /// MEF, etc.). When supplied, the analyzer treats symbols flagged
    /// by the provider as live and excludes them from the result.
    /// </summary>
    public LifebloodDeadCodeAnalyzer(IUnityReachabilityProvider? runtimeReachability)
    {
        _runtimeReachability = runtimeReachability;
    }

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
            if (options.ExcludeTests && PathBucketClassifier.IsTest(sym.FilePath)) continue;

            if (HasIncomingReference(graph, sym.Id)) continue;

            // Runtime-dispatch reachability: framework-level entry points
            // that no static call site reaches. The provider is optional;
            // when unset, the analyzer behaves exactly as it did before P3.
            if (_runtimeReachability != null
                && _runtimeReachability.IsRuntimeReachable(graph, sym, out _))
                continue;

            results.Add(new DeadCodeResult(
                CanonicalId: sym.Id,
                Kind: sym.Kind,
                Name: sym.Name,
                FilePath: sym.FilePath,
                Line: sym.Line,
                Reason: BuildReason(sym, options))
            {
                DirectDependants = CountDirectDependants(graph, sym.Id),
                Bucket = (DeadCodeBucket)PathBucketClassifier.Classify(sym.FilePath),
                DeclarationOnly = sym.IsAbstract,
            });
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

    private static string BuildReason(Symbol sym, DeadCodeOptions options)
    {
        var scope = options.ExcludePublic ? "non-public " : "";
        return $"{scope}{sym.Kind.ToString().ToLowerInvariant()} with no incoming semantic references";
    }

    /// <summary>
    /// Count incoming non-Contains edges. Classic findings always carry 0
    /// (the analyzer drops any symbol with such edges via
    /// <see cref="HasIncomingReference"/>); the field is on the wire as
    /// forward-compatible signal for future relaxed criteria where it
    /// would surface the "barely reachable" class. INV-DEADCODE-TRIAGE-001.
    /// </summary>
    private static int CountDirectDependants(SemanticGraph graph, string symbolId)
    {
        int count = 0;
        foreach (int idx in graph.GetIncomingEdgeIndexes(symbolId))
        {
            if (graph.Edges[idx].Kind == EdgeKind.Contains) continue;
            count++;
        }
        return count;
    }

}

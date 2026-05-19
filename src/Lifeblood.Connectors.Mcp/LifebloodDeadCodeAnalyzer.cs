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

            // Liveness check. A method/property implementing an interface
            // member is reachable by definition — that branch ALWAYS short-
            // circuits regardless of incoming refs or the same-class option.
            if (IsLiveByOutgoingImplements(graph, sym.Id)) continue;

            // Incoming-reference filter. Default mode drops any symbol with
            // a non-Contains incoming edge. F2: when
            // IncludeSameClassOnlyConsumers=true, also surface symbols whose
            // ONLY incoming refs come from same-class members — useful for
            // private-field cleanup triage. INV-DEADCODE-TRIAGE-002.
            bool hasAnyIncoming = HasAnyIncomingNonContainsEdge(graph, sym.Id);
            if (hasAnyIncoming)
            {
                if (!options.IncludeSameClassOnlyConsumers) continue;
                if (!HasOnlySameClassReferences(graph, sym)) continue;
            }

            // Static constructors are runtime entry points: explicit and
            // synthesized .cctor methods are invoked by the CLR on type init.
            // Without this guard, a dispatch table with a correctly surfaced
            // explicit .cctor would shift the dead-code false positive from
            // the delegate target to the static constructor itself.
            if (sym.Kind == SymbolKind.Method && sym.Name == ".cctor") continue;

            // Synthesized parameterless .ctor surfaces (see
            // RoslynSymbolExtractor.SurfaceSynthesizedInitializerConstructors,
            // INV-EXTRACT-SYNTHESIZED-CTOR-001). The CLR invokes these on
            // first instance construction, so they are inherently live
            // regardless of static-graph reachability.
            if (sym.Properties.TryGetValue("synthesized", out var synthesized)
                && synthesized == "true") continue;

            // Runtime-dispatch reachability: framework-level entry points
            // that no static call site reaches. The provider is optional;
            // when unset, the analyzer behaves exactly as it did before P3.
            if (_runtimeReachability != null
                && _runtimeReachability.IsRuntimeReachable(graph, sym, out _))
                continue;

            int sameClassConsumers = CountSameClassConsumers(graph, sym);
            results.Add(new DeadCodeResult(
                CanonicalId: sym.Id,
                Kind: sym.Kind,
                Name: sym.Name,
                FilePath: sym.FilePath,
                Line: sym.Line,
                Reason: BuildReason(sym, options, sameClassConsumers))
            {
                DirectDependants = CountDirectDependants(graph, sym.Id),
                Bucket = (DeadCodeBucket)PathBucketClassifier.Classify(sym.FilePath),
                DeclarationOnly = sym.IsAbstract,
                SameClassConsumerCount = sameClassConsumers,
            });
        }
        return results
            .OrderBy(r => r.FilePath, StringComparer.Ordinal)
            .ThenBy(r => r.Line)
            .ThenBy(r => r.CanonicalId, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// True if the symbol has at least one incoming non-Contains edge.
    /// Contains is the type→member parent link, not a usage.
    /// </summary>
    private static bool HasAnyIncomingNonContainsEdge(SemanticGraph graph, string symbolId)
    {
        foreach (int idx in graph.GetIncomingEdgeIndexes(symbolId))
        {
            if (graph.Edges[idx].Kind == EdgeKind.Contains) continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// True if the symbol has an OUTGOING Implements edge — it implements an
    /// interface member and is reachable by callers invoking through the
    /// interface. Separated from incoming-ref logic so the F2 same-class
    /// relaxation can't accidentally surface interface implementers.
    /// </summary>
    private static bool IsLiveByOutgoingImplements(SemanticGraph graph, string symbolId)
    {
        foreach (int idx in graph.GetOutgoingEdgeIndexes(symbolId))
        {
            if (graph.Edges[idx].Kind == EdgeKind.Implements) return true;
        }
        return false;
    }

    /// <summary>
    /// True iff every incoming non-Contains reference comes from a symbol
    /// whose ParentId matches the candidate's ParentId (same containing
    /// type) AND at least one such reference exists. Used by F2's
    /// IncludeSameClassOnlyConsumers relaxation — the symbol is only
    /// surfaced when all of its consumers are class-internal.
    /// </summary>
    private static bool HasOnlySameClassReferences(SemanticGraph graph, Symbol sym)
    {
        if (string.IsNullOrEmpty(sym.ParentId)) return false;
        bool anyRef = false;
        foreach (int idx in graph.GetIncomingEdgeIndexes(sym.Id))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind == EdgeKind.Contains) continue;
            anyRef = true;
            var sourceSym = graph.GetSymbol(edge.SourceId);
            if (sourceSym == null) return false; // unknown source — refuse the relaxation
            if (sourceSym.ParentId != sym.ParentId) return false;
        }
        return anyRef;
    }

    private static string BuildReason(Symbol sym, DeadCodeOptions options, int sameClassConsumers)
    {
        var scope = options.ExcludePublic ? "non-public " : "";
        var kindStr = sym.Kind.ToString().ToLowerInvariant();
        return sameClassConsumers > 0
            ? $"{scope}{kindStr} with only same-class consumers ({sameClassConsumers})"
            : $"{scope}{kindStr} with no incoming semantic references";
    }

    /// <summary>
    /// Count incoming non-Contains edges. Classic findings always carry 0
    /// (the analyzer drops any symbol with such edges in default mode);
    /// non-zero values appear only under F2's IncludeSameClassOnlyConsumers
    /// relaxation where the field surfaces the "barely reachable" class.
    /// INV-DEADCODE-TRIAGE-001.
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

    /// <summary>
    /// Count incoming non-Contains edges whose source symbol shares the
    /// candidate's ParentId (same containing type). Always 0 when the
    /// candidate has no ParentId or no such edges. INV-DEADCODE-TRIAGE-002.
    /// </summary>
    private static int CountSameClassConsumers(SemanticGraph graph, Symbol sym)
    {
        if (string.IsNullOrEmpty(sym.ParentId)) return 0;
        int count = 0;
        foreach (int idx in graph.GetIncomingEdgeIndexes(sym.Id))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind == EdgeKind.Contains) continue;
            var sourceSym = graph.GetSymbol(edge.SourceId);
            if (sourceSym == null) continue;
            if (sourceSym.ParentId == sym.ParentId) count++;
        }
        return count;
    }
}

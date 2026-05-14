using System;
using System.Linq;
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
/// 2026-04-11 (Phase 6 / B5) to close the R1 finding.
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
    /// Phase P3 (2026-04-26).
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
            if (options.ExcludeTests && LooksLikeTestFile(sym.FilePath)) continue;

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
                Bucket = ClassifyBucket(sym.FilePath),
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

    /// <summary>
    /// Path bucket. Production by default. Mirrors the
    /// <c>blast_radius groupBy=bucket</c> taxonomy
    /// (INV-BLAST-RADIUS-GROUP-001) so a caller can join the two tool
    /// surfaces. Classification is segment-aware (not substring): the
    /// normalized POSIX path is split on <c>/</c> and matched as
    /// whole segments, so a folder named <c>obj</c> at the project
    /// root classifies identically to a nested <c>/obj/</c>, and a
    /// filename containing the word "test" does not accidentally
    /// trigger the Test bucket.
    ///
    /// Precedence (most authoritative signal wins):
    ///   1. <see cref="DeadCodeBucket.Generated"/> — filename matches
    ///      <c>*.Generated.*</c>, or any path segment is
    ///      <c>generated</c> / <c>obj</c> / <c>bin</c>. Build artifacts
    ///      and codegen are never a refactor target regardless of any
    ///      other signal in the path.
    ///   2. <see cref="DeadCodeBucket.Test"/> — any path segment is
    ///      <c>tests</c>, or filename ends with <c>Tests.cs</c> /
    ///      <c>Test.cs</c>. Test beats Editor because a fixture under
    ///      <c>Tests/Editor/Foo.cs</c> is a test fixture (its Tests
    ///      root + filename convention define what it is); the
    ///      <c>Editor/</c> subfolder there is just NUnit PlayMode
    ///      assembly placement.
    ///   3. <see cref="DeadCodeBucket.Editor"/> — any path segment is
    ///      <c>editor</c>. Unity editor-only utility, excluded from
    ///      runtime builds.
    ///   4. <see cref="DeadCodeBucket.Production"/> — otherwise.
    ///
    /// Comparisons are case-insensitive on the path-separator-normalized
    /// form so Windows and POSIX inputs collapse to one match table.
    /// INV-DEADCODE-TRIAGE-001.
    /// </summary>
    internal static DeadCodeBucket ClassifyBucket(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return DeadCodeBucket.Production;
        var lower = filePath.Replace('\\', '/').ToLowerInvariant();
        var segments = lower.Split('/');

        if (lower.Contains(".generated.")
            || segments.Any(s => s == "generated" || s == "obj" || s == "bin"))
            return DeadCodeBucket.Generated;

        if (segments.Any(s => s == "tests")
            || lower.EndsWith("tests.cs", StringComparison.Ordinal)
            || lower.EndsWith("test.cs", StringComparison.Ordinal))
            return DeadCodeBucket.Test;

        if (segments.Any(s => s == "editor"))
            return DeadCodeBucket.Editor;

        return DeadCodeBucket.Production;
    }
}

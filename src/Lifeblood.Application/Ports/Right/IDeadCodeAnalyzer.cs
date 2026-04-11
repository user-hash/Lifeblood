using Lifeblood.Domain.Graph;

namespace Lifeblood.Application.Ports.Right;

/// <summary>
/// Right-side analyzer that identifies symbols with no incoming semantic
/// edges — the static definition of "dead code". A walk over
/// <c>graph.GetIncomingEdgeIndexes(sym.Id)</c> filtered to the relevant
/// edge kinds is the entire algorithm; everything else the caller cares
/// about (public-API boundary, reflection-reachable allow-list, test-
/// only filter) is configuration on the input.
///
/// Added 2026-04-11 (Phase 6) to close DAWG R1. Stateless per
/// INV-ANALYSIS-001, read-only per INV-GRAPH-004.
/// </summary>
public interface IDeadCodeAnalyzer
{
    DeadCodeResult[] FindDeadCode(SemanticGraph graph, DeadCodeOptions options);
}

/// <summary>
/// Configuration for <see cref="IDeadCodeAnalyzer.FindDeadCode"/>.
///
/// <paramref name="IncludeKinds"/> narrows the scan. Default is methods
/// + types + properties + fields — everything a developer would
/// meaningfully delete. Namespaces and modules are excluded because
/// a "dead namespace" with no members is structural noise.
///
/// <paramref name="ExcludePublic"/> skips symbols with public visibility
/// because public surface is assumed reachable from outside the graph
/// (external consumers, reflection, dynamic loading). Default true.
///
/// <paramref name="ExcludeTests"/> skips symbols whose containing file
/// sits under a path segment matching "tests", "Tests.", or "Test.cs".
/// Tests are "dead" from the production graph's perspective but not
/// actually dead. Default true.
/// </summary>
public sealed record DeadCodeOptions(
    SymbolKind[]? IncludeKinds = null,
    bool ExcludePublic = true,
    bool ExcludeTests = true);

/// <summary>
/// One dead-code finding. The canonical ID is suitable for feeding into
/// any other read-side tool. <see cref="Reason"/> documents WHY this
/// symbol was flagged, so the consumer can sanity-check before deleting.
/// </summary>
public sealed record DeadCodeResult(
    string CanonicalId,
    SymbolKind Kind,
    string Name,
    string FilePath,
    int Line,
    string Reason);

using Lifeblood.Domain.Graph;

namespace Lifeblood.Application.Ports.Right;

/// <summary>
/// Right side. Serves the semantic graph over MCP protocol to AI agents.
/// INV-CONN-002: Read-only. Does not modify the graph.
/// </summary>
public interface IMcpGraphProvider
{
    Symbol? LookupSymbol(SemanticGraph graph, string symbolId);
    string[] GetDependencies(SemanticGraph graph, string symbolId);
    string[] GetDependants(SemanticGraph graph, string symbolId);
    string[] GetBlastRadius(SemanticGraph graph, string symbolId, int maxDepth = 10);
    FileImpactResult GetFileImpact(SemanticGraph graph, string fileId);

    /// <summary>
    /// Outgoing edge detail for <paramref name="symbolId"/> — one entry per
    /// unique (sourceId, targetId, kind) edge in the graph, with the first
    /// observed source-location <see cref="CallSite"/> attached. Multiple
    /// authoring expressions from the same source method to the same target
    /// of the same kind (e.g. two <c>obj.Method()</c> calls on different
    /// lines) collapse to ONE entry — graph-level edge deduplication
    /// (<c>INV-STREAM-005</c>) is load-bearing and runs before this port
    /// sees the data, so the CallSite reflects the FIRST extracted
    /// occurrence, not every occurrence. CallSite is null for graph-derived
    /// edges with no single authoring location (module→module DependsOn,
    /// type→type Inherits without a surfaced clause node).
    /// INV-EDGE-CALLSITE-001.
    /// </summary>
    EdgeDetail[] GetDependencyEdges(SemanticGraph graph, string symbolId);

    /// <summary>
    /// Incoming edge detail for <paramref name="symbolId"/> — every edge whose
    /// target is the symbol, with edge kind and source-occurrence
    /// <see cref="CallSite"/> attached. Same shape contract as
    /// <see cref="GetDependencyEdges"/> but with <see cref="EdgeDetail.OtherEndId"/>
    /// pointing at the SOURCE rather than the target.
    /// </summary>
    EdgeDetail[] GetDependantEdges(SemanticGraph graph, string symbolId);

    /// <summary>
    /// Classify the blast-radius set of <paramref name="symbolId"/> into
    /// buckets and per-module groups so callers can triage by source kind
    /// (production / test / editor / generated) or by module/asmdef.
    /// A flat <c>affectedCount=221</c> is a warning; structured grouping
    /// makes it actionable. INV-BLAST-RADIUS-GROUP-001.
    ///
    /// Classification heuristics are path-based and match the conventions
    /// already used by <c>lifeblood_dead_code</c>:
    /// <list type="bullet">
    ///   <item><b>Test</b>: any path segment <c>/tests/</c>, or filename
    ///         ending in <c>Tests.cs</c> / <c>Test.cs</c>.</item>
    ///   <item><b>Editor</b>: any path segment <c>/Editor/</c> (Unity convention).</item>
    ///   <item><b>Generated</b>: path segments <c>/obj/</c>, <c>/Generated/</c>,
    ///         or files matching <c>*.Generated.cs</c> / <c>*.g.cs</c>.</item>
    ///   <item><b>Production</b>: everything else.</item>
    /// </list>
    /// Module assignment walks each affected symbol's Parent chain to its
    /// containing Module symbol.
    /// </summary>
    /// <param name="maxResults">Cap on preview entries per bucket / module. 0 = no preview.</param>
    BlastRadiusGroups ClassifyBlastRadius(
        SemanticGraph graph, string symbolId, int maxDepth = 10, int maxResults = 10);

    /// <summary>
    /// Filter and/or group a one-hop edge list (the result of
    /// <see cref="GetDependantEdges"/> or <see cref="GetDependencyEdges"/>) by
    /// path bucket and module so a large caller list becomes triage-ready
    /// without the caller hand-classifying every endpoint. Filters
    /// (<see cref="EdgeGroupOptions.ExcludeTests"/> /
    /// <see cref="EdgeGroupOptions.ExcludeGenerated"/> /
    /// <see cref="EdgeGroupOptions.IncludeBuckets"/>) reduce the returned flat
    /// <see cref="EdgeGroupResult.Edges"/>; grouping populates
    /// <see cref="EdgeGroupResult.ByBucket"/> / <see cref="EdgeGroupResult.ByModule"/>
    /// over the surviving edges. Bucket + module classification reuse the exact
    /// same heuristics as <see cref="ClassifyBlastRadius"/> (one SSoT,
    /// <c>PathBucketClassifier</c> + Parent-chain module walk). The endpoint
    /// classified is the OTHER end of each edge (<see cref="EdgeDetail.OtherEndId"/>).
    /// INV-EDGE-GROUP-001.
    /// </summary>
    EdgeGroupResult ClassifyEdges(
        SemanticGraph graph, IReadOnlyList<EdgeDetail> edges, EdgeGroupOptions options);
}

/// <summary>
/// Filter + grouping request for <see cref="IMcpGraphProvider.ClassifyEdges"/>.
/// All defaults are no-op so a caller that sets nothing gets the unfiltered,
/// ungrouped edge list back (the legacy flat shape). INV-EDGE-GROUP-001.
/// </summary>
public sealed class EdgeGroupOptions
{
    /// <summary>Drop edges whose endpoint classifies to the <c>Test</c> bucket.</summary>
    public bool ExcludeTests { get; init; }

    /// <summary>Drop edges whose endpoint classifies to the <c>Generated</c> bucket.</summary>
    public bool ExcludeGenerated { get; init; }

    /// <summary>
    /// When non-empty, keep only edges whose endpoint bucket is in this set
    /// (case-insensitive). Null / empty = no bucket allowlist.
    /// </summary>
    public IReadOnlyList<string>? IncludeBuckets { get; init; }

    /// <summary>Populate <see cref="EdgeGroupResult.ByBucket"/>.</summary>
    public bool GroupByBucket { get; init; }

    /// <summary>Populate <see cref="EdgeGroupResult.ByModule"/>.</summary>
    public bool GroupByModule { get; init; }

    /// <summary>Cap on preview endpoint-ids per group. 0 = counts only.</summary>
    public int PreviewPerGroup { get; init; } = 5;
}

/// <summary>
/// Result of <see cref="IMcpGraphProvider.ClassifyEdges"/>. <see cref="Edges"/>
/// is the surviving flat list after filtering (full <see cref="EdgeDetail"/>
/// fidelity preserved); the grouped maps are null unless the matching
/// <c>GroupBy*</c> option was set. INV-EDGE-GROUP-001.
/// </summary>
public sealed class EdgeGroupResult
{
    /// <summary>Edges that survived the filters, in input order.</summary>
    public required EdgeDetail[] Edges { get; init; }

    /// <summary>Edge count before any filter was applied.</summary>
    public required int TotalBeforeFilter { get; init; }

    /// <summary>Bucket name → grouped surviving endpoints (null unless grouped by bucket).</summary>
    public IReadOnlyDictionary<string, GroupedBucket>? ByBucket { get; init; }

    /// <summary>Module name → grouped surviving endpoints (null unless grouped by module).</summary>
    public IReadOnlyDictionary<string, GroupedBucket>? ByModule { get; init; }
}

/// <summary>
/// Grouped view of a blast-radius result. Buckets classify by path
/// convention (Test / Editor / Generated / Production); module map groups
/// by containing assembly/asmdef. Counts are always populated; previews
/// are capped by the caller's <c>maxResults</c> argument.
/// INV-BLAST-RADIUS-GROUP-001.
/// </summary>
public sealed class BlastRadiusGroups
{
    /// <summary>Transitive affected count (sum across all buckets / modules).</summary>
    public required int TotalAffected { get; init; }

    /// <summary>One-hop incoming non-Contains edge count.</summary>
    public required int DirectDependants { get; init; }

    /// <summary>
    /// Bucket name (Production / Test / Editor / Generated) → grouped entries.
    /// </summary>
    public IReadOnlyDictionary<string, GroupedBucket> ByBucket { get; init; }
        = new Dictionary<string, GroupedBucket>();

    /// <summary>
    /// Module name → grouped entries. Symbols whose containing module
    /// cannot be resolved land under the synthetic key <c>"(unknown)"</c>.
    /// </summary>
    public IReadOnlyDictionary<string, GroupedBucket> ByModule { get; init; }
        = new Dictionary<string, GroupedBucket>();
}

/// <summary>One bucket / module group in a <see cref="BlastRadiusGroups"/>.</summary>
public sealed class GroupedBucket
{
    public required int Count { get; init; }
    public string[] Preview { get; init; } = System.Array.Empty<string>();
}

/// <summary>
/// One edge in a dependency / dependant query response. Carries the canonical
/// id of the OTHER endpoint (target for dependency queries, source for
/// dependant queries), the edge kind, and the optional source-occurrence
/// <see cref="CallSite"/>. INV-EDGE-CALLSITE-001.
/// </summary>
public sealed class EdgeDetail
{
    /// <summary>Canonical id of the other endpoint (target or source depending on query direction).</summary>
    public required string OtherEndId { get; init; }
    public required EdgeKind Kind { get; init; }
    public CallSite? CallSite { get; init; }
    /// <summary>INV-MULTI-DEFINE-EDGE-PROFILES-001.</summary>
    public IReadOnlyList<string>? Profiles { get; init; }
}

/// <summary>
/// Result of a file-level impact query. Shows which files depend on and are depended on by a given file.
/// </summary>
public sealed class FileImpactResult
{
    public required string FileId { get; init; }
    public required string FilePath { get; init; }
    public required FileEdge[] DependsOn { get; init; }
    public required FileEdge[] DependedOnBy { get; init; }
}

public sealed class FileEdge
{
    public required string FileId { get; init; }
    public required string FilePath { get; init; }
    public required int EdgeCount { get; init; }
}

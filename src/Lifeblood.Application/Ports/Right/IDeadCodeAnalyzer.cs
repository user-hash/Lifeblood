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
/// Stateless per INV-ANALYSIS-001, read-only per INV-GRAPH-004.
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
/// Path-bucket classification for a dead-code finding. Derived from
/// <see cref="DeadCodeResult.FilePath"/> using the same segment-aware
/// rules as <c>blast_radius groupBy=bucket</c>. Lets consumers triage
/// findings by the part of the codebase they live in without re-parsing
/// paths. INV-DEADCODE-TRIAGE-001.
///
/// Classification is segment-aware (not substring): the normalized
/// POSIX path is split on <c>/</c> and matched as whole segments, so
/// a folder named <c>obj</c> at the project root classifies identically
/// to a nested <c>/obj/</c>, and a filename containing the word "test"
/// does not accidentally trigger the Test bucket.
///
/// Precedence (most authoritative signal wins): Generated → Test →
/// Editor → Production. Integer values mirror
/// <c>Lifeblood.Domain.PathClassification.PathBucket</c> — the canonical
/// classifier — so the analyzer casts the result directly. Drift caught
/// by <c>PathBucketParityTests</c>.
/// </summary>
public enum DeadCodeBucket
{
    /// <summary>Default. The finding is in normal production code.</summary>
    Production = 0,

    /// <summary>
    /// File path has a <c>tests</c> path segment, or filename ends with
    /// <c>Tests.cs</c> / <c>Test.cs</c>. Findings in this bucket are
    /// usually safe to delete only after confirming the test fixture
    /// is no longer referenced by any CI gate. Test beats Editor in
    /// precedence: a fixture under <c>Tests/Editor/Foo.cs</c> is a
    /// test fixture (defined by its Tests root + filename convention),
    /// not an Editor utility.
    /// </summary>
    Test = 1,

    /// <summary>
    /// File path has an <c>editor</c> path segment. Common in Unity
    /// workspaces where editor-only utilities are excluded from runtime
    /// builds; static analysis often flags them dead because the
    /// reachability root is the Editor process, not a Program.Main.
    /// </summary>
    Editor = 2,

    /// <summary>
    /// Filename matches <c>*.Generated.*</c>, or any path segment is
    /// <c>generated</c> / <c>obj</c> / <c>bin</c> (typical build-output
    /// or codegen roots). Generated code routinely flags as dead
    /// because the producing tool emits more than the consumer uses;
    /// almost never a real refactor target. Highest-precedence bucket —
    /// wins over every other signal.
    /// </summary>
    Generated = 3,
}

/// <summary>
/// One dead-code finding. The canonical ID is suitable for feeding into
/// any other read-side tool. <see cref="Reason"/> documents WHY this
/// symbol was flagged, so the consumer can sanity-check before deleting.
///
/// Triage fields (INV-DEADCODE-TRIAGE-001):
///   <see cref="DirectDependants"/> — incoming non-Contains edge count;
///     <c>0</c> for every classic finding (the analyzer filters them out
///     otherwise), but kept on the wire as forward-compatible signal for
///     future relaxed criteria (reachable-only-via-Implements,
///     reachable-only-via-Override, etc.) where the count would surface
///     the "barely reachable" class.
///   <see cref="Bucket"/> — path-prefix classification; lets a caller
///     filter to Production-only or fold the giant Editor/Generated tail
///     in one pass instead of re-parsing the path string.
///   <see cref="DeclarationOnly"/> — true iff the underlying symbol is
///     abstract (interface method, abstract method, abstract type,
///     abstract property). Deleting one of these is a public-contract
///     change that breaks every implementor; the flag stops a consumer
///     from treating it as a normal-method delete.
/// </summary>
public sealed record DeadCodeResult(
    string CanonicalId,
    SymbolKind Kind,
    string Name,
    string FilePath,
    int Line,
    string Reason)
{
    /// <summary>
    /// Incoming-edge count (excluding the structural <c>Contains</c>
    /// link). Classic findings always carry <c>0</c> because the
    /// analyzer drops any symbol with non-Contains incoming edges;
    /// non-zero values appear only under future relaxed criteria.
    /// </summary>
    public int DirectDependants { get; init; }

    /// <summary>
    /// Path-prefix bucket. <see cref="DeadCodeBucket.Production"/> by
    /// default. Aligns with the <c>blast_radius groupBy=bucket</c>
    /// taxonomy (INV-BLAST-RADIUS-GROUP-001).
    /// </summary>
    public DeadCodeBucket Bucket { get; init; }

    /// <summary>
    /// True iff the symbol is abstract — interface method, abstract
    /// method, abstract type, abstract property accessor. Such symbols
    /// are part of a public contract; deleting one is a breaking
    /// change for every implementor and should not be treated as a
    /// routine dead-code cleanup.
    /// </summary>
    public bool DeclarationOnly { get; init; }
}

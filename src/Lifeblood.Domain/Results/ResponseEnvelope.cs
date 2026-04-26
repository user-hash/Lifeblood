namespace Lifeblood.Domain.Results;

/// <summary>
/// Truth-envelope metadata attached to every read-side MCP tool response.
/// The envelope tells callers HOW MUCH they should trust a result and
/// WHEN it was true: the underlying analysis tier, a confidence band,
/// the wall-clock staleness vs the loaded workspace, the evidence source
/// (semantic / inferred / heuristic), and any documented limitations
/// for that specific tool's output. Pure data — INV-DOMAIN-001 keeps
/// the record dependency-free; staleness is computed at decoration time
/// from the loaded graph and the file system, not from a clock baked
/// into Domain.
///
/// Closes LB-INBOX-001 (truth envelope) and LB-OBS-004 (staleness
/// surface) in one shape. See INV-ENVELOPE-001 in CLAUDE.md.
/// </summary>
public sealed class ResponseEnvelope
{
    /// <summary>
    /// What kind of analysis produced the result. Drives caller decisions
    /// about how aggressively to act on the result.
    /// </summary>
    public TruthTier TruthTier { get; init; } = TruthTier.Semantic;

    /// <summary>
    /// Confidence band for the specific result. Derived from
    /// <see cref="TruthTier"/> + the tool-specific limitations the
    /// adapter knows about. Tools may downgrade this from the tier
    /// default when they hit a known fall-back path.
    /// </summary>
    public ConfidenceBand Confidence { get; init; } = ConfidenceBand.Proven;

    /// <summary>
    /// Where the underlying evidence came from. Mirrors
    /// <see cref="Lifeblood.Domain.Capabilities.EvidenceKind"/> values
    /// so envelope and edge evidence speak the same vocabulary.
    /// </summary>
    public string EvidenceSource { get; init; } = "Semantic";

    /// <summary>
    /// Wall-clock seconds between the loaded graph's analyze time and
    /// "now" at the moment the response was decorated. Zero when no
    /// staleness can be computed (no graph, no analyze timestamp).
    /// </summary>
    public long StalenessSeconds { get; init; }

    /// <summary>
    /// Number of tracked source files whose mtime is newer than the
    /// graph's analyze time. A non-zero value means the graph is
    /// out-of-date with respect to disk; callers should consider
    /// re-running <c>lifeblood_analyze</c> with <c>incremental:true</c>.
    /// </summary>
    public int FilesChangedSinceAnalyze { get; init; }

    /// <summary>
    /// Tool-specific limitations the caller should weigh against the
    /// result. Examples: "Unity reflection-based dispatch is not
    /// reachable from static analysis"; "result truncated at maxResults".
    /// Empty when the tool ran in its full-precision happy path.
    /// </summary>
    public string[] Limitations { get; init; } = System.Array.Empty<string>();
}

/// <summary>
/// Categorical analysis tier the result was produced at. Coarse on
/// purpose: callers do not need a fine-grained story, they need a
/// triage bucket.
/// </summary>
public enum TruthTier
{
    /// <summary>
    /// The result comes from a real semantic compilation: Roslyn /
    /// language-adapter SymbolInfo, type binding, full
    /// resolved-symbol surface. Highest fidelity.
    /// </summary>
    Semantic,

    /// <summary>
    /// The result is derived from the semantic graph by walking edges
    /// (e.g. blast radius BFS, dependants/dependencies aggregations,
    /// file-impact rollups). Edges themselves are semantic, but the
    /// rollup is one step removed.
    /// </summary>
    Derived,

    /// <summary>
    /// The result comes from a heuristic that is not load-bearing on
    /// type-binding alone — fuzzy short-name suggestions, dead-code
    /// reachability with documented FP classes, etc. Treat as a hint,
    /// not as ground truth.
    /// </summary>
    Heuristic,

    /// <summary>
    /// Inferred from secondary signals (file-level edges aggregated
    /// from symbol-level edges, file-impact heuristics that cross
    /// language boundaries). Lower than <see cref="Derived"/> because
    /// the inference is from data Lifeblood synthesized rather than
    /// extracted directly.
    /// </summary>
    Inferred,
}

/// <summary>
/// Per-tool truth classification. Owned by each tool's registration in
/// the MCP tool registry — the envelope decorator reads it directly so
/// tier / confidence / evidence / limitations cannot drift between
/// registry and classifier. Pure data; INV-DOMAIN-001 keeps the record
/// dependency-free.
/// </summary>
public sealed class EnvelopeClassification
{
    public required TruthTier TruthTier { get; init; }
    public required ConfidenceBand Confidence { get; init; }
    public string EvidenceSource { get; init; } = "Semantic";
    public string[] Limitations { get; init; } = System.Array.Empty<string>();
}

/// <summary>
/// How much weight the caller should put behind a single result.
/// Three bands: Proven (deterministic graph hit), Advisory (correct
/// most of the time but with documented FP classes), Speculative
/// (best-effort suggestion). Mirrors
/// <see cref="Lifeblood.Domain.Capabilities.ConfidenceLevel"/>'s
/// semantics in envelope-shaped form.
/// </summary>
public enum ConfidenceBand
{
    /// <summary>The result is a direct graph or compilation lookup.</summary>
    Proven,
    /// <summary>The result is correct in the common case but the tool documents specific false-positive or false-negative classes.</summary>
    Advisory,
    /// <summary>The result is a ranked suggestion, not a definitive answer (fuzzy resolver near-matches, etc.).</summary>
    Speculative,
}

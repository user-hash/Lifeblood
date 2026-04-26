using Lifeblood.Application.Ports.Right;
using Lifeblood.Domain.Results;

namespace Lifeblood.Connectors.Mcp;

/// <summary>
/// Reference implementation of <see cref="IResponseDecorator"/>. Owns
/// the per-tool classification table and the staleness scan. Pure with
/// respect to call ordering — same input produces the same envelope.
///
/// The classification table is the single source of truth for what
/// truth-tier and confidence band each MCP tool ships at. Adding a new
/// read-side tool is a one-line entry here; missing entries fall through
/// to the conservative default (<see cref="TruthTier.Heuristic"/> /
/// <see cref="ConfidenceBand.Speculative"/>) so a missed registration
/// surfaces as an obviously-degraded envelope rather than silent
/// over-confidence.
///
/// See INV-ENVELOPE-001 in CLAUDE.md.
/// </summary>
public sealed class LifebloodResponseDecorator : IResponseDecorator
{
    /// <summary>
    /// Classification of every read-side tool currently shipped by
    /// Lifeblood.Server.Mcp. Pinned by <c>ResponseEnvelopeTests</c>
    /// against <c>ToolRegistry</c> so a new read-side tool added
    /// without an entry here fails the ratchet.
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<string, ResponseClassification>
        Classifications = new(System.StringComparer.Ordinal)
    {
        // Workspace load — the analyze response itself.
        ["lifeblood_analyze"]             = new(TruthTier.Semantic, ConfidenceBand.Proven, "Semantic"),

        // Direct graph / compilation lookups.
        ["lifeblood_lookup"]              = new(TruthTier.Semantic, ConfidenceBand.Proven, "Semantic"),
        ["lifeblood_dependants"]          = new(TruthTier.Semantic, ConfidenceBand.Proven, "Semantic"),
        ["lifeblood_dependencies"]        = new(TruthTier.Semantic, ConfidenceBand.Proven, "Semantic"),
        ["lifeblood_resolve_short_name"]  = new(TruthTier.Semantic, ConfidenceBand.Proven, "Semantic"),
        ["lifeblood_documentation"]       = new(TruthTier.Semantic, ConfidenceBand.Proven, "Semantic"),
        ["lifeblood_partial_view"]        = new(TruthTier.Semantic, ConfidenceBand.Proven, "Semantic"),
        ["lifeblood_invariant_check"]     = new(TruthTier.Semantic, ConfidenceBand.Proven, "Semantic"),
        ["lifeblood_context"]             = new(TruthTier.Semantic, ConfidenceBand.Proven, "Semantic"),

        // Graph rollups — semantic edges, derived aggregations.
        ["lifeblood_blast_radius"]        = new(TruthTier.Derived,  ConfidenceBand.Proven, "Semantic"),
        ["lifeblood_file_impact"]         = new(TruthTier.Derived,  ConfidenceBand.Proven, "Inferred"),

        // Heuristic / advisory.
        ["lifeblood_search"]              = new(TruthTier.Heuristic, ConfidenceBand.Advisory,    "Heuristic"),
        ["lifeblood_dead_code"]           = new(TruthTier.Heuristic, ConfidenceBand.Advisory,    "Heuristic",
            new[] {
                "Dead-code is reachability-only — runtime entry points (Program.Main), Unity reflection-based dispatch ([RuntimeInitializeOnLoadMethod], MonoBehaviour magic methods, UnityEvent YAML bindings) are not visible to static analysis and may surface as false positives.",
            }),
    };

    /// <summary>
    /// Per-tool classification entry. Compact record so the table above
    /// reads cleanly. <see cref="Limitations"/> is the documented
    /// known-FP / known-FN class for the tool; copied into the envelope
    /// verbatim.
    /// </summary>
    private sealed record ResponseClassification(
        TruthTier TruthTier,
        ConfidenceBand Confidence,
        string EvidenceSource,
        string[]? Limitations = null);

    public ResponseEnvelope Decorate(string toolName, EnvelopeContext context)
    {
        if (!Classifications.TryGetValue(toolName, out var cls))
        {
            // Unknown tool — surface the degradation in the envelope
            // rather than throw. Caller still gets a usable response
            // with maximally-conservative metadata so an audit can
            // spot the missing registration.
            return new ResponseEnvelope
            {
                TruthTier = TruthTier.Heuristic,
                Confidence = ConfidenceBand.Speculative,
                EvidenceSource = "Unknown",
                StalenessSeconds = 0,
                FilesChangedSinceAnalyze = 0,
                Limitations = new[] {
                    $"Unregistered tool '{toolName}' — envelope downgraded to the most conservative classification. Add an entry to LifebloodResponseDecorator.Classifications.",
                },
            };
        }

        var (stalenessSeconds, filesChanged) = ComputeStaleness(context);

        return new ResponseEnvelope
        {
            TruthTier = cls.TruthTier,
            Confidence = cls.Confidence,
            EvidenceSource = cls.EvidenceSource,
            StalenessSeconds = stalenessSeconds,
            FilesChangedSinceAnalyze = filesChanged,
            Limitations = cls.Limitations ?? System.Array.Empty<string>(),
        };
    }

    /// <summary>
    /// Compute staleness from the supplied context. Returns (0, 0) when
    /// no analyze timestamp is available — JSON-graph sessions and
    /// pre-analyze invocations both legitimately lack timestamp state.
    /// </summary>
    private static (long stalenessSeconds, int filesChanged) ComputeStaleness(EnvelopeContext ctx)
    {
        if (ctx.AnalyzedAtUtc is not System.DateTime analyzedAt)
            return (0, 0);

        var nowUtc = System.DateTime.UtcNow;
        var seconds = (long)System.Math.Max(0, (nowUtc - analyzedAt).TotalSeconds);

        if (ctx.FileSystem == null || ctx.TrackedFilePaths.Length == 0)
            return (seconds, 0);

        int changed = 0;
        int scanned = 0;
        foreach (var path in ctx.TrackedFilePaths)
        {
            scanned++;
            if (string.IsNullOrEmpty(path)) continue;
            try
            {
                var mtime = ctx.FileSystem.GetLastWriteTimeUtc(path);
                if (mtime > analyzedAt) changed++;
            }
            catch
            {
                // File missing / inaccessible — not stale, just gone.
                // Do not throw out of envelope decoration.
            }
            // Honor the scan cap once we've found at least one drift signal,
            // so the response can report "yes, something changed" even when
            // the workspace is huge and we don't want to stat all of it.
            if (scanned >= ctx.FileScanLimit && changed > 0) break;
        }
        return (seconds, changed);
    }
}

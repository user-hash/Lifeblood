using Lifeblood.Application.Ports.Right;
using Lifeblood.Domain.Results;

namespace Lifeblood.Connectors.Mcp;

/// <summary>
/// Reference implementation of <see cref="IResponseDecorator"/>. Pure
/// with respect to call ordering — same input produces the same envelope.
///
/// The decorator does NOT own the per-tool classification table. The
/// table is injected at construction time, sourced from whatever
/// registry the host owns (in the Lifeblood MCP server it is
/// <c>ToolRegistry.GetDefinitions()</c>; tests pass an inline map). This
/// keeps the adapter project-independent: the same decorator drives any
/// hexagonal MCP server whose registry can hand over a
/// <see cref="EnvelopeClassification"/> per tool name.
///
/// Missing entries fall through to the conservative default
/// (<see cref="TruthTier.Heuristic"/> / <see cref="ConfidenceBand.Speculative"/>)
/// so a missed registration surfaces as an obviously-degraded envelope
/// rather than silent over-confidence.
///
/// See INV-ENVELOPE-001 in CLAUDE.md.
/// </summary>
public sealed class LifebloodResponseDecorator : IResponseDecorator
{
    private readonly System.Collections.Generic.IReadOnlyDictionary<string, EnvelopeClassification> _classifications;
    private readonly StalenessPolicy _stalenessPolicy;

    /// <summary>
    /// Construct the decorator with an explicit classification lookup
    /// and an optional <see cref="StalenessPolicy"/>. The composition
    /// root passes whatever its tool registry provides; tests and
    /// contract checks can pass an inline dictionary. Defaults to
    /// <see cref="StalenessPolicy.Default"/> when no policy is
    /// supplied — INV-ANALYZE-SKIPPED-PROMINENCE-001.
    /// </summary>
    public LifebloodResponseDecorator(
        System.Collections.Generic.IReadOnlyDictionary<string, EnvelopeClassification> classifications,
        StalenessPolicy? stalenessPolicy = null)
    {
        _classifications = classifications ?? throw new System.ArgumentNullException(nameof(classifications));
        _stalenessPolicy = stalenessPolicy ?? StalenessPolicy.Default;
    }

    /// <summary>
    /// Parameterless ctor for tests and call sites that just want
    /// envelope plumbing without per-tool tiers wired up. Every tool
    /// resolves to the conservative default in this mode, which is
    /// honest behavior for "I don't know what tool this is."
    /// </summary>
    public LifebloodResponseDecorator()
        : this(new System.Collections.Generic.Dictionary<string, EnvelopeClassification>(System.StringComparer.Ordinal))
    {
    }

    public ResponseEnvelope Decorate(string toolName, EnvelopeContext context)
    {
        // Staleness is computed regardless of tool registration — the
        // timestamp + filesystem signal is independent of per-tool
        // classification, and a caller of an unregistered tool still
        // benefits from knowing how stale the workspace is.
        var (stalenessSeconds, filesChanged) = ComputeStaleness(context);

        if (!_classifications.TryGetValue(toolName, out var cls))
        {
            // Unknown tool — surface the degradation in the envelope
            // rather than throw. Caller still gets a usable response
            // with maximally-conservative metadata so an audit can
            // spot the missing registration.
            var unregisteredLimits = AppendStalenessLimitations(
                new[]
                {
                    $"Unregistered tool '{toolName}' — envelope downgraded to the most conservative classification. The host's tool registry has no EnvelopeClassification for this name.",
                },
                stalenessSeconds,
                filesChanged);
            return new ResponseEnvelope
            {
                TruthTier = TruthTier.Heuristic,
                Confidence = ConfidenceBand.Speculative,
                EvidenceSource = "Unknown",
                StalenessSeconds = stalenessSeconds,
                FilesChangedSinceAnalyze = filesChanged,
                Limitations = unregisteredLimits,
            };
        }

        return new ResponseEnvelope
        {
            TruthTier = cls.TruthTier,
            Confidence = cls.Confidence,
            EvidenceSource = cls.EvidenceSource,
            StalenessSeconds = stalenessSeconds,
            FilesChangedSinceAnalyze = filesChanged,
            Limitations = AppendStalenessLimitations(cls.Limitations, stalenessSeconds, filesChanged),
        };
    }

    /// <summary>
    /// Promote the staleness signal into one or two
    /// <see cref="ResponseEnvelope.Limitations"/> entries when it
    /// exceeds the configured <see cref="StalenessPolicy"/> thresholds.
    /// Static tool-classification limitations come first; staleness
    /// limitations are appended so a consumer reading from index 0
    /// still sees the per-tool caveats. Returns the original array
    /// when nothing is above threshold so existing tests of the
    /// non-stale path stay byte-stable.
    /// INV-ANALYZE-SKIPPED-PROMINENCE-001.
    /// </summary>
    private string[] AppendStalenessLimitations(string[] baseLimitations, long stalenessSeconds, int filesChanged)
    {
        bool stalenessAboveThreshold = stalenessSeconds >= _stalenessPolicy.StalenessSecondsWarnThreshold;
        bool filesChangedAboveThreshold = filesChanged >= _stalenessPolicy.FilesChangedWarnThreshold;
        if (!stalenessAboveThreshold && !filesChangedAboveThreshold)
            return baseLimitations;

        var merged = new System.Collections.Generic.List<string>(baseLimitations.Length + 2);
        merged.AddRange(baseLimitations);
        if (stalenessAboveThreshold)
        {
            merged.Add(
                $"Workspace graph is {stalenessSeconds} s stale (threshold {_stalenessPolicy.StalenessSecondsWarnThreshold} s) — re-run analyze before acting on results that depend on current-source truth.");
        }
        if (filesChangedAboveThreshold)
        {
            merged.Add(
                $"{filesChanged} tracked file(s) changed since analyze (threshold {_stalenessPolicy.FilesChangedWarnThreshold}) — re-run analyze for accurate edge/symbol coverage.");
        }
        return merged.ToArray();
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

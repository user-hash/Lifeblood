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

    /// <summary>
    /// Construct the decorator with an explicit classification lookup.
    /// The composition root passes whatever its tool registry provides;
    /// tests and contract checks can pass an inline dictionary.
    /// </summary>
    public LifebloodResponseDecorator(
        System.Collections.Generic.IReadOnlyDictionary<string, EnvelopeClassification> classifications)
    {
        _classifications = classifications ?? throw new System.ArgumentNullException(nameof(classifications));
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
            return new ResponseEnvelope
            {
                TruthTier = TruthTier.Heuristic,
                Confidence = ConfidenceBand.Speculative,
                EvidenceSource = "Unknown",
                StalenessSeconds = stalenessSeconds,
                FilesChangedSinceAnalyze = filesChanged,
                Limitations = new[]
                {
                    $"Unregistered tool '{toolName}' — envelope downgraded to the most conservative classification. The host's tool registry has no EnvelopeClassification for this name.",
                },
            };
        }

        return new ResponseEnvelope
        {
            TruthTier = cls.TruthTier,
            Confidence = cls.Confidence,
            EvidenceSource = cls.EvidenceSource,
            StalenessSeconds = stalenessSeconds,
            FilesChangedSinceAnalyze = filesChanged,
            Limitations = cls.Limitations,
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

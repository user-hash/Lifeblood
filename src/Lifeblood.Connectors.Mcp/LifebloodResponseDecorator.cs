using Lifeblood.Application.Ports.Right;
using Lifeblood.Domain.Capabilities;
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
            return ApplyAdapterCapability(new ResponseEnvelope
            {
                TruthTier = TruthTier.Heuristic,
                Confidence = ConfidenceBand.Speculative,
                EvidenceSource = "Unknown",
                StalenessSeconds = stalenessSeconds,
                FilesChangedSinceAnalyze = filesChanged,
                Limitations = unregisteredLimits,
                AnalysisGeneration = context.AnalysisGeneration,
            }, context.AdapterCapability);
        }

        return ApplyAdapterCapability(new ResponseEnvelope
        {
            TruthTier = cls.TruthTier,
            Confidence = cls.Confidence,
            EvidenceSource = cls.EvidenceSource,
            StalenessSeconds = stalenessSeconds,
            FilesChangedSinceAnalyze = filesChanged,
            Limitations = AppendStalenessLimitations(cls.Limitations, stalenessSeconds, filesChanged),
            AnalysisGeneration = context.AnalysisGeneration,
        }, context.AdapterCapability);
    }

    /// <summary>
    /// Apply loaded-adapter capability to the per-tool classification.
    /// Roslyn's fully-proven capability leaves envelopes unchanged. Other
    /// adapters keep the tool's base tier but carry adapter identity and
    /// declared limits on the wire, and best-effort symbol/call extraction
    /// downgrades Proven confidence to Advisory.
    /// INV-NATIVE-CLANG-ENVELOPE-001.
    /// </summary>
    private static ResponseEnvelope ApplyAdapterCapability(
        ResponseEnvelope envelope,
        AdapterCapability? capability)
    {
        if (capability == null || IsFullyProvenRoslyn(capability))
            return envelope;

        var limitations = new System.Collections.Generic.List<string>(envelope.Limitations);
        limitations.Add(AdapterCapabilityLimitation(capability));

        var confidence = envelope.Confidence;
        if (confidence == ConfidenceBand.Proven && HasBestEffortCoreFacts(capability))
            confidence = ConfidenceBand.Advisory;

        return new ResponseEnvelope
        {
            TruthTier = envelope.TruthTier,
            Confidence = confidence,
            EvidenceSource = envelope.EvidenceSource,
            StalenessSeconds = envelope.StalenessSeconds,
            FilesChangedSinceAnalyze = envelope.FilesChangedSinceAnalyze,
            Limitations = limitations.ToArray(),
            AnalysisGeneration = envelope.AnalysisGeneration,
        };
    }

    private static bool IsFullyProvenRoslyn(AdapterCapability capability)
        => string.Equals(capability.AdapterName, "Roslyn", System.StringComparison.OrdinalIgnoreCase)
           && string.Equals(capability.Language, "csharp", System.StringComparison.OrdinalIgnoreCase)
           && capability.CanDiscoverSymbols
           && capability.TypeResolution == ConfidenceLevel.Proven
           && capability.CallResolution == ConfidenceLevel.Proven
           && capability.ImplementationResolution == ConfidenceLevel.Proven
           && capability.CrossModuleReferences == ConfidenceLevel.Proven
           && capability.OverrideResolution == ConfidenceLevel.Proven;

    private static bool HasBestEffortCoreFacts(AdapterCapability capability)
        => !capability.CanDiscoverSymbols
           || capability.TypeResolution != ConfidenceLevel.Proven
           || capability.CallResolution != ConfidenceLevel.Proven;

    private static string AdapterCapabilityLimitation(AdapterCapability capability)
    {
        var adapter = string.IsNullOrWhiteSpace(capability.AdapterName)
            ? "unknown adapter"
            : capability.AdapterName;
        var language = string.IsNullOrWhiteSpace(capability.Language)
            ? "unknown"
            : capability.Language;

        return
            $"Loaded graph was produced by {adapter} for language '{language}' (version '{capability.AdapterVersion}'). Adapter capability bounds this response: discoverSymbols={capability.CanDiscoverSymbols}, typeResolution={capability.TypeResolution}, callResolution={capability.CallResolution}, implementationResolution={capability.ImplementationResolution}, crossModuleReferences={capability.CrossModuleReferences}, overrideResolution={capability.OverrideResolution}. Treat results as proven only within the emitted graph's build profile and extraction scope.";
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
            // Honor the scan cap unconditionally — the cap exists to
            // bound per-call cost on large workspaces, and bounding only
            // when drift is already found leaves the no-drift case
            // paying the full scan cost (silently violating the port
            // contract). When the cap is hit, the response reports the
            // count observed within the scanned subset; callers who
            // need exhaustive drift detection pass FileScanLimit =
            // int.MaxValue (the default).
            if (scanned >= ctx.FileScanLimit) break;
        }
        return (seconds, changed);
    }
}

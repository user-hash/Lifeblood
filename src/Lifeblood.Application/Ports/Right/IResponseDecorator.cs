using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Domain.Results;

namespace Lifeblood.Application.Ports.Right;

/// <summary>
/// Builds the <see cref="ResponseEnvelope"/> attached to every read-side
/// MCP tool response. Stateless: every input flows in via parameters so
/// the decorator can be unit-tested without setting up a session. The
/// adapter owns the per-tool classification (truth tier, confidence,
/// evidence source) and computes wall-clock staleness against the
/// supplied context.
///
/// Closes LB-INBOX-001 + LB-OBS-004. See INV-ENVELOPE-001 in CLAUDE.md.
/// </summary>
public interface IResponseDecorator
{
    /// <summary>
    /// Build the envelope for one tool invocation.
    /// </summary>
    /// <param name="toolName">The MCP tool name (e.g. <c>lifeblood_lookup</c>).
    /// Unknown / unregistered tools fall back to the most conservative
    /// classification (<see cref="TruthTier.Heuristic"/> /
    /// <see cref="ConfidenceBand.Speculative"/>) so a missed registration
    /// surfaces as an obviously-degraded envelope rather than silent
    /// over-confidence.</param>
    /// <param name="context">Session state needed for staleness math.
    /// May be empty when no workspace is loaded; the decorator returns
    /// zero staleness and an empty file-changed count in that case.</param>
    ResponseEnvelope Decorate(string toolName, EnvelopeContext context);
}

/// <summary>
/// Session-level snapshot the decorator reads to compute staleness.
/// All fields are optional; the decorator degrades gracefully when a
/// field is null/empty so write-side tools and JSON-graph imports
/// (which have no on-disk source files to scan) still get an envelope.
/// </summary>
public sealed class EnvelopeContext
{
    /// <summary>
    /// UTC moment at which the loaded graph's analyze step finished.
    /// Null when no graph is loaded or the import path doesn't track a
    /// timestamp (JSON graph imports). When null, the decorator emits
    /// zero staleness fields.
    /// </summary>
    public System.DateTime? AnalyzedAtUtc { get; init; }

    /// <summary>
    /// Source files Lifeblood considers "tracked" for the loaded
    /// workspace. The decorator walks this list, calls
    /// <see cref="IFileSystem.GetLastWriteTimeUtc"/> on each, and counts
    /// how many are newer than <see cref="AnalyzedAtUtc"/>. Empty when
    /// no workspace is loaded.
    /// </summary>
    public string[] TrackedFilePaths { get; init; } = System.Array.Empty<string>();

    /// <summary>
    /// File-system port the decorator uses to mtime-check tracked files.
    /// May be null in unit tests that do not exercise the staleness scan.
    /// </summary>
    public IFileSystem? FileSystem { get; init; }

    /// <summary>
    /// Optional cap on how many tracked files the decorator will mtime-
    /// stat per call. Defaults to <see cref="int.MaxValue"/> (full scan);
    /// callers serving very large workspaces can lower it to bound the
    /// per-call cost. The decorator stops as soon as the cap is reached
    /// AND it has found at least one stale file (so the response still
    /// reports `filesChangedSinceAnalyze >= 1` accurately for the "any
    /// drift?" case).
    /// </summary>
    public int FileScanLimit { get; init; } = int.MaxValue;
}

/// <summary>
/// Thresholds that turn the always-computed staleness signal
/// (<see cref="ResponseEnvelope.StalenessSeconds"/> /
/// <see cref="ResponseEnvelope.FilesChangedSinceAnalyze"/>) into an
/// explicit <see cref="ResponseEnvelope.Limitations"/> entry on every
/// read-side response. Pre-policy, a caller with a 30-day stale graph
/// got the same envelope as a caller whose graph is one second old —
/// the numbers were on the wire but every consumer had to walk them by
/// hand to know whether to act on the response. The policy promotes
/// "yes, the workspace has drifted enough to matter" into the
/// limitations array so an AI agent or human reader sees the warning
/// alongside the tool-specific limitations, in one place.
///
/// Thresholds are configuration, not code. Composition roots can
/// override either field via environment variables at startup
/// (<c>LIFEBLOOD_STALENESS_SECONDS_THRESHOLD</c> /
/// <c>LIFEBLOOD_FILES_CHANGED_THRESHOLD</c>); tests pass explicit
/// instances; the documented <see cref="Default"/> values are
/// conservative anchors, not Lifeblood opinions. Setting a threshold
/// to <see cref="int.MaxValue"/> / <see cref="long.MaxValue"/>
/// effectively disables the corresponding limitation entry.
/// INV-ANALYZE-SKIPPED-PROMINENCE-001.
/// </summary>
public sealed record StalenessPolicy(
    long StalenessSecondsWarnThreshold,
    int FilesChangedWarnThreshold)
{
    /// <summary>
    /// Default thresholds: emit a staleness limitation when the loaded
    /// graph is more than one hour old (3600 s), or when at least ten
    /// tracked files have mtimes newer than the analyze timestamp. Both
    /// numbers are documented anchors picked to surface "workspace has
    /// meaningfully drifted" without firing for normal edit loops.
    /// </summary>
    public static StalenessPolicy Default { get; } = new(
        StalenessSecondsWarnThreshold: 3600,
        FilesChangedWarnThreshold: 10);
}

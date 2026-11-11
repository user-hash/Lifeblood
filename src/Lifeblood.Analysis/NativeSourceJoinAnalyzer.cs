using Lifeblood.Domain.Results;

namespace Lifeblood.Analysis;

/// <summary>Compares caller-reported debug-document identities with bounded retained-source observations; no artifact is opened or authenticated.</summary>
public static class NativeSourceJoinAnalyzer
{
    public static NativeSourceJoinReport Compare(
        PerformanceCapture capture,
        IReadOnlyList<NativeSourceObservation> observations,
        OperationFactScanReceipt scan,
        bool ignorePathCase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(scan);
        if (capture.NativeArtifacts.Length > 32 || capture.NativeArtifacts.Sum(artifact => artifact.Documents.Length) > 256)
            throw new ArgumentException("Native source evidence exceeds its artifact/document bounds.");
        if (capture.CaptureId.Length > 256 || capture.Identity.BuildId.Length > 256)
            throw new ArgumentException("Native capture/build identity exceeds 256 characters.");
        string PathKey(string path) => ignorePathCase ? path.Replace('\\', '/').ToUpperInvariant() : path.Replace('\\', '/');
        var bySource = observations.GroupBy(observation => (PathKey(observation.WorkspaceFilePath), observation.ModuleName, observation.ProfileScope, Algorithm(observation.ChecksumAlgorithm)))
            .ToDictionary(group => group.Key, group => group.Select(observation => observation.Checksum).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        var rows = new List<NativeSourceJoinRow>();
        foreach (var artifact in capture.NativeArtifacts)
        foreach (var document in artifact.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = NativeSourceJoinOutcome.SourceNotObserved;
            string? observed = null;
            var algorithm = Algorithm(document.ChecksumAlgorithm);
            if (string.IsNullOrWhiteSpace(capture.Identity.BuildId) || string.IsNullOrWhiteSpace(artifact.ArtifactId)
                || string.IsNullOrWhiteSpace(artifact.BuildId) || string.IsNullOrWhiteSpace(artifact.DebugId)
                || string.IsNullOrWhiteSpace(artifact.DebugFormat) || string.IsNullOrWhiteSpace(document.ModuleName)
                || string.IsNullOrWhiteSpace(document.ProfileScope) || string.IsNullOrWhiteSpace(document.WorkspaceFilePath))
                outcome = NativeSourceJoinOutcome.MissingIdentity;
            else if (capture.Identity.BuildId != artifact.BuildId)
                outcome = NativeSourceJoinOutcome.BuildMismatch;
            else if (document.ProfileScope != scan.ProfileScope
                || !capture.Identity.DefineProfiles.Contains(document.ProfileScope, StringComparer.Ordinal))
                outcome = NativeSourceJoinOutcome.ProfileMismatch;
            else if (document.ChecksumKind != "DebugDocument")
                outcome = NativeSourceJoinOutcome.ChecksumDomainMismatch;
            else if (algorithm is not ("SHA1" or "SHA256"))
                outcome = NativeSourceJoinOutcome.UnsupportedChecksumAlgorithm;
            else if (!ValidChecksum(document.Checksum, algorithm))
                outcome = NativeSourceJoinOutcome.InvalidChecksum;
            else if (scan.Status != OperationFactScanStatus.Completed || !scan.InputIdentityVerifiedAtStart || scan.Truncated || scan.StoppedByConsumer)
                outcome = NativeSourceJoinOutcome.IncompleteSemanticScan;
            else if (bySource.TryGetValue((PathKey(document.WorkspaceFilePath), document.ModuleName, document.ProfileScope, algorithm), out var candidates))
            {
                if (candidates.Length != 1) outcome = NativeSourceJoinOutcome.AmbiguousSourceIdentity;
                else
                {
                    observed = candidates[0];
                    outcome = string.Equals(observed, document.Checksum, StringComparison.OrdinalIgnoreCase)
                        ? NativeSourceJoinOutcome.MatchedSourceChecksum : NativeSourceJoinOutcome.MismatchedSourceChecksum;
                }
            }
            rows.Add(new NativeSourceJoinRow(artifact.ArtifactId, artifact.BuildId, artifact.DebugId, artifact.DebugFormat, document, outcome, observed));
        }
        return new NativeSourceJoinReport
        {
            Status = rows.Count == 0 ? "NoSourceDocuments" : "Compared",
            CaptureId = capture.CaptureId,
            CaptureSourceName = capture.ImportReceipt.SourceName,
            CaptureBuildId = capture.Identity.BuildId,
            ArtifactCount = capture.NativeArtifacts.Length,
            Documents = rows.ToArray(),
            Limitations = new[]
            {
                "Build/debug/document identities are caller-reported. This comparison does not open or authenticate a PE, native binary or PDB; nativeArtifactVerified remains false.",
                "MatchedSourceChecksum compares only the declared debug-document digest with retained compiler text under the stated module/profile and leased publication. It does not verify current raw-file bytes, defines, machine-code behavior, scheduling, purity or timing.",
                "Alternate checksum algorithms re-encode retained decoded text using its known encoding, or the compiler's default when absent. Lost original encoding/BOM information can prevent a match; no current-file hashing substitutes for retained evidence.",
                "Workspace paths are explicit caller mappings, not inferred from native source names. Unknown, conflicting or incomplete observations remain explicit outcomes.",
            },
        };
    }

    private static string Algorithm(string algorithm) => algorithm.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
    private static bool ValidChecksum(string checksum, string algorithm)
        => checksum.Length == (algorithm == "SHA1" ? 40 : 64) && checksum.All(Uri.IsHexDigit);
}

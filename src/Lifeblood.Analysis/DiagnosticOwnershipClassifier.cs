using Lifeblood.Domain.Results;

namespace Lifeblood.Analysis;

/// <summary>
/// Stateless join between language-adapter diagnostics and neutral source-
/// control change evidence. It classifies locations only; it never claims a
/// changed line caused a diagnostic reported elsewhere.
/// </summary>
public static class DiagnosticOwnershipClassifier
{
    public static DiagnosticOwnershipReport Classify(
        IReadOnlyList<DiagnosticInfo> diagnostics,
        SourceChangeSnapshot changes,
        bool diagnosticsPossiblyStale = false)
    {
        if (diagnosticsPossiblyStale || !changes.EvidenceComplete)
        {
            var evidence = diagnosticsPossiblyStale
                ? "diagnosticsPossiblyStale"
                : "changeEvidenceUnavailable";
            return new DiagnosticOwnershipReport(diagnostics
                .Select((_, index) => new DiagnosticOwnershipClassification(
                    index,
                    DiagnosticOwnership.UnknownOwnership,
                    evidence))
                .ToArray());
        }

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var changedFiles = changes.Files
            .GroupBy(file => NormalizePath(file.FilePath, changes.RepositoryRoot), comparer)
            .ToDictionary(group => group.Key, group => group.First(), comparer);
        var classifications = new DiagnosticOwnershipClassification[diagnostics.Count];

        for (var index = 0; index < diagnostics.Count; index++)
        {
            var diagnostic = diagnostics[index];
            var path = NormalizePath(diagnostic.FilePath, changes.RepositoryRoot);
            if (path.Length == 0)
            {
                classifications[index] = Classify(
                    index,
                    DiagnosticOwnership.UnknownOwnership,
                    "missingDiagnosticPath");
                continue;
            }

            if (!IsWithinRepository(path, changes.RepositoryRoot))
            {
                classifications[index] = Classify(
                    index,
                    DiagnosticOwnership.UnknownOwnership,
                    "diagnosticPathOutsideRepository");
                continue;
            }

            if (!changedFiles.TryGetValue(path, out var changedFile))
            {
                classifications[index] = Classify(
                    index,
                    DiagnosticOwnership.PreExistingUnrelated,
                    "fileOutsideChangeSet");
                continue;
            }

            if (changedFile.IsNew)
            {
                classifications[index] = Classify(
                    index,
                    DiagnosticOwnership.IntroducedByDiff,
                    "newFile");
                continue;
            }

            if (!changedFile.HasLineEvidence)
            {
                classifications[index] = Classify(
                    index,
                    DiagnosticOwnership.UnknownOwnership,
                    "touchedFileWithoutLineEvidence");
                continue;
            }

            if (diagnostic.Line <= 0)
            {
                classifications[index] = Classify(
                    index,
                    DiagnosticOwnership.UnknownOwnership,
                    "missingDiagnosticLine");
                continue;
            }

            var changedLine = changedFile.ChangedLines.Any(
                range => diagnostic.Line >= range.StartLine && diagnostic.Line <= range.EndLine);
            classifications[index] = changedLine
                ? Classify(index, DiagnosticOwnership.IntroducedByDiff, "changedCurrentLine")
                : Classify(index, DiagnosticOwnership.PreExistingTouchedFile, "touchedFileOutsideChangedLines");
        }

        return new DiagnosticOwnershipReport(classifications);
    }

    private static DiagnosticOwnershipClassification Classify(
        int index,
        DiagnosticOwnership ownership,
        string evidence)
        => new(index, ownership, evidence);

    private static string NormalizePath(string? path, string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            return Path.GetFullPath(
                Path.IsPathRooted(path)
                    ? path
                    : Path.Combine(repositoryRoot, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "";
        }
    }

    private static bool IsWithinRepository(string fullPath, string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot)) return false;
        try
        {
            var relative = Path.GetRelativePath(repositoryRoot, fullPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            return relative != ".."
                && !relative.StartsWith("../", StringComparison.Ordinal)
                && !Path.IsPathRooted(relative);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

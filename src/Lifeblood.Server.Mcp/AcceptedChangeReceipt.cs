using Lifeblood.Application.Ports.Left;

namespace Lifeblood.Server.Mcp;

/// <summary>Caller-owned projection policy for accepted-change evidence.</summary>
public sealed record AcceptedChangeReceiptRequest
{
    public const int DefaultLimit = 50;
    public const int MaximumLimit = 200;

    public static AcceptedChangeReceiptRequest Summary { get; } = new(
        AcceptedChangeReceiptMode.Summary,
        DefaultLimit);

    public AcceptedChangeReceiptRequest(AcceptedChangeReceiptMode mode, int limit)
    {
        Mode = mode;
        Limit = mode == AcceptedChangeReceiptMode.Summary
            ? DefaultLimit
            : Math.Clamp(limit, 1, MaximumLimit);
    }

    public AcceptedChangeReceiptMode Mode { get; }

    public int Limit { get; }

    public static AcceptedChangeReceiptRequest Create(string? mode, int? limit)
        => new(
            string.Equals(mode, "detail", StringComparison.OrdinalIgnoreCase)
                ? AcceptedChangeReceiptMode.Detail
                : AcceptedChangeReceiptMode.Summary,
            limit ?? DefaultLimit);
}

public enum AcceptedChangeReceiptMode
{
    Summary,
    Detail,
}

/// <summary>
/// MCP projection of the canonical application receipt. One bounded union of
/// paths carries per-path cause flags, avoiding repeated path arrays.
/// </summary>
public static class AcceptedChangeReceipt
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static object Build(
        AcceptedChangeSet changes,
        AcceptedChangeReceiptRequest request)
    {
        var reanalyzed = changes.ReanalyzedSourceFiles.ToHashSet(PathComparer);
        var mtimeTouched = changes.MtimeTouchedSourceFiles.ToHashSet(PathComparer);
        var contentChanged = changes.ContentChangedSourceFiles.ToHashSet(PathComparer);
        var descriptorForced = changes.DescriptorForcedSourceFiles.ToHashSet(PathComparer);
        var deleted = changes.DeletedSourceFiles.ToHashSet(PathComparer);
        var mtimeOnlyCount = changes.MtimeTouchedSourceFiles.Count(path =>
            !contentChanged.Contains(path)
            && !descriptorForced.Contains(path)
            && !deleted.Contains(path));

        var causes = new List<string>(capacity: 5);
        if (contentChanged.Count > 0) causes.Add("sourceContent");
        if (descriptorForced.Count > 0) causes.Add("descriptorRecompile");
        if (deleted.Count > 0) causes.Add("sourceDeleted");
        if (mtimeOnlyCount > 0) causes.Add("mtimeOnly");
        if (changes.FullFallback) causes.Add("fullFallback");

        var evidenceFileCount = changes.EvidenceSourceFiles.Count;
        var returnedPaths = request.Mode == AcceptedChangeReceiptMode.Detail
            ? changes.EvidenceSourceFiles.Take(request.Limit).ToArray()
            : Array.Empty<string>();
        var omittedFileCount = evidenceFileCount - returnedPaths.Length;
        var files = returnedPaths.Select(path => new
        {
            path,
            reanalyzed = reanalyzed.Contains(path),
            mtimeTouched = mtimeTouched.Contains(path),
            contentChanged = contentChanged.Contains(path),
            descriptorForced = descriptorForced.Contains(path),
            deleted = deleted.Contains(path),
        }).ToArray();

        return new
        {
            mode = request.Mode == AcceptedChangeReceiptMode.Detail ? "detail" : "summary",
            scanMode = WireScanMode(changes.ScanMode),
            fullFallback = changes.FullFallback,
            causes = causes.ToArray(),
            interpretation = BuildInterpretation(
                changes,
                reanalyzed.Count,
                mtimeOnlyCount,
                contentChanged.Count,
                descriptorForced.Count,
                deleted.Count),
            counts = new
            {
                changedSourceFiles = changes.ChangedFileCount,
                reanalyzedSourceFiles = reanalyzed.Count,
                mtimeTouchedSourceFiles = mtimeTouched.Count,
                contentChangedSourceFiles = contentChanged.Count,
                descriptorForcedSourceFiles = descriptorForced.Count,
                deletedSourceFiles = deleted.Count,
            },
            files,
            evidenceFileCount,
            returnedFileCount = files.Length,
            omittedFileCount,
            truncated = request.Mode == AcceptedChangeReceiptMode.Detail && omittedFileCount > 0,
        };
    }

    private static object BuildInterpretation(
        AcceptedChangeSet changes,
        int reanalyzedSourceFiles,
        int mtimeOnlySourceFiles,
        int contentChangedSourceFiles,
        int descriptorForcedSourceFiles,
        int deletedSourceFiles)
    {
        var work = WorkClassification(
            changes,
            mtimeOnlySourceFiles,
            contentChangedSourceFiles,
            descriptorForcedSourceFiles,
            deletedSourceFiles);
        return new
        {
            work,
            changedSourceFilesMeaning = "reanalyzedOrDeleted",
            contentChangeStatus = contentChangedSourceFiles == 0 ? "none" : "present",
            summary = BuildHumanSummary(
                changes,
                reanalyzedSourceFiles,
                mtimeOnlySourceFiles,
                contentChangedSourceFiles,
                descriptorForcedSourceFiles,
                deletedSourceFiles),
        };
    }

    private static string WorkClassification(
        AcceptedChangeSet changes,
        int mtimeOnlySourceFiles,
        int contentChangedSourceFiles,
        int descriptorForcedSourceFiles,
        int deletedSourceFiles)
    {
        if (changes.FullFallback)
            return "fullFallbackReanalysis";
        if (contentChangedSourceFiles > 0
            && (mtimeOnlySourceFiles > 0 || descriptorForcedSourceFiles > 0 || deletedSourceFiles > 0))
            return "mixedAcceptedChanges";
        if (contentChangedSourceFiles > 0)
            return "sourceContentChange";
        if (descriptorForcedSourceFiles > 0)
            return "descriptorRecompile";
        if (deletedSourceFiles > 0)
            return "sourceDeletion";
        if (mtimeOnlySourceFiles > 0)
            return "mtimeOnlyTouch";
        if (changes.ReanalyzedSourceFiles.Count > 0)
            return "sourceReanalysis";
        return "none";
    }

    private static string BuildHumanSummary(
        AcceptedChangeSet changes,
        int reanalyzedSourceFiles,
        int mtimeOnlySourceFiles,
        int contentChangedSourceFiles,
        int descriptorForcedSourceFiles,
        int deletedSourceFiles)
    {
        if (changes.FullFallback)
        {
            return $"Full fallback reanalyzed {DescribeCount(reanalyzedSourceFiles, "source file")}; "
                   + "this is not evidence of source content churn. "
                   + $"contentChangedSourceFiles={contentChangedSourceFiles}.";
        }

        if (contentChangedSourceFiles > 0)
        {
            return $"Reanalyzed {DescribeCount(reanalyzedSourceFiles, "source file")}; "
                   + $"{DescribeCount(contentChangedSourceFiles, "source file")} had content changes.";
        }

        if (descriptorForcedSourceFiles > 0)
        {
            return $"Reanalyzed {DescribeCount(reanalyzedSourceFiles, "source file")} because descriptor changes forced recompilation; "
                   + "source content changes were not detected.";
        }

        if (deletedSourceFiles > 0)
        {
            return $"Accepted {DescribeCount(deletedSourceFiles, "deleted source file")}; "
                   + "deleted files are counted as changedSourceFiles but not reanalyzed.";
        }

        if (mtimeOnlySourceFiles > 0)
        {
            return $"Observed {DescribeCount(mtimeOnlySourceFiles, "mtime-only source touch")} without source content changes.";
        }

        if (reanalyzedSourceFiles > 0)
        {
            return $"Reanalyzed {DescribeCount(reanalyzedSourceFiles, "source file")} without detected source content changes.";
        }

        return "No accepted source file changes.";
    }

    private static string DescribeCount(int count, string singular)
        => count == 1 ? $"1 {singular}" : $"{count} {singular}s";

    private static string WireScanMode(ChangeScanMode mode) => mode switch
    {
        ChangeScanMode.FilesystemPrefilter => "filesystemPrefilter",
        ChangeScanMode.AuthoritativeChangedSet => "authoritativeChangedSet",
        ChangeScanMode.FullFallback => "fullFallback",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}

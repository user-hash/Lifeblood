using System.Text.RegularExpressions;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-TRACKING-SSOT-001. The living tracking ledger is readable Markdown, but
/// its status surface is machine-checked so summaries cannot drift from entry
/// bodies.
/// </summary>
public class TrackingLedgerTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string TrackingPath = Path.Combine(RepoRoot, "devmemory", "lifeblood-tracking.md");

    [Fact]
    public void TrackingLedger_StatusSummaryAnchors_MatchParsedEntries()
    {
        var entries = ParseEntries();
        var counts = entries
            .GroupBy(e => e.Category)
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(GetCount(counts, TrackingStatus.Shipped), ReadStatusAnchor("Shipped"));
        Assert.Equal(GetCount(counts, TrackingStatus.PartiallyShipped), ReadStatusAnchor("PartiallyShipped"));
        Assert.Equal(GetCount(counts, TrackingStatus.Receipt), ReadStatusAnchor("Receipt"));
        Assert.Equal(GetCount(counts, TrackingStatus.Open), ReadStatusAnchor("Open"));
    }

    [Fact]
    public void TrackingLedger_HasNoPlainOpenOrCandidateEntries()
    {
        var stillOpen = ParseEntries()
            .Where(e => e.Category is TrackingStatus.Open or TrackingStatus.Candidate)
            .Select(e => $"{e.Title} ({e.Status})")
            .ToArray();

        Assert.True(
            stillOpen.Length == 0,
            "Tracking entries still marked Open/Candidate: " + string.Join("; ", stillOpen));
    }

    [Fact]
    public void TrackingLedger_ActiveBacklogList_EqualsPartiallyShippedEntries()
    {
        var expected = ParseEntries()
            .Where(e => e.Category == TrackingStatus.PartiallyShipped)
            .Select(e => e.Title)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToArray();

        var actual = ReadActiveBacklogTitles()
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TrackingLedger_PartiallyShippedEntries_DeclareRemainingOpenWork()
    {
        var missing = ParseEntries()
            .Where(e => e.Category == TrackingStatus.PartiallyShipped)
            .Where(e => !e.Body.Contains("Remaining open work:", StringComparison.Ordinal))
            .Select(e => e.Title)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "Partially shipped entries must name their remaining open work: " + string.Join("; ", missing));
    }

    [Fact]
    public void StatusDoc_CurrentCapabilityProse_NamesJsonCompatAndTelemetrySsot()
    {
        var status = File.ReadAllText(Path.Combine(RepoRoot, "docs", "STATUS.md"));

        Assert.Contains("LIFEBLOOD_JSON_COMPAT=legacy|warn|strict", status);
        Assert.Contains("LIFEBLOOD_STRICT_JSON", status);
        Assert.Contains("lifeblood.tool.arguments", status);
        Assert.Contains("lifeblood.analyze.phase", status);
        Assert.DoesNotContain(
            "adds opt-in strict JSON duplicate-property rejection behind `LIFEBLOOD_STRICT_JSON`",
            status,
            StringComparison.Ordinal);
    }

    private static int GetCount(IReadOnlyDictionary<TrackingStatus, int> counts, TrackingStatus status)
        => counts.TryGetValue(status, out var count) ? count : 0;

    private static int ReadStatusAnchor(string name)
    {
        var text = File.ReadAllText(TrackingPath);
        var match = Regex.Match(
            text,
            $@"<!--\s*trackingStatus{Regex.Escape(name)}Count:\s*(\d+)\s*-->");
        Assert.True(match.Success, $"Tracking ledger must declare trackingStatus{name}Count.");
        return int.Parse(match.Groups[1].Value);
    }

    private static string[] ReadActiveBacklogTitles()
    {
        var titles = new List<string>();
        var inBlock = false;

        foreach (var line in File.ReadLines(TrackingPath))
        {
            if (line.Contains("trackingActiveBacklog:start", StringComparison.Ordinal))
            {
                inBlock = true;
                continue;
            }

            if (line.Contains("trackingActiveBacklog:end", StringComparison.Ordinal))
            {
                Assert.True(inBlock, "Tracking active-backlog end marker appeared before start marker.");
                return titles.ToArray();
            }

            if (!inBlock)
            {
                continue;
            }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                titles.Add(trimmed.Substring(2).Trim());
            }
        }

        Assert.Fail("Tracking ledger must declare a trackingActiveBacklog block.");
        return Array.Empty<string>();
    }

    private static TrackingEntry[] ParseEntries()
    {
        var lines = File.ReadAllLines(TrackingPath);
        var headings = ReadHeadings(lines);
        var entries = new List<TrackingEntry>();

        for (var i = 0; i < headings.Length; i++)
        {
            var heading = headings[i];
            var bodyStart = heading.LineIndex + 1;
            var bodyEnd = i + 1 < headings.Length ? headings[i + 1].LineIndex : lines.Length;
            var bodyLines = lines[bodyStart..bodyEnd];
            var status = ReadStatus(bodyLines);
            if (status is null)
            {
                continue;
            }

            if (status.Contains('|', StringComparison.Ordinal))
            {
                continue; // Required Entry Template example row, not a real ledger entry.
            }

            var body = string.Join(Environment.NewLine, bodyLines);
            entries.Add(new TrackingEntry(heading.Title, status, ClassifyStatus(status), body));
        }

        Assert.NotEmpty(entries);
        return entries.ToArray();
    }

    private static Heading[] ReadHeadings(string[] lines)
    {
        var headings = new List<Heading>();
        var inFence = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                continue;
            }

            if (TryReadHeading(trimmed, i, out var heading))
            {
                headings.Add(heading);
            }
        }

        return headings.ToArray();
    }

    private static bool TryReadHeading(string trimmedLine, int lineIndex, out Heading heading)
    {
        const string Level2Prefix = "## ";
        const string Level3Prefix = "### ";

        if (trimmedLine.StartsWith(Level3Prefix, StringComparison.Ordinal))
        {
            heading = new Heading(trimmedLine.Substring(Level3Prefix.Length).Trim(), lineIndex);
            return true;
        }

        if (trimmedLine.StartsWith(Level2Prefix, StringComparison.Ordinal))
        {
            heading = new Heading(trimmedLine.Substring(Level2Prefix.Length).Trim(), lineIndex);
            return true;
        }

        heading = default;
        return false;
    }

    private static string? ReadStatus(IEnumerable<string> bodyLines)
    {
        const string StatusPrefix = "Status:";

        foreach (var line in bodyLines)
        {
            if (line.StartsWith(StatusPrefix, StringComparison.Ordinal))
            {
                return line.Substring(StatusPrefix.Length).Trim();
            }
        }

        return null;
    }

    private static TrackingStatus ClassifyStatus(string status)
    {
        if (status.StartsWith("Shipped", StringComparison.OrdinalIgnoreCase))
        {
            return TrackingStatus.Shipped;
        }

        if (status.StartsWith("Partially shipped", StringComparison.OrdinalIgnoreCase))
        {
            return TrackingStatus.PartiallyShipped;
        }

        if (status.StartsWith("Receipt", StringComparison.OrdinalIgnoreCase))
        {
            return TrackingStatus.Receipt;
        }

        if (status.StartsWith("Open", StringComparison.OrdinalIgnoreCase))
        {
            return TrackingStatus.Open;
        }

        if (status.StartsWith("Candidate", StringComparison.OrdinalIgnoreCase))
        {
            return TrackingStatus.Candidate;
        }

        throw new InvalidOperationException($"Unknown tracking status: {status}");
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "Lifeblood.sln")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return current!.FullName;
    }

    private sealed record TrackingEntry(
        string Title,
        string Status,
        TrackingStatus Category,
        string Body);

    private readonly record struct Heading(string Title, int LineIndex);

    private enum TrackingStatus
    {
        Shipped,
        PartiallyShipped,
        Receipt,
        Open,
        Candidate,
    }
}

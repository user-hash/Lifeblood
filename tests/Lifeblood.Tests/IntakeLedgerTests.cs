using System.Text.RegularExpressions;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-INTAKE-SHAPE-001. The intake file (<c>devmemory/lifeblood-intake.md</c>)
/// is the front door for un-started dogfood findings: <see cref="TrackingLedgerTests"/>
/// rejects parked Open/Candidate entries in the living ledger, so new findings
/// land in intake first. That makes intake provenance-critical and unguarded —
/// this ratchet pins its shape (well-formed unique ids, required metadata, the
/// three authoring sections) so the backlog cannot silently lose traceability
/// while the tracking ledger stays green. Shape/metadata only — intake is
/// intentionally un-prioritized (no status model) until an item is promoted.
/// </summary>
public class IntakeLedgerTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string IntakePath = Path.Combine(RepoRoot, "devmemory", "lifeblood-intake.md");
    private static readonly string TrackingPath = Path.Combine(RepoRoot, "devmemory", "lifeblood-tracking.md");

    private const string IntakeIdPattern = @"^LB-INTAKE-\d{8}-\d{3}$";

    [Fact]
    public void IntakeFile_DeclaresRoutingContract()
    {
        var text = File.ReadAllText(IntakePath);

        Assert.Contains("new findings land here first", text);
        Assert.Contains("When work begins, promote the item", text);
    }

    [Fact]
    public void EveryIntakeHeadingMentioningIntake_IsAWellFormedEntry()
    {
        // A heading that contains "LB-INTAKE" but whose leading token is not a
        // canonical id is a typo (e.g. missing a digit) — it would silently
        // escape ParseEntries' id filter, so catch it explicitly.
        var malformed = ReadEntryHeadings()
            .Where(h => h.Title.Contains("LB-INTAKE", StringComparison.Ordinal))
            .Where(h => !Regex.IsMatch(LeadingToken(h.Title), IntakeIdPattern))
            .Select(h => h.Title)
            .ToArray();

        Assert.True(
            malformed.Length == 0,
            "Intake headings mention LB-INTAKE but are not canonical ids: " + string.Join("; ", malformed));
    }

    [Fact]
    public void EveryEntryId_MatchesCanonicalPattern()
    {
        var bad = ParseEntries()
            .Where(e => !Regex.IsMatch(e.Id, IntakeIdPattern))
            .Select(e => e.Id)
            .ToArray();

        Assert.True(bad.Length == 0, "Non-canonical intake ids: " + string.Join("; ", bad));
    }

    [Fact]
    public void EntryIds_AreUnique()
    {
        var duplicates = ParseEntries()
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} (x{g.Count()})")
            .ToArray();

        Assert.True(duplicates.Length == 0, "Duplicate intake ids: " + string.Join("; ", duplicates));
    }

    [Fact]
    public void EveryEntry_DeclaresRequiredMetadata()
    {
        var required = new[] { "Type:", "Priority:", "Source:", "Workspace:" };
        var missing = ParseEntries()
            .SelectMany(e => required
                .Where(token => !e.Body.Contains(token, StringComparison.Ordinal))
                .Select(token => $"{e.Id} missing {token}"))
            .ToArray();

        Assert.True(missing.Length == 0, "Intake entries missing required metadata: " + string.Join("; ", missing));
    }

    [Fact]
    public void EveryEntry_DeclaresAuthoringSections()
    {
        var required = new[] { "What:", "Why it matters:", "Fix shape:" };
        var missing = ParseEntries()
            .SelectMany(e => required
                .Where(token => !e.Body.Contains(token, StringComparison.Ordinal))
                .Select(token => $"{e.Id} missing '{token}'"))
            .ToArray();

        Assert.True(missing.Length == 0, "Intake entries missing authoring sections: " + string.Join("; ", missing));
    }

    [Fact]
    public void NoIntakeId_AlsoAppearsInTrackingLedger()
    {
        // After promotion an item is recorded in the tracking ledger and deleted
        // from intake; it must not live in both at once.
        var tracking = File.ReadAllText(TrackingPath);
        var leaked = ParseEntries()
            .Where(e => tracking.Contains(e.Id, StringComparison.Ordinal))
            .Select(e => e.Id)
            .ToArray();

        Assert.True(leaked.Length == 0, "Intake ids also present in tracking ledger: " + string.Join("; ", leaked));
    }

    private static string LeadingToken(string title)
    {
        var space = title.IndexOf(' ');
        return space < 0 ? title : title.Substring(0, space);
    }

    private static IntakeEntry[] ParseEntries()
    {
        var lines = File.ReadAllLines(IntakePath);
        var headings = ReadEntryHeadings(lines)
            .Where(h => h.Title.StartsWith("LB-INTAKE-", StringComparison.Ordinal))
            .ToArray();

        var entries = new List<IntakeEntry>();
        for (var i = 0; i < headings.Length; i++)
        {
            var heading = headings[i];
            var bodyStart = heading.LineIndex + 1;
            var bodyEnd = i + 1 < headings.Length ? headings[i + 1].LineIndex : lines.Length;
            var body = string.Join(Environment.NewLine, lines[bodyStart..bodyEnd]);
            entries.Add(new IntakeEntry(LeadingToken(heading.Title), heading.Title, body));
        }

        return entries.ToArray();
    }

    private static Heading[] ReadEntryHeadings() => ReadEntryHeadings(File.ReadAllLines(IntakePath));

    private static Heading[] ReadEntryHeadings(string[] lines)
    {
        const string Level2Prefix = "## ";
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

            if (trimmed.StartsWith(Level2Prefix, StringComparison.Ordinal))
            {
                headings.Add(new Heading(trimmed.Substring(Level2Prefix.Length).Trim(), i));
            }
        }

        return headings.ToArray();
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

    private sealed record IntakeEntry(string Id, string Title, string Body);

    private readonly record struct Heading(string Title, int LineIndex);
}

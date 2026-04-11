using Lifeblood.Adapters.CSharp;
using Lifeblood.Connectors.Mcp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Dogfoods the Phase 8 invariant pipeline against Lifeblood's own
/// <c>CLAUDE.md</c>. Proves that:
///
/// <list type="bullet">
///   <item>the provider can locate Lifeblood's root from the test
///     runner's working directory,</item>
///   <item>the parser recognises every invariant marker authored in
///     the repo's canonical prose,</item>
///   <item>the invariant count is at or above a floor that makes sense
///     for this release (50+ — today's repo has ~60),</item>
///   <item>every core category the codebase uses (DOMAIN, GRAPH, MCP,
///     RESOLVER, CANONICAL, BCL) shows up in the audit so a silent
///     regex regression in the parser would fail this test.</item>
/// </list>
///
/// This is the "tool works against the real project" smoke test. If
/// the parser ever drifts from CLAUDE.md's authoring conventions, this
/// test is the first alarm.
/// </summary>
public class LifebloodClaudeMdSelfTests
{
    private static readonly string LifebloodRoot = FindLifebloodRoot();
    private static readonly PhysicalFileSystem Fs = new();

    [SkippableFact]
    public void SelfAudit_ParsesAtLeast50Invariants()
    {
        Skip.IfNot(Directory.Exists(LifebloodRoot),
            "Lifeblood root not found — test runner cwd is detached from the source tree.");
        Skip.IfNot(File.Exists(Path.Combine(LifebloodRoot, "CLAUDE.md")),
            $"CLAUDE.md missing at {LifebloodRoot}");

        var provider = new LifebloodInvariantProvider(Fs);
        var audit = provider.Audit(LifebloodRoot);

        Assert.True(audit.TotalCount >= 50,
            $"Expected at least 50 invariants in Lifeblood's CLAUDE.md, found {audit.TotalCount}. " +
            "Either the parser regressed or the project removed invariants — update the floor if intentional.");
    }

    [SkippableFact]
    public void SelfAudit_EveryCoreCategoryIsRepresented()
    {
        Skip.IfNot(Directory.Exists(LifebloodRoot), "Lifeblood root not found.");
        Skip.IfNot(File.Exists(Path.Combine(LifebloodRoot, "CLAUDE.md")), "CLAUDE.md missing.");

        var provider = new LifebloodInvariantProvider(Fs);
        var audit = provider.Audit(LifebloodRoot);
        var categoryNames = audit.CategoryCounts.Select(c => c.Category).ToHashSet();

        // These are architectural pillars the codebase has explicit
        // invariants for. Missing any of them means either the parser
        // regressed on that category's bullet shape or the category's
        // invariants were deleted without announcement.
        var requiredCategories = new[]
        {
            "DOMAIN",
            "GRAPH",
            "MCP",
            "RESOLVER",
            "CANONICAL",
            "BCL",
        };
        foreach (var cat in requiredCategories)
        {
            Assert.True(categoryNames.Contains(cat),
                $"Category '{cat}' not represented in parsed audit. Found categories: " +
                string.Join(", ", categoryNames.OrderBy(c => c)));
        }
    }

    [SkippableFact]
    public void SelfAudit_KnownInvariantIds_LookupByIdReturnsBody()
    {
        Skip.IfNot(Directory.Exists(LifebloodRoot), "Lifeblood root not found.");
        Skip.IfNot(File.Exists(Path.Combine(LifebloodRoot, "CLAUDE.md")), "CLAUDE.md missing.");

        var provider = new LifebloodInvariantProvider(Fs);

        // Pin a handful of invariants that have been stable in this
        // repository for multiple releases. Every one MUST resolve via
        // GetById; if any of these vanish the lookup API is broken or
        // the parser no longer understands the bullet shape the
        // invariant was authored in.
        var mustExist = new[]
        {
            "INV-CANONICAL-001",
            "INV-RESOLVER-001",
            "INV-RESOLVER-005",
            "INV-BCL-001",
            "INV-MCP-001",
            "INV-TOOLREG-001",
        };
        foreach (var id in mustExist)
        {
            var inv = provider.GetById(LifebloodRoot, id);
            Assert.True(inv != null, $"Invariant '{id}' could not be resolved by id lookup.");
            Assert.False(string.IsNullOrWhiteSpace(inv!.Body),
                $"Invariant '{id}' resolved but Body is empty — parser dropped the block.");
            Assert.False(string.IsNullOrWhiteSpace(inv.Title),
                $"Invariant '{id}' resolved but Title is empty — parser failed title extraction.");
        }
    }

    [SkippableFact]
    public void SelfAudit_NoDuplicateIds()
    {
        Skip.IfNot(Directory.Exists(LifebloodRoot), "Lifeblood root not found.");
        Skip.IfNot(File.Exists(Path.Combine(LifebloodRoot, "CLAUDE.md")), "CLAUDE.md missing.");

        var provider = new LifebloodInvariantProvider(Fs);
        var audit = provider.Audit(LifebloodRoot);

        Assert.True(audit.Duplicates.Length == 0,
            "Duplicate invariant ids found in Lifeblood CLAUDE.md: " +
            string.Join("; ", audit.Duplicates.Select(d =>
                $"{d.Id} at lines [{string.Join(", ", d.SourceLines)}]")));
    }

    [SkippableFact]
    public void SelfAudit_NoParseWarnings()
    {
        Skip.IfNot(Directory.Exists(LifebloodRoot), "Lifeblood root not found.");
        Skip.IfNot(File.Exists(Path.Combine(LifebloodRoot, "CLAUDE.md")), "CLAUDE.md missing.");

        var provider = new LifebloodInvariantProvider(Fs);
        var audit = provider.Audit(LifebloodRoot);

        Assert.True(audit.ParseWarnings.Length == 0,
            "Lifeblood CLAUDE.md parse warnings: " + string.Join("; ", audit.ParseWarnings));
    }

    private static string FindLifebloodRoot()
    {
        // The test runner's working directory is
        // tests/Lifeblood.Tests/bin/Debug/net8.0. Walk up until we find
        // a directory that has both CLAUDE.md and src/Lifeblood.Domain.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            if (dir == null) break;
            if (File.Exists(Path.Combine(dir, "CLAUDE.md"))
                && Directory.Exists(Path.Combine(dir, "src", "Lifeblood.Domain")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return "";
    }
}

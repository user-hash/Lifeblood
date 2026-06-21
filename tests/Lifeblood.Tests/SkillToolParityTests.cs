using System.Text.RegularExpressions;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-SKILL-TOOL-PARITY-001. The public agent skill
/// (<c>skills/lifeblood-mcp/</c>) ships alongside Lifeblood and is the front door
/// agents use to choose tools — so its tool catalog MUST stay in lockstep with the
/// live <see cref="ToolRegistry"/>. A new tool added without a routing entry would
/// silently never be reached by skill-driven agents; a renamed/removed tool would
/// leave a dangling reference. This ratchet pins both directions: every registry
/// tool is documented in <c>references/tool-routing.md</c>, and every
/// <c>lifeblood_*</c> name the skill mentions is a real tool (modulo the
/// documented Unity-bridge alias). Semantic (reads the live registry, not a
/// hardcoded count) so it survives tool renames.
/// </summary>
public class SkillToolParityTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string RoutingPath = Path.Combine(RepoRoot, "skills", "lifeblood-mcp", "references", "tool-routing.md");
    private static readonly string SkillPath = Path.Combine(RepoRoot, "skills", "lifeblood-mcp", "SKILL.md");

    // Unity-bridge convenience alias that wraps lifeblood_analyze; documented in
    // the skill's Unity notes but intentionally not a core ToolRegistry entry.
    private static readonly HashSet<string> KnownNonRegistryToolMentions = new(StringComparer.Ordinal)
    {
        "lifeblood_analyze_project",
    };

    [Fact]
    public void SkillFiles_Exist()
    {
        Assert.True(File.Exists(RoutingPath), $"missing tool-routing reference: {RoutingPath}");
        Assert.True(File.Exists(SkillPath), $"missing SKILL.md: {SkillPath}");
    }

    [Fact]
    public void EveryRegistryTool_IsDocumentedInToolRouting()
    {
        var routing = File.ReadAllText(RoutingPath);
        var undocumented = ToolRegistry.GetTools()
            .Select(t => t.Name)
            .Where(name => !routing.Contains(name, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            undocumented.Length == 0,
            "Tools missing from skills/lifeblood-mcp/references/tool-routing.md — add a routing entry in the same atom that adds the tool: "
            + string.Join(", ", undocumented));
    }

    [Fact]
    public void EveryToolMentionedInSkill_IsARealRegistryTool()
    {
        var registry = ToolRegistry.GetTools().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var text = File.ReadAllText(RoutingPath) + "\n" + File.ReadAllText(SkillPath);
        var dangling = Regex.Matches(text, @"lifeblood_[a-z_]+")
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .Where(name => !registry.Contains(name) && !KnownNonRegistryToolMentions.Contains(name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            dangling.Length == 0,
            "Skill mentions lifeblood_* names that are not in ToolRegistry (typo, rename, or removed tool?): "
            + string.Join(", ", dangling));
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
}

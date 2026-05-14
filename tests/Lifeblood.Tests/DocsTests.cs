using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Ratchet tests that keep docs honest. Doc numbers drift silently if nobody
/// enforces them. Every number these tests cover derives from a single source
/// of truth in docs/STATUS.md (HTML comments like <!-- portCount: 17 -->),
/// and the tests assert the comment matches the live repository state.
///
/// Invariants enforced:
/// INV-DOCS-001. Docs/STATUS.md port/test/tool counts match the repository.
/// INV-CHANGELOG-001. Every ## [X.Y.Z] heading has a matching link reference.
/// </summary>
public class DocsTests
{
  private static readonly string RepoRoot = FindRepoRoot();

  [Fact]
  public void StatusDoc_PortCount_MatchesApplicationPortsDeclarations()
  {
  // Single source of truth: <!-- portCount: N --> comment in docs/STATUS.md.
  // Live truth: count of `public interface I*` declarations under
  // src/Lifeblood.Application/Ports. Counting DECLARATIONS (not files) is
  // correct because some port pairs (IUsageProbe + IUsageCapture) share
  // one file by design.
  var statusPath = Path.Combine(RepoRoot, "docs", "STATUS.md");
  var status = File.ReadAllText(statusPath);

  var match = Regex.Match(status, @"<!--\s*portCount:\s*(\d+)\s*-->");
  Assert.True(match.Success,
  "docs/STATUS.md must declare <!-- portCount: N --> so this ratchet has a single source of truth.");
  var declared = int.Parse(match.Groups[1].Value);

  var portsDir = Path.Combine(RepoRoot, "src", "Lifeblood.Application", "Ports");
  Assert.True(Directory.Exists(portsDir), $"Ports directory not found: {portsDir}");

  var live = 0;
  foreach (var file in Directory.EnumerateFiles(portsDir, "*.cs", SearchOption.AllDirectories))
  {
  var content = File.ReadAllText(file);
  live += Regex.Matches(content, @"\bpublic\s+interface\s+I[A-Z][A-Za-z0-9_]*\b").Count;
  }

  Assert.True(declared == live,
  $"docs/STATUS.md declares portCount={declared} but src/Lifeblood.Application/Ports " +
  $"has {live} `public interface I*` declarations. Update the HTML comment in STATUS.md " +
  "to the live count, or add/remove the interface that caused the drift.");
  }

  [Fact]
  public void StatusDoc_ToolCount_MatchesToolRegistryLiterals()
  {
  // Single source of truth: <!-- toolCount: N --> comment in docs/STATUS.md.
  // Live truth: count of Name = "lifeblood_" literals in ToolRegistry.cs.
  var statusPath = Path.Combine(RepoRoot, "docs", "STATUS.md");
  var status = File.ReadAllText(statusPath);

  var match = Regex.Match(status, @"<!--\s*toolCount:\s*(\d+)\s*-->");
  Assert.True(match.Success,
  "docs/STATUS.md must declare <!-- toolCount: N --> so this ratchet has a single source of truth.");
  var declared = int.Parse(match.Groups[1].Value);

  var toolRegistryPath = Path.Combine(
  RepoRoot, "src", "Lifeblood.Server.Mcp", "ToolRegistry.cs");
  Assert.True(File.Exists(toolRegistryPath), $"ToolRegistry.cs not found: {toolRegistryPath}");

  var registry = File.ReadAllText(toolRegistryPath);
  var live = Regex.Matches(registry, @"Name\s*=\s*""lifeblood_[a-z_]+""").Count;

  Assert.True(declared == live,
  $"docs/STATUS.md declares toolCount={declared} but ToolRegistry.cs has {live} lifeblood_* tool literals.");
  }

  [Fact]
  public void Changelog_EveryHeadingHasLinkReference()
  {
  // INV-CHANGELOG-001
  // Parse the changelog for every ## [X.Y.Z] heading and every [X.Y.Z]: URL
  // reference. Assert bijection. Missing references are how the v0.6.0 release
  // shipped with stale bottom-of-file links.
  var changelogPath = Path.Combine(RepoRoot, "CHANGELOG.md");
  var changelog = File.ReadAllText(changelogPath);

  var headings = Regex.Matches(changelog, @"^##\s*\[([^\]]+)\]", RegexOptions.Multiline)
  .Select(m => m.Groups[1].Value)
  .Where(v => v != "Unreleased")
  .ToHashSet(StringComparer.Ordinal);

  var references = Regex.Matches(changelog, @"^\[([^\]]+)\]:\s*http", RegexOptions.Multiline)
  .Select(m => m.Groups[1].Value)
  .Where(v => v != "Unreleased")
  .ToHashSet(StringComparer.Ordinal);

  var headingsWithoutRef = headings.Except(references).ToArray();
  var referencesWithoutHeading = references.Except(headings).ToArray();

  Assert.True(headingsWithoutRef.Length == 0,
  "CHANGELOG.md headings without a matching [X.Y.Z]: link reference: " +
  string.Join(", ", headingsWithoutRef));
  Assert.True(referencesWithoutHeading.Length == 0,
  "CHANGELOG.md link references without a matching ## [X.Y.Z] heading: " +
  string.Join(", ", referencesWithoutHeading));
  }

  [Fact]
  public void StatusDoc_VisibleToolCount_MatchesHiddenAnchor()
  {
  // INV-DOCS-002. Visible "N MCP tools" / "N tools" prose in docs/STATUS.md
  // must match the hidden `<!-- toolCount: N -->` anchor. Catches the
  // drift class that shipped 28→29 transitions to 6 doc surfaces while
  // the live-truth ratchet stayed silent (it only reads the HTML anchor).
  var statusPath = Path.Combine(RepoRoot, "docs", "STATUS.md");
  var status = File.ReadAllText(statusPath);

  var anchor = Regex.Match(status, @"<!--\s*toolCount:\s*(\d+)\s*-->");
  Assert.True(anchor.Success, "docs/STATUS.md must declare <!-- toolCount: N --> anchor.");
  var declared = int.Parse(anchor.Groups[1].Value);

  // Match any "<number> MCP tools" / "<number> tools" / "<number>R + <number>W"
  // prose in the body. The visible-count regex deliberately captures the
  // FIRST numeric token preceding "tools" / "MCP tools" so a future
  // re-phrasing doesn't silently bypass the ratchet.
  // Negative-lookbehind on `.` / word-char so version strings like
  // "v0.7.3 tools land" don't false-positive against "3 tools".
  var visibleMatches = Regex.Matches(status, @"(?<![.\w])(\d+)\s+(?:MCP\s+)?tools?\b");
  Assert.NotEmpty(visibleMatches);

  foreach (Match m in visibleMatches)
  {
  var visible = int.Parse(m.Groups[1].Value);
  Assert.True(visible == declared,
  $"docs/STATUS.md has visible \"{m.Value}\" but hidden anchor declares toolCount={declared}. " +
  "Update the prose to match, or update the anchor.");
  }
  }

  [Fact]
  public void StatusDoc_VisibleReadWriteSplit_MatchesRegistryAvailability()
  {
  // INV-DOCS-003. Visible "(17 read + 12 write)" / "(17R + 12W)" prose in
  // docs/STATUS.md must match the live ReadSide / WriteSide split in
  // ToolRegistry.cs. Mirrors the visible-tool-count ratchet for the
  // read/write breakdown — a second drift class the original ratchet
  // missed.
  var statusPath = Path.Combine(RepoRoot, "docs", "STATUS.md");
  var status = File.ReadAllText(statusPath);

  var registryPath = Path.Combine(RepoRoot, "src", "Lifeblood.Server.Mcp", "ToolRegistry.cs");
  var registry = File.ReadAllText(registryPath);
  var liveRead = Regex.Matches(registry, @"Availability\s*=\s*ToolAvailability\.ReadSide").Count;
  var liveWrite = Regex.Matches(registry, @"Availability\s*=\s*ToolAvailability\.WriteSide").Count;

  // Two phrase shapes appear in the doc: "(17 read + 12 write)" and "17R + 12W".
  var prose = Regex.Matches(status, @"\(\s*(\d+)\s+read(?:-side)?\s*\+\s*(\d+)\s+write(?:-side)?\s*\)");
  foreach (Match m in prose)
  {
  var r = int.Parse(m.Groups[1].Value);
  var w = int.Parse(m.Groups[2].Value);
  Assert.True(r == liveRead && w == liveWrite,
  $"docs/STATUS.md has visible \"{m.Value}\" but registry has {liveRead} read + {liveWrite} write.");
  }
  }

  private static string FindRepoRoot()
  {
  var dir = AppDomain.CurrentDomain.BaseDirectory;
  while (dir != null)
  {
  if (File.Exists(Path.Combine(dir, "Lifeblood.sln")))
  return dir;
  dir = Path.GetDirectoryName(dir);
  }
  throw new DirectoryNotFoundException("Could not locate Lifeblood.sln from test base directory.");
  }
}

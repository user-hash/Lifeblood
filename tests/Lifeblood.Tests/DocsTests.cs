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

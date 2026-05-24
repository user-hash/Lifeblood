using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Adapters.CSharp;
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
/// INV-DOCS-005. Docs/STATUS.md invariantCount anchor matches live audit.
/// INV-DOCS-006. Docs/STATUS.md invariantCategoryCount anchor matches live audit.
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
  public void StatusDoc_TestCount_MatchesDiscoveredCases()
  {
  // INV-DOCS-004. Single source of truth: <!-- testCount: N --> comment.
  // Live truth: count of xUnit-discovered test cases across the
  // Lifeblood.Tests assembly. Each [Fact]-derived method counts once;
  // each [Theory] method expands by data source — [InlineData] rows
  // counted directly; [MemberData] with a static IEnumerable<object[]>
  // source counted by enumeration; [ClassData] counted by instantiating
  // the source. Sibling to portCount / toolCount ratchets above.
  //
  // The ratchet matches xUnit's runtime test-case expansion (the
  // count `dotnet test --list-tests` reports). Pre-2026-05-24 the
  // ratchet only counted [InlineData] rows, falling back to 1 for
  // [MemberData]/[ClassData] Theories — which silently undercounted
  // user-visible suite size by the number of MemberData rows. The
  // expansion below resolves the data-source method via reflection.
  var statusPath = Path.Combine(RepoRoot, "docs", "STATUS.md");
  var status = File.ReadAllText(statusPath);

  var match = Regex.Match(status, @"<!--\s*testCount:\s*(\d+)\s*-->");
  Assert.True(match.Success,
  "docs/STATUS.md must declare <!-- testCount: N --> so this ratchet has a single source of truth.");
  var declared = int.Parse(match.Groups[1].Value);

  var assembly = typeof(DocsTests).Assembly;
  var factAttr = typeof(FactAttribute);
  var theoryAttr = typeof(TheoryAttribute);
  var inlineAttr = typeof(InlineDataAttribute);
  var memberDataAttr = typeof(MemberDataAttribute);
  var classDataAttr = typeof(ClassDataAttribute);

  var live = 0;
  foreach (var t in assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract))
  {
  foreach (var m in t.GetMethods(
  BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
  {
  if (m.GetCustomAttributes(factAttr, inherit: false).Length == 0) continue;
  var isTheory = m.GetCustomAttributes(theoryAttr, inherit: false).Length > 0;
  if (!isTheory) { live++; continue; }

  var inlineRows = m.GetCustomAttributes(inlineAttr, inherit: false).Length;
  if (inlineRows > 0) { live += inlineRows; continue; }

  // [MemberData("MemberName")] — resolve the referenced static member
  // and enumerate it. Source member can be a property OR method,
  // returning IEnumerable<object[]>.
  var memberDataAttrs = m.GetCustomAttributes(memberDataAttr, inherit: false);
  if (memberDataAttrs.Length > 0)
  {
  var rows = 0;
  foreach (var attr in memberDataAttrs.Cast<MemberDataAttribute>())
  {
  rows += CountMemberDataRows(t, attr.MemberName);
  }
  // If we couldn't resolve any rows (private / instance / dynamic),
  // fall back to 1 — same as the pre-fix behavior for that edge.
  live += Math.Max(1, rows);
  continue;
  }

  // [ClassData(typeof(SourceType))] — instantiate and enumerate.
  var classDataAttrs = m.GetCustomAttributes(classDataAttr, inherit: false);
  if (classDataAttrs.Length > 0)
  {
  var rows = 0;
  foreach (var attr in classDataAttrs.Cast<ClassDataAttribute>())
  {
  rows += CountClassDataRows(attr.Class);
  }
  live += Math.Max(1, rows);
  continue;
  }

  // Theory with no recognised data source (custom data-attribute
  // sub-class, dynamic source) — preserve fallback-to-1 so the
  // ratchet doesn't crash on novel xUnit extensions.
  live++;
  }
  }

  Assert.True(declared == live,
  $"docs/STATUS.md declares testCount={declared} but Lifeblood.Tests discovery yields {live} " +
  "test cases ([Fact] + [InlineData] rows + [MemberData]/[ClassData] expansion of [Theory]). " +
  "Update the HTML comment in STATUS.md to the live count, or restore the test that caused the drift.");
  }

  private static int CountMemberDataRows(Type containingType, string memberName)
  {
  // Static property or method, parameterless, returns IEnumerable<object[]>.
  const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic;
  var prop = containingType.GetProperty(memberName, flags);
  System.Collections.IEnumerable? source = null;
  if (prop != null && prop.GetGetMethod(nonPublic: true) != null)
  source = prop.GetValue(null) as System.Collections.IEnumerable;
  else
  {
  var method = containingType.GetMethod(memberName, flags, binder: null, types: Type.EmptyTypes, modifiers: null);
  if (method != null)
  source = method.Invoke(null, null) as System.Collections.IEnumerable;
  }
  if (source == null) return 0;
  var count = 0;
  foreach (var _ in source) count++;
  return count;
  }

  private static int CountClassDataRows(Type sourceType)
  {
  var instance = Activator.CreateInstance(sourceType) as System.Collections.IEnumerable;
  if (instance == null) return 0;
  var count = 0;
  foreach (var _ in instance) count++;
  return count;
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

  [Fact]
  public void StatusDoc_SkippableFactCount_MatchesLiveDiscovery()
  {
  // INV-DOCS-007. Single source of truth: <!-- skippableFactCount: N -->.
  // Live truth: count of test methods carrying SkippableFactAttribute.
  // Mechanical declared-count check (env-independent). The runtime-skip
  // count varies with gates like LIFEBLOOD_REQUIRE_NATIVE_CLANG, so the
  // ratchet anchors the DECLARED surface, not the runtime outcome.
  // Closes the drift class where "zero skipped" prose silently outlived
  // the addition of runtime-gated tests.
  var statusPath = Path.Combine(RepoRoot, "docs", "STATUS.md");
  var status = File.ReadAllText(statusPath);

  var match = Regex.Match(status, @"<!--\s*skippableFactCount:\s*(\d+)\s*-->");
  Assert.True(match.Success,
  "docs/STATUS.md must declare <!-- skippableFactCount: N --> so this ratchet has a single source of truth.");
  var declared = int.Parse(match.Groups[1].Value);

  var assembly = typeof(DocsTests).Assembly;
  // SkippableFactAttribute lives in the Xunit.SkippableFact NuGet package,
  // a reference of Lifeblood.Tests. Resolve by FullName from the assembly's
  // referenced assemblies so the test does not hard-code the package version.
  Type? skippableFactAttr = null;
  foreach (var name in assembly.GetReferencedAssemblies())
  {
  try
  {
  var refAsm = System.Reflection.Assembly.Load(name);
  skippableFactAttr = refAsm.GetType("Xunit.SkippableFactAttribute");
  if (skippableFactAttr != null) break;
  }
  catch { /* unloadable refs are not skippable-fact carriers */ }
  }
  Assert.True(skippableFactAttr != null,
  "Xunit.SkippableFactAttribute not found in referenced assemblies — the Xunit.SkippableFact package reference is load-bearing for this ratchet.");

  var live = 0;
  foreach (var t in assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract))
  {
  foreach (var m in t.GetMethods(
  BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
  {
  if (m.GetCustomAttributes(skippableFactAttr!, inherit: false).Length > 0) live++;
  }
  }

  Assert.True(declared == live,
  $"docs/STATUS.md declares skippableFactCount={declared} but Lifeblood.Tests carries {live} " +
  "[SkippableFact] methods. Update the HTML comment in STATUS.md to the live count, or " +
  "convert the gated method back to plain [Fact].");
  }

  [Fact]
  public void StatusDoc_InvariantCount_MatchesLiveAudit()
  {
  // INV-DOCS-005. Single source of truth: <!-- invariantCount: N --> comment.
  // Live truth: LifebloodInvariantProvider.Audit(repoRoot).TotalCount.
  // Sibling to portCount / toolCount / testCount ratchets above.
  var statusPath = Path.Combine(RepoRoot, "docs", "STATUS.md");
  var status = File.ReadAllText(statusPath);

  var match = Regex.Match(status, @"<!--\s*invariantCount:\s*(\d+)\s*-->");
  Assert.True(match.Success,
  "docs/STATUS.md must declare <!-- invariantCount: N --> so this ratchet has a single source of truth.");
  var declared = int.Parse(match.Groups[1].Value);

  var provider = new LifebloodInvariantProvider(new PhysicalFileSystem());
  var audit = provider.Audit(RepoRoot);
  var live = audit.TotalCount;

  Assert.True(declared == live,
  $"docs/STATUS.md declares invariantCount={declared} but live audit reports {live} invariants. " +
  "Update the HTML comment in STATUS.md to the live count, or restore/remove the invariant " +
  "in CLAUDE.md / docs/invariants/**.md that caused the drift.");
  }

  [Fact]
  public void StatusDoc_InvariantCategoryCount_MatchesLiveAudit()
  {
  // INV-DOCS-006. Single source of truth: <!-- invariantCategoryCount: N -->.
  // Live truth: LifebloodInvariantProvider.Audit(repoRoot).CategoryCounts.Length.
  // Catches the drift class where total count stays stable but category set
  // shifts (e.g. INV-FOO-001 renamed to INV-BAR-001).
  var statusPath = Path.Combine(RepoRoot, "docs", "STATUS.md");
  var status = File.ReadAllText(statusPath);

  var match = Regex.Match(status, @"<!--\s*invariantCategoryCount:\s*(\d+)\s*-->");
  Assert.True(match.Success,
  "docs/STATUS.md must declare <!-- invariantCategoryCount: N --> so this ratchet has a single source of truth.");
  var declared = int.Parse(match.Groups[1].Value);

  var provider = new LifebloodInvariantProvider(new PhysicalFileSystem());
  var audit = provider.Audit(RepoRoot);
  var live = audit.CategoryCounts.Length;

  Assert.True(declared == live,
  $"docs/STATUS.md declares invariantCategoryCount={declared} but live audit reports {live} categories. " +
  "Update the HTML comment in STATUS.md to the live count, or restore the invariant whose category disappeared.");
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

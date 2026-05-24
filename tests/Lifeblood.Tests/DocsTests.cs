using System.Reflection;
using System.Text.RegularExpressions;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Connectors.Mcp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Ratchet tests that keep docs honest. Doc numbers drift silently if nobody
/// enforces them. Every number these tests cover derives from a single source
/// of truth in docs/STATUS.md (HTML comments like <!-- portCount: 17 -->), and
/// the tests assert the comment matches the live repository state.
///
/// Architecture: ONE table — <see cref="Anchors"/> — declares every
/// (anchorName, liveSourceFn, optional visiblePattern) tuple. Two table-driven
/// Theories enforce the SSoT:
///   1. <see cref="Anchor_MatchesLiveSource"/> — STATUS.md anchor == live source.
///   2. <see cref="StatusVisible_MatchesAnchor"/> — STATUS.md's own visible prose
///      matches its own anchor (self-consistency).
///   3. <see cref="NonStatusSurface_VisibleCitationMatchesAnchor"/> — if a
///      non-STATUS surface (README / ARCHITECTURE.md / architecture.html /
///      TOOLS.md / MCP_SETUP.md / UNITY.md) cites a numeric count matching an
///      anchor's pattern, it MUST equal the anchor. Strip-and-link is preferred
///      over copy-and-sync; the ratchet remains as a safety net for regressions.
///
/// Adding a new ratcheted count = adding ONE row to <see cref="Anchors"/>.
/// No new test methods, no per-key copy-paste.
///
/// Invariants enforced:
/// INV-DOCS-001. Docs/STATUS.md port/test/tool counts match the repository.
/// INV-DOCS-002. Docs/STATUS.md visible prose matches its own anchors.
/// INV-DOCS-003. Visible read/write tool split matches ToolRegistry availability.
/// INV-DOCS-004. testCount anchor matches xUnit runtime test-case expansion.
/// INV-DOCS-005. invariantCount anchor matches live LifebloodInvariantProvider audit.
/// INV-DOCS-006. invariantCategoryCount anchor matches live audit category count.
/// INV-DOCS-007. skippableFactCount anchor matches declared [SkippableFact] count.
/// INV-DOCS-008. selfAnalyze* anchors match live RoslynWorkspaceAnalyzer self-analyze.
/// INV-DOCS-009. staticTablesDefault* anchors track RoslynStaticTableExtractor constants.
/// INV-DOCS-SURFACE-PARITY-001. Every non-STATUS surface that cites a SSoT count agrees.
/// INV-CHANGELOG-001. Every ## [X.Y.Z] heading has a matching link reference.
/// </summary>
public class DocsTests
{
  private static readonly string RepoRoot = FindRepoRoot();

  /// <summary>
  /// INV-DOCS-008. Lifeblood self-analysis snapshot shared across selfAnalyze*
  /// anchor rows. One analyze per test session; rows read the tuple by field.
  /// </summary>
  private static readonly Lazy<(int Symbols, int Edges, int Modules, int Types)> SelfAnalyzeSnapshot = new(() =>
  {
    var fs = new PhysicalFileSystem();
    var analyzer = new RoslynWorkspaceAnalyzer(fs);
    var graph = analyzer.AnalyzeWorkspace(RepoRoot, new AnalysisConfig());
    var analysis = Lifeblood.Analysis.AnalysisPipeline.Run(graph, rules: null);
    return (graph.Symbols.Count, graph.Edges.Count, analysis.Metrics.TotalModules, analysis.Metrics.TotalTypes);
  });

  // SSoT table for every ratcheted count. Add a new key = add one row.
  //   Name           STATUS.md `<!-- name: N -->` anchor
  //   Live()         source-of-truth lookup (SelfAnalyzeSnapshot for analyzer counts)
  //   FailHint       in assertion failure messages
  //   VisiblePattern enforces human-readable citation parity (STATUS visible + every non-STATUS surface)
  //   VisibleMin/Max outlier filter (version strings, year numbers, historical narrative)

  internal sealed record DocsAnchor(
    string Name,
    Func<int> Live,
    string FailHint,
    string? VisiblePattern = null,
    int VisibleMin = 5,
    int VisibleMax = 5000);

  private static readonly DocsAnchor[] Anchors = new DocsAnchor[]
  {
    new(
      Name: "portCount",
      Live: LivePortCount,
      FailHint: "src/Lifeblood.Application/Ports `public interface I*` declarations",
      VisiblePattern: @"(?<![.\w])(\d+)\s*(?:/\s*\d+)?\s+port[s]?(?:\s+interfaces?)?\b",
      VisibleMin: 5, VisibleMax: 200),
    new(
      Name: "toolCount",
      Live: LiveToolCount,
      FailHint: "Name = \"lifeblood_*\" literals in ToolRegistry.cs",
      VisiblePattern: @"(?<![.\w])(\d+)\s+(?:MCP\s+)?[Tt]ools?\b",
      VisibleMin: 5, VisibleMax: 200),
    new(
      Name: "testCount",
      Live: LiveTestCount,
      FailHint: "xUnit runtime test-case expansion (Fact + Theory/InlineData/MemberData/ClassData)"),
    new(
      Name: "invariantCount",
      Live: LiveInvariantCount,
      FailHint: "live LifebloodInvariantProvider.Audit().TotalCount",
      // Mandatory typed/queryable qualifier — bare "N invariants" matches
      // historical dogfood snapshots and would false-positive.
      VisiblePattern: @"(?<![.\w])(\d+)\s+(?:typed\s+(?:architectural\s+)?|queryable\s+)invariants?\b",
      VisibleMin: 10, VisibleMax: 1000),
    new(
      Name: "invariantCategoryCount",
      Live: LiveInvariantCategoryCount,
      FailHint: "live LifebloodInvariantProvider.Audit().CategoryCounts.Length"),
    new(
      Name: "skippableFactCount",
      Live: LiveSkippableFactCount,
      FailHint: "[SkippableFact] methods discovered via reflection in Lifeblood.Tests"),
    new(
      Name: "selfAnalyzeSymbols",
      Live: () => SelfAnalyzeSnapshot.Value.Symbols,
      FailHint: "live RoslynWorkspaceAnalyzer self-analyze graph.Symbols.Count"),
    new(
      Name: "selfAnalyzeEdges",
      Live: () => SelfAnalyzeSnapshot.Value.Edges,
      FailHint: "live RoslynWorkspaceAnalyzer self-analyze graph.Edges.Count"),
    new(
      Name: "selfAnalyzeModules",
      Live: () => SelfAnalyzeSnapshot.Value.Modules,
      FailHint: "live self-analyze AnalysisResult.Metrics.TotalModules"),
    new(
      Name: "selfAnalyzeTypes",
      Live: () => SelfAnalyzeSnapshot.Value.Types,
      FailHint: "live self-analyze AnalysisResult.Metrics.TotalTypes"),
    new(
      Name: "staticTablesDefaultMaxRows",
      Live: () => RoslynStaticTableExtractor.DefaultMaxRows,
      FailHint: "RoslynStaticTableExtractor.DefaultMaxRows constant"),
    new(
      Name: "staticTablesDefaultMaxTables",
      Live: () => RoslynStaticTableExtractor.DefaultMaxTables,
      FailHint: "RoslynStaticTableExtractor.DefaultMaxTables constant"),
  };

  public static IEnumerable<object[]> AnchorNames =>
    Anchors.Select(a => new object[] { a.Name });

  /// <summary>
  /// Non-STATUS reviewer-facing surfaces. STATUS.md is the SoT; every other
  /// surface either points at STATUS.md (preferred) or carries a copy that
  /// must agree. The list is the eternal regression guard.
  /// </summary>
  public static IEnumerable<object[]> NonStatusSurfaces => new[]
  {
    new object[] { "README.md" },
    new object[] { Path.Combine("docs", "ARCHITECTURE.md") },
    new object[] { Path.Combine("docs", "architecture.html") },
    new object[] { Path.Combine("docs", "TOOLS.md") },
    new object[] { Path.Combine("docs", "MCP_SETUP.md") },
    new object[] { Path.Combine("docs", "UNITY.md") },
  };

  public static IEnumerable<object[]> SurfaceAnchorPairs =>
    from surfaceRow in NonStatusSurfaces
    from anchor in Anchors
    where anchor.VisiblePattern != null
    select new object[] { (string)surfaceRow[0], anchor.Name };

  // Three table-driven Theories cover the SSoT contract.

  /// <summary>
  /// INV-DOCS-001/-004/-005/-006/-007/-008/-009. STATUS.md anchor equals the
  /// row's <c>Live</c> delegate.
  /// </summary>
  [Theory]
  [MemberData(nameof(AnchorNames))]
  public void Anchor_MatchesLiveSource(string anchorName)
  {
    var entry = Anchors.Single(a => a.Name == anchorName);
    var declared = ReadStatusAnchor(anchorName);
    var live = entry.Live();
    Assert.True(declared == live,
      $"docs/STATUS.md declares {anchorName}={declared} but live source ({entry.FailHint}) reports {live}. " +
      "Update the HTML comment in STATUS.md to the live count, or restore/remove the source artefact that caused the drift.");
  }

  /// <summary>
  /// INV-DOCS-002. STATUS.md visible prose matches its own anchors —
  /// self-consistency check internal to STATUS.md.
  /// </summary>
  [Theory]
  [MemberData(nameof(AnchorNames))]
  public void StatusVisible_MatchesAnchor(string anchorName)
  {
    var entry = Anchors.Single(a => a.Name == anchorName);
    if (entry.VisiblePattern == null) return;

    var statusPath = Path.Combine(RepoRoot, "docs", "STATUS.md");
    var status = File.ReadAllText(statusPath);
    var declared = ReadStatusAnchor(anchorName);

    foreach (var match in Regex.Matches(status, entry.VisiblePattern).Cast<Match>())
    {
      var visible = int.Parse(match.Groups[1].Value);
      if (visible == declared) continue;
      if (visible < entry.VisibleMin || visible > entry.VisibleMax) continue;
      Assert.Fail(
        $"docs/STATUS.md visible \"{match.Value}\" does not match its own anchor {anchorName}={declared}. " +
        "Update the visible prose, or the hidden anchor — they MUST agree inside STATUS.md.");
    }
  }

  /// <summary>
  /// INV-DOCS-SURFACE-PARITY-001. Non-STATUS surface that cites a numeric
  /// count matching an anchor's visible pattern MUST equal the anchor.
  /// Stripped citations match nothing and pass silently — preferred shape.
  /// </summary>
  [Theory]
  [MemberData(nameof(SurfaceAnchorPairs))]
  public void NonStatusSurface_VisibleCitationMatchesAnchor(string surfaceRelPath, string anchorName)
  {
    var entry = Anchors.Single(a => a.Name == anchorName);
    if (entry.VisiblePattern == null) return;

    var path = Path.Combine(RepoRoot, surfaceRelPath);
    if (!File.Exists(path)) return; // surface optional — UNITY.md may not exist on every branch

    var declared = ReadStatusAnchor(anchorName);
    var content = File.ReadAllText(path);
    foreach (var match in Regex.Matches(content, entry.VisiblePattern).Cast<Match>())
    {
      var visible = int.Parse(match.Groups[1].Value);
      if (visible == declared) continue;
      if (visible < entry.VisibleMin || visible > entry.VisibleMax) continue;
      Assert.Fail(
        $"{surfaceRelPath}: visible \"{match.Value}\" does not match STATUS.md anchor {anchorName}={declared}. " +
        "Update the prose, the anchor, or (preferred) strip the count and link to docs/STATUS.md — STATUS.md is the single source of truth.");
    }
  }

  // Specialized ratchets — non-integer-equality contracts.

  /// <summary>INV-CHANGELOG-001. Every ## [X.Y.Z] heading has a matching link reference.</summary>
  [Fact]
  public void Changelog_EveryHeadingHasLinkReference()
  {
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
      "CHANGELOG.md headings without a matching [X.Y.Z]: link reference: " + string.Join(", ", headingsWithoutRef));
    Assert.True(referencesWithoutHeading.Length == 0,
      "CHANGELOG.md link references without a matching ## [X.Y.Z] heading: " + string.Join(", ", referencesWithoutHeading));
  }

  /// <summary>
  /// INV-DOCS-003. STATUS.md "(N read + M write)" / "(NR + MW)" prose
  /// matches ToolRegistry ReadSide/WriteSide split. Two-number contract,
  /// outside the single-integer anchor table.
  /// </summary>
  [Fact]
  public void StatusDoc_VisibleReadWriteSplit_MatchesRegistryAvailability()
  {
    var statusPath = Path.Combine(RepoRoot, "docs", "STATUS.md");
    var status = File.ReadAllText(statusPath);

    var registryPath = Path.Combine(RepoRoot, "src", "Lifeblood.Server.Mcp", "ToolRegistry.cs");
    var registry = File.ReadAllText(registryPath);
    var liveRead = Regex.Matches(registry, @"Availability\s*=\s*ToolAvailability\.ReadSide").Count;
    var liveWrite = Regex.Matches(registry, @"Availability\s*=\s*ToolAvailability\.WriteSide").Count;

    var prose = Regex.Matches(status, @"\(\s*(\d+)\s+read(?:-side)?\s*\+\s*(\d+)\s+write(?:-side)?\s*\)");
    foreach (Match m in prose)
    {
      var r = int.Parse(m.Groups[1].Value);
      var w = int.Parse(m.Groups[2].Value);
      Assert.True(r == liveRead && w == liveWrite,
        $"docs/STATUS.md has visible \"{m.Value}\" but registry has {liveRead} read + {liveWrite} write.");
    }
  }

  // Live-source helpers — one per anchor.

  private static int LivePortCount()
  {
    var portsDir = Path.Combine(RepoRoot, "src", "Lifeblood.Application", "Ports");
    Assert.True(Directory.Exists(portsDir), $"Ports directory not found: {portsDir}");

    var count = 0;
    foreach (var file in Directory.EnumerateFiles(portsDir, "*.cs", SearchOption.AllDirectories))
    {
      var content = File.ReadAllText(file);
      count += Regex.Matches(content, @"\bpublic\s+interface\s+I[A-Z][A-Za-z0-9_]*\b").Count;
    }
    return count;
  }

  private static int LiveToolCount()
  {
    var toolRegistryPath = Path.Combine(RepoRoot, "src", "Lifeblood.Server.Mcp", "ToolRegistry.cs");
    Assert.True(File.Exists(toolRegistryPath), $"ToolRegistry.cs not found: {toolRegistryPath}");
    var registry = File.ReadAllText(toolRegistryPath);
    return Regex.Matches(registry, @"Name\s*=\s*""lifeblood_[a-z_]+""").Count;
  }

  private static int LiveInvariantCount()
  {
    var provider = new LifebloodInvariantProvider(new PhysicalFileSystem());
    return provider.Audit(RepoRoot).TotalCount;
  }

  private static int LiveInvariantCategoryCount()
  {
    var provider = new LifebloodInvariantProvider(new PhysicalFileSystem());
    return provider.Audit(RepoRoot).CategoryCounts.Length;
  }

  private static int LiveTestCount()
  {
    var assembly = typeof(DocsTests).Assembly;
    var factAttr = typeof(FactAttribute);
    var theoryAttr = typeof(TheoryAttribute);
    var inlineAttr = typeof(InlineDataAttribute);
    var memberDataAttr = typeof(MemberDataAttribute);
    var classDataAttr = typeof(ClassDataAttribute);

    var count = 0;
    foreach (var t in assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract))
    {
      foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
      {
        if (m.GetCustomAttributes(factAttr, inherit: false).Length == 0) continue;
        var isTheory = m.GetCustomAttributes(theoryAttr, inherit: false).Length > 0;
        if (!isTheory) { count++; continue; }

        var inlineRows = m.GetCustomAttributes(inlineAttr, inherit: false).Length;
        if (inlineRows > 0) { count += inlineRows; continue; }

        var memberDataAttrs = m.GetCustomAttributes(memberDataAttr, inherit: false);
        if (memberDataAttrs.Length > 0)
        {
          var rows = 0;
          foreach (var attr in memberDataAttrs.Cast<MemberDataAttribute>())
            rows += CountMemberDataRows(t, attr.MemberName);
          count += Math.Max(1, rows);
          continue;
        }

        var classDataAttrs = m.GetCustomAttributes(classDataAttr, inherit: false);
        if (classDataAttrs.Length > 0)
        {
          var rows = 0;
          foreach (var attr in classDataAttrs.Cast<ClassDataAttribute>())
            rows += CountClassDataRows(attr.Class);
          count += Math.Max(1, rows);
          continue;
        }

        count++;
      }
    }
    return count;
  }

  private static int LiveSkippableFactCount()
  {
    var assembly = typeof(DocsTests).Assembly;
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

    var count = 0;
    foreach (var t in assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract))
    {
      foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
      {
        if (m.GetCustomAttributes(skippableFactAttr!, inherit: false).Length > 0) count++;
      }
    }
    return count;
  }

  private static int CountMemberDataRows(Type containingType, string memberName)
  {
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

  private static int ReadStatusAnchor(string name)
  {
    var statusPath = Path.Combine(RepoRoot, "docs", "STATUS.md");
    var status = File.ReadAllText(statusPath);
    var match = Regex.Match(status, $@"<!--\s*{Regex.Escape(name)}:\s*(\d+)\s*-->");
    Assert.True(match.Success, $"docs/STATUS.md must declare <!-- {name}: N -->.");
    return int.Parse(match.Groups[1].Value);
  }

  private static string FindRepoRoot()
  {
    var dir = AppDomain.CurrentDomain.BaseDirectory;
    while (dir != null)
    {
      if (File.Exists(Path.Combine(dir, "Lifeblood.sln"))) return dir;
      dir = Path.GetDirectoryName(dir);
    }
    throw new DirectoryNotFoundException("Could not locate Lifeblood.sln from test base directory.");
  }
}

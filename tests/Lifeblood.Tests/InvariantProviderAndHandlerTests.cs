using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Right.Invariants;
using Lifeblood.Connectors.Mcp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Integration coverage for the Phase 8 invariant pipeline:
///   - <see cref="LifebloodInvariantProvider"/> direct tests exercise
///     the provider against a real temp-dir <c>CLAUDE.md</c>, proving
///     the end-to-end filesystem → parser → cache → port-shape path
///     works without touching the MCP layer.
///   - <see cref="Lifeblood.Server.Mcp.ToolHandler"/> dispatch tests
///     exercise the <c>lifeblood_invariant_check</c> handler's
///     argument parsing, error envelopes, and mode routing.
///   - The final end-to-end test analyzes a minimal Roslyn workspace
///     that ALSO carries a <c>CLAUDE.md</c> with two invariants and
///     proves the real <c>lifeblood_analyze → lifeblood_invariant_check</c>
///     chain surfaces them via the MCP tool response.
///
/// This file pins the architectural contract of Phase 8: the tool
/// reads CLAUDE.md from the loaded project root, parses it without
/// extra configuration, and returns structured data on every query.
/// </summary>
public class InvariantProviderAndHandlerTests : IDisposable
{
    private static readonly PhysicalFileSystem Fs = new();

    private readonly string _tempDir;

    public InvariantProviderAndHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-inv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    // ─────────────────────────────────────────────────────────────────────
    // LifebloodInvariantProvider direct tests.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Provider_GetAll_NoClaudeMd_ReturnsEmpty()
    {
        // No CLAUDE.md in the temp dir — the provider must return an
        // empty list rather than throwing or returning null.
        var provider = new LifebloodInvariantProvider(Fs);

        var all = provider.GetAll(_tempDir);

        Assert.Empty(all);
    }

    [Fact]
    public void Provider_GetAll_SimpleClaudeMd_ReturnsExpectedInvariants()
    {
        File.WriteAllText(
            Path.Combine(_tempDir, "CLAUDE.md"),
            "# Test project\n\n" +
            "## Invariants\n\n" +
            "- **INV-TEST-001**: Tests must be reproducible.\n" +
            "- **INV-TEST-002**: Tests must run without network access.\n");

        var provider = new LifebloodInvariantProvider(Fs);
        var all = provider.GetAll(_tempDir);

        Assert.Equal(2, all.Length);
        Assert.Equal("INV-TEST-001", all[0].Id);
        Assert.Equal("TEST", all[0].Category);
        Assert.Equal("INV-TEST-002", all[1].Id);
    }

    [Fact]
    public void Provider_GetById_ExistingId_ReturnsInvariant()
    {
        File.WriteAllText(
            Path.Combine(_tempDir, "CLAUDE.md"),
            "- **INV-FOO-001. The foo invariant title.** Foo body text here.\n");

        var provider = new LifebloodInvariantProvider(Fs);
        var inv = provider.GetById(_tempDir, "INV-FOO-001");

        Assert.NotNull(inv);
        Assert.Equal("INV-FOO-001", inv!.Id);
        Assert.Equal("The foo invariant title", inv.Title);
        Assert.Equal("FOO", inv.Category);
    }

    [Fact]
    public void Provider_GetById_MissingId_ReturnsNull()
    {
        File.WriteAllText(
            Path.Combine(_tempDir, "CLAUDE.md"),
            "- **INV-FOO-001**: some body.\n");

        var provider = new LifebloodInvariantProvider(Fs);

        Assert.Null(provider.GetById(_tempDir, "INV-BAR-999"));
    }

    [Fact]
    public void Provider_Audit_ComputesCategoryCountsAndDuplicates()
    {
        File.WriteAllText(
            Path.Combine(_tempDir, "CLAUDE.md"),
            "- **INV-FOO-001**: foo one.\n" +
            "- **INV-FOO-002**: foo two.\n" +
            "- **INV-BAR-001**: bar one.\n" +
            "\n## heading\n\n" +
            "- **INV-FOO-001**: duplicate of the first one.\n");

        var provider = new LifebloodInvariantProvider(Fs);
        var audit = provider.Audit(_tempDir);

        // Unique ids: INV-FOO-001, INV-FOO-002, INV-BAR-001 → 3
        Assert.Equal(3, audit.TotalCount);
        // Category counts sorted by count desc, then name asc: FOO=2, BAR=1
        Assert.Equal(2, audit.CategoryCounts.Length);
        Assert.Equal("FOO", audit.CategoryCounts[0].Category);
        Assert.Equal(2, audit.CategoryCounts[0].Count);
        Assert.Equal("BAR", audit.CategoryCounts[1].Category);
        Assert.Equal(1, audit.CategoryCounts[1].Count);
        // Duplicate reported
        var dup = Assert.Single(audit.Duplicates);
        Assert.Equal("INV-FOO-001", dup.Id);
        Assert.Equal(2, dup.SourceLines.Length);
    }

    [Fact]
    public void Provider_Caches_SecondCallDoesNotReparse()
    {
        File.WriteAllText(
            Path.Combine(_tempDir, "CLAUDE.md"),
            "- **INV-FOO-001**: one.\n");

        var provider = new LifebloodInvariantProvider(Fs);
        var first = provider.GetAll(_tempDir);

        // Mutate the file AND reset its timestamp to the original so
        // the cache's timestamp-based invalidation does not notice.
        // This is the only reliable way to prove the cache is actually
        // returning a cached result rather than reparsing every call.
        var claudeMdPath = Path.Combine(_tempDir, "CLAUDE.md");
        var originalTimestamp = File.GetLastWriteTimeUtc(claudeMdPath);
        File.WriteAllText(claudeMdPath, "- **INV-FOO-001**: one.\n- **INV-FOO-002**: two.\n");
        File.SetLastWriteTimeUtc(claudeMdPath, originalTimestamp);

        var second = provider.GetAll(_tempDir);

        Assert.Single(first);
        Assert.Single(second); // cached — reparse would return 2
    }

    [Fact]
    public void Provider_Invalidates_OnTimestampChange()
    {
        var claudeMdPath = Path.Combine(_tempDir, "CLAUDE.md");
        File.WriteAllText(claudeMdPath, "- **INV-FOO-001**: one.\n");

        var provider = new LifebloodInvariantProvider(Fs);
        _ = provider.GetAll(_tempDir); // warm the cache

        // Edit the file AND advance its timestamp so the cache invalidates.
        File.WriteAllText(claudeMdPath, "- **INV-FOO-001**: one.\n- **INV-FOO-002**: two.\n");
        File.SetLastWriteTimeUtc(claudeMdPath, File.GetLastWriteTimeUtc(claudeMdPath).AddSeconds(10));

        var after = provider.GetAll(_tempDir);

        Assert.Equal(2, after.Length);
    }

    // ─────────────────────────────────────────────────────────────────────
    // ToolHandler dispatch — proves argument parsing and error envelopes.
    // Uses the real LifebloodInvariantProvider and a real GraphSession.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Handle_InvariantCheck_WithoutWorkspace_ReturnsError()
    {
        var handler = CreateHandlerWithFreshSession();

        var result = handler.Handle("lifeblood_invariant_check", null);

        Assert.True(result.IsError);
        Assert.Contains("No workspace loaded", result.Content[0].Text);
    }

    [Fact]
    public void Handle_InvariantCheck_AfterAnalyze_DefaultModeIsAudit()
    {
        // Build a minimal Roslyn workspace with a CLAUDE.md that has
        // two invariants. Analyze it, then call invariant_check with
        // no arguments — default mode is audit.
        CreateMinimalRoslynWorkspaceWithClaudeMd();
        var handler = CreateHandlerWithFreshSession();

        var analyze = handler.Handle("lifeblood_analyze", MakeArgs(new { projectPath = _tempDir }));
        Assert.Null(analyze.IsError);

        var result = handler.Handle("lifeblood_invariant_check", null);

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        Assert.Contains("\"mode\": \"audit\"", text);
        Assert.Contains("\"totalCount\": 2", text);
        // Audit surfaces categories, not individual ids. FOO and BAR are
        // the two categories derived from the invariants' id prefixes.
        Assert.Contains("\"category\": \"FOO\"", text);
        Assert.Contains("\"category\": \"BAR\"", text);
    }

    [Fact]
    public void Handle_InvariantCheck_ModeList_ReturnsAllIds()
    {
        CreateMinimalRoslynWorkspaceWithClaudeMd();
        var handler = CreateHandlerWithFreshSession();
        handler.Handle("lifeblood_analyze", MakeArgs(new { projectPath = _tempDir }));

        var result = handler.Handle("lifeblood_invariant_check", MakeArgs(new { mode = "list" }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        Assert.Contains("\"mode\": \"list\"", text);
        Assert.Contains("\"count\": 2", text);
        Assert.Contains("INV-FOO-001", text);
        Assert.Contains("INV-BAR-001", text);
    }

    [Fact]
    public void Handle_InvariantCheck_ById_ReturnsFullBody()
    {
        CreateMinimalRoslynWorkspaceWithClaudeMd();
        var handler = CreateHandlerWithFreshSession();
        handler.Handle("lifeblood_analyze", MakeArgs(new { projectPath = _tempDir }));

        var result = handler.Handle("lifeblood_invariant_check", MakeArgs(new { id = "INV-FOO-001" }));

        Assert.Null(result.IsError);
        var text = result.Content[0].Text;
        Assert.Contains("INV-FOO-001", text);
        Assert.Contains("body text", text); // substring of the CLAUDE.md body
    }

    [Fact]
    public void Handle_InvariantCheck_ById_NotFound_ReturnsError()
    {
        CreateMinimalRoslynWorkspaceWithClaudeMd();
        var handler = CreateHandlerWithFreshSession();
        handler.Handle("lifeblood_analyze", MakeArgs(new { projectPath = _tempDir }));

        var result = handler.Handle("lifeblood_invariant_check", MakeArgs(new { id = "INV-DOES-NOT-EXIST-999" }));

        Assert.True(result.IsError);
        Assert.Contains("INV-DOES-NOT-EXIST-999", result.Content[0].Text);
        Assert.Contains("not found", result.Content[0].Text);
    }

    [Fact]
    public void Handle_InvariantCheck_BothIdAndMode_ReturnsError()
    {
        CreateMinimalRoslynWorkspaceWithClaudeMd();
        var handler = CreateHandlerWithFreshSession();
        handler.Handle("lifeblood_analyze", MakeArgs(new { projectPath = _tempDir }));

        var result = handler.Handle("lifeblood_invariant_check",
            MakeArgs(new { id = "INV-FOO-001", mode = "audit" }));

        Assert.True(result.IsError);
        Assert.Contains("exactly one", result.Content[0].Text);
    }

    [Fact]
    public void Handle_InvariantCheck_UnknownMode_ReturnsError()
    {
        CreateMinimalRoslynWorkspaceWithClaudeMd();
        var handler = CreateHandlerWithFreshSession();
        handler.Handle("lifeblood_analyze", MakeArgs(new { projectPath = _tempDir }));

        var result = handler.Handle("lifeblood_invariant_check", MakeArgs(new { mode = "not-a-real-mode" }));

        Assert.True(result.IsError);
        Assert.Contains("Unknown mode", result.Content[0].Text);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    private Lifeblood.Server.Mcp.ToolHandler CreateHandlerWithFreshSession()
    {
        var session = new Lifeblood.Server.Mcp.GraphSession(Fs);
        var provider = new LifebloodMcpProvider(new TestBlastRadius());
        var resolver = new LifebloodSymbolResolver();
        var search = new LifebloodSemanticSearchProvider();
        var deadCode = new LifebloodDeadCodeAnalyzer();
        var partialView = new LifebloodPartialViewBuilder(Fs);
        IInvariantProvider invariants = new LifebloodInvariantProvider(Fs);
        return new Lifeblood.Server.Mcp.ToolHandler(
            session, provider, resolver, search, deadCode, partialView, invariants);
    }

    private void CreateMinimalRoslynWorkspaceWithClaudeMd()
    {
        File.WriteAllText(Path.Combine(_tempDir, "CLAUDE.md"),
            "# Test project\n\n" +
            "## Invariants\n\n" +
            "- **INV-FOO-001**: first invariant body text.\n" +
            "- **INV-BAR-001**: second invariant body text.\n");

        File.WriteAllText(Path.Combine(_tempDir, "TestProject.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
            "  <PropertyGroup>\n" +
            "    <TargetFramework>net8.0</TargetFramework>\n" +
            "  </PropertyGroup>\n" +
            "</Project>\n");

        File.WriteAllText(Path.Combine(_tempDir, "Foo.cs"),
            "namespace TestNs { public class Foo { public void M() { } } }\n");
    }

    private static JsonElement? MakeArgs(object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private sealed class TestBlastRadius : Lifeblood.Application.Ports.Analysis.IBlastRadiusProvider
    {
        public Lifeblood.Domain.Results.BlastRadiusResult Analyze(
            Lifeblood.Domain.Graph.SemanticGraph graph, string symbolId, int maxDepth = 10)
            => Lifeblood.Analysis.BlastRadiusAnalyzer.Analyze(graph, symbolId, maxDepth);
    }
}

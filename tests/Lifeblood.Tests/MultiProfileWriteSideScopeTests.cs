using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Analysis;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-MULTI-DEFINE-WRITESIDE-001. The live Roslyn write-side tools
/// (find_references, find_definition, find_implementations, rename) walk the
/// retained (first) profile's compilations only. On a multi-profile snapshot
/// their responses MUST surface that limitation inline — `analyzedUnderProfile`
/// + a `limitations[]` entry naming the retained profile + the other-profile
/// gap. `profileScope` must fail loudly on mismatch instead of returning a
/// wrong-profile view.
///
/// Closes the contract gap external-review pass 2026-05-24 flagged: the Wave 6
/// CHANGELOG claim "L-LIM-001 closed end-to-end for find_references" was
/// overbroad because the tool's handler called the retained compilation host
/// directly with no profile awareness. This test pins the honesty discipline
/// post-fix.
/// </summary>
public sealed class MultiProfileWriteSideScopeTests : IDisposable
{
    private readonly string _tempDir;
    private static readonly PhysicalFileSystem Fs = new();

    public MultiProfileWriteSideScopeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lifeblood-mpws-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        WriteWorkspace();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void FindReferences_OnMultiProfileGraph_Carries_AnalyzedUnderProfile_And_Limitations()
    {
        var handler = CreateHandler();
        LoadMultiProfile(handler, new[] { "Editor", "Player" });

        var result = handler.Handle("lifeblood_find_references",
            MakeArgs(new { symbolId = "type:Mpws.Target" }));
        AssertNotError(result);

        var payload = ParseInner(result);
        Assert.Equal("Editor", payload.GetProperty("analyzedUnderProfile").GetString());
        var limitations = payload.GetProperty("limitations");
        Assert.True(limitations.GetArrayLength() > 0,
            "Multi-profile snapshot MUST surface a write-side-retained-profile limitations[] entry.");
        var msg = limitations[0].GetString() ?? "";
        Assert.Contains("multi-profile", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Editor", msg);
        Assert.Contains("Player", msg);
    }

    [Fact]
    public void FindReferences_ProfileScope_MismatchedValue_FailsLoudly()
    {
        var handler = CreateHandler();
        LoadMultiProfile(handler, new[] { "Editor", "Player" });

        var result = handler.Handle("lifeblood_find_references",
            MakeArgs(new { symbolId = "type:Mpws.Target", profileScope = "Player" }));

        Assert.Equal(true, result.IsError);
        var text = ExtractText(result);
        Assert.Contains("retained profile", text);
        Assert.Contains("Editor", text);
        Assert.Contains("Player", text);
    }

    [Fact]
    public void FindReferences_ProfileScope_MatchingValue_Succeeds()
    {
        var handler = CreateHandler();
        LoadMultiProfile(handler, new[] { "Editor", "Player" });

        var result = handler.Handle("lifeblood_find_references",
            MakeArgs(new { symbolId = "type:Mpws.Target", profileScope = "Editor" }));

        AssertNotError(result);
    }

    [Fact]
    public void FindReferences_OnSingleProfileGraph_HasEmptyLimitations()
    {
        var handler = CreateHandler();
        LoadMultiProfile(handler, new[] { "Editor" });

        var result = handler.Handle("lifeblood_find_references",
            MakeArgs(new { symbolId = "type:Mpws.Target" }));
        AssertNotError(result);

        var payload = ParseInner(result);
        Assert.Equal(0, payload.GetProperty("limitations").GetArrayLength());
    }

    [Fact]
    public void FindDefinition_OnMultiProfileGraph_CarriesAnalyzedUnderProfile()
    {
        var handler = CreateHandler();
        LoadMultiProfile(handler, new[] { "Editor", "Player" });

        var result = handler.Handle("lifeblood_find_definition",
            MakeArgs(new { symbolId = "type:Mpws.Target" }));
        AssertNotError(result);

        var payload = ParseInner(result);
        Assert.Equal("Editor", payload.GetProperty("analyzedUnderProfile").GetString());
        Assert.True(payload.GetProperty("limitations").GetArrayLength() > 0);
    }

    [Fact]
    public void FindImplementations_OnMultiProfileGraph_CarriesAnalyzedUnderProfile()
    {
        var handler = CreateHandler();
        LoadMultiProfile(handler, new[] { "Editor", "Player" });

        var result = handler.Handle("lifeblood_find_implementations",
            MakeArgs(new { symbolId = "type:Mpws.ITarget" }));
        AssertNotError(result);

        var payload = ParseInner(result);
        Assert.Equal("Editor", payload.GetProperty("analyzedUnderProfile").GetString());
        Assert.True(payload.GetProperty("limitations").GetArrayLength() > 0);
    }

    [Fact]
    public void Rename_OnMultiProfileGraph_CarriesAnalyzedUnderProfile()
    {
        var handler = CreateHandler();
        LoadMultiProfile(handler, new[] { "Editor", "Player" });

        var result = handler.Handle("lifeblood_rename",
            MakeArgs(new { symbolId = "type:Mpws.Target", newName = "TargetRenamed" }));
        AssertNotError(result);

        var payload = ParseInner(result);
        Assert.Equal("Editor", payload.GetProperty("analyzedUnderProfile").GetString());
        Assert.True(payload.GetProperty("limitations").GetArrayLength() > 0);
    }

    private void LoadMultiProfile(ToolHandler handler, string[] profiles)
    {
        var args = MakeArgs(new { projectPath = _tempDir, defineProfiles = profiles, readOnly = false });
        var result = handler.Handle("lifeblood_analyze", args);
        AssertNotError(result);
    }

    private void WriteWorkspace()
    {
        // UnityDefineProfileResolver requires Library/ to return 2-profile set.
        // The composition root in GraphSession.Load injects that resolver by
        // default — mirror the workspace shape it expects.
        Directory.CreateDirectory(Path.Combine(_tempDir, "Library"));
        File.WriteAllText(Path.Combine(_tempDir, "Target.cs"), """
            namespace Mpws {
              public interface ITarget { void Run(); }
              public sealed class Target : ITarget { public void Run() { } }
              public class Caller {
                public void Hit() { var t = new Target(); t.Run(); }
              }
            }
            """);
        File.WriteAllText(Path.Combine(_tempDir, "Mpws.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <AssemblyName>Mpws</AssemblyName>
              </PropertyGroup>
            </Project>
            """);
    }

    private static ToolHandler CreateHandler()
    {
        IMcpGraphProvider provider = new LifebloodMcpProvider(new TestBlastRadiusProvider());
        ISymbolResolver resolver = new LifebloodSymbolResolver();
        ISemanticSearchProvider search = new LifebloodSemanticSearchProvider();
        IDeadCodeAnalyzer deadCode = new LifebloodDeadCodeAnalyzer();
        IPartialViewBuilder partialView = new LifebloodPartialViewBuilder(Fs);
        Lifeblood.Application.Ports.Right.Invariants.IInvariantProvider invariants
            = new LifebloodInvariantProvider(Fs);
        var classifications = ToolRegistry.GetDefinitions()
            .Where(d => d.EnvelopeClassification != null)
            .ToDictionary(d => d.Name, d => d.EnvelopeClassification!, StringComparer.Ordinal);
        IResponseDecorator decorator = new LifebloodResponseDecorator(classifications);
        return new ToolHandler(new GraphSession(Fs), provider, resolver, search, deadCode, partialView, invariants, decorator);
    }

    private static void AssertNotError(McpToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        Assert.NotNull(result.Content);
        Assert.NotEmpty(result.Content!);
    }

    private static string ExtractText(McpToolResult result)
    {
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Content));
        return doc.RootElement[0].GetProperty("text").GetString() ?? "";
    }

    /// <summary>
    /// Read-side tools get a truth envelope wrapped around the inner payload
    /// (`{ data: {...}, envelope: {...} }`); write-side tools through
    /// WrapWriteSide get the same envelope shape too. ParseInner unwraps
    /// the `data` field when present so per-payload assertions hit the same
    /// object the handler serialized.
    /// </summary>
    private static JsonElement ParseInner(McpToolResult result)
    {
        var text = ExtractText(result);
        var root = JsonDocument.Parse(text).RootElement;
        return root.TryGetProperty("data", out var inner) ? inner : root;
    }

    private static JsonElement? MakeArgs(object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private sealed class TestBlastRadiusProvider : IBlastRadiusProvider
    {
        public BlastRadiusResult Analyze(SemanticGraph graph, string targetSymbolId, int maxDepth = 10)
            => BlastRadiusAnalyzer.Analyze(graph, targetSymbolId, maxDepth);
    }
}

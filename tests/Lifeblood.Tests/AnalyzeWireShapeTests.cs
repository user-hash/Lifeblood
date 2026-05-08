using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-ANALYZE-FALLBACK-001 wire-shape contract. Pins the JSON response
/// shape of <c>lifeblood_analyze</c> as observed by an MCP client through
/// <see cref="GraphSession.Load"/>. The contract this file enforces:
///
/// <list type="bullet">
///   <item><c>mode</c> reports what the adapter DID
///         (<c>full</c> / <c>incremental</c> / <c>incremental-noop</c> / <c>rejected</c>).</item>
///   <item><c>requestedMode</c> reports what the caller ASKED
///         (<c>full</c> / <c>incremental</c>). Disambiguates fallback from
///         original-intent-full without inventing a hybrid mode value.</item>
///   <item><c>fallbackReason</c> + <c>fallbackDetail</c> appear whenever the
///         cheap path could not be honored cleanly — both on <c>rejected</c>
///         and on widened <c>full</c> + <c>requestedMode:incremental</c>.</item>
///   <item>Rejection responses additionally carry <c>canRetryFull:true</c>
///         and a <c>suggestedRetry</c> object so the agent's next move is
///         self-documenting.</item>
///   <item>Rejection is a NORMAL structured result — not a transport / tool
///         error. The wire shape stays inside the same JSON envelope.</item>
/// </list>
/// </summary>
public class AnalyzeWireShapeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly PhysicalFileSystem _fs = new();

    public AnalyzeWireShapeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lifeblood-wire-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Load_FullAnalyze_WireCarriesModeFullAndRequestedModeFull()
    {
        WriteSingleFileProject("public class Foo { }");
        var session = new GraphSession(_fs);

        var json = session.Load(_tempDir, graphPath: null, rulesPath: null, incremental: false);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("full", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("full", doc.RootElement.GetProperty("requestedMode").GetString());
        AssertNullProperty(doc.RootElement, "fallbackReason");
        AssertNullProperty(doc.RootElement, "fallbackDetail");
        AssertNullProperty(doc.RootElement, "canRetryFull");
        AssertNullProperty(doc.RootElement, "suggestedRetry");
    }

    [Fact]
    public void Load_IncrementalNoEdits_WireCarriesIncrementalNoopAndRequestedIncremental()
    {
        WriteSingleFileProject("public class Foo { }");
        var session = new GraphSession(_fs);
        session.Load(_tempDir, graphPath: null, rulesPath: null, incremental: false);

        // Second call with incremental:true on an unchanged workspace must
        // hit the "incremental-noop" fast path. Default allowFullFallback
        // (false) is irrelevant here because no fallback is triggered.
        var json = session.Load(_tempDir, graphPath: null, rulesPath: null, incremental: true);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("incremental-noop", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("incremental", doc.RootElement.GetProperty("requestedMode").GetString());
        AssertNullProperty(doc.RootElement, "fallbackReason");
        AssertNullProperty(doc.RootElement, "canRetryFull");
    }

    [Fact]
    public void Load_IncrementalAfterEdit_WireCarriesIncrementalMode()
    {
        var filePath = WriteSingleFileProject("public class Foo { }");
        var session = new GraphSession(_fs);
        session.Load(_tempDir, graphPath: null, rulesPath: null, incremental: false);

        Thread.Sleep(50);
        File.WriteAllText(filePath, "public class Foo { public void Bar() { } }");

        var json = session.Load(_tempDir, graphPath: null, rulesPath: null, incremental: true);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("incremental", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("incremental", doc.RootElement.GetProperty("requestedMode").GetString());
        AssertNullProperty(doc.RootElement, "fallbackReason");
        Assert.True(doc.RootElement.GetProperty("changedSourceFiles").GetInt32() > 0);
    }

    [Fact]
    public void Load_IncrementalAfterModuleSetChanged_RejectedShape_AllowFullFallbackFalse()
    {
        // INV-ANALYZE-FALLBACK-001 rejection wire shape — the contract this
        // whole change exists to enforce. Two-module workspace, full analyze,
        // delete one module's csproj, retry incremental:true with default
        // (allowFullFallback:false). Wire shape MUST carry canRetryFull +
        // suggestedRetry so the agent can self-correct.
        WriteTwoModuleProject();
        var session = new GraphSession(_fs);
        session.Load(_tempDir, graphPath: null, rulesPath: null, incremental: false);

        File.Delete(Path.Combine(_tempDir, "ModuleB", "ModuleB.csproj"));
        File.Delete(Path.Combine(_tempDir, "ModuleB", "B.cs"));

        var json = session.Load(_tempDir, graphPath: null, rulesPath: null,
                                incremental: true, allowFullFallback: false);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("rejected", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("incremental", doc.RootElement.GetProperty("requestedMode").GetString());
        Assert.Equal("moduleSetChanged", doc.RootElement.GetProperty("fallbackReason").GetString());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("fallbackDetail").GetString()));
        Assert.True(doc.RootElement.GetProperty("canRetryFull").GetBoolean());

        var suggested = doc.RootElement.GetProperty("suggestedRetry");
        Assert.True(suggested.GetProperty("incremental").GetBoolean());
        Assert.True(suggested.GetProperty("allowFullFallback").GetBoolean());

        // Rejection means no work done — summary block is null.
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("summary").ValueKind);
    }

    [Fact]
    public void Load_IncrementalAfterModuleSetChanged_FullFallbackShape_AllowFullFallbackTrue()
    {
        // Same trigger, opposite policy. mode == "full" (the truth: the adapter
        // did a full re-analyze) + requestedMode == "incremental" (the truth:
        // the caller asked for cheap) + fallbackReason populated so the cache
        // miss stays visible. canRetryFull is null because the work succeeded.
        WriteTwoModuleProject();
        var session = new GraphSession(_fs);
        session.Load(_tempDir, graphPath: null, rulesPath: null, incremental: false);

        File.Delete(Path.Combine(_tempDir, "ModuleB", "ModuleB.csproj"));
        File.Delete(Path.Combine(_tempDir, "ModuleB", "B.cs"));

        var json = session.Load(_tempDir, graphPath: null, rulesPath: null,
                                incremental: true, allowFullFallback: true);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("full", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("incremental", doc.RootElement.GetProperty("requestedMode").GetString());
        Assert.Equal("moduleSetChanged", doc.RootElement.GetProperty("fallbackReason").GetString());
        AssertNullProperty(doc.RootElement, "canRetryFull");
        AssertNullProperty(doc.RootElement, "suggestedRetry");

        // Work succeeded — summary populated.
        Assert.NotEqual(JsonValueKind.Null, doc.RootElement.GetProperty("summary").ValueKind);
    }

    private static void AssertNullProperty(JsonElement element, string propertyName)
    {
        Assert.True(element.TryGetProperty(propertyName, out var prop),
            $"Expected property '{propertyName}' to exist on the response.");
        Assert.Equal(JsonValueKind.Null, prop.ValueKind);
    }

    // ── Helpers ──

    private string WriteSingleFileProject(string code)
    {
        var csproj = @"<Project Sdk=""Microsoft.NET.Sdk""><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>";
        File.WriteAllText(Path.Combine(_tempDir, "TestProject.csproj"), csproj);
        var csPath = Path.Combine(_tempDir, "Program.cs");
        File.WriteAllText(csPath, code);
        return csPath;
    }

    private void WriteTwoModuleProject()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "ModuleA"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "ModuleB"));

        var csproj = @"<Project Sdk=""Microsoft.NET.Sdk""><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>";
        File.WriteAllText(Path.Combine(_tempDir, "ModuleA", "ModuleA.csproj"), csproj);
        File.WriteAllText(Path.Combine(_tempDir, "ModuleB", "ModuleB.csproj"), csproj);
        File.WriteAllText(Path.Combine(_tempDir, "ModuleA", "A.cs"), "namespace A; public class ATypeA { }");
        File.WriteAllText(Path.Combine(_tempDir, "ModuleB", "B.cs"), "namespace B; public class BTypeB { }");
    }
}
